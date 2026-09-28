using System;
using System.Drawing;
using System.Drawing.Printing;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading;
using LumeRemote;

static partial class Tests
{
    static void PrintingChecks()
    {
        Run("Windows PDF rendering preserves page geometry and colours", PdfRendering);
        Run("Printing rejects non-PDF input and honours cancellation", PdfSafety);
        Run("Remote PDF download is verified before local rendering", RemotePdfTransfer);
    }
    static void WritePdfFixture(string path)
    {
        string content = "1 0 0 rg 0 150 300 150 re f 0 0 1 rg 0 0 300 150 re f\n";
        string[] objects = { "<< /Type /Catalog /Pages 2 0 R >>", "<< /Type /Pages /Kids [3 0 R] /Count 1 >>", "<< /Type /Page /Parent 2 0 R /MediaBox [0 0 300 300] /Resources << >> /Contents 4 0 R >>", "<< /Length " + content.Length + " >>\nstream\n" + content + "endstream" };
        using (var stream = new MemoryStream()) using (var writer = new StreamWriter(stream, new UTF8Encoding(false)))
        {
            writer.NewLine = "\n"; writer.Write("%PDF-1.4\n"); long[] offsets = new long[objects.Length + 1];
            for (int i = 0; i < objects.Length; i++) { writer.Flush(); offsets[i + 1] = stream.Position; writer.Write((i + 1) + " 0 obj\n" + objects[i] + "\nendobj\n"); }
            writer.Flush(); long start = stream.Position; writer.Write("xref\n0 5\n0000000000 65535 f \n"); for (int i = 1; i < offsets.Length; i++) writer.Write(offsets[i].ToString("D10") + " 00000 n \n"); writer.Write("trailer\n<< /Size 5 /Root 1 0 R >>\nstartxref\n" + start + "\n%%EOF\n"); writer.Flush(); File.WriteAllBytes(path, stream.ToArray());
        }
    }
    static void PdfRendering()
    {
        string root = Path.Combine(Path.GetTempPath(), "lume-print-render-" + Guid.NewGuid().ToString("N")); Directory.CreateDirectory(root);
        try
        {
            string file = Path.Combine(root, "fixture.pdf"); WritePdfFixture(file);
            using (var document = Await(RemotePrintDocument.Open(file, CancellationToken.None))) using (var image = Await(document.Render(0)))
            {
                Check(document.PageCount == 1 && image.Width == image.Height && image.Width >= 300, "PDF geometry changed.");
                Color top = image.GetPixel(30, 30), bottom = image.GetPixel(30, image.Height - 30);
                Check(top.R > 220 && top.B < 30 && bottom.B > 220 && bottom.R < 30, "PDF renderer changed orientation or colour.");
                string evidence = Path.GetFullPath(Path.Combine("verification", "print-preview-fixture.png")); Directory.CreateDirectory(Path.GetDirectoryName(evidence)); image.Save(evidence, System.Drawing.Imaging.ImageFormat.Png);
                Console.WriteLine("PDF_RENDER: " + image.Width + " x " + image.Height + "; correct red/blue orientation.");
            }
        }
        finally { Directory.Delete(root, true); }
    }
    static void PdfSafety()
    {
        string root = Path.Combine(Path.GetTempPath(), "lume-print-safety-" + Guid.NewGuid().ToString("N")); Directory.CreateDirectory(root);
        try
        {
            string invalid = Path.Combine(root, "bad.pdf"); File.WriteAllText(invalid, "not a PDF"); Reject(delegate { Await(RemotePrintDocument.Open(invalid, CancellationToken.None)); });
            string executable = Path.Combine(root, "document.exe"); WritePdfFixture(executable); Reject(delegate { Await(RemotePrintDocument.Open(executable, CancellationToken.None)); });
            string pdf = Path.Combine(root, "valid.pdf"); WritePdfFixture(pdf);
            using (var cancel = new CancellationTokenSource()) { cancel.Cancel(); Reject(delegate { Await(RemotePrintDocument.Open(pdf, cancel.Token)); }); }
            using (var document = Await(RemotePrintDocument.Open(pdf, CancellationToken.None))) { Reject(delegate { Await(document.Render(1)); }); Reject(delegate { Await(document.Render(0, 10000)); }); }
            Check(Directory.GetFiles(root).Length == 3, "Parsing changed input files.");
        }
        finally { Directory.Delete(root, true); }
    }
    static void RemotePdfTransfer()
    {
        using (var fixture = new FilesFixture())
        {
            string source = Path.Combine(fixture.Root, "remote", "fixture.pdf"); WritePdfFixture(source);
            string received = Await(fixture.Viewer.Files.Download(source, Path.Combine(fixture.Root, "local"), CancellationToken.None)); Check(SameFile(source, received), "Remote print download changed bytes.");
            using (var document = Await(RemotePrintDocument.Open(received, CancellationToken.None))) using (var image = Await(document.Render(0))) Check(image.Width > 0, "Downloaded PDF did not render.");
        }
    }
    static void PrintToPdfFixture()
    {
        string printer = PrinterSettings.InstalledPrinters.Cast<string>().FirstOrDefault(p => p.Equals("Microsoft Print to PDF", StringComparison.OrdinalIgnoreCase));
        Check(printer != null, "The owned virtual-printer fixture requires Microsoft Print to PDF.");
        string root = Path.GetFullPath(Path.Combine("verification", "print-spool-fixture-" + Guid.NewGuid().ToString("N"))); Directory.CreateDirectory(root);
        string source = Path.Combine(root, "source.pdf"), output = Path.Combine(root, "printed.pdf"); WritePdfFixture(source);
        using (var document = Await(RemotePrintDocument.Open(source, CancellationToken.None)))
        {
            document.PrinterSettings.PrinterName = printer; document.PrinterSettings.PrintToFile = true; document.PrinterSettings.PrintFileName = output;
            document.PrintController = new StandardPrintController(); document.Print();
        }
        Spin(delegate { return File.Exists(output) && new FileInfo(output).Length > 100; }, 15000, "Virtual print job did not produce a PDF.");
        using (var printed = Await(RemotePrintDocument.Open(output, CancellationToken.None))) using (var image = Await(printed.Render(0))) Check(printed.PageCount == 1 && image.Width > 0, "Printed PDF cannot be rendered.");
        Console.WriteLine("PRINT_SPOOL_FIXTURE: " + output + "; virtual PDF printer only, no physical paper.");
    }
}
