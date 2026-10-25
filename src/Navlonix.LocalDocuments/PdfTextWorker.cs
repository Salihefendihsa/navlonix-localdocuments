using System.Text;
using UglyToad.PdfPig;
using UglyToad.PdfPig.DocumentLayoutAnalysis.TextExtractor;
using UglyToad.PdfPig.Exceptions;

namespace Navlonix.LocalDocuments;

/// <summary>
/// PDF metin çıkarma YARDIMCI SÜRECİ (PdfPig). API süreci PDF'i artık kendi içinde ayrıştırmaz: <see cref="PdfTextProcessRunner"/> bu
/// işi ayrı bir süreçte (<c>dotnet Yukle.Api.dll --pdf-worker ...</c>) çalıştırır; böylece takılan ya da çok uzun süren bir ayrıştırma
/// süreç ağacıyla birlikte KESİN olarak sonlandırılabilir ve API içinde arka planda iş kalmaz. Bu, tam işletim sistemi güvenlik izolasyonu
/// DEĞİLDİR (aynı kullanıcı, aynı dosya sistemi, ağ kısıtı yok); yalnız sonlandırılabilir ve kaynak sınırlı bir çalıştırma birimidir.
/// <para>
/// Protokol: argümanlar <c>--pdf-worker input.pdf {maxPages} {maxChars} {minMeaningfulChars}</c> (hepsi sunucudan gelir, kullanıcı verisi içermez).
/// stdout: ilk satır <c>RESULT v1 {Status} {pages} {truncated 0|1}</c>, kalanı düz metin (UTF-8, en çok maxChars karakter).
/// Çıkış kodu 0 = protokol tamam. Belge içeriği ve çıkarılan metin stderr'e/loga YAZILMAZ.
/// </para>
/// </summary>
public static class PdfTextWorker
{
    public const string Flag = "--pdf-worker";
    public const string Header = "RESULT v1";

    /// <summary>Ayrıştırma çıktısı: yalnız durum, sayfa sayısı, kesilme ve (Extracted ise) metin.</summary>
    internal readonly record struct Outcome(TextExtractionStatus Status, int Pages, bool Truncated, string? Text);

    /// <summary>Yardımcı süreç girişi. Döndürülen değer süreç çıkış kodudur.</summary>
    public static int Run(string[] args)
    {
        try
        {
            // args: [--pdf-worker, file, maxPages, maxChars, minMeaningful]
            if (args.Length != 5 || args[0] != Flag
                || !int.TryParse(args[2], out var maxPages) || !int.TryParse(args[3], out var maxChars) || !int.TryParse(args[4], out var minMeaningful))
                return 2;

            var bytes = File.ReadAllBytes(args[1]);
            var o = Extract(bytes, maxPages, maxChars, minMeaningful);

            using var stdout = Console.OpenStandardOutput();
            var head = Encoding.UTF8.GetBytes($"{Header} {o.Status} {o.Pages} {(o.Truncated ? 1 : 0)}\n");
            stdout.Write(head);
            if (o.Text is { Length: > 0 } t) stdout.Write(Encoding.UTF8.GetBytes(t));
            stdout.Flush();
            return 0;
        }
        catch
        {
            return 1;   // iletide belge içeriği taşınabilir: yazılmaz; üst süreç yalnız çıkış kodunu görür
        }
    }

    /// <summary>Eski süreç-içi <c>ReadPdf</c> mantığı (durumlar ve sınırlar aynı).</summary>
    internal static Outcome Extract(byte[] bytes, int maxPages, int maxChars, int minMeaningful)
    {
        PdfDocument document;
        try
        {
            document = PdfDocument.Open(bytes, new ParsingOptions { UseLenientParsing = true, SkipMissingFonts = true });
        }
        catch (PdfDocumentEncryptedException)
        {
            return new(TextExtractionStatus.Encrypted, 0, false, null);
        }
        catch
        {
            return new(TextExtractionStatus.Corrupt, 0, false, null);
        }

        using (document)
        {
            try
            {
                if (document.IsEncrypted)
                    return new(TextExtractionStatus.Encrypted, 0, false, null);

                var pages = document.NumberOfPages;
                if (pages > maxPages)
                    return new(TextExtractionStatus.TooManyPages, pages, false, null);

                var builder = new StringBuilder();
                var truncated = false;
                for (var i = 1; i <= pages; i++)
                {
                    var pageText = ContentOrderTextExtractor.GetText(document.GetPage(i));
                    if (builder.Length > 0) builder.Append('\n');
                    builder.Append(pageText);
                    if (builder.Length >= maxChars)
                    {
                        truncated = true;
                        break;
                    }
                }

                var text = LocalDocumentTextExtractor.Normalize(builder.ToString());
                if (text.Length > maxChars)
                {
                    text = text[..maxChars];
                    truncated = true;
                }

                return LocalDocumentTextExtractor.CountMeaningful(text) >= minMeaningful
                    ? new(TextExtractionStatus.Extracted, pages, truncated, text)
                    : new(TextExtractionStatus.NoTextLayer, pages, false, null);
            }
            catch (PdfDocumentEncryptedException)
            {
                return new(TextExtractionStatus.Encrypted, 0, false, null);
            }
            catch
            {
                return new(TextExtractionStatus.Corrupt, 0, false, null);
            }
        }
    }
}

