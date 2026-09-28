using System;
using System.IO;

namespace LumeRemote
{
    public sealed class StreamMetrics
    {
        public double CaptureMilliseconds, EncodeMilliseconds, WaitMilliseconds, SendMilliseconds, ChecksPerSecond;
        public bool Idle, Hardware;
        public string Backend = "";
        public void Write(BinaryWriter writer)
        {
            writer.Write(CaptureMilliseconds); writer.Write(EncodeMilliseconds); writer.Write(WaitMilliseconds);
            writer.Write(SendMilliseconds); writer.Write(ChecksPerSecond); writer.Write(Idle); writer.Write(Hardware); Wire.Text(writer, Backend);
        }
        public static StreamMetrics Read(BinaryReader reader)
        {
            StreamMetrics value = new StreamMetrics { CaptureMilliseconds = Number(reader), EncodeMilliseconds = Number(reader), WaitMilliseconds = Number(reader), SendMilliseconds = Number(reader), ChecksPerSecond = Number(reader), Idle = reader.ReadBoolean(), Hardware = reader.ReadBoolean() };
            int length = reader.ReadInt32(); if (length < 0 || length > 512) throw new InvalidDataException("Invalid codec description.");
            byte[] bytes = reader.ReadBytes(length); if (bytes.Length != length) throw new EndOfStreamException();
            value.Backend = new System.Text.UTF8Encoding(false,true).GetString(bytes);
            if (HostService.HasControlChars(value.Backend)) throw new InvalidDataException("Invalid codec description."); return value;
        }
        static double Number(BinaryReader reader)
        { double value = reader.ReadDouble(); if (Double.IsNaN(value) || Double.IsInfinity(value) || value < 0 || value > 60000) throw new InvalidDataException("Invalid stream timing."); return value; }
    }
}
