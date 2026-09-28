using System;
using System.Collections.Generic;
using System.IO;

namespace LumeRemote
{
    // Reject incompatible SPS dimensions before handing authenticated video to Windows.
    public static class H264Bounds
    {
        public static void Validate(byte[] bytes, int width, int height, bool requireHeader)
        {
            if (width < 2 || height < 2 || width > 4096 || height > 4096 || (long)width * height > 4096L * 2160 || (width & 1) != 0 || (height & 1) != 0 || bytes == null || bytes.Length < 4 || bytes.Length > 32 * 1024 * 1024)
                throw new InvalidDataException("Unsupported video dimensions or packet size.");
            bool header = false, idr = false, found = false; int count = 0;
            for (int i = 0; i + 3 < bytes.Length; )
            {
                int prefix = Prefix(bytes, i);
                if (prefix == 0) { if (!found && bytes[i] != 0) throw new InvalidDataException("Annex B video is required."); i++; continue; }
                int start = i + prefix, end = start;
                if (start >= bytes.Length || (bytes[start] & 128) != 0 || ++count > 4096) throw new InvalidDataException("Invalid video NAL unit.");
                found = true; while (end < bytes.Length && Prefix(bytes, end) == 0) end++;
                int kind = bytes[start] & 31;
                if (kind == 7) { CheckSps(bytes, start + 1, end, width, height); header = true; }
                if (kind == 5) idr = true;
                i = end;
            }
            if (!found || (requireHeader && (!header || !idr))) throw new InvalidDataException("A complete video keyframe with dimensions is required.");
        }
        static int Prefix(byte[] b, int i)
        {
            if (i + 2 >= b.Length || b[i] != 0 || b[i + 1] != 0) return 0;
            if (b[i + 2] == 1) return 3;
            return i + 3 < b.Length && b[i + 2] == 0 && b[i + 3] == 1 ? 4 : 0;
        }
        static void CheckSps(byte[] data, int start, int end, int width, int height)
        {
            if (end - start > 4096) throw new InvalidDataException("Oversized video sequence header.");
            List<byte> payload = new List<byte>(); int zeros = 0;
            for (int i = start; i < end; i++)
            {
                byte value = data[i];
                if (zeros >= 2 && value == 3) { zeros = 0; continue; }
                payload.Add(value); zeros = value == 0 ? zeros + 1 : 0;
            }
            Bits bits = new Bits(payload.ToArray()); uint profile = bits.Read(8); bits.Read(16); bits.Unsigned();
            if (profile != 66 && profile != 77 && profile != 88) throw new InvalidDataException("Only 8-bit 4:2:0 baseline/main video is supported.");
            if (bits.Unsigned() > 12) throw new InvalidDataException("Invalid video frame numbering.");
            uint order = bits.Unsigned();
            if (order == 0) { if (bits.Unsigned() > 12) throw new InvalidDataException("Invalid video picture order."); }
            else if (order == 1) { bits.Read(1); bits.Unsigned(); bits.Unsigned(); uint cycles = bits.Unsigned(); if (cycles > 255) throw new InvalidDataException("Invalid video cycle count."); for (uint i = 0; i < cycles; i++) bits.Unsigned(); }
            else if (order != 2) throw new InvalidDataException("Invalid video picture order.");
            if (bits.Unsigned() > 16) throw new InvalidDataException("Too many reference frames.");
            bits.Read(1); uint columns = bits.Unsigned() + 1, rows = bits.Unsigned() + 1;
            if (columns > 256 || rows > 256 || columns == 0 || rows == 0) throw new InvalidDataException("Video coded dimensions exceed bounds.");
            uint progressive = bits.Read(1); if (progressive != 1) throw new InvalidDataException("Interlaced video is unavailable.");
            bits.Read(1); uint left = 0, right = 0, top = 0, bottom = 0;
            if (bits.Read(1) != 0) { left = bits.Unsigned(); right = bits.Unsigned(); top = bits.Unsigned(); bottom = bits.Unsigned(); }
            long codedWidth = columns * 16L, codedHeight = rows * 16L;
            if (codedWidth - 2L * (left + (long)right) != width || codedHeight - 2L * (top + (long)bottom) != height || left != 0 || top != 0)
                throw new InvalidDataException("Video header dimensions do not match the frame envelope.");
        }
        sealed class Bits
        {
            readonly byte[] bytes; int offset;
            public Bits(byte[] bytes) { this.bytes = bytes; }
            public uint Read(int count)
            {
                if (count < 0 || count > 32 || offset > bytes.Length * 8 - count) throw new InvalidDataException("Truncated video sequence header.");
                uint value = 0; for (int i = 0; i < count; i++, offset++) value = (value << 1) | (uint)((bytes[offset / 8] >> (7 - offset % 8)) & 1); return value;
            }
            public uint Unsigned()
            { int zeros = 0; while (Read(1) == 0) { if (++zeros > 30) throw new InvalidDataException("Video integer exceeds bounds."); } return ((1U << zeros) - 1) + Read(zeros); }
        }
    }
}
