using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Runtime.InteropServices;
using System.Text;
using System.Threading;

namespace LumeRemote
{
    // Ordered, reliable SCTP data channels provide congestion control and retransmission.
    // The existing pinned TLS and local consent protocol runs inside this stream.
    public sealed class PeerTransport : Stream
    {
        readonly object nativeGate = new object(), stateGate = new object(), receiveGate = new object();
        readonly Queue<byte[]> incoming = new Queue<byte[]>();
        readonly Rtc.StateCallback stateCallback, iceCallback, gatheringCallback, channelCallback;
        readonly Rtc.SimpleCallback openCallback, closedCallback, bufferCallback;
        readonly Rtc.MessageCallback messageCallback;
        readonly Rtc.ErrorCallback errorCallback;
        int peer = -1, channel = -1, disposed, queued, offset;
        bool gathered, opened;
        volatile bool ended, checking, routed, secured;
        string failure;
        int readTimeout = 30000, writeTimeout = 15000;
        public PeerTransport(bool useStun = true)
        {
            // Phase-only diagnostics: which layer failed, never addresses, credentials or SDP.
            stateCallback = delegate(int id, int state, IntPtr pointer)
            {
                if (state == 2) secured = true;
                else if (state == 4 || state == 5) End(routed ? (secured ? "The P2P connection was lost." : "A network route was found, but the encrypted WebRTC handshake did not finish.") : NoRoute);
            };
            iceCallback = delegate(int id, int state, IntPtr pointer)
            {
                if (state == 1) checking = true; else if (state == 2 || state == 3) routed = true;
                else if (state == 4 && !routed) End(NoRoute);
            };
            gatheringCallback = delegate(int id, int state, IntPtr pointer) { if (state == 2) lock (stateGate) { gathered = true; Monitor.PulseAll(stateGate); } };
            channelCallback = delegate(int id, int dataChannel, IntPtr pointer) { try { Attach(dataChannel); } catch (Exception error) { End(error.Message); } };
            openCallback = delegate { lock (stateGate) { opened = true; Monitor.PulseAll(stateGate); } };
            closedCallback = delegate { End("The other P2P endpoint closed the connection."); };
            bufferCallback = delegate { lock (stateGate) Monitor.PulseAll(stateGate); };
            errorCallback = delegate(int id, IntPtr error, IntPtr pointer) { End("WebRTC data channel error: " + Marshal.PtrToStringAnsi(error)); };
            messageCallback = Receive;
            IntPtr server = IntPtr.Zero, servers = IntPtr.Zero;
            try
            {
                Rtc.rtcInitLogger(0, IntPtr.Zero);
                Rtc.Configuration config = new Rtc.Configuration { DisableAutoNegotiation = true, MaxMessageSize = 16384 };
                if (useStun)
                {
                    server = Marshal.StringToHGlobalAnsi("stun:stun.cloudflare.com:3478");
                    servers = Marshal.AllocHGlobal(IntPtr.Size); Marshal.WriteIntPtr(servers, server);
                    config.IceServers = servers; config.IceServersCount = 1;
                }
                peer = Checked(Rtc.rtcCreatePeerConnection(ref config));
                Checked(Rtc.rtcSetStateChangeCallback(peer, stateCallback));
                Checked(Rtc.rtcSetIceStateChangeCallback(peer, iceCallback));
                Checked(Rtc.rtcSetGatheringStateChangeCallback(peer, gatheringCallback));
                Checked(Rtc.rtcSetDataChannelCallback(peer, channelCallback));
            }
            catch { Dispose(); throw; }
            finally { if (servers != IntPtr.Zero) Marshal.FreeHGlobal(servers); if (server != IntPtr.Zero) Marshal.FreeHGlobal(server); }
        }
        const string NoRoute = "No direct network route was found between the two PCs. Some routers block direct connections; a shared VPN or your own relay avoids this.";
        // Current setup phase for status text; safe to read from any thread.
        public string Phase
        {
            get
            {
                if (opened && !ended) return "Connected.";
                if (secured) return "Secure link ready. Opening the data channel...";
                if (routed) return "Network route found. Securing the connection...";
                if (checking) return "Checking network routes between the two PCs...";
                return "Waiting for the other PC...";
            }
        }
        static int Checked(int code) { if (code < 0) throw new IOException("The native WebRTC operation failed (" + code + ")."); return code; }
        void CheckOpen() { if (ended || disposed != 0) throw new IOException(failure ?? "The P2P connection was closed."); }
        void Attach(int id)
        {
            lock (nativeGate)
            {
                if (disposed != 0 || channel >= 0) { Rtc.rtcDeleteDataChannel(id); return; }
                channel = id;
                Checked(Rtc.rtcSetOpenCallback(id, openCallback)); Checked(Rtc.rtcSetClosedCallback(id, closedCallback));
                Checked(Rtc.rtcSetErrorCallback(id, errorCallback)); Checked(Rtc.rtcSetMessageCallback(id, messageCallback));
                Checked(Rtc.rtcSetBufferedAmountLowThreshold(id, 65536)); Checked(Rtc.rtcSetBufferedAmountLowCallback(id, bufferCallback));
                if (Rtc.rtcIsOpen(id)) { lock (stateGate) { opened = true; Monitor.PulseAll(stateGate); } }
            }
        }
        public string CreateOffer()
        {
            lock (nativeGate) { CheckOpen(); Attach(Checked(Rtc.rtcCreateDataChannel(peer, "lume-tls"))); Checked(Rtc.rtcSetLocalDescription(peer, "offer")); }
            return GatherDescription();
        }
        public string CreateAnswer(string offer)
        {
            PeerSignal.ValidateSdp(offer);
            lock (nativeGate) { CheckOpen(); Checked(Rtc.rtcSetRemoteDescription(peer, offer, "offer")); Checked(Rtc.rtcSetLocalDescription(peer, "answer")); }
            return GatherDescription();
        }
        public void AcceptAnswer(string answer)
        {
            PeerSignal.ValidateSdp(answer);
            lock (nativeGate) { CheckOpen(); Checked(Rtc.rtcSetRemoteDescription(peer, answer, "answer")); }
        }
        string GatherDescription()
        {
            Stopwatch clock = Stopwatch.StartNew();
            lock (stateGate)
            {
                while (!gathered && !ended) { int left = 25000 - (int)clock.ElapsedMilliseconds; if (left <= 0) throw new TimeoutException("P2P address discovery timed out. Check outbound UDP and try again."); Monitor.Wait(stateGate, left); }
            }
            lock (nativeGate)
            {
                CheckOpen(); StringBuilder sdp = new StringBuilder(32768); Checked(Rtc.rtcGetLocalDescription(peer, sdp, sdp.Capacity));
                string value = sdp.ToString(); PeerSignal.ValidateSdp(value); return value;
            }
        }
        public void WaitReady(int milliseconds)
        {
            Stopwatch clock = Stopwatch.StartNew();
            lock (stateGate)
            {
                while (!opened && !ended)
                {
                    int left = milliseconds - (int)clock.ElapsedMilliseconds;
                    if (left <= 0) throw new TimeoutException(routed ? "A network route was found, but the secure P2P channel did not open in time." :
                        "No direct network route was found in time. Check that the reply was applied on the sharing PC. If it was, the routers may block direct connections; a shared VPN or your own relay avoids this.");
                    Monitor.Wait(stateGate, left);
                }
            }
            CheckOpen();
        }
        public string RouteSummary()
        {
            lock (nativeGate)
            {
                if (ended || disposed != 0) return "P2P WebRTC";
                StringBuilder local = new StringBuilder(2048), remote = new StringBuilder(2048);
                int result = Rtc.rtcGetSelectedCandidatePair(peer, local, local.Capacity, remote, remote.Capacity);
                if (result < 0) return "P2P WebRTC";
                string a = local.ToString(), b = remote.ToString();
                if (a.Contains(" typ relay") || b.Contains(" typ relay")) return "WebRTC relay";
                return "Direct P2P / " + (a.Contains(" typ srflx") || b.Contains(" typ srflx") || a.Contains(" typ prflx") || b.Contains(" typ prflx") ? "NAT traversal" : "host candidates");
            }
        }
        void Receive(int id, IntPtr data, int length, IntPtr pointer)
        {
            try
            {
                if (ended) return;
                if (length < 1 || length > 16384) { End("Invalid P2P data chunk."); return; }
                lock (receiveGate)
                {
                    if (ended) return;
                    // Above the host's unacknowledged frame window (16 MiB, FrameWindow), with slack for
                    // control traffic, so a slow decode never ends a legitimate session.
                    if (queued + length > 24 * 1024 * 1024) { End("The P2P receive buffer limit was exceeded."); return; }
                    byte[] bytes = new byte[length]; Marshal.Copy(data, bytes, 0, length); incoming.Enqueue(bytes); queued += length; Monitor.PulseAll(receiveGate);
                }
            }
            catch (Exception error) { End(error.Message); }
        }
        void End(string reason)
        {
            Interlocked.CompareExchange(ref failure, reason, null); ended = true;
            lock (stateGate) Monitor.PulseAll(stateGate);
            lock (receiveGate) Monitor.PulseAll(receiveGate);
        }
        public override int Read(byte[] buffer, int start, int count)
        {
            if (buffer == null || start < 0 || count < 0 || start > buffer.Length - count) throw new ArgumentException("Invalid read buffer.");
            if (count == 0) return 0; Stopwatch clock = Stopwatch.StartNew();
            lock (receiveGate)
            {
                while (incoming.Count == 0)
                {
                    if (ended) { if (disposed != 0) return 0; throw new IOException(failure ?? "The P2P connection ended."); }
                    int left = readTimeout < 0 ? Timeout.Infinite : readTimeout - (int)clock.ElapsedMilliseconds;
                    if (readTimeout >= 0 && left <= 0) throw new IOException("P2P read timed out.");
                    Monitor.Wait(receiveGate, left);
                }
                byte[] head = incoming.Peek(); int length = Math.Min(count, head.Length - offset);
                Buffer.BlockCopy(head, offset, buffer, start, length); offset += length; queued -= length;
                if (offset == head.Length) { incoming.Dequeue(); offset = 0; } return length;
            }
        }
        public override void Write(byte[] buffer, int start, int count)
        {
            if (buffer == null || start < 0 || count < 0 || start > buffer.Length - count) throw new ArgumentException("Invalid write buffer.");
            Stopwatch clock = Stopwatch.StartNew();
            while (count > 0)
            {
                bool sent = false;
                lock (nativeGate)
                {
                    CheckOpen(); if (channel < 0 || !opened) throw new IOException("P2P data channel is not open.");
                    if (Checked(Rtc.rtcGetBufferedAmount(channel)) <= 262144)
                    {
                        int length = Math.Min(count, 16384); GCHandle pin = GCHandle.Alloc(buffer, GCHandleType.Pinned);
                        try { Checked(Rtc.rtcSendMessage(channel, IntPtr.Add(pin.AddrOfPinnedObject(), start), length)); }
                        finally { pin.Free(); }
                        start += length; count -= length; sent = true;
                    }
                }
                if (!sent)
                {
                    if (writeTimeout >= 0 && clock.ElapsedMilliseconds >= writeTimeout) throw new IOException("The P2P send queue timed out.");
                    lock (stateGate) { if (!ended) Monitor.Wait(stateGate, 50); }
                }
            }
        }
        protected override void Dispose(bool disposing)
        {
            if (Interlocked.Exchange(ref disposed, 1) != 0) return;
            End("P2P stopped locally."); int pc, dc;
            lock (nativeGate) { pc = peer; dc = channel; peer = channel = -1; }
            // Deletion waits for native callbacks, so no managed lock is held here.
            if (dc >= 0) Rtc.rtcDeleteDataChannel(dc);
            if (pc >= 0) Rtc.rtcDeletePeerConnection(pc);
            lock (receiveGate) { incoming.Clear(); queued = offset = 0; }
            base.Dispose(disposing);
        }
        public override bool CanRead { get { return !ended; } }
        public override bool CanWrite { get { return !ended; } }
        public override bool CanSeek { get { return false; } }
        public override bool CanTimeout { get { return true; } }
        public override int ReadTimeout { get { return readTimeout; } set { if (value == 0 || value < -1) throw new ArgumentOutOfRangeException(); readTimeout = value; } }
        public override int WriteTimeout { get { return writeTimeout; } set { if (value == 0 || value < -1) throw new ArgumentOutOfRangeException(); writeTimeout = value; } }
        public override void Flush() { }
        public override long Length { get { throw new NotSupportedException(); } }
        public override long Position { get { throw new NotSupportedException(); } set { throw new NotSupportedException(); } }
        public override long Seek(long offset, SeekOrigin origin) { throw new NotSupportedException(); }
        public override void SetLength(long value) { throw new NotSupportedException(); }
    }

    static class Rtc
    {
        const string Library = "datachannel.dll";
        [StructLayout(LayoutKind.Sequential)] internal struct Configuration
        {
            public IntPtr IceServers; public int IceServersCount; public IntPtr ProxyServer, BindAddress;
            public int CertificateType, TransportPolicy;
            [MarshalAs(UnmanagedType.I1)] public bool EnableIceTcp;
            [MarshalAs(UnmanagedType.I1)] public bool EnableIceUdpMux;
            [MarshalAs(UnmanagedType.I1)] public bool DisableAutoNegotiation;
            [MarshalAs(UnmanagedType.I1)] public bool ForceMediaTransport;
            public ushort PortRangeBegin, PortRangeEnd; public int Mtu, MaxMessageSize;
        }
        [UnmanagedFunctionPointer(CallingConvention.Cdecl)] internal delegate void StateCallback(int id, int state, IntPtr pointer);
        [UnmanagedFunctionPointer(CallingConvention.Cdecl)] internal delegate void SimpleCallback(int id, IntPtr pointer);
        [UnmanagedFunctionPointer(CallingConvention.Cdecl)] internal delegate void MessageCallback(int id, IntPtr data, int length, IntPtr pointer);
        [UnmanagedFunctionPointer(CallingConvention.Cdecl)] internal delegate void ErrorCallback(int id, IntPtr error, IntPtr pointer);
        [DllImport(Library, CallingConvention = CallingConvention.Cdecl)] internal static extern void rtcInitLogger(int level, IntPtr callback);
        [DllImport(Library, CallingConvention = CallingConvention.Cdecl)] internal static extern int rtcCreatePeerConnection(ref Configuration config);
        [DllImport(Library, CallingConvention = CallingConvention.Cdecl)] internal static extern int rtcDeletePeerConnection(int id);
        [DllImport(Library, CallingConvention = CallingConvention.Cdecl)] internal static extern int rtcSetStateChangeCallback(int id, StateCallback callback);
        [DllImport(Library, CallingConvention = CallingConvention.Cdecl)] internal static extern int rtcSetIceStateChangeCallback(int id, StateCallback callback);
        [DllImport(Library, CallingConvention = CallingConvention.Cdecl)] internal static extern int rtcSetGatheringStateChangeCallback(int id, StateCallback callback);
        [DllImport(Library, CallingConvention = CallingConvention.Cdecl)] internal static extern int rtcSetDataChannelCallback(int id, StateCallback callback);
        [DllImport(Library, CallingConvention = CallingConvention.Cdecl, CharSet = CharSet.Ansi)] internal static extern int rtcSetLocalDescription(int id, string type);
        [DllImport(Library, CallingConvention = CallingConvention.Cdecl, CharSet = CharSet.Ansi)] internal static extern int rtcSetRemoteDescription(int id, string sdp, string type);
        [DllImport(Library, CallingConvention = CallingConvention.Cdecl, CharSet = CharSet.Ansi)] internal static extern int rtcGetLocalDescription(int id, StringBuilder sdp, int capacity);
        [DllImport(Library, CallingConvention = CallingConvention.Cdecl, CharSet = CharSet.Ansi)] internal static extern int rtcGetSelectedCandidatePair(int id, StringBuilder local, int localCapacity, StringBuilder remote, int remoteCapacity);
        [DllImport(Library, CallingConvention = CallingConvention.Cdecl, CharSet = CharSet.Ansi)] internal static extern int rtcCreateDataChannel(int id, string label);
        [DllImport(Library, CallingConvention = CallingConvention.Cdecl)] internal static extern int rtcDeleteDataChannel(int id);
        [DllImport(Library, CallingConvention = CallingConvention.Cdecl)] internal static extern int rtcSetOpenCallback(int id, SimpleCallback callback);
        [DllImport(Library, CallingConvention = CallingConvention.Cdecl)] internal static extern int rtcSetClosedCallback(int id, SimpleCallback callback);
        [DllImport(Library, CallingConvention = CallingConvention.Cdecl)] internal static extern int rtcSetErrorCallback(int id, ErrorCallback callback);
        [DllImport(Library, CallingConvention = CallingConvention.Cdecl)] internal static extern int rtcSetMessageCallback(int id, MessageCallback callback);
        [DllImport(Library, CallingConvention = CallingConvention.Cdecl)] internal static extern int rtcSetBufferedAmountLowThreshold(int id, int amount);
        [DllImport(Library, CallingConvention = CallingConvention.Cdecl)] internal static extern int rtcSetBufferedAmountLowCallback(int id, SimpleCallback callback);
        [DllImport(Library, CallingConvention = CallingConvention.Cdecl)] internal static extern int rtcSendMessage(int id, IntPtr bytes, int length);
        [DllImport(Library, CallingConvention = CallingConvention.Cdecl)] internal static extern int rtcGetBufferedAmount(int id);
        [DllImport(Library, CallingConvention = CallingConvention.Cdecl)] [return: MarshalAs(UnmanagedType.I1)] internal static extern bool rtcIsOpen(int id);
    }
}
