using System;
using System.Drawing;
using System.IO;

namespace LumeRemote
{
    public sealed class AdaptiveFrameEncoder : IDisposable
    {
        readonly FrameEncoder image = new FrameEncoder();
        VideoEncoder video;
        int width, height, rate, bitrate;
        bool resetVideo;
        public string Backend { get { return video == null ? "Image updates" : video.Name; } }
        public bool Hardware { get { return video != null && video.Hardware; } }
        public byte[] Encode(Bitmap bitmap, int sequence, StreamQuality quality, bool force, int sourceHz)
        {
            if (!quality.Video) { Dispose(); return image.Encode(bitmap, sequence, quality.JpegQuality, force, quality.Lossless, quality.PortableImages); }
            int target = quality.Limit(sourceHz); if (target < 0) target = sourceHz;
            if (target > 240) throw new InvalidOperationException("H.264 supports targets up to 240 FPS. Lossless remains available for higher source rates.");
            if (video == null || width != bitmap.Width || height != bitmap.Height || rate != target || bitrate != quality.BitrateKbps)
            {
                Dispose(); video = new VideoEncoder(bitmap.Width, bitmap.Height, target, quality.BitrateKbps);
                width = bitmap.Width; height = bitmap.Height; rate = target; bitrate = quality.BitrateKbps; resetVideo = true;
            }
            if (!resetVideo && image.Changes(bitmap, force).Count == 0) return null;
            byte[] encoded = video.Encode(bitmap, resetVideo || force); if (encoded == null) return null;
            bool reset = resetVideo; resetVideo = false;
            H264Bounds.Validate(encoded, bitmap.Width, bitmap.Height, reset);
            return Wire.Message(Kind.Frame, delegate(BinaryWriter writer) {
                writer.Write(sequence); writer.Write(bitmap.Width); writer.Write(bitmap.Height); writer.Write(1);
                writer.Write(0); writer.Write(0); writer.Write(bitmap.Width); writer.Write(bitmap.Height); writer.Write((byte)2);
                writer.Write(encoded.Length + 2); writer.Write((byte)1); writer.Write((byte)(reset ? 1 : 0)); writer.Write(encoded);
            });
        }
        public void Dispose() { if (video != null) { video.Dispose(); video = null; } }
    }
}
