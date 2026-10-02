using System;
using System.Collections.Generic;
using System.Drawing;
using System.IO;
using System.Runtime.InteropServices;
using System.Threading;
using System.Windows.Forms;

namespace LumeRemote
{
    // Local permission prompts appear on the streamed desktop. While one is open, remote
    // presses, pointer moves and wheel input are dropped so a controller cannot answer it.
    // Releases still pass, so keys and buttons held before the prompt never stick.
    public static class LocalConsent
    {
        static int open;
        public static bool Active { get { return System.Threading.Volatile.Read(ref open) > 0; } }
        public static IDisposable Begin() { System.Threading.Interlocked.Increment(ref open); return new Scope(); }
        sealed class Scope : IDisposable
        {
            int ended;
            public void Dispose() { if (System.Threading.Interlocked.Exchange(ref ended, 1) == 0) System.Threading.Interlocked.Decrement(ref open); }
        }
    }

    // Tells every Lume process in this Windows session that a guest currently controls the
    // desktop, so owner-only actions (pairing, access, folders) cannot be driven by the
    // guest's injected input from another Lume window. A dedicated thread holds a named
    // mutex while this process has at least one such session.
    public static class GuestControl
    {
        const string Name = "Local\\LumeRemoteGuestControl";
        static readonly object gate = new object();
        static int count;
        static ManualResetEvent release;
        public static IDisposable Begin()
        {
            lock (gate)
            {
                if (count++ == 0)
                {
                    ManualResetEvent stop = release = new ManualResetEvent(false);
                    Thread holder = new Thread(delegate()
                    {
                        using (Mutex mutex = new Mutex(false, Name))
                        {
                            bool owned = false;
                            try
                            {
                                try { owned = WaitHandle.WaitAny(new WaitHandle[] { mutex, stop }) == 0; }
                                catch (AbandonedMutexException) { owned = true; }
                                if (owned) stop.WaitOne();
                            }
                            finally { if (owned) mutex.ReleaseMutex(); stop.Dispose(); }
                        }
                    }) { IsBackground = true, Name = "Lume guest control marker" };
                    holder.Start();
                }
            }
            return new Scope();
        }
        static void End() { lock (gate) { if (--count == 0) release.Set(); } }
        public static bool Active
        {
            get
            {
                if (Volatile.Read(ref count) > 0) return true;
                try
                {
                    using (Mutex mutex = Mutex.OpenExisting(Name))
                    {
                        bool free;
                        try { free = mutex.WaitOne(0); } catch (AbandonedMutexException) { free = true; }
                        if (free) { mutex.ReleaseMutex(); return false; }
                        return true;
                    }
                }
                catch (WaitHandleCannotBeOpenedException) { return false; }
                catch (UnauthorizedAccessException) { return true; }
            }
        }
        sealed class Scope : IDisposable
        {
            int ended;
            public void Dispose() { if (Interlocked.Exchange(ref ended, 1) == 0) End(); }
        }
    }

    public sealed class InputController : IDisposable
    {
        Rectangle bounds;
        readonly HashSet<int> keys = new HashSet<int>();
        readonly HashSet<int> buttons = new HashSet<int>();
        readonly object gate = new object();
        readonly Action<INPUT> inject;
        public InputController(Rectangle bounds) : this(bounds, RealInput) { }
        public InputController(Rectangle bounds, Action<INPUT> inject) { this.bounds = bounds; this.inject = inject; }
        public void UpdateBounds(Rectangle value) { lock (gate) { Release(); bounds = value; } }
        public void Apply(byte action, int a, int b)
        {
            lock (gate)
            {
                if (LocalConsent.Active && action != 2 && action != 5) return;
                INPUT input = new INPUT();
                if (action == 0)
                {
                    if (a < 0 || a > 65535 || b < 0 || b > 65535) throw new InvalidDataException("Invalid pointer position.");
                    Rectangle desktop = SystemInformation.VirtualScreen;
                    int x = bounds.X + (int)((long)a * (bounds.Width - 1) / 65535), y = bounds.Y + (int)((long)b * (bounds.Height - 1) / 65535);
                    input.u.mouse.dx = (int)((long)(x - desktop.X) * 65535 / Math.Max(1, desktop.Width - 1));
                    input.u.mouse.dy = (int)((long)(y - desktop.Y) * 65535 / Math.Max(1, desktop.Height - 1));
                    input.u.mouse.flags = 0xC001;
                }
                else if (action == 1 || action == 2)
                {
                    if (a < 0 || a > 2 || b != 0) throw new InvalidDataException("Invalid mouse button.");
                    if (action == 1) buttons.Add(a); else if (!buttons.Remove(a)) return;
                    input.u.mouse.flags = ButtonFlag(a, action == 2);
                }
                else if (action == 3)
                {
                    if (a < -1200 || a > 1200 || b != 0) throw new InvalidDataException("Invalid mouse wheel delta.");
                    input.u.mouse.flags = 0x800; input.u.mouse.data = unchecked((uint)a);
                }
                else if (action == 4 || action == 5)
                {
                    if (a < 1 || a > 254 || b < 0 || b > 1) throw new InvalidDataException("Invalid key.");
                    int id = a | b << 16;
                    if (action == 4) keys.Add(id); else if (!keys.Remove(id)) return;
                    input.type = 1; input.u.keyboard.vk = (ushort)a; input.u.keyboard.flags = (uint)(b | (action == 5 ? 2 : 0));
                }
                else throw new InvalidDataException("Unknown input action.");
                inject(input);
            }
        }
        static uint ButtonFlag(int button, bool up) { return button == 0 ? (up ? 4U : 2U) : button == 1 ? (up ? 16U : 8U) : (up ? 64U : 32U); }
        public void Release()
        {
            lock (gate)
            {
                foreach (int key in keys) { INPUT input = new INPUT(); input.type = 1; input.u.keyboard.vk = (ushort)(key & 65535); input.u.keyboard.flags = (uint)((key >> 16) | 2); try { inject(input); } catch { } }
                foreach (int button in buttons) { INPUT input = new INPUT(); input.u.mouse.flags = ButtonFlag(button, true); try { inject(input); } catch { } }
                keys.Clear(); buttons.Clear();
            }
        }
        static void RealInput(INPUT input)
        {
            using (DesktopAttachment desktop = new DesktopAttachment())
                if (SendInput(1, new INPUT[] { input }, Marshal.SizeOf(typeof(INPUT))) != 1) throw new InvalidOperationException("Windows blocked input on this desktop. The portable guest app cannot control elevated windows.");
        }
        public void Dispose() { Release(); }
        [StructLayout(LayoutKind.Sequential)] public struct INPUT { public uint type; public UNION u; }
        [StructLayout(LayoutKind.Explicit)] public struct UNION { [FieldOffset(0)] public MOUSE mouse; [FieldOffset(0)] public KEYBOARD keyboard; }
        [StructLayout(LayoutKind.Sequential)] public struct MOUSE { public int dx, dy; public uint data, flags, time; public UIntPtr extra; }
        [StructLayout(LayoutKind.Sequential)] public struct KEYBOARD { public ushort vk, scan; public uint flags, time; public UIntPtr extra; }
        [DllImport("user32.dll", SetLastError = true)] static extern uint SendInput(uint count, INPUT[] inputs, int size);
    }
}
