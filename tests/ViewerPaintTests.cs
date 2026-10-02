using System;
using System.Diagnostics;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Drawing.Imaging;
using LumeRemote;

// The viewer scales every frame on the UI thread; a large window must stay cheap.
static partial class Tests
{
    static Bitmap PaintSource(int width, int height)
    {
        Bitmap image = new Bitmap(width, height, PixelFormat.Format32bppRgb);
        using (Graphics g = Graphics.FromImage(image))
        {
            g.Clear(Color.FromArgb(30, 60, 90));
            using (Font font = new Font("Segoe UI", 11))
                for (int y = 0; y < height; y += 24) g.DrawString("The quick brown fox jumps over the lazy dog 0123456789", font, Brushes.White, 8, y);
            using (Pen edge = new Pen(Color.FromArgb(250, 200, 40))) g.DrawRectangle(edge, 0, 0, width - 1, height - 1);
        }
        return image;
    }
    static void ViewerFramePaint()
    {
        using (Bitmap source = PaintSource(320, 200))
        {
            // Same size: an exact copy.
            using (Bitmap target = new Bitmap(320, 200, PixelFormat.Format32bppRgb))
            {
                using (Graphics g = Graphics.FromImage(target)) RemoteCanvas.DrawFrame(g, source, new Rectangle(0, 0, 320, 200));
                for (int y = 0; y < 200; y += 7) for (int x = 0; x < 320; x += 5)
                    Check(target.GetPixel(x, y).ToArgb() == source.GetPixel(x, y).ToArgb(), "A same-size frame was not copied exactly.");
            }
            // Scaled: the outer edge keeps its colour instead of fading to the background.
            using (Bitmap target = new Bitmap(400, 250, PixelFormat.Format32bppRgb))
            {
                using (Graphics g = Graphics.FromImage(target)) { g.Clear(Color.Black); RemoteCanvas.DrawFrame(g, source, new Rectangle(0, 0, 400, 250)); g.FillRectangle(Brushes.Red, 390, 240, 4, 4); }
                Color corner = target.GetPixel(0, 0), middle = target.GetPixel(200, 0);
                Check(corner.R > 150 && corner.G > 110 && middle.R > 150, "Scaling faded the frame edge.");
                // Later drawing (pointer, annotations) blends normally again.
                Check(target.GetPixel(391, 241).ToArgb() == Color.Red.ToArgb(), "Drawing after the frame did not compose normally.");
            }
        }
    }
    // Prints timings only; hosted runners have no GPU and vary, so this never fails on speed.
    static void ViewerPaintTiming()
    {
        using (Bitmap source = PaintSource(2560, 1440))
        {
            Size[] targets = { new Size(2544, 1371), new Size(1904, 1031), new Size(1280, 720), new Size(960, 540) };
            foreach (Size size in targets)
            using (Bitmap window = new Bitmap(size.Width, size.Height, PixelFormat.Format32bppPArgb))
            using (Graphics g = Graphics.FromImage(window))
            {
                Rectangle r = new Rectangle(Point.Empty, size);
                double before = TimePaint(delegate { g.CompositingMode = CompositingMode.SourceOver; g.InterpolationMode = InterpolationMode.HighQualityBilinear; g.DrawImage(source, r); });
                double after = TimePaint(delegate { RemoteCanvas.DrawFrame(g, source, r); });
                Console.WriteLine("PAINT 2560x1440 -> " + size.Width + "x" + size.Height + ": previous " + before.ToString("0.0") + " ms, current " + after.ToString("0.0") + " ms per frame");
            }
        }
    }
    static double TimePaint(Action paint)
    {
        paint(); Stopwatch clock = Stopwatch.StartNew(); int frames = 0;
        while (clock.ElapsedMilliseconds < 1500 || frames < 3) { paint(); frames++; }
        return clock.Elapsed.TotalMilliseconds / frames;
    }
}
