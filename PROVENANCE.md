# Source and release status

This standalone extraction is licensed under MIT with the user's explicit release authorization on 2026-10-25. Publication is tracked separately from this local preparation.

Original repository: https://github.com/Salihefendihsa/YUK-LE
Source tree: db6ea9e2a44563bf7aa649ce4f133d40abd1e8db
Original files: apps/api/Yukle.Api/Services/Documents/Text/{DocumentTextExtraction.cs, ImageHeaderInspector.cs, PdfTextProcessRunner.cs, PdfTextWorker.cs, ProcessSlotGate.cs, TesseractImageTextRecognizer.cs}

The parent Navlonix application's proprietary license is unchanged. The MIT license here applies only to this extracted package and its standalone additions.
No customer data, credentials, database, application settings, demo accounts, payment integrations or document-verification rules were copied.

Adaptations: standalone namespace, explicit configuration/logging dependencies, independent PDF worker, worker discovery.
The PDF worker uses PdfPig. Image OCR invokes an independently installed Tesseract executable. Their rights are separate from this project's license.

This package does not authenticate documents or provide an operating-system security sandbox.

