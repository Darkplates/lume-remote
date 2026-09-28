using System;
using System.Drawing;
using System.Drawing.Imaging;
using System.IO;
using System.Runtime.InteropServices;
using System.Text;

namespace LumeRemote
{
    public sealed class VideoEncoder : IDisposable
    {
        IntPtr handle;
        readonly int width, height;
        public bool Hardware { get; private set; }
        public string Name { get; private set; }
        public VideoEncoder(int width, int height, int fps, int bitrateKbps, int hardwareMode = 0)
        {
            this.width = width; this.height = height;
            VideoNative.Check(VideoNative.CreateEncoder(width, height, fps, bitrateKbps, hardwareMode, out handle));
            int hardware; StringBuilder name = new StringBuilder(160);
            try { VideoNative.Check(VideoNative.Info(handle, out hardware, name, name.Capacity)); Hardware = hardware != 0; Name = name.ToString(); }
            catch { Dispose(); throw; }
        }
        public byte[] Encode(Bitmap image, bool keyframe)
        {
            if (handle == IntPtr.Zero) throw new ObjectDisposedException("VideoEncoder");
            if (image.Width != width || image.Height != height) throw new ArgumentException("Video encoder dimensions changed.");
            BitmapData pixels = image.LockBits(new Rectangle(Point.Empty, image.Size), ImageLockMode.ReadOnly, PixelFormat.Format32bppRgb);
            try
            {
                IntPtr data; int length;
                int result = VideoNative.Encode(handle, pixels.Scan0, pixels.Stride, keyframe ? 1 : 0, out data, out length);
                VideoNative.Check(result); if (result == 1) return null;
                if (length < 4 || length > 32 * 1024 * 1024 || data == IntPtr.Zero) throw new InvalidDataException("Invalid encoded video length.");
                byte[] bytes = new byte[length]; Marshal.Copy(data, bytes, 0, length); return bytes;
            }
            finally { image.UnlockBits(pixels); }
        }
        public void Dispose() { if (handle != IntPtr.Zero) { VideoNative.Destroy(handle); handle = IntPtr.Zero; } }
    }

    public sealed class VideoDecoder : IDisposable
    {
        IntPtr handle;
        readonly int width, height;
        static int available;
        public VideoDecoder(int width, int height) { this.width = width; this.height = height; VideoNative.Check(VideoNative.CreateDecoder(width, height, out handle)); }
        public static bool Available()
        {
            if (System.Threading.Volatile.Read(ref available) != 0) return available == 1;
            bool supported = false; try { using (VideoDecoder decoder = new VideoDecoder(64,64)) supported = true; } catch { }
            System.Threading.Interlocked.CompareExchange(ref available, supported ? 1 : -1, 0); return supported;
        }
        public bool Decode(byte[] bytes, Bitmap image)
        {
            if (handle == IntPtr.Zero) throw new ObjectDisposedException("VideoDecoder");
            if (image.Width != width || image.Height != height) throw new ArgumentException("Video decoder dimensions changed.");
            BitmapData pixels = image.LockBits(new Rectangle(Point.Empty, image.Size), ImageLockMode.WriteOnly, PixelFormat.Format32bppRgb);
            try { int result = VideoNative.Decode(handle, bytes, bytes.Length, pixels.Scan0, pixels.Stride); VideoNative.Check(result); return result == 0; }
            finally { image.UnlockBits(pixels); }
        }
        public void Dispose() { if (handle != IntPtr.Zero) { VideoNative.Destroy(handle); handle = IntPtr.Zero; } }
    }

    static class VideoNative
    {
        internal static void Check(int result) { if (result < 0) throw new InvalidOperationException("Windows video codec failed (0x" + result.ToString("X8") + "). Try Lossless or JPEG."); }
        [DllImport("LumeVideo.dll", EntryPoint="lume_video_encoder_create", CallingConvention=CallingConvention.Cdecl)] internal static extern int CreateEncoder(int width, int height, int fps, int bitrateKbps, int hardwareMode, out IntPtr handle);
        [DllImport("LumeVideo.dll", EntryPoint="lume_video_decoder_create", CallingConvention=CallingConvention.Cdecl)] internal static extern int CreateDecoder(int width, int height, out IntPtr handle);
        [DllImport("LumeVideo.dll", EntryPoint="lume_video_encode", CallingConvention=CallingConvention.Cdecl)] internal static extern int Encode(IntPtr handle, IntPtr pixels, int stride, int keyframe, out IntPtr data, out int length);
        [DllImport("LumeVideo.dll", EntryPoint="lume_video_decode", CallingConvention=CallingConvention.Cdecl)] internal static extern int Decode(IntPtr handle, byte[] data, int length, IntPtr pixels, int stride);
        [DllImport("LumeVideo.dll", EntryPoint="lume_video_info", CallingConvention=CallingConvention.Cdecl, CharSet=CharSet.Unicode)] internal static extern int Info(IntPtr handle, out int hardware, StringBuilder name, int capacity);
        [DllImport("LumeVideo.dll", EntryPoint="lume_video_destroy", CallingConvention=CallingConvention.Cdecl)] internal static extern void Destroy(IntPtr handle);
    }
}
