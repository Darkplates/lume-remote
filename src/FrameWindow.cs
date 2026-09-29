using System;
using System.Collections.Generic;
using System.IO;
using System.Threading;

namespace LumeRemote
{
    public sealed class FrameWindow
    {
        readonly Queue<KeyValuePair<int, int>> pending = new Queue<KeyValuePair<int, int>>();
        readonly object gate = new object();
        int bytes;
        public bool HasSpace(int size, int limit)
        { lock (gate) return pending.Count == 0 || (pending.Count < limit && (long)bytes + size <= 16 * 1024 * 1024); }
        public void Add(int sequence, int size)
        { lock (gate) { if (sequence < 1 || size < 1 || pending.Count >= 32) throw new InvalidDataException("Invalid frame window."); pending.Enqueue(new KeyValuePair<int, int>(sequence, size)); bytes += size; } }
        public void Acknowledge(int sequence)
        { lock (gate) { if (pending.Count == 0 || pending.Peek().Key != sequence) throw new InvalidDataException("Unexpected frame acknowledgement."); bytes -= pending.Dequeue().Value; } }
        public bool IsEmpty { get { lock (gate) return pending.Count == 0; } }
    }

    sealed class FramePacer : IDisposable
    {
        readonly EventWaitHandle timer;
        public FramePacer()
        {
            IntPtr handle = CreateWaitableTimerEx(IntPtr.Zero, null, 2, 0x1F0003);
            if (handle != IntPtr.Zero && handle != new IntPtr(-1))
            { timer = new EventWaitHandle(false, EventResetMode.AutoReset); var placeholder = timer.SafeWaitHandle; timer.SafeWaitHandle = new Microsoft.Win32.SafeHandles.SafeWaitHandle(handle, true); placeholder.Dispose(); }
        }
        public bool Wait(double milliseconds, WaitHandle stop)
        {
            if (milliseconds <= 0) return stop.WaitOne(0);
            if (timer == null) return stop.WaitOne(Math.Max(1, (int)Math.Ceiling(milliseconds)));
            long due = -Math.Max(1, (long)(milliseconds * 10000));
            if (!SetWaitableTimer(timer.SafeWaitHandle.DangerousGetHandle(), ref due, 0, IntPtr.Zero, IntPtr.Zero, false)) return stop.WaitOne(Math.Max(1, (int)Math.Ceiling(milliseconds)));
            return WaitHandle.WaitAny(new WaitHandle[] { stop, timer }) == 0;
        }
        public void Dispose() { if (timer != null) timer.Dispose(); }
        [System.Runtime.InteropServices.DllImport("kernel32.dll", CharSet = System.Runtime.InteropServices.CharSet.Unicode)] static extern IntPtr CreateWaitableTimerEx(IntPtr security, string name, uint flags, uint access);
        [System.Runtime.InteropServices.DllImport("kernel32.dll")] static extern bool SetWaitableTimer(IntPtr timer, ref long due, int period, IntPtr completion, IntPtr argument, bool resume);
    }
}
