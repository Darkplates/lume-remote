using System;
using System.Collections.Generic;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using System.Windows.Forms;

namespace LumeRemote
{
    public static class AnnotationWire
    {
        public static Point[] Read(Packet packet)
        {
            int count = packet.Reader.ReadInt32(); if (count < 0 || count > 128 || count == 1) throw new InvalidDataException("Invalid annotation size.");
            Point[] points = new Point[count];
            for (int i = 0; i < count; i++) { int x = packet.Reader.ReadInt32(), y = packet.Reader.ReadInt32(); if (x < 0 || x > 65535 || y < 0 || y > 65535) throw new InvalidDataException("Invalid annotation position."); points[i] = new Point(x, y); }
            packet.End(); return points;
        }
        public static void Draw(Graphics graphics, Rectangle bounds, Point[] points)
        {
            if (points.Length < 2) return; PointF[] scaled = new PointF[points.Length];
            for (int i = 0; i < points.Length; i++) scaled[i] = new PointF(bounds.X + points[i].X * (bounds.Width - 1) / 65535f, bounds.Y + points[i].Y * (bounds.Height - 1) / 65535f);
            using (Pen pen = new Pen(Color.FromArgb(80, 245, 181), 4)) { pen.StartCap = pen.EndCap = LineCap.Round; graphics.SmoothingMode = SmoothingMode.AntiAlias; graphics.DrawLines(pen, scaled); }
        }
    }
    internal sealed class AnnotationOverlay : Form
    {
        readonly Queue<Point[]> strokes = new Queue<Point[]>();
        readonly System.Windows.Forms.Timer expiry = new System.Windows.Forms.Timer { Interval = 1000 };
        DateTime last;
        public AnnotationOverlay()
        {
            FormBorderStyle = FormBorderStyle.None; ShowInTaskbar = false; TopMost = true; BackColor = TransparencyKey = Color.Magenta; DoubleBuffered = true;
            expiry.Tick += delegate { if ((DateTime.UtcNow - last).TotalSeconds > 30) { strokes.Clear(); Hide(); } }; expiry.Start();
        }
        protected override bool ShowWithoutActivation { get { return true; } }
        protected override CreateParams CreateParams { get { CreateParams value = base.CreateParams; value.ExStyle |= 0x08000000 | 0x20 | 0x80; return value; } }
        public void DrawStroke(Rectangle desktop, Point[] points)
        {
            if (Bounds != desktop) { strokes.Clear(); Bounds = desktop; }
            if (points.Length == 0) { strokes.Clear(); Hide(); return; }
            strokes.Enqueue(points); while (strokes.Count > 64) strokes.Dequeue(); last = DateTime.UtcNow; Show(); Invalidate();
        }
        protected override void OnPaint(PaintEventArgs e) { base.OnPaint(e); foreach (Point[] stroke in strokes) AnnotationWire.Draw(e.Graphics, ClientRectangle, stroke); }
        protected override void Dispose(bool disposing) { if (disposing) expiry.Dispose(); base.Dispose(disposing); }
    }
    public sealed class HostAnnotations : IDisposable
    {
        readonly object gate = new object();
        readonly TaskCompletionSource<AnnotationOverlay> ready = new TaskCompletionSource<AnnotationOverlay>(TaskCreationOptions.RunContinuationsAsynchronously);
        Thread thread; volatile bool disposed;
        public async Task Draw(Rectangle desktop, Point[] points)
        {
            lock (gate)
            {
                if (disposed) throw new OperationCanceledException();
                if (thread == null)
                {
                    if (points.Length == 0) return;
                    thread = new Thread(delegate()
                    {
                        try { using (var overlay = new AnnotationOverlay()) { var handle = overlay.Handle; ready.TrySetResult(overlay); Application.Run(); } }
                        catch (Exception error) { ready.TrySetException(error); }
                    }) { IsBackground = true, Name = "Lume annotations" }; thread.SetApartmentState(ApartmentState.STA); thread.Start();
                }
            }
            AnnotationOverlay target = await ready.Task.ConfigureAwait(false);
            var completion = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
            target.BeginInvoke((Action)delegate { try { if (disposed) throw new OperationCanceledException(); target.DrawStroke(desktop, points); completion.TrySetResult(true); } catch (Exception error) { completion.TrySetException(error); } });
            await completion.Task.ConfigureAwait(false);
        }
        public void Dispose()
        {
            lock (gate) { if (disposed) return; disposed = true; if (thread == null) return; }
            ready.Task.ContinueWith(delegate(Task<AnnotationOverlay> done)
            {
                if (done.IsFaulted) { var observed = done.Exception; return; }
                try { done.Result.BeginInvoke((Action)delegate { done.Result.Dispose(); Application.ExitThread(); }); } catch (InvalidOperationException) { }
            });
        }
    }
}
