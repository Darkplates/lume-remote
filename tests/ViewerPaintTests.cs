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
        bool windows = Environment.OSVersion.Platform == PlatformID.Win32NT;
        using (Bitmap source = PaintSource(320, 200))
        foreach (RemoteCanvas.FrameScaling mode in Enum.GetValues(typeof(RemoteCanvas.FrameScaling)))
        {
            if (!windows && (mode == RemoteCanvas.FrameScaling.GdiHalftone || mode == RemoteCanvas.FrameScaling.GdiColorOnColor)) continue;
            // Same size: an exact copy.
            using (Bitmap target = new Bitmap(320, 200, PixelFormat.Format32bppRgb))
            {
                using (Graphics g = Graphics.FromImage(target)) RemoteCanvas.DrawFrame(g, source, new Rectangle(0, 0, 320, 200), mode);
                for (int y = 0; y < 200; y += 7) for (int x = 0; x < 320; x += 5)
                    Check(target.GetPixel(x, y).ToArgb() == source.GetPixel(x, y).ToArgb(), mode + ": a same-size frame was not copied exactly.");
            }
            // Scaled into an offset rectangle: the picture lands there, and later drawing still works.
            using (Bitmap target = new Bitmap(420, 270, PixelFormat.Format32bppRgb))
            {
                using (Graphics g = Graphics.FromImage(target)) { g.Clear(Color.Black); RemoteCanvas.DrawFrame(g, source, new Rectangle(10, 10, 400, 250), mode); g.FillRectangle(Brushes.Red, 395, 245, 4, 4); }
                Color inside = target.GetPixel(200, 130), outside = target.GetPixel(3, 3);
                Check(inside.B > 60 && inside.B < 140 || inside.R > 150, mode + ": the scaled frame did not land in its rectangle.");
                Check(outside.ToArgb() == Color.Black.ToArgb(), mode + ": the scaled frame drew outside its rectangle.");
                Check(target.GetPixel(396, 246).ToArgb() == Color.Red.ToArgb(), mode + ": drawing after the frame did not compose normally.");
            }
        }
    }
    // Prints timings only; hosted runners have no GPU and vary, so this never fails on speed.
    static void ViewerPaintTiming()
    {
        using (Bitmap source = PaintSource(2560, 1440))
        {
            Size[] targets = { new Size(2544, 1371), new Size(1904, 1031), new Size(1280, 720) };
            foreach (Size size in targets)
            using (Bitmap window = new Bitmap(size.Width, size.Height, PixelFormat.Format32bppPArgb))
            using (Graphics g = Graphics.FromImage(window))
            {
                Rectangle r = new Rectangle(Point.Empty, size);
                string line = "PAINT 2560x1440 -> " + size.Width + "x" + size.Height + ":";
                foreach (RemoteCanvas.FrameScaling mode in Enum.GetValues(typeof(RemoteCanvas.FrameScaling)))
                {
                    RemoteCanvas.FrameScaling current = mode;
                    line += " " + mode + " " + TimePaint(delegate { RemoteCanvas.DrawFrame(g, source, r, current); }).ToString("0.0") + " ms;";
                }
                Console.WriteLine(line);
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
