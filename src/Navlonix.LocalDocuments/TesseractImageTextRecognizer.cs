using System.Diagnostics;
using System.Text;

namespace Navlonix.LocalDocuments;

/// <summary>OCR motoru kontrollü bir sonuç üretemedi; <see cref="Status"/> doğrudan metin çıkarma durumuna eşlenir. İleti belge içeriği taşımaz.</summary>
public sealed class OcrException(TextExtractionStatus status) : Exception(status.ToString())
{
    public TextExtractionStatus Status { get; } = status;
}

/// <summary>
/// Yerel Tesseract OCR ayarları. Hepsi sunucu tarafında zorlanır. Model/dil dosyası ÇALIŞMA SIRASINDA indirilmez:
/// motor yalnız <see cref="TessdataDir"/> (ya da paketin kendi tessdata dizini) altındaki dosyaları kullanır.
/// </summary>
public sealed record TesseractOcrOptions
{
    public string ExecutablePath { get; init; } = "tesseract";
    /// <summary>Boşsa motorun varsayılan tessdata dizini kullanılır (paket kurulumu).</summary>
    public string? TessdataDir { get; init; }
    public string Languages { get; init; } = "tur+eng";
    /// <summary>Geçici iş dizinlerinin kökü; boşsa sistem geçici dizini. Her istek kendi alt dizinini açar ve siler.</summary>
    public string? TempRoot { get; init; }
    public long MaxBytes { get; init; } = 10 * 1024 * 1024;
    public long MaxPixels { get; init; } = 25_000_000;
    public long MaxSide { get; init; } = 12_000;
    public int MaxOutputChars { get; init; } = 50_000;
    public int MaxConcurrent { get; init; } = 2;
    public TimeSpan Timeout { get; init; } = TimeSpan.FromSeconds(15);
    /// <summary>Sonlandırma sonrası sürecin gerçekten bittiğini doğrulamak için beklenen EN UZUN süre (zaman sınırına EK).</summary>
    public TimeSpan KillWait { get; init; } = TimeSpan.FromSeconds(2);
    /// <summary>Dolu kapasitede yetim sürece yeniden sonlandırma denensin mi (varsayılan evet). Yalnız testler false verir.</summary>
    public bool RetryKillOnSweep { get; init; } = true;
    public TimeSpan ProbeTimeout { get; init; } = TimeSpan.FromSeconds(10);
    /// <summary>Başarısız yoklama sonucunun yeniden denenmeden önce önbellekte kalma süresi.</summary>
    public TimeSpan FailedProbeRetry { get; init; } = TimeSpan.FromMinutes(1);

    public static TesseractOcrOptions FromConfiguration(IConfiguration c)
    {
        var d = new TesseractOcrOptions();
        return d with
        {
            ExecutablePath = c["Ocr:ExecutablePath"] is { Length: > 0 } e ? e : d.ExecutablePath,
            TessdataDir = c["Ocr:TessdataDir"] is { Length: > 0 } t ? t : null,
            Languages = c["Ocr:Languages"] is { Length: > 0 } l ? l : d.Languages,
            MaxBytes = c.GetValue("Ocr:MaxBytes", d.MaxBytes),
            MaxPixels = c.GetValue("Ocr:MaxPixels", d.MaxPixels),
            MaxSide = c.GetValue("Ocr:MaxSide", d.MaxSide),
            MaxOutputChars = c.GetValue("Ocr:MaxOutputChars", d.MaxOutputChars),
            MaxConcurrent = Math.Max(1, c.GetValue("Ocr:MaxConcurrent", d.MaxConcurrent)),
            Timeout = TimeSpan.FromSeconds(Math.Max(1, c.GetValue("Ocr:TimeoutSeconds", (int)d.Timeout.TotalSeconds))),
        };
    }
}

/// <summary>
/// Gerçek yerel OCR: Tesseract AYRI SÜREÇTE çalışır. Dış servise veri gitmez. Görsel, istek başına benzersiz bir
/// geçici dizine SABİT bir ad ("input.img") ile yazılır; kullanıcı dosya adı komuta hiç girmez ve komut shell
/// üzerinden değil argüman listesiyle çalıştırılır. Boyut, piksel, çıktı uzunluğu, süre ve eşzamanlı iş sayısı
/// sınırlıdır; zaman aşımında ve iptalde süreç ağacı sonlandırılır ve geçici dizin her durumda silinir.
/// Belge içeriği ve motor çıktısı loglanmaz.
/// </summary>
public sealed class TesseractImageTextRecognizer(TesseractOcrOptions? options = null, ILogger? logger = null) : IImageTextRecognizer
{
    private readonly TesseractOcrOptions _o = options ?? new TesseractOcrOptions();
    // Süreç kapasitesi: yuva, süreç çıkışı DOĞRULANANA dek yeniden kullanılmaz (bkz. ProcessSlotGate).
    private readonly ProcessSlotGate _gate = new((options ?? new TesseractOcrOptions()).MaxConcurrent, logger, "OCR", (options ?? new TesseractOcrOptions()).RetryKillOnSweep);
    private long _cleanupFailures;

    /// <summary>Çıkışı istek süresinde DOĞRULANAMAYAN süreç sayısı (toplam; temiz sonlanma sayılmadı).</summary>
    public long UnverifiedTerminations => _gate.UnverifiedTotal;
    /// <summary>Şu an çıkışı doğrulanamamış ve yuvasını tutan süreç sayısı (kaybedilmiş kapasite).</summary>
    public int OrphanedProcesses => _gate.Orphaned;
    /// <summary>Yetim bırakılıp sonradan çıkışı doğrulanan süreç sayısı.</summary>
    public long RecoveredAfterDelay => _gate.RecoveredTotal;
    /// <summary>Geçici dizini silinemeyen istek sayısı.</summary>
    public long CleanupFailures => Interlocked.Read(ref _cleanupFailures);

    internal ProcessSlotGate Gate => _gate;
    /// <summary>Yalnız testler: sonlandırma + doğrulama davranışını değiştirir (true = süreç bitti doğrulandı).</summary>
    internal Func<Process, TimeSpan, Task<bool>>? KillAndVerify { get; init; }
    private readonly object _probeLock = new();
    private bool? _available;
    private DateTime _probedAtUtc;

    /// <summary>
    /// Motor çalıştırılabiliyor VE istenen dil verilerinin hepsi yerelde var mı. Eksikse false (belge manuel incelemede kalır;
    /// OCR varmış gibi davranılmaz). Başarı kalıcı, başarısızlık kısa süre önbelleğe alınır.
    /// </summary>
    public bool IsAvailable
    {
        get
        {
            lock (_probeLock)
            {
                if (_available is true) return true;
                if (_available is false && DateTime.UtcNow - _probedAtUtc < _o.FailedProbeRetry) return false;
                _available = Probe();
                _probedAtUtc = DateTime.UtcNow;
                return _available.Value;
            }
        }
    }

    private bool Probe()
    {
        try
        {
            var psi = NewStartInfo(Directory.GetCurrentDirectory());
            psi.ArgumentList.Add("--list-langs");
            if (_o.TessdataDir is { } dir) { psi.ArgumentList.Add("--tessdata-dir"); psi.ArgumentList.Add(dir); }
            using var p = Process.Start(psi);
            if (p is null) return false;
            var stdout = p.StandardOutput.ReadToEndAsync();
            _ = p.StandardError.BaseStream.CopyToAsync(Stream.Null);
            if (!p.WaitForExit((int)_o.ProbeTimeout.TotalMilliseconds))
            {
                KillTree(p);
                return false;
            }
            if (p.ExitCode != 0) return false;
            var have = stdout.GetAwaiter().GetResult()
                .Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
                .Skip(1) // başlık satırı
                .ToHashSet(StringComparer.Ordinal);
            return _o.Languages.Split('+', StringSplitOptions.RemoveEmptyEntries).All(have.Contains);
        }
        catch
        {
            return false; // motor yok / çalıştırılamadı
        }
    }

    public async Task<string?> RecognizeAsync(byte[] imageBytes, CancellationToken ct)
    {
        if (imageBytes is null || imageBytes.Length == 0) throw new OcrException(TextExtractionStatus.Corrupt);
        if (imageBytes.Length > _o.MaxBytes) throw new OcrException(TextExtractionStatus.TooLarge);

        // Görseli çözmeden boyut denetimi: bozuk başlık Corrupt, aşırı çözünürlük TooLarge.
        var dims = ImageHeaderInspector.TryRead(imageBytes) ?? throw new OcrException(TextExtractionStatus.Corrupt);
        if (dims.Pixels > _o.MaxPixels || dims.Width > _o.MaxSide || dims.Height > _o.MaxSide)
            throw new OcrException(TextExtractionStatus.TooLarge);

        if (!IsAvailable) throw new OcrException(TextExtractionStatus.OcrUnavailable);

        // Yuva yoksa (yetimler dahil) süreç BAŞLATILMADAN Busy: kuyruk/arka plan birikimi yok.
        var lease = _gate.TryAcquire() ?? throw new OcrException(TextExtractionStatus.Busy);
        var workDir = Path.Combine(_o.TempRoot ?? Path.GetTempPath(), "navlonix-ocr-" + Guid.NewGuid().ToString("N"));
        Process? process = null;
        try
        {
            Directory.CreateDirectory(workDir);
            var input = Path.Combine(workDir, "input.img");
            await File.WriteAllBytesAsync(input, imageBytes, ct);

            using var timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
            timeoutCts.CancelAfter(_o.Timeout);
            var token = timeoutCts.Token;

            var psi = NewStartInfo(workDir);
            psi.ArgumentList.Add("input.img");
            psi.ArgumentList.Add("stdout");
            psi.ArgumentList.Add("-l"); psi.ArgumentList.Add(_o.Languages);
            psi.ArgumentList.Add("--psm"); psi.ArgumentList.Add("3");
            if (_o.TessdataDir is { } dir) { psi.ArgumentList.Add("--tessdata-dir"); psi.ArgumentList.Add(dir); }
            psi.Environment["OMP_THREAD_LIMIT"] = "1"; // süreç başına tek iş parçacığı: CPU patlaması yok

            try
            {
                process = Process.Start(psi) ?? throw new OcrException(TextExtractionStatus.OcrUnavailable);
                _ = DrainQuietlyAsync(process.StandardError.BaseStream, token); // hata çıktısı tüketilir, saklanmaz/loglanmaz
                var text = await ReadBoundedAsync(process.StandardOutput, _o.MaxOutputChars, token);
                if (text.Truncated) await TerminateAsync(process);
                await process.WaitForExitAsync(token);
                if (!text.Truncated && process.ExitCode != 0) throw new OcrException(TextExtractionStatus.OcrFailed);
                return text.Value;
            }
            catch (OperationCanceledException) when (!ct.IsCancellationRequested)
            {
                throw new OcrException(TextExtractionStatus.TimedOut);
            }
        }
        catch (OcrException) { throw; }
        catch (OperationCanceledException) { throw; }
        catch (Exception ex) when (ex is System.ComponentModel.Win32Exception or IOException or InvalidOperationException)
        {
            throw new OcrException(TextExtractionStatus.OcrFailed);
        }
        finally
        {
            // Süreç nesnesi + geçici dizin YALNIZ süreç bittiği DOĞRULANINCA bırakılır; yuva da öyle (bkz. ProcessSlotGate).
            var released = 0;
            void ReleaseResources()
            {
                if (Interlocked.Exchange(ref released, 1) != 0) return;
                try { process?.Dispose(); } catch { /* yut */ }
                try { if (Directory.Exists(workDir)) Directory.Delete(workDir, recursive: true); }
                catch
                {
                    Interlocked.Increment(ref _cleanupFailures);   // başarılı temizleme SAYILMAZ
                    logger?.LogWarning("OCR yardımcı süreci geçici dizini silinemedi; dizin yetim kalmış olabilir.");
                }
            }

            var exited = process is null || SafeExited(process);
            if (!exited) exited = await TerminateAsync(process!);
            if (exited)
            {
                ReleaseResources();
                lease.Release();
            }
            else _gate.Orphan(lease, process!, ReleaseResources);   // çıkış doğrulanamadı: dizin ve yuva gecikmiş çıkışa dek tutulur
        }
    }

    private async Task<bool> TerminateAsync(Process p)
    {
        try
        {
            return KillAndVerify is not null
                ? await KillAndVerify(p, _o.KillWait)
                : await ProcessTermination.KillAndVerifyAsync(p, _o.KillWait);
        }
        catch { return false; }
    }

    private static bool SafeExited(Process p)
    {
        try { return p.HasExited; } catch { return false; }
    }

    private ProcessStartInfo NewStartInfo(string workDir) => new(_o.ExecutablePath)
    {
        WorkingDirectory = workDir,
        RedirectStandardOutput = true,
        RedirectStandardError = true,
        RedirectStandardInput = false,
        UseShellExecute = false,
        CreateNoWindow = true,
        StandardOutputEncoding = Encoding.UTF8,
    };

    private static async Task DrainQuietlyAsync(Stream s, CancellationToken ct)
    {
        try { await s.CopyToAsync(Stream.Null, ct); }
        catch { /* iptal/süreç sonlandırma: gözlenmeyen istisna bırakılmaz */ }
    }

    private static void KillTree(Process p)
    {
        try { p.Kill(entireProcessTree: true); } catch { /* bitmiş olabilir */ }
        try { p.WaitForExit(2000); } catch { /* yut */ }
    }

    private static async Task<(string Value, bool Truncated)> ReadBoundedAsync(StreamReader reader, int maxChars, CancellationToken ct)
    {
        var sb = new StringBuilder();
        var buf = new char[4096];
        while (true)
        {
            var n = await reader.ReadAsync(buf.AsMemory(), ct);
            if (n == 0) return (sb.ToString(), false);
            if (sb.Length + n > maxChars)
            {
                sb.Append(buf, 0, maxChars - sb.Length);
                return (sb.ToString(), true);
            }
            sb.Append(buf, 0, n);
        }
    }
}

