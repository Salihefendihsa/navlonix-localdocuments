# Navlonix.LocalDocuments

Local PDF text extraction and image OCR for .NET applications, with bounded helper processes.

**License: MIT.** This standalone extraction is released separately from the private Navlonix application. See LICENSE and PROVENANCE.md.

## What exists

- PDF text-layer extraction using PdfPig in a separate .NET worker.
- Optional local image OCR through an installed Tesseract executable (Turkish and English by default).
- Byte, page, character, pixel, timeout and concurrency limits.
- Explicit outcomes for corrupt/encrypted files, missing OCR, timeouts and busy workers.
- Process exit tracking: capacity is not returned until a worker's exit is observed.

This is an extraction of existing Navlonix code, not a new OCR engine. It makes no adoption, accuracy or performance claims.

## Build and test

Requires .NET 9 SDK. NuGet restore needs network access or cached dependencies.

```powershell
dotnet build src/Navlonix.LocalDocuments.Worker/Navlonix.LocalDocuments.Worker.csproj
dotnet test tests/Navlonix.LocalDocuments.Tests/Navlonix.LocalDocuments.Tests.csproj
dotnet publish src/Navlonix.LocalDocuments.Worker/Navlonix.LocalDocuments.Worker.csproj -c Release -o artifacts/worker
```

## Integration

Deploy the **entire worker publish directory**, including runtimeconfig and dependencies. Configure an absolute path to its DLL:

```csharp
using Navlonix.LocalDocuments;

var pdfRunner = new PdfTextProcessRunner(new PdfWorkerOptions {
    ExecutablePath = "dotnet",
    PrefixArguments = new[] { "/absolute/path/worker/Navlonix.LocalDocuments.Worker.dll", PdfTextWorker.Flag }
});
var extractor = new LocalDocumentTextExtractor(
    new TesseractImageTextRecognizer(new TesseractOcrOptions { Languages = "tur+eng" }),
    new TextExtractionLimits(MaxBytes: 10 * 1024 * 1024, MaxPages: 20, MaxChars: 50_000),
    pdfRunner);
var result = await extractor.ExtractAsync(await File.ReadAllBytesAsync("document.pdf"));
Console.WriteLine(result.Status);
// Handle the returned text privately; avoid logging document contents.
```

If Tesseract is absent, image extraction reports OcrUnavailable. Install its executable and language data separately; the library does not download them. Omitting the recognizer disables image OCR.

## Boundaries

- Image-only/scanned PDFs report NoTextLayer. PDF rasterization plus OCR is not implemented.
- Extraction is not identity verification, signature validation or document authenticity verification.
- Process separation is not an OS sandbox: helpers share the launching account's access.
- The PDF heap limit is a managed heap setting, not a complete OS memory/CPU quota. OCR has input and concurrency limits, not a hard OS memory limit.
- Timeout may be followed by a bounded termination wait. Unverified exits keep their slot; failed cleanup is observable.
- Concurrency limits apply per runner/recognizer instance. Reuse instances; they are not distributed limits.
- Supported image header formats are PNG, JPEG and WebP. Format headers do not prove a file is safe.
- Current target is net9.0. No broad cross-platform certification or package-registry publication is claimed.

## Validation and remaining checks

The standalone Windows build and 13 automated tests passed, and the published PDF worker processed a synthetic PDF. Real Tesseract execution and Linux checks remain to be verified. See VALIDATION.md and THIRD-PARTY-NOTICES.md. No NuGet publication is claimed.

