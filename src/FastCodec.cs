using System;
using System.ComponentModel;
using System.Drawing;
using System.Drawing.Imaging;
using System.IO;
using System.Runtime.InteropServices;

namespace LumeRemote
{
    // The Windows Compression API avoids image re-encoding for text-heavy desktops.
    public static class FastCodec
    {
        public static byte[] Encode(Bitmap image, Rectangle region)
        {
            int stride = checked(region.Width * 4); byte[] pixels = new byte[checked(stride * region.Height)];
            BitmapData data = image.LockBits(region, ImageLockMode.ReadOnly, PixelFormat.Format32bppRgb);
            try { for (int y = 0; y < region.Height; y++) Marshal.Copy(IntPtr.Add(data.Scan0, y * data.Stride), pixels, y * stride, stride); }
            finally { image.UnlockBits(data); }
            IntPtr compressor;
            if (!CreateCompressor(4, IntPtr.Zero, out compressor)) throw new Win32Exception(Marshal.GetLastWin32Error());
            try
            {
                byte[] compressed = new byte[pixels.Length + 65536]; UIntPtr size;
                if (!Compress(compressor, pixels, (UIntPtr)pixels.Length, compressed, (UIntPtr)compressed.Length, out size)) throw new Win32Exception(Marshal.GetLastWin32Error());
                Array.Resize(ref compressed, checked((int)size.ToUInt64())); return compressed;
            }
            finally { CloseCompressor(compressor); }
        }
        public static void Decode(byte[] compressed, Bitmap destination, Rectangle region)
        {
            int stride = checked(region.Width * 4); byte[] pixels = new byte[checked(stride * region.Height)]; IntPtr decompressor;
            if (!CreateDecompressor(4, IntPtr.Zero, out decompressor)) throw new Win32Exception(Marshal.GetLastWin32Error());
            try
            {
                UIntPtr written;
                if (!Decompress(decompressor, compressed, (UIntPtr)compressed.Length, pixels, (UIntPtr)pixels.Length, out written) || written.ToUInt64() != (ulong)pixels.Length)
                    throw new InvalidDataException("Invalid lossless desktop block.");
            }
            finally { CloseDecompressor(decompressor); }
            BitmapData data = destination.LockBits(region, ImageLockMode.WriteOnly, PixelFormat.Format32bppRgb);
            try { for (int y = 0; y < region.Height; y++) Marshal.Copy(pixels, y * stride, IntPtr.Add(data.Scan0, y * data.Stride), stride); }
            finally { destination.UnlockBits(data); }
        }
        [DllImport("cabinet.dll", SetLastError = true)] static extern bool CreateCompressor(uint algorithm, IntPtr allocation, out IntPtr compressor);
        [DllImport("cabinet.dll", SetLastError = true)] static extern bool Compress(IntPtr compressor, byte[] source, UIntPtr sourceSize, byte[] destination, UIntPtr capacity, out UIntPtr size);
        [DllImport("cabinet.dll")] static extern bool CloseCompressor(IntPtr compressor);
        [DllImport("cabinet.dll", SetLastError = true)] static extern bool CreateDecompressor(uint algorithm, IntPtr allocation, out IntPtr decompressor);
        [DllImport("cabinet.dll", SetLastError = true)] static extern bool Decompress(IntPtr decompressor, byte[] source, UIntPtr sourceSize, byte[] destination, UIntPtr capacity, out UIntPtr size);
        [DllImport("cabinet.dll")] static extern bool CloseDecompressor(IntPtr decompressor);
    }
}
