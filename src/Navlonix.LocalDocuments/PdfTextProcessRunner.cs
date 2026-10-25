using System.ComponentModel;
using System.Diagnostics;
using System.Globalization;
using System.Text;

namespace Navlonix.LocalDocuments;

/// <summary>
/// Yardımcı süreç ayarları. <see cref="ExecutablePath"/>/<see cref="PrefixArguments"/> boşsa varsayılan: <c>dotnet {Yukle.Api.dll} --pdf-worker</c>
/// (yayın çıktısında ve Docker imajında zaten bulunan aynı derleme; ek dosya yok).
/// </summary>
public sealed record PdfWorkerOptions
{
    public string? ExecutablePath { get; init; }
    public IReadOnlyList<string>? PrefixArguments { get; init; }
    /// <summary>Geçici iş dizinlerinin kökü; boşsa sistem geçici dizini. Her istek kendi alt dizinini açar ve siler.</summary>
    public string? TempRoot { get; init; }
    /// <summary>Sonlandırma sonrası sürecin gerçekten bittiğini doğrulamak için beklenen EN UZUN süre (zaman sınırına EK).</summary>
    public TimeSpan KillWait { get; init; } = TimeSpan.FromSeconds(2);
    /// <summary>Aynı anda çalışabilecek (ya da çıkışı doğrulanamadığı için yuvasını tutan) en çok yardımcı süreç. Boşsa ilk çağrıdaki <see cref="TextExtractionLimits.MaxConcurrent"/> kullanılır.</summary>
    public int? MaxConcurrent { get; init; }
    /// <summary>Dolu kapasitede yetim sürece yeniden sonlandırma denensin mi (varsayılan evet). Yalnız testler "sonlandırılamayan süreç" benzetimi için false verir.</summary>
    public bool RetryKillOnSweep { get; init; } = true;
    /// <summary>Yardımcı sürecin yönetilen yığın üst sınırı (DOTNET_GCHeapHardLimit); aşılırsa süreç çöker ve sonuç <c>Failed</c> olur. 0 = sınır yok.</summary>
    public long MaxMemoryBytes { get; init; } = 512L * 1024 * 1024;
}

/// <summary>
/// PDF'i AYRI SÜREÇTE çıkarır. Tüm girdiler sunucu denetimindedir: PDF, istek başına benzersiz bir geçici dizine SABİT adla
/// (<c>input.pdf</c>) yazılır; komut shell ile değil <c>ArgumentList</c> ile kurulur. stdout sınırlı okunur, stderr tüketilip atılır (kanal
/// dolup süreci kilitlemez, içerik saklanmaz/loglanmaz).
/// <para>
/// <b>Süre sınırı</b> duvar saati sınırıdır ve süreç BAŞLATMAYI (host + çalışma zamanı + PdfPig yüklenmesi), geçici dosya yazımını, ayrıştırmayı ve çıktı
/// okumayı İÇERİR (<see cref="TextExtractionLimits.EffectiveTimeout"/>, zamanlayıcı süreç başlatılmadan önce kurulur). Süre dolunca ya da istek iptal
/// olunca süreç ağacı sonlandırılır ve çıkışı en çok <see cref="PdfWorkerOptions.KillWait"/> kadar gözlenir; bu bekleme zaman sınırına EKtir
/// (en kötü durum: sınır + sonlandırma bekleme süresi). Sonlanma doğrulanamazsa <see cref="UnverifiedTerminations"/> artar ve temizlik başarılı sayılmaz.
/// </para>
/// </summary>
public sealed class PdfTextProcessRunner(PdfWorkerOptions? options = null, ILogger? logger = null)
{
    private readonly PdfWorkerOptions _o = options ?? new PdfWorkerOptions();
    private readonly object _gateLock = new();
    private ProcessSlotGate? _gate;
    private long _cleanupFailures;

    /// <summary>Süreç kapasitesi kapısı (ilk çağrıda kurulur). Bkz. <see cref="ProcessSlotGate"/>: yuva, süreç çıkışı doğrulanana dek yeniden kullanılmaz.</summary>
    internal ProcessSlotGate GetGate(TextExtractionLimits limits)
    {
        if (_gate is { } g) return g;
        lock (_gateLock) return _gate ??= new ProcessSlotGate(_o.MaxConcurrent ?? limits.MaxConcurrent, logger, "PDF", _o.RetryKillOnSweep);
    }

    /// <summary>Çıkışı istek süresinde DOĞRULANAMAYAN süreç sayısı (toplam; her biri yetim bırakıldı, temiz sonlanma sayılmadı).</summary>
    public long UnverifiedTerminations => _gate?.UnverifiedTotal ?? 0;
    /// <summary>Şu an çıkışı doğrulanamamış ve yuvasını tutan süreç sayısı (kaybedilmiş kapasite).</summary>
    public int OrphanedProcesses => _gate?.Orphaned ?? 0;
    /// <summary>Yetim bırakılıp sonradan çıkışı doğrulanan süreç sayısı.</summary>
    public long RecoveredAfterDelay => _gate?.RecoveredTotal ?? 0;
    /// <summary>Geçici dizini silinemeyen istek sayısı (dizin yetim kalmış olabilir).</summary>
    public long CleanupFailures => Interlocked.Read(ref _cleanupFailures);

    /// <summary>Yalnız testler: sonlandırma + doğrulama davranışını değiştirir (true = süreç bitti doğrulandı).</summary>
    internal Func<Process, TimeSpan, Task<bool>>? KillAndVerify { get; init; }
    /// <summary>Yalnız testler: geçici dizin silmeyi değiştirir (istisna = silinemedi).</summary>
    internal Action<string>? DeleteDirectory { get; init; }

    public async Task<TextExtractionResult> RunAsync(byte[] pdfBytes, TextExtractionLimits limits, CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();

        // Kapasite KONTROLÜ süreç başlatmadan ÖNCE: dolu ise (yetimler dahil) süreç da geçici dizin de oluşturulmaz.
        var gate = GetGate(limits);
        var lease = gate.TryAcquire();
        if (lease is null) return TextExtractionResult.Fail(TextExtractionStatus.Busy);

        using var timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        timeoutCts.CancelAfter(limits.EffectiveTimeout);   // süreç başlatma dahil: zamanlayıcı BURADA başlar
        var token = timeoutCts.Token;

        var workDir = Path.Combine(_o.TempRoot ?? Path.GetTempPath(), "navlonix-pdf-" + Guid.NewGuid().ToString("N"));
        Process? process = null;
        try
        {
            Directory.CreateDirectory(workDir);
            await File.WriteAllBytesAsync(Path.Combine(workDir, "input.pdf"), pdfBytes, token);

            var (exe, prefix) = Resolve();
            var psi = new ProcessStartInfo(exe)
            {
                WorkingDirectory = workDir,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                RedirectStandardInput = false,
                UseShellExecute = false,
                CreateNoWindow = true,
                StandardOutputEncoding = Encoding.UTF8,
            };
            foreach (var a in prefix) psi.ArgumentList.Add(a);
            psi.ArgumentList.Add("input.pdf");
            psi.ArgumentList.Add(limits.MaxPages.ToString(CultureInfo.InvariantCulture));
            psi.ArgumentList.Add(limits.MaxChars.ToString(CultureInfo.InvariantCulture));
            psi.ArgumentList.Add(limits.MinMeaningfulChars.ToString(CultureInfo.InvariantCulture));
            psi.Environment["DOTNET_NOLOGO"] = "1";
            psi.Environment["DOTNET_CLI_TELEMETRY_OPTOUT"] = "1";
            if (_o.MaxMemoryBytes > 0) psi.Environment["DOTNET_GCHeapHardLimit"] = _o.MaxMemoryBytes.ToString("X", CultureInfo.InvariantCulture);

            try { process = Process.Start(psi); }
            catch (Exception ex) when (ex is Win32Exception or InvalidOperationException or FileNotFoundException)
            {
                return TextExtractionResult.Fail(TextExtractionStatus.Failed);   // yardımcı çalıştırılabilir bulunamadı/başlatılamadı
            }
            if (process is null) return TextExtractionResult.Fail(TextExtractionStatus.Failed);

            _ = DrainQuietlyAsync(process.StandardError.BaseStream, token);   // stderr: tüketilir, saklanmaz, loglanmaz
            var (output, overflow) = await ReadBoundedAsync(process.StandardOutput, limits.MaxChars + 512, token);
            if (overflow)
            {
                await TerminateAsync(process);
                return TextExtractionResult.Fail(TextExtractionStatus.Failed);   // protokol dışı/aşırı çıktı: çıktı atılır
            }

            await process.WaitForExitAsync(token);
            if (process.ExitCode != 0) return TextExtractionResult.Fail(TextExtractionStatus.Failed);
            return Parse(output, limits);
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested)
        {
            // Zaman sınırı doldu (yalnız çağıran iptal etmedi): süreç ağacı finally'de sonlandırılır.
            return TextExtractionResult.Fail(TextExtractionStatus.TimedOut);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return TextExtractionResult.Fail(TextExtractionStatus.Failed);
        }
        finally
        {
            // Süreç kaynakları (süreç nesnesi + geçici dizin) YALNIZ süreç bittiği DOĞRULANINCA bırakılır; yuva da öyle.
            var released = 0;
            void ReleaseResources()
            {
                if (Interlocked.Exchange(ref released, 1) != 0) return;
                try { process?.Dispose(); } catch { /* yut */ }
                try
                {
                    if (DeleteDirectory is not null) DeleteDirectory(workDir);
                    else if (Directory.Exists(workDir)) Directory.Delete(workDir, recursive: true);
                }
                catch
                {
                    Interlocked.Increment(ref _cleanupFailures);   // başarılı temizleme SAYILMAZ
                    logger?.LogWarning("PDF yardımcı süreci geçici dizini silinemedi; dizin yetim kalmış olabilir.");
                }
            }

            var exited = process is null || SafeExited(process);
            if (!exited) exited = await TerminateAsync(process!);
            if (exited)
            {
                ReleaseResources();
                lease.Release();
            }
            else
            {
                // Çıkış doğrulanamadı: süreç çalışıyor OLABİLİR. Geçici dizin ERKEN silinmez, yuva SERBEST bırakılmaz; ikisi de
                // gecikmiş çıkış doğrulanınca (Exited olayı ya da dolu kapasitede tarama) bırakılır. Bkz. ProcessSlotGate.
                gate.Orphan(lease, process!, ReleaseResources);
            }
        }
    }

    /// <summary>Süreç ağacını sonlandırır ve çıkışı <see cref="PdfWorkerOptions.KillWait"/> içinde gözler. true = çıkış doğrulandı.</summary>
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

    private (string Exe, IReadOnlyList<string> Prefix) Resolve()
    {
        if (_o.ExecutablePath is { Length: > 0 } e) return (e, _o.PrefixArguments ?? []);
        var dll = Path.Combine(AppContext.BaseDirectory, "Navlonix.LocalDocuments.Worker.dll");
        if (!File.Exists(dll)) throw new FileNotFoundException("Configure PdfWorkerOptions.ExecutablePath and PrefixArguments for the separately published worker.");
        return (DotnetHost(), _o.PrefixArguments ?? [dll, PdfTextWorker.Flag]);
    }

    private static string DotnetHost()
    {
        var current = Environment.ProcessPath;
        if (current is not null && Path.GetFileNameWithoutExtension(current).Equals("dotnet", StringComparison.OrdinalIgnoreCase)) return current;
        if (Environment.GetEnvironmentVariable("DOTNET_HOST_PATH") is { Length: > 0 } h) return h;
        return "dotnet";
    }

    private static TextExtractionResult Parse(string output, TextExtractionLimits limits)
    {
        var nl = output.IndexOf('\n');
        var head = (nl < 0 ? output : output[..nl]).Split(' ');
        if (head.Length != 5 || $"{head[0]} {head[1]}" != PdfTextWorker.Header
            || !Enum.TryParse<TextExtractionStatus>(head[2], out var status) || !Enum.IsDefined(status)
            || !int.TryParse(head[3], NumberStyles.None, CultureInfo.InvariantCulture, out var pages)
            || head[4] is not ("0" or "1"))
            return TextExtractionResult.Fail(TextExtractionStatus.Failed);   // protokol ihlali

        var truncated = head[4] == "1";
        switch (status)
        {
            case TextExtractionStatus.Extracted:
                var text = nl < 0 ? string.Empty : output[(nl + 1)..];
                if (text.Length > limits.MaxChars) { text = text[..limits.MaxChars]; truncated = true; }
                return text.Length == 0
                    ? TextExtractionResult.Fail(TextExtractionStatus.Failed)
                    : new TextExtractionResult(TextExtractionStatus.Extracted, text, pages, truncated, "pdf-text-layer");
            case TextExtractionStatus.NoTextLayer:
            case TextExtractionStatus.TooManyPages:
                return TextExtractionResult.Fail(status, pages);
            case TextExtractionStatus.Encrypted:
            case TextExtractionStatus.Corrupt:
                return TextExtractionResult.Fail(status);
            default:
                return TextExtractionResult.Fail(TextExtractionStatus.Failed);   // yardımcı yalnız bu durumları üretir
        }
    }

    private static async Task<(string Value, bool Overflow)> ReadBoundedAsync(StreamReader reader, int maxChars, CancellationToken ct)
    {
        var sb = new StringBuilder();
        var buf = new char[4096];
        while (true)
        {
            var n = await reader.ReadAsync(buf.AsMemory(), ct);
            if (n == 0) return (sb.ToString(), false);
            if (sb.Length + n > maxChars) return (string.Empty, true);   // sınır aşıldı: içerik atılır
            sb.Append(buf, 0, n);
        }
    }

    private static async Task DrainQuietlyAsync(Stream s, CancellationToken ct)
    {
        try { await s.CopyToAsync(Stream.Null, ct); }
        catch { /* iptal/süreç sonlandırma */ }
    }
}

