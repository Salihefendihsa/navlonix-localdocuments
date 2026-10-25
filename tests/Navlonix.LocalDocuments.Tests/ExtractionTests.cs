using System.Text;
using Navlonix.LocalDocuments;
using Xunit;

namespace Navlonix.LocalDocuments.Tests;

public sealed class ExtractionTests
{
    private static PdfTextProcessRunner Runner()
    {
        var root = new DirectoryInfo(AppContext.BaseDirectory);
        while (root is not null && !Directory.Exists(Path.Combine(root.FullName, "src", "Navlonix.LocalDocuments.Worker")))
            root = root.Parent;
        Assert.NotNull(root);
#if DEBUG
        const string configuration = "Debug";
#else
        const string configuration = "Release";
#endif
        var dll = Path.Combine(root!.FullName, "src", "Navlonix.LocalDocuments.Worker", "bin", configuration, "net9.0", "Navlonix.LocalDocuments.Worker.dll");
        Assert.True(File.Exists(dll), "Worker must build before integration tests.");
        return new PdfTextProcessRunner(new PdfWorkerOptions {
            ExecutablePath = "dotnet", PrefixArguments = new[] { dll, PdfTextWorker.Flag }
        });
    }

    private static LocalDocumentTextExtractor Extractor(TextExtractionLimits? limits = null) =>
        new(limits: limits, pdfRunner: Runner());

    [Fact]
    public async Task RealWorker_ExtractsTextFromSyntheticPdf()
    {
        var result = await Extractor().ExtractAsync(Pdf("SYNTHETIC DOCUMENT sample text for extraction 2030"));
        Assert.Equal(TextExtractionStatus.Extracted, result.Status);
        Assert.Contains("SYNTHETIC DOCUMENT", result.Text);
        Assert.Equal(1, result.PageCount);
        Assert.Equal("pdf-text-layer", result.Method);
    }

    [Fact]
    public async Task RealWorker_RejectsCorruptPdfWithoutLeakingParserErrors()
    {
        var result = await Extractor().ExtractAsync(Encoding.ASCII.GetBytes("%PDF-1.4 broken"));
        Assert.Equal(TextExtractionStatus.Corrupt, result.Status);
        Assert.Null(result.Text);
    }

    [Fact]
    public async Task ByteLimit_IsAppliedBeforeTheWorker()
    {
        var result = await Extractor(new TextExtractionLimits(MaxBytes: 8)).ExtractAsync(Pdf("Synthetic text"));
        Assert.Equal(TextExtractionStatus.TooLarge, result.Status);
    }

    [Fact]
    public async Task RealWorker_PageLimitHasExplicitOutcome()
    {
        var result = await Extractor(new TextExtractionLimits(MaxPages: 0)).ExtractAsync(Pdf("Synthetic document content"));
        Assert.Equal(TextExtractionStatus.TooManyPages, result.Status);
    }

    [Fact]
    public async Task RealWorker_CharacterLimitBoundsReturnedText()
    {
        var result = await Extractor(new TextExtractionLimits(MaxChars: 32, MinMeaningfulChars: 5))
            .ExtractAsync(Pdf(new string('A', 200)));
        Assert.Equal(TextExtractionStatus.Extracted, result.Status);
        Assert.True(result.Truncated);
        Assert.InRange(result.TextLength, 5, 32);
    }

    [Fact]
    public async Task RealWorker_BlankPdfDoesNotInventText()
    {
        var result = await Extractor().ExtractAsync(Pdf(""));
        Assert.Equal(TextExtractionStatus.NoTextLayer, result.Status);
        Assert.Null(result.Text);
    }

    [Fact]
    public async Task MissingWorker_IsControlledFailure()
    {
        var runner = new PdfTextProcessRunner(new PdfWorkerOptions {
            ExecutablePath = Path.Combine(Path.GetTempPath(), Guid.NewGuid() + "-missing")
        });
        var result = await new LocalDocumentTextExtractor(pdfRunner: runner).ExtractAsync(Pdf("Synthetic text"));
        Assert.Equal(TextExtractionStatus.Failed, result.Status);
        Assert.Equal(0, runner.OrphanedProcesses);
    }

    [Fact]
    public async Task MissingImageRecognizer_DoesNotPretendToReadText()
    {
        var result = await new LocalDocumentTextExtractor().ExtractAsync(PngHeader(100, 100));
        Assert.Equal(TextExtractionStatus.OcrUnavailable, result.Status);
    }

    [Fact]
    public async Task CallerCancellation_PropagatesFromThePdfWorker()
    {
        using var cts = new CancellationTokenSource();
        cts.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
            Extractor().ExtractAsync(Pdf("Synthetic content"), cts.Token));
    }

    [Fact]
    public async Task RealWorker_TimeoutReturnsSlotAndTracksCleanup()
    {
        var runner = Runner();
        var extractor = new LocalDocumentTextExtractor(
            limits: new TextExtractionLimits(Timeout: TimeSpan.FromTicks(1)), pdfRunner: runner);
        var result = await extractor.ExtractAsync(Pdf("Synthetic content"));
        Assert.Equal(TextExtractionStatus.TimedOut, result.Status);
        Assert.Equal(0, runner.OrphanedProcesses);
        Assert.Equal(0, runner.CleanupFailures);
    }

    [Fact]
    public void PixelBombHeaderCanBeInspectedWithoutDecodingTheImage()
    {
        var dimensions = ImageHeaderInspector.TryRead(PngHeader(100_000, 100_000));
        Assert.NotNull(dimensions);
        Assert.Equal(10_000_000_000L, dimensions!.Value.Pixels);
    }

    [Fact]
    public void CapacityLease_DoubleReleaseDoesNotExceedLimit()
    {
        var gate = new ProcessSlotGate(1);
        var lease = gate.TryAcquire();
        Assert.NotNull(lease);
        Assert.Null(gate.TryAcquire());
        lease!.Release();
        lease.Release();
        Assert.Equal(1, gate.Available);
    }

    [Fact]
    public async Task UnsupportedInputReturnsExplicitStatus()
    {
        var result = await new LocalDocumentTextExtractor().ExtractAsync(Encoding.UTF8.GetBytes("not a document"));
        Assert.Equal(TextExtractionStatus.Unsupported, result.Status);
    }

    private static byte[] PngHeader(uint width, uint height)
    {
        var b = new byte[24];
        new byte[] {137,80,78,71,13,10,26,10}.CopyTo(b, 0);
        Encoding.ASCII.GetBytes("IHDR").CopyTo(b, 12);
        System.Buffers.Binary.BinaryPrimitives.WriteUInt32BigEndian(b.AsSpan(16), width);
        System.Buffers.Binary.BinaryPrimitives.WriteUInt32BigEndian(b.AsSpan(20), height);
        return b;
    }

    private static byte[] Pdf(string text)
    {
        var stream = "BT /F1 12 Tf 40 700 Td (" + text + ") Tj ET";
        var objects = new[] {
            "<< /Type /Catalog /Pages 2 0 R >>",
            "<< /Type /Pages /Kids [3 0 R] /Count 1 >>",
            "<< /Type /Page /Parent 2 0 R /MediaBox [0 0 612 792] /Contents 4 0 R /Resources << /Font << /F1 << /Type /Font /Subtype /Type1 /BaseFont /Helvetica >> >> >> >>",
            "<< /Length " + Encoding.ASCII.GetByteCount(stream) + " >>\nstream\n" + stream + "\nendstream"
        };
        var pdf = new StringBuilder("%PDF-1.4\n");
        var offsets = new List<int>();
        for (var i = 0; i < objects.Length; i++) {
            offsets.Add(Encoding.ASCII.GetByteCount(pdf.ToString()));
            pdf.Append(i + 1).Append(" 0 obj\n").Append(objects[i]).Append("\nendobj\n");
        }
        var xref = Encoding.ASCII.GetByteCount(pdf.ToString());
        pdf.Append("xref\n0 5\n0000000000 65535 f \n");
        foreach (var offset in offsets) pdf.Append(offset.ToString("D10")).Append(" 00000 n \n");
        pdf.Append("trailer\n<< /Size 5 /Root 1 0 R >>\nstartxref\n").Append(xref).Append("\n%%EOF\n");
        return Encoding.ASCII.GetBytes(pdf.ToString());
    }
}


