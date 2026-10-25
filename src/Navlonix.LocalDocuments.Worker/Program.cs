using Navlonix.LocalDocuments;

if (args.Length > 0 && args[0] == PdfTextWorker.Flag)
    return PdfTextWorker.Run(args);

Console.Error.WriteLine("This worker is launched by PdfTextProcessRunner. Usage: --pdf-worker file.pdf maxPages maxChars minMeaningfulChars");
return 2;

