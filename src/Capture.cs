using System;
using System.Collections.Generic;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Drawing.Imaging;
using System.IO;
using System.Runtime.InteropServices;
using System.Windows.Forms;

namespace LumeRemote
{
    public sealed class Profile
    {
        public string Name;
        public int MaxWidth, Fps, Quality;
        public bool Lossless;
        public override string ToString() { return Name; }
        public static Profile[] All { get { return new Profile[] {
            new Profile { Name = "Eco - 1280 px / up to 15 fps", MaxWidth = 1280, Fps = 15, Quality = 65 },
            new Profile { Name = "Balanced - 1600 px / lossless / up to 25 fps", MaxWidth = 1600, Fps = 25, Quality = 80, Lossless = true },
            new Profile { Name = "Detail - 1920 px / up to 20 fps", MaxWidth = 1920, Fps = 20, Quality = 92 },
            new Profile { Name = "Source - original resolution / lossless / source refresh", MaxWidth = 0, Fps = 0, Quality = 100, Lossless = true },
            new Profile { Name = "Low bandwidth - 640 px / up to 10 fps", MaxWidth = 640, Fps = 10, Quality = 65 }
        }; } }
    }
    public interface IScreenSource : IDisposable { Rectangle Bounds { get; } Bitmap Capture(); }
    public interface IFrameChangeSource { bool FrameChanged { get; } }

    public sealed class DesktopSource : IScreenSource, IAdaptiveScreenSource, IFrameChangeSource
    {
        readonly Rectangle bounds;
        Bitmap bitmap;
        Graphics graphics;
        IntPtr duplication;
        public string Backend { get { return duplication != IntPtr.Zero ? "DXGI" : "GDI fallback"; } }
        public bool FrameChanged { get; private set; }
        public Rectangle Bounds { get { return bounds; } }
        public int RefreshRate { get { return DisplayInfo.RefreshRate(bounds); } }
        public DesktopSource(Rectangle bounds, Profile profile)
        {
            this.bounds = bounds;
            Configure(StreamQuality.FromProfile(profile, bounds));
            try { if (LumeCreate(bounds.X, bounds.Y, out duplication) < 0) duplication = IntPtr.Zero; }
            catch (DllNotFoundException) { duplication = IntPtr.Zero; }
            catch (EntryPointNotFoundException) { duplication = IntPtr.Zero; }
            catch (BadImageFormatException) { duplication = IntPtr.Zero; }
        }
        public void Configure(StreamQuality quality)
        {
            Size size = quality.Dimensions(bounds.Size); if (bitmap != null && bitmap.Size == size) return;
            Bitmap next = new Bitmap(size.Width, size.Height, PixelFormat.Format32bppRgb); Graphics nextGraphics = Graphics.FromImage(next);
            if (graphics != null) graphics.Dispose(); if (bitmap != null) bitmap.Dispose(); bitmap = next; graphics = nextGraphics;
        }
        public Bitmap Capture()
        { using (DesktopAttachment desktop = new DesktopAttachment()) return CaptureDesktop(); }
        Bitmap CaptureDesktop()
        {
            FrameChanged = true;
            if (duplication != IntPtr.Zero)
            {
                BitmapData pixels = bitmap.LockBits(new Rectangle(0, 0, bitmap.Width, bitmap.Height), ImageLockMode.WriteOnly, PixelFormat.Format32bppRgb);
                try
                {
                    int hr = LumeCapture(duplication, pixels.Scan0, bitmap.Width, bitmap.Height, pixels.Stride);
                    FrameChanged = hr != 1;
                    if (hr < 0) { LumeDestroy(duplication); duplication = IntPtr.Zero; }
                }
                finally { bitmap.UnlockBits(pixels); }
            }
            if (duplication == IntPtr.Zero)
            {
                IntPtr screen = Native.GetDC(IntPtr.Zero), target = graphics.GetHdc();
                try
                {
                    Native.SetStretchBltMode(target, 3);
                    if (!Native.StretchBlt(target, 0, 0, bitmap.Width, bitmap.Height, screen, bounds.X, bounds.Y, bounds.Width, bounds.Height, 0x40CC0020))
                        throw new InvalidOperationException("Desktop capture is unavailable. Unlock the normal Windows desktop and retry.");
                }
                finally { graphics.ReleaseHdc(target); Native.ReleaseDC(IntPtr.Zero, screen); }
            }
            return bitmap;
        }
        public static int[] CursorState(Rectangle bounds)
        {
            Native.CURSORINFO cursor = new Native.CURSORINFO(); cursor.cbSize = Marshal.SizeOf(cursor);
            if (!Native.GetCursorInfo(ref cursor) || cursor.flags != 1 || !bounds.Contains(cursor.ptScreenPos)) return new int[] { 0, 0, 0 };
            int kind = cursor.hCursor == Cursors.IBeam.Handle ? 2 : cursor.hCursor == Cursors.Hand.Handle ? 3 : cursor.hCursor == Cursors.WaitCursor.Handle ? 4 : 1;
            return new int[] { (int)((long)(cursor.ptScreenPos.X - bounds.X) * 65535 / Math.Max(1, bounds.Width - 1)), (int)((long)(cursor.ptScreenPos.Y - bounds.Y) * 65535 / Math.Max(1, bounds.Height - 1)), kind };
        }
        public void Dispose() { if (duplication != IntPtr.Zero) { LumeDestroy(duplication); duplication = IntPtr.Zero; } if (graphics != null) graphics.Dispose(); if (bitmap != null) bitmap.Dispose(); }
        [DllImport("LumeCapture.dll", EntryPoint = "lume_create", CallingConvention = CallingConvention.Cdecl)] static extern int LumeCreate(int x, int y, out IntPtr capture);
        [DllImport("LumeCapture.dll", EntryPoint = "lume_capture", CallingConvention = CallingConvention.Cdecl)] static extern int LumeCapture(IntPtr capture, IntPtr destination, int width, int height, int stride);
        [DllImport("LumeCapture.dll", EntryPoint = "lume_destroy", CallingConvention = CallingConvention.Cdecl)] static extern void LumeDestroy(IntPtr capture);
    }

    public sealed class FrameEncoder
    {
        const int Tile = 96;
        ulong[] previous;
        int width, height;
        readonly ImageCodecInfo jpeg;
        public FrameEncoder()
        {
            foreach (ImageCodecInfo codec in ImageCodecInfo.GetImageEncoders()) if (codec.FormatID == ImageFormat.Jpeg.Guid) jpeg = codec;
            if (jpeg == null) throw new InvalidOperationException("The Windows JPEG codec is unavailable.");
        }
        public unsafe List<Rectangle> Changes(Bitmap bitmap, bool force)
        {
            int cols = (bitmap.Width + Tile - 1) / Tile, rows = (bitmap.Height + Tile - 1) / Tile;
            if (width != bitmap.Width || height != bitmap.Height || previous == null)
            { width = bitmap.Width; height = bitmap.Height; previous = new ulong[cols * rows]; force = true; }
            List<Rectangle> regions = new List<Rectangle>(); int changed = 0;
            BitmapData pixels = bitmap.LockBits(new Rectangle(0, 0, width, height), ImageLockMode.ReadOnly, PixelFormat.Format32bppRgb);
            try
            {
                for (int row = 0; row < rows; row++)
                {
                    int run = -1;
                    for (int col = 0; col <= cols; col++)
                    {
                        bool dirty = false;
                        if (col < cols)
                        {
                            ulong hash = 14695981039346656037UL;
                            for (int y = row * Tile; y < Math.Min(height, (row + 1) * Tile); y++)
                            {
                                uint* scan = (uint*)((byte*)pixels.Scan0 + y * pixels.Stride);
                                for (int x = col * Tile; x < Math.Min(width, (col + 1) * Tile); x++) hash = unchecked((hash ^ (scan[x] & 0xFFFFFFU)) * 1099511628211UL);
                            }
                            int index = row * cols + col; dirty = force || hash != previous[index]; previous[index] = hash;
                        }
                        if (dirty) { changed++; if (run < 0) run = col; }
                        else if (run >= 0)
                        {
                            regions.Add(new Rectangle(run * Tile, row * Tile, Math.Min(width, col * Tile) - run * Tile, Math.Min(Tile, height - row * Tile))); run = -1;
                        }
                    }
                }
            }
            finally { bitmap.UnlockBits(pixels); }
            if (force || regions.Count > 24 || changed * 100 > cols * rows * 60) return new List<Rectangle> { new Rectangle(0, 0, width, height) };
            return regions;
        }
        public byte[] Encode(Bitmap bitmap, int sequence, int quality, bool force, bool lossless = false, bool portable = false)
        {
            List<Rectangle> regions = Changes(bitmap, force);
            if (regions.Count == 0) return null;
            return Wire.Message(Kind.Frame, delegate(BinaryWriter writer)
            {
                writer.Write(sequence); writer.Write(bitmap.Width); writer.Write(bitmap.Height); writer.Write(regions.Count);
                using (EncoderParameters parameters = new EncoderParameters(1))
                {
                    parameters.Param[0] = new EncoderParameter(System.Drawing.Imaging.Encoder.Quality, (long)quality);
                    foreach (Rectangle r in regions)
                    {
                        writer.Write(r.X); writer.Write(r.Y); writer.Write(r.Width); writer.Write(r.Height); writer.Write((byte)(lossless ? portable ? 3 : 1 : 0));
                        if (lossless && !portable)
                        {
                            byte[] block = FastCodec.Encode(bitmap, r); writer.Write(block.Length); writer.Write(block); continue;
                        }
                        using (Bitmap patch = bitmap.Clone(r, PixelFormat.Format24bppRgb))
                        using (MemoryStream bytes = new MemoryStream())
                        {
                            if (lossless) patch.Save(bytes, ImageFormat.Png); else patch.Save(bytes, jpeg, parameters);
                            writer.Write((int)bytes.Length); writer.Write(bytes.GetBuffer(), 0, (int)bytes.Length);
                        }
                    }
                }
            });
        }
    }

    public sealed class FrameDecoder : IDisposable
    {
        VideoDecoder video;
        public bool FrameReady { get; private set; }
        public Bitmap Image { get; private set; }
        public int Sequence { get; private set; }
        public void Apply(Packet packet)
        {
            FrameReady = true;
            int sequence = packet.Reader.ReadInt32(), w = packet.Reader.ReadInt32(), h = packet.Reader.ReadInt32(), count = packet.Reader.ReadInt32();
            if (sequence <= 0 || w < 1 || h < 1 || w > StreamQuality.MaxDimension || h > StreamQuality.MaxDimension || (long)w * h > StreamQuality.MaxPixels || count < 1 || count > 24) throw new InvalidDataException("Invalid frame dimensions.");
            bool reset = Image == null || Image.Width != w || Image.Height != h;
            if (reset && count != 1) throw new InvalidDataException("A complete first frame is required.");
            if (reset) { if (video != null) { video.Dispose(); video = null; } if (Image != null) Image.Dispose(); Image = new Bitmap(w, h, PixelFormat.Format32bppRgb); }
            long decodedPixels = 0;
            // Each decoder owns the image for the duration of this update.
            {
                for (int i = 0; i < count; i++)
                {
                    int x = packet.Reader.ReadInt32(), y = packet.Reader.ReadInt32(), pw = packet.Reader.ReadInt32(), ph = packet.Reader.ReadInt32();
                    byte codec = packet.Reader.ReadByte(); int size = packet.Reader.ReadInt32();
                    if (x < 0 || y < 0 || pw < 1 || ph < 1 || x > w - pw || y > h - ph || size < 4 || size > Wire.MaxPacket - 64 || size > (long)pw * ph * 4 + 65536 ||
                        (reset && (x != 0 || y != 0 || pw != w || ph != h))) throw new InvalidDataException("Invalid frame region.");
                    decodedPixels += (long)pw * ph;
                    if (decodedPixels > (long)w * h) throw new InvalidDataException("Frame regions exceed the desktop area.");
                    byte[] bytes = packet.Reader.ReadBytes(size);
                    if (bytes.Length != size) throw new EndOfStreamException();
                    if (codec == 2)
                    {
                        if (count != 1 || x != 0 || y != 0 || pw != w || ph != h || bytes.Length < 6 || bytes[0] != 1 || bytes[1] > 1)
                            throw new InvalidDataException("Invalid video frame envelope.");
                        byte[] encoded = new byte[bytes.Length - 2]; Buffer.BlockCopy(bytes, 2, encoded, 0, encoded.Length);
                        H264Bounds.Validate(encoded, w, h, bytes[1] == 1 || video == null);
                        if (bytes[1] == 1 && video != null) { video.Dispose(); video = null; }
                        if (video == null) video = new VideoDecoder(w, h);
                        FrameReady = video.Decode(encoded, Image); continue;
                    }
                    if (video != null) { video.Dispose(); video = null; }
                    if (codec == 1) { FastCodec.Decode(bytes, Image, new Rectangle(x, y, pw, ph)); continue; }
                    if (codec != 0 && codec != 3) throw new InvalidDataException("Unknown image codec.");
                    if (codec == 3) ValidatePng(bytes, pw, ph); else ValidateJpeg(bytes, pw, ph);
                    using (MemoryStream buffer = new MemoryStream(bytes))
                    using (System.Drawing.Image patch = System.Drawing.Image.FromStream(buffer, false, true))
                    using (Graphics canvas = Graphics.FromImage(Image))
                    {
                        if (patch.Width != pw || patch.Height != ph) throw new InvalidDataException("Unexpected image dimensions.");
                        // Map pixels 1:1. DrawImageUnscaled honours the patch's embedded DPI, which
                        // differs between hosts and viewers with different display scaling.
                        canvas.InterpolationMode = System.Drawing.Drawing2D.InterpolationMode.NearestNeighbor; canvas.PixelOffsetMode = System.Drawing.Drawing2D.PixelOffsetMode.Half;
                        canvas.DrawImage(patch, new Rectangle(x, y, pw, ph), 0, 0, pw, ph, GraphicsUnit.Pixel);
                    }
                }
            }
            packet.End(); Sequence = sequence;
        }
        public static void ValidatePng(byte[] bytes, int width, int height)
        {
            byte[] signature = { 137, 80, 78, 71, 13, 10, 26, 10 };
            if (bytes.Length < 33) throw new InvalidDataException("PNG image required.");
            for (int i = 0; i < signature.Length; i++) if (bytes[i] != signature[i]) throw new InvalidDataException("PNG image required.");
            if (bytes[8] != 0 || bytes[9] != 0 || bytes[10] != 0 || bytes[11] != 13 || bytes[12] != 73 || bytes[13] != 72 || bytes[14] != 68 || bytes[15] != 82 ||
                PngInt(bytes, 16) != width || PngInt(bytes, 20) != height) throw new InvalidDataException("PNG dimensions do not match the region.");
        }
        static uint PngInt(byte[] b, int i) { return (uint)b[i] << 24 | (uint)b[i + 1] << 16 | (uint)b[i + 2] << 8 | b[i + 3]; }
        public static void ValidateJpeg(byte[] bytes, int width, int height)
        {
            if (bytes.Length < 4 || bytes[0] != 255 || bytes[1] != 216) throw new InvalidDataException("JPEG image required.");
            int i = 2;
            while (i + 3 < bytes.Length)
            {
                if (bytes[i++] != 255) throw new InvalidDataException("Invalid JPEG marker.");
                while (i < bytes.Length && bytes[i] == 255) i++;
                if (i >= bytes.Length) break;
                int marker = bytes[i++];
                if (marker == 217 || marker == 218) break;
                if (marker == 1 || (marker >= 208 && marker <= 215)) continue;
                if (i + 1 >= bytes.Length) break;
                int size = bytes[i] * 256 + bytes[i + 1];
                if (size < 2 || size > bytes.Length - i) break;
                if (marker == 192 || marker == 193 || marker == 194)
                {
                    if (size < 8 || bytes[i + 3] * 256 + bytes[i + 4] != height || bytes[i + 5] * 256 + bytes[i + 6] != width) throw new InvalidDataException("JPEG dimensions do not match the region.");
                    return;
                }
                i += size;
            }
            throw new InvalidDataException("JPEG dimensions are missing.");
        }
        public void Dispose() { if (video != null) { video.Dispose(); video = null; } if (Image != null) { Image.Dispose(); Image = null; } }
    }

    internal static class Native
    {
        [StructLayout(LayoutKind.Sequential)] internal struct CURSORINFO { public int cbSize, flags; public IntPtr hCursor; public Point ptScreenPos; }
        [StructLayout(LayoutKind.Sequential)] internal struct ICONINFO { public bool fIcon; public int xHotspot, yHotspot; public IntPtr hbmMask, hbmColor; }
        [DllImport("user32.dll")] internal static extern IntPtr GetDC(IntPtr window);
        [DllImport("user32.dll")] internal static extern int ReleaseDC(IntPtr window, IntPtr dc);
        [DllImport("gdi32.dll")] internal static extern int SetStretchBltMode(IntPtr dc, int mode);
        [DllImport("gdi32.dll", SetLastError = true)] internal static extern bool StretchBlt(IntPtr target, int x, int y, int w, int h, IntPtr source, int sx, int sy, int sw, int sh, int rop);
        [DllImport("user32.dll")] internal static extern bool GetCursorInfo(ref CURSORINFO info);
        [DllImport("user32.dll")] internal static extern bool GetIconInfo(IntPtr icon, out ICONINFO info);
        [DllImport("user32.dll")] internal static extern bool DrawIconEx(IntPtr dc, int x, int y, IntPtr icon, int w, int h, int step, IntPtr brush, int flags);
        [DllImport("gdi32.dll")] internal static extern bool DeleteObject(IntPtr obj);
        [DllImport("user32.dll")] internal static extern bool SetProcessDPIAware();
        [DllImport("kernel32.dll", SetLastError = true)] internal static extern bool SetDefaultDllDirectories(uint flags);
    }
}
