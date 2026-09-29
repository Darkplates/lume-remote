using System;
using System.Drawing;
using System.IO;

namespace LumeRemote
{
    public sealed class AdaptiveFrameEncoder : IDisposable
    {
        readonly FrameEncoder image = new FrameEncoder();
        readonly Func<int, int, int, int, IVideoFrameEncoder> videoFactory;
        IVideoFrameEncoder video;
        int width, height, rate, bitrate;
        bool resetVideo, pendingOutput;
        // True while the video encoder has accepted input without producing an access unit. The capture loop must keep
        // feeding frames, even unchanged ones, until the buffered picture is flushed to the viewer.
        public AdaptiveFrameEncoder() : this(null) { }
        public AdaptiveFrameEncoder(Func<int, int, int, int, IVideoFrameEncoder> videoFactory) { this.videoFactory = videoFactory; }
        public bool PendingOutput { get { return video != null && pendingOutput; } }
        public string Backend { get { return video == null ? "Image updates" : video.Name; } }
        public bool Hardware { get { return video != null && video.Hardware; } }
        public byte[] Encode(Bitmap bitmap, int sequence, StreamQuality quality, bool force, int sourceHz)
        {
            if (!quality.Video) { Dispose(); return image.Encode(bitmap, sequence, quality.JpegQuality, force, quality.Lossless, quality.PortableImages); }
            int target = quality.Limit(sourceHz); if (target < 0) target = sourceHz;
            if (target > 240) throw new InvalidOperationException("H.264 supports targets up to 240 FPS. Lossless remains available for higher source rates.");
            if (video == null || width != bitmap.Width || height != bitmap.Height || rate != target || bitrate != quality.BitrateKbps)
            {
                Dispose(); video = videoFactory != null ? videoFactory(bitmap.Width, bitmap.Height, target, quality.BitrateKbps) : new VideoEncoder(bitmap.Width, bitmap.Height, target, quality.BitrateKbps);
                width = bitmap.Width; height = bitmap.Height; rate = target; bitrate = quality.BitrateKbps; resetVideo = true;
            }
            // Hash every frame so the change gate reflects what was fed, but never let it hide a buffered picture.
            bool changed = image.Changes(bitmap, force || resetVideo).Count != 0;
            if (!resetVideo && !pendingOutput && !changed) return null;
            byte[] encoded = video.Encode(bitmap, resetVideo || force); pendingOutput = encoded == null; if (encoded == null) return null;
            bool reset = resetVideo; resetVideo = false;
            H264Bounds.Validate(encoded, bitmap.Width, bitmap.Height, reset);
            return Wire.Message(Kind.Frame, delegate(BinaryWriter writer) {
                writer.Write(sequence); writer.Write(bitmap.Width); writer.Write(bitmap.Height); writer.Write(1);
                writer.Write(0); writer.Write(0); writer.Write(bitmap.Width); writer.Write(bitmap.Height); writer.Write((byte)2);
                writer.Write(encoded.Length + 2); writer.Write((byte)1); writer.Write((byte)(reset ? 1 : 0)); writer.Write(encoded);
            });
        }
        public void Dispose() { pendingOutput = false; if (video != null) { video.Dispose(); video = null; } }
    }
}
