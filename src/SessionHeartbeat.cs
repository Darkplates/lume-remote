using System;
using System.Threading;

namespace LumeRemote
{
    // Network liveness must not depend on the Windows message loop or input queue.
    public sealed class SessionHeartbeat : IDisposable
    {
        readonly object gate = new object();
        bool stopped;
        public SessionHeartbeat(Action send, Action<Exception> failed, int intervalMilliseconds = 3000)
        {
            if (intervalMilliseconds < 1) throw new ArgumentOutOfRangeException("intervalMilliseconds");
            Thread thread = new Thread(delegate()
            {
                while (true)
                {
                    lock (gate) { if (stopped) return; Monitor.Wait(gate, intervalMilliseconds); if (stopped) return; }
                    try { send(); }
                    catch (Exception error) { lock (gate) { if (stopped) return; stopped = true; } failed(error); return; }
                }
            });
            thread.IsBackground = true; thread.Name = "Lume session heartbeat"; thread.Start();
        }
        public void Dispose() { lock (gate) { stopped = true; Monitor.PulseAll(gate); } }
    }
}
