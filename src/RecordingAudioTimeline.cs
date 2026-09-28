using System;
using System.Collections.Generic;
using System.IO;

namespace LumeRemote
{
    // Bounded arrival timeline. Missing/dropped audio becomes silence, not growing latency.
    internal sealed class RecordingAudioTimeline
    {
        sealed class Segment { public byte[] Pcm; public long Start; public long End { get { return Start + Pcm.Length / 4; } } }
        readonly List<Segment> segments = new List<Segment>();
        public void Add(byte[] pcm, long endTime)
        {
            if (pcm == null || pcm.Length < 4 || pcm.Length > AudioBlock.Maximum || pcm.Length % 4 != 0 || endTime < 0) throw new InvalidDataException("Invalid recording audio.");
            lock (segments) { if (segments.Count == 16) segments.RemoveAt(0); segments.Add(new Segment { Pcm = (byte[])pcm.Clone(), Start = Math.Max(0, (long)(endTime * (48000.0 / 10000000)) - pcm.Length / 4) }); }
        }
        public byte[] Read(long start, int frames)
        {
            if (start < 0 || frames < 1 || frames > 4800) throw new InvalidDataException("Invalid audio timeline request.");
            byte[] result = new byte[frames * 4]; long end = start + frames;
            lock (segments)
            {
                foreach (Segment segment in segments) { long from = Math.Max(start, segment.Start), to = Math.Min(end, segment.End); if (to > from) Buffer.BlockCopy(segment.Pcm, (int)((from - segment.Start) * 4), result, (int)((from - start) * 4), (int)((to - from) * 4)); }
                segments.RemoveAll(segment => segment.End <= end);
            }
            return result;
        }
    }
}
