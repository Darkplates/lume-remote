using System;
using System.Drawing;
using System.Drawing.Printing;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using System.Windows.Forms;
using Windows.Data.Pdf;
using Windows.Storage;
using Windows.Storage.Streams;

namespace LumeRemote
{
    // Render-only Windows PDF API: received documents are never opened through shell verbs.
    internal sealed class RemotePrintDocument : PrintDocument
    {
        readonly PdfDocument pdf;
        readonly Stream lease;
        readonly IRandomAccessStream content;
        int page, lastPage;
        readonly CancellationToken cancellation;
        public int PageCount { get { return checked((int)pdf.PageCount); } }
        RemotePrintDocument(PdfDocument pdf, Stream lease, IRandomAccessStream content, string name, CancellationToken cancellation)
        {
            this.pdf = pdf; this.lease = lease; this.content = content; this.cancellation = cancellation; DocumentName = name;
            DefaultPageSettings.Margins = new Margins(25, 25, 25, 25);
            PrinterSettings.MinimumPage = 1; PrinterSettings.MaximumPage = PageCount;
            PrinterSettings.FromPage = 1; PrinterSettings.ToPage = PageCount;
        }
        public static async Task<RemotePrintDocument> Open(string path, CancellationToken cancellation)
        {
            cancellation.ThrowIfCancellationRequested();
            if (!String.Equals(Path.GetExtension(path), ".pdf", StringComparison.OrdinalIgnoreCase)) throw new InvalidDataException("Export the document as PDF before sending it to a local printer.");
            Stream lease = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read);
            IRandomAccessStream content = null;
            try
            {
                byte[] magic = new byte[5]; if (lease.Read(magic, 0, magic.Length) != magic.Length || System.Text.Encoding.ASCII.GetString(magic) != "%PDF-") throw new InvalidDataException("This file is not a PDF document.");
                lease.Position = 0; content = WindowsRuntimeStreamExtensions.AsRandomAccessStream(lease);
                PdfDocument pdf = await WindowsRuntimeSystemExtensions.AsTask<PdfDocument>(PdfDocument.LoadFromStreamAsync(content), cancellation).ConfigureAwait(false);
                if (pdf.PageCount < 1 || pdf.PageCount > Int32.MaxValue) throw new InvalidDataException("The PDF page count is unsupported.");
                return new RemotePrintDocument(pdf, lease, content, Path.GetFileName(path), cancellation);
            }
            catch { if (content != null) content.Dispose(); lease.Dispose(); throw; }
        }
        public async Task<Bitmap> Render(int index, int dpi = 150)
        {
            cancellation.ThrowIfCancellationRequested(); if (index < 0 || index >= PageCount || dpi < 36 || dpi > 600) throw new ArgumentOutOfRangeException();
            using (PdfPage page = pdf.GetPage((uint)index)) using (var stream = new InMemoryRandomAccessStream())
            {
                double width = page.Size.Width * dpi / 96.0, height = page.Size.Height * dpi / 96.0;
                if (Double.IsNaN(width) || Double.IsInfinity(width) || Double.IsNaN(height) || Double.IsInfinity(height) || width < 1 || height < 1) throw new InvalidDataException("Invalid PDF page size.");
                double scale = Math.Min(1, Math.Min(8192 / Math.Max(width, height), Math.Sqrt(16000000 / (width * height))));
                var options = new PdfPageRenderOptions { DestinationWidth = (uint)Math.Max(1, width * scale), DestinationHeight = (uint)Math.Max(1, height * scale) };
                await WindowsRuntimeSystemExtensions.AsTask(page.RenderToStreamAsync(stream, options), cancellation).ConfigureAwait(false);
                if (stream.Size == 0 || stream.Size > 67108864) throw new InvalidDataException("The rendered PDF page is too large.");
                using (Stream bytes = WindowsRuntimeStreamExtensions.AsStreamForRead(stream)) using (var image = new Bitmap(bytes)) return new Bitmap(image);
            }
        }
        protected override void OnBeginPrint(PrintEventArgs e)
        {
            base.OnBeginPrint(e); cancellation.ThrowIfCancellationRequested();
            page = PrinterSettings.PrintRange == PrintRange.SomePages ? Math.Max(0, PrinterSettings.FromPage - 1) : 0;
            lastPage = PrinterSettings.PrintRange == PrintRange.SomePages ? Math.Min(PageCount - 1, PrinterSettings.ToPage - 1) : PageCount - 1;
            if (page > lastPage) throw new InvalidOperationException("Choose a valid page range.");
        }
        protected override void OnPrintPage(PrintPageEventArgs e)
        {
            cancellation.ThrowIfCancellationRequested();
            using (Bitmap rendered = Render(page, 300).GetAwaiter().GetResult())
            {
                double scale = Math.Min(e.MarginBounds.Width / (double)rendered.Width, e.MarginBounds.Height / (double)rendered.Height);
                int width = (int)(rendered.Width * scale), height = (int)(rendered.Height * scale);
                e.Graphics.DrawImage(rendered, e.MarginBounds.Left + (e.MarginBounds.Width - width) / 2, e.MarginBounds.Top + (e.MarginBounds.Height - height) / 2, width, height);
            }
            e.HasMorePages = ++page <= lastPage; base.OnPrintPage(e);
        }
        protected override void Dispose(bool disposing) { if (disposing) { content.Dispose(); lease.Dispose(); } base.Dispose(disposing); }
    }
    internal static class RemotePrinting
    {
        public static async Task<bool> Show(IWin32Window owner, string path, CancellationToken cancellation)
        {
            cancellation.ThrowIfCancellationRequested();
            using (var document = await RemotePrintDocument.Open(path, cancellation))
            using (var preview = new Form { Text = "Lume - Print " + Path.GetFileName(path), Icon = Brand.Icon, StartPosition = FormStartPosition.CenterParent, BackColor = Theme.Background, ForeColor = Theme.Text })
            using (var image = new PictureBox { Dock = DockStyle.Fill, SizeMode = PictureBoxSizeMode.Zoom, BackColor = Color.DimGray })
            {
                Theme.BeginLayout(preview); preview.Size = new Size(850, 750); preview.MinimumSize = new Size(600, 420);
                int page = 0; bool submitted = false, rendering = false;
                var bar = new FlowLayoutPanel { Dock = DockStyle.Bottom, Height = 58, BackColor = Theme.Card, Padding = new Padding(12, 5, 12, 5) };
                var previous = Theme.Button("Previous", false); var next = Theme.Button("Next", false); var print = Theme.Button("Choose printer", true); print.Width = 180;
                var label = Theme.Label("", 10, Theme.Text); label.Margin = new Padding(8, 14, 8, 0); bar.Controls.Add(previous); bar.Controls.Add(next); bar.Controls.Add(label); bar.Controls.Add(print); preview.Controls.Add(image); preview.Controls.Add(bar); Theme.EndLayout(preview);
                Func<Task> render = async delegate
                {
                    if (rendering) return; rendering = true; previous.Enabled = next.Enabled = print.Enabled = false;
                    try { Bitmap bitmap = await document.Render(page); if (preview.IsDisposed) { bitmap.Dispose(); return; } Image old = image.Image; image.Image = bitmap; if (old != null) old.Dispose(); label.Text = (page + 1) + " / " + document.PageCount; }
                    catch (Exception error) { if (!preview.IsDisposed) MessageBox.Show(preview, error.Message, "Lume - PDF preview", MessageBoxButtons.OK, MessageBoxIcon.Error); }
                    finally { rendering = false; if (!preview.IsDisposed) { previous.Enabled = page > 0; next.Enabled = page + 1 < document.PageCount; print.Enabled = image.Image != null; } }
                };
                previous.Click += async delegate { page--; await render(); }; next.Click += async delegate { page++; await render(); };
                print.Click += delegate
                {
                    try { using (var dialog = new PrintDialog { Document = document, AllowSomePages = true, UseEXDialog = true }) if (dialog.ShowDialog(preview) == DialogResult.OK) { cancellation.ThrowIfCancellationRequested(); document.Print(); submitted = true; preview.Close(); } }
                    catch (Exception error) { MessageBox.Show(preview, error.Message, "Lume - Printing", MessageBoxButtons.OK, MessageBoxIcon.Error); }
                };
                preview.Shown += async delegate { await render(); };
                var handle = preview.Handle; cancellation.ThrowIfCancellationRequested();
                using (cancellation.Register(delegate { try { preview.BeginInvoke((Action)preview.Close); } catch (InvalidOperationException) { } })) preview.ShowDialog(owner);
                if (image.Image != null) { image.Image.Dispose(); image.Image = null; } return submitted;
            }
        }
    }
}
