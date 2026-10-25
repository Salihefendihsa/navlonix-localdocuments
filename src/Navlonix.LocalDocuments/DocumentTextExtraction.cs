using System.Text;

namespace Navlonix.LocalDocuments;

/// <summary>
/// Yerel metin çıkarma sonucunun durumu. Hiçbiri "belge doğrulandı" anlamına gelmez; yalnız
/// "metin okunabildi mi" sorusunu yanıtlar. Okunamayan belge reddedilmez, manuel incelemeye düşer.
/// </summary>
public enum TextExtractionStatus
{
    /// <summary>Metin katmanından yeterli metin çıkarıldı.</summary>
    Extracted,
    /// <summary>PDF geçerli ama metin katmanı yok ya da boş (taranmış belge olabilir).</summary>
    NoTextLayer,
    /// <summary>PDF şifreli; açılmaya çalışılmaz.</summary>
    Encrypted,
    /// <summary>Dosya bozuk ya da ayrıştırılamadı.</summary>
    Corrupt,
    /// <summary>Desteklenmeyen dosya türü.</summary>
    Unsupported,
    /// <summary>Dosya boyut sınırını aşıyor.</summary>
    TooLarge,
    /// <summary>Sayfa sayısı sınırı aşıldı.</summary>
    TooManyPages,
    /// <summary>İşlem süre sınırını aştı; arka plan işi bir sonraki sayfa sınırında durdurulur.</summary>
    TimedOut,
    /// <summary>Eşzamanlı metin çıkarma sınırı dolu (önceki işler hâlâ sürüyor); sonra tekrar denenebilir.</summary>
    Busy,
    /// <summary>Görsel/taranmış belge: yerel OCR motoru bu ortamda yok.</summary>
    OcrUnavailable,
    /// <summary>Yerel OCR motoru çalıştı ama metin üretemedi.</summary>
    OcrFailed,
    /// <summary>Beklenmeyen hata.</summary>
    Failed,
}

/// <summary>Metin çıkarma sınırları. Hepsi sunucu tarafında zorlanır.</summary>
public sealed record TextExtractionLimits(
    long MaxBytes = 10 * 1024 * 1024,
    int MaxPages = 20,
    int MaxChars = 50_000,
    TimeSpan? Timeout = null,
    int MinMeaningfulChars = 20,
    int MaxConcurrent = 2,
    TimeSpan? OcrTimeout = null,
    TimeSpan? OcrCancelGrace = null)
{
    /// <summary>İstemci iptalinde tanıyıcının süreci sonlandırıp bitmesi için beklenen EN UZUN süre (varsayılan 5 sn). Süre dolarsa istek yine de döner;
    /// bu, sürecin/dosyaların temizlendiğinin garantisi DEĞİLDİR (kooperatif olmayan tanıyıcı arka planda sürebilir).</summary>
    public TimeSpan EffectiveOcrCancelGrace => OcrCancelGrace ?? TimeSpan.FromSeconds(5);
    public TimeSpan EffectiveTimeout => Timeout ?? TimeSpan.FromSeconds(8);
    /// <summary>Görsel OCR dış sınırı: açıkça verilmediyse <see cref="Timeout"/>, o da yoksa 20 sn (motorun kendi sınırı 15 sn).</summary>
    public TimeSpan EffectiveOcrTimeout => OcrTimeout ?? Timeout ?? TimeSpan.FromSeconds(20);
    public static TextExtractionLimits Default { get; } = new();
}

/// <param name="Text">Çıkarılan düz metin; yalnız <see cref="TextExtractionStatus.Extracted"/> durumunda dolu. Loglanmaz.</param>
/// <param name="Method">"pdf-text-layer" ya da "image-ocr"; çıkarma yapılmadıysa null.</param>
public sealed record TextExtractionResult(
    TextExtractionStatus Status,
    string? Text,
    int PageCount,
    bool Truncated,
    string? Method)
{
    public bool HasText => Status == TextExtractionStatus.Extracted && !string.IsNullOrEmpty(Text);
    public int TextLength => Text?.Length ?? 0;

    public static TextExtractionResult Fail(TextExtractionStatus status, int pages = 0) =>
        new(status, null, pages, false, null);
}

/// <summary>Yerel görsel OCR motoru soyutlaması. Dış servise belge göndermez.</summary>
public interface IImageTextRecognizer
{
    bool IsAvailable { get; }
    Task<string?> RecognizeAsync(byte[] imageBytes, CancellationToken ct);
}

/// <summary>
/// Varsayılan: bu dağıtımda çalıştırılabilir ve test edilebilir bir yerel OCR motoru yok. Taranmış
/// ya da görsel belgeler manuel incelemede kalır; OCR varmış gibi davranılmaz.
/// </summary>
public sealed class NoImageTextRecognizer : IImageTextRecognizer
{
    public bool IsAvailable => false;
    public Task<string?> RecognizeAsync(byte[] imageBytes, CancellationToken ct) => Task.FromResult<string?>(null);
}

public interface IDocumentTextExtractor
{
    Task<TextExtractionResult> ExtractAsync(byte[] bytes, CancellationToken ct = default);
}

/// <summary>
/// Dış servis kullanmadan yerel metin çıkarma: metin katmanlı PDF'ler (PdfPig) ve, bir yerel OCR
/// motoru bağlıysa, görseller. Boyut, sayfa, süre ve metin uzunluğu sınırlıdır; bozuk, şifreli, boş
/// ve desteklenmeyen dosyalar istisna fırlatmadan durum olarak döner. Belge içeriği loglanmaz.
/// </summary>
public sealed class LocalDocumentTextExtractor(
    IImageTextRecognizer? imageRecognizer = null,
    TextExtractionLimits? limits = null,
    PdfTextProcessRunner? pdfRunner = null) : IDocumentTextExtractor
{
    private readonly IImageTextRecognizer _recognizer = imageRecognizer ?? new NoImageTextRecognizer();
    private readonly TextExtractionLimits _limits = limits ?? TextExtractionLimits.Default;

    /// <summary>PDF'i ayrı süreçte çalıştırır (bkz. <see cref="PdfTextWorker"/>).</summary>
    internal PdfTextProcessRunner PdfRunner { get; } = pdfRunner ?? new PdfTextProcessRunner();

    public async Task<TextExtractionResult> ExtractAsync(byte[] bytes, CancellationToken ct = default)
    {
        try
        {
            if (bytes is null || bytes.Length == 0)
                return TextExtractionResult.Fail(TextExtractionStatus.Unsupported);
            if (bytes.Length > _limits.MaxBytes)
                return TextExtractionResult.Fail(TextExtractionStatus.TooLarge);

            if (IsPdf(bytes))
                return await ExtractPdfAsync(bytes, ct);
            if (IsImage(bytes))
                return await ExtractImageAsync(bytes, ct);
            return TextExtractionResult.Fail(TextExtractionStatus.Unsupported);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            throw;
        }
        catch
        {
            // Hata iletisi belge içeriği taşıyabilir; yalnız durum döner.
            return TextExtractionResult.Fail(TextExtractionStatus.Failed);
        }
    }

    private Task<TextExtractionResult> ExtractPdfAsync(byte[] bytes, CancellationToken ct) =>
        // Ayrı süreç + süreç kapasitesi (yuva, süreç çıkışı doğrulanana dek tutulur; dolu ise süreç başlatılmadan Busy). Çağıran iptali OperationCanceledException olarak yayılır.
        PdfRunner.RunAsync(bytes, _limits, ct);

    private async Task<TextExtractionResult> ExtractImageAsync(byte[] bytes, CancellationToken ct)
    {
        if (!_recognizer.IsAvailable)
            return TextExtractionResult.Fail(TextExtractionStatus.OcrUnavailable, 1);

        using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        cts.CancelAfter(_limits.EffectiveOcrTimeout);
        try
        {
            var recognition = _recognizer.RecognizeAsync(bytes, cts.Token);
            string? raw;
            try
            {
                raw = await recognition.WaitAsync(_limits.EffectiveOcrTimeout, ct);
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested)
            {
                // İstemci iptali: tanıyıcı bağlı belirteçle süreç ağacını sonlandırır. Yanıt dönmeden önce bitişi SINIRLI bir süre beklenir;
                // böylece süreç ve geçici dosyalar iptal gözlendiğinde temizlenmiş olur (kooperatif olmayan tanıyıcı isteği bu süreden fazla tutamaz; süre dolarsa temizlik GARANTİ DEĞİLDİR, bkz. EffectiveOcrCancelGrace).
                cts.Cancel();
                try { await recognition.WaitAsync(_limits.EffectiveOcrCancelGrace); } catch { /* iptal/zaman aşımı/hata: özgün iptal yayılır */ }
                throw;
            }
            var text = Normalize(raw ?? string.Empty);
            if (text.Length > _limits.MaxChars) text = text[.._limits.MaxChars];
            return CountMeaningful(text) >= _limits.MinMeaningfulChars
                ? new TextExtractionResult(TextExtractionStatus.Extracted, text, 1, text.Length >= _limits.MaxChars, "image-ocr")
                : TextExtractionResult.Fail(TextExtractionStatus.OcrFailed, 1);
        }
        catch (OcrException oe)
        {
            return TextExtractionResult.Fail(oe.Status, 1); // motorun kontrollü durumu (Corrupt, TooLarge, Busy, TimedOut...); içerik taşımaz
        }
        catch (TimeoutException)
        {
            return TextExtractionResult.Fail(TextExtractionStatus.TimedOut, 1);
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested)
        {
            return TextExtractionResult.Fail(TextExtractionStatus.TimedOut, 1);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            return TextExtractionResult.Fail(TextExtractionStatus.OcrFailed, 1);
        }
    }

    private static bool IsPdf(byte[] b)
    {
        // %PDF- imzası dosyanın ilk 1024 baytında aranır (bazı üreticiler başa bayt ekler).
        var span = b.AsSpan(0, Math.Min(b.Length, 1024));
        return span.IndexOf("%PDF-"u8) >= 0;
    }

    private static bool IsImage(byte[] b) =>
        (b.Length >= 3 && b[0] == 0xFF && b[1] == 0xD8 && b[2] == 0xFF)
        || (b.Length >= 8 && b[0] == 0x89 && b[1] == 0x50 && b[2] == 0x4E && b[3] == 0x47)
        || (b.Length >= 12 && b[0] == 0x52 && b[1] == 0x49 && b[2] == 0x46 && b[3] == 0x46 && b[8] == 0x57 && b[9] == 0x45);

    /// <summary>Kontrol karakterlerini atar (düz metin güvenliği), boşlukları ve satırları sadeleştirir.</summary>
    internal static string Normalize(string raw)
    {
        var sb = new StringBuilder(raw.Length);
        var lastWasSpace = false;
        foreach (var ch in raw)
        {
            if (ch is '\n' or '\r')
            {
                if (sb.Length > 0 && sb[^1] != '\n') sb.Append('\n');
                lastWasSpace = false;
            }
            else if (char.IsControl(ch) || ch == ' ' || char.IsWhiteSpace(ch))
            {
                if (!lastWasSpace && sb.Length > 0 && sb[^1] != '\n') sb.Append(' ');
                lastWasSpace = true;
            }
            else
            {
                sb.Append(ch);
                lastWasSpace = false;
            }
        }
        return sb.ToString().Trim();
    }

    internal static int CountMeaningful(string text) => text.Count(char.IsLetterOrDigit);
}

