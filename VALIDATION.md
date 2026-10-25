# Validation of the local extraction draft

Validation date: 2026-10-25 (Europe/Istanbul).
Source snapshot: db6ea9e2a44563bf7aa649ce4f133d40abd1e8db.

- Independent library, worker and test project built on Windows with .NET 9.
- 13 automated tests passed, 0 failed. Tests include the real PDF worker, corrupt/blank input, page/byte/character limits, cancellation, timeout, missing worker/recognizer, image-header dimensions and capacity lease release.
- Release worker publish completed successfully.
- The published worker extracted text from a synthetic one-page PDF: RESULT v1 Extracted 1 0.
- Source review archive excludes build outputs and the applicant's account email. The application draft is stored separately.

Not verified: real Tesseract/image OCR (no Tesseract executable found), Linux execution, encrypted-PDF behavior in the new standalone test suite, high-load behavior or exhaustive security properties. A Windows/Linux CI workflow is prepared but has not been run on GitHub.

The original repository remains unchanged. The user authorized public release of this standalone extraction under MIT on 2026-10-25. LICENSE now contains MIT for this package only. No application submission or NuGet publication is claimed by this validation record.
