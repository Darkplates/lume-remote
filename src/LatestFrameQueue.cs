using System;
using System.Drawing;

namespace LumeRemote
{
    // The decoder must apply every delta. Only completed presentation copies may be replaced.
    public sealed class LatestFrameQueue : IDisposable
    {
        readonly object gate = new object(); Bitmap pending; bool closed; int pendingEpoch;
        public void Publish(Bitmap image) { Publish(image, 1); }
        public void Publish(Bitmap image, int epoch)
        {
            lock (gate)
            {
                if (closed) { image.Dispose(); return; }
                Bitmap old = pending; pending = image; pendingEpoch = epoch; if (old != null) old.Dispose();
            }
        }
        public Bitmap Take() { int epoch; return Take(out epoch); }
        public Bitmap Take(out int epoch) { lock (gate) { Bitmap next = pending; pending = null; epoch = pendingEpoch; return next; } }
        public void Dispose() { lock (gate) { closed = true; if (pending != null) { pending.Dispose(); pending = null; } } }
    }
}
