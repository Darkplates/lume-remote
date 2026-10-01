using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Net;
using System.Net.Sockets;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using LumeRemote;

// Dedicated native regression: peers and UDP proxies only, no desktop session or UI.
static partial class Tests
{
    static IntPtr signalingDelayLibrary;
    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, ExactSpelling = true, SetLastError = true)]
    static extern IntPtr LoadLibraryExW(string path, IntPtr reserved, uint flags);
    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, ExactSpelling = true, SetLastError = true)]
    static extern uint GetModuleFileNameW(IntPtr module, StringBuilder path, int capacity);
    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, ExactSpelling = true)]
    static extern IntPtr GetModuleHandleW(string module);

    static void PeerSignalingDelayChecks()
    {
        Console.WriteLine("SCOPE Owned native peers and loopback UDP proxies; no public STUN, capture, input, approval or clipboard. No SDP, addresses or credentials are logged.");
        Run("Native manual P2P connects with an immediate reply", delegate { LoadSignalingDelayLibrary(); PeerSignalingDelay(0, false); });
        if (failed != 0) return; // A failed control cannot support a delayed comparison.
        Run("Native ICE survives a reply delayed by 45 seconds", delegate { PeerSignalingDelay(45000, false); });
        Run("Native DTLS survives an early route and reply delayed by 150 seconds", delegate { PeerSignalingDelay(150000, true); });
        // Firewalls and NAT routers admit a peer's packets only shortly after sending to it.
        Run("Native ICE survives a reply delayed by 150 seconds behind 30-second stateful firewalls", delegate { PeerSignalingDelay(150000, false, 30000); });
    }
    static void LoadSignalingDelayLibrary()
    {
        string configured = Environment.GetEnvironmentVariable("LUME_TEST_DATACHANNEL");
        string path = Path.GetFullPath(String.IsNullOrWhiteSpace(configured) ? Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "datachannel.dll") : configured);
        Check(Path.GetFileName(path).Equals("datachannel.dll", StringComparison.OrdinalIgnoreCase) && File.Exists(path), "LUME_TEST_DATACHANNEL must select an existing datachannel.dll.");
        Check(GetModuleHandleW("datachannel.dll") == IntPtr.Zero, "The dedicated fixture must select its native library before any peer library is loaded.");
        // Keep the module for this dedicated process lifetime; native teardown uses worker callbacks.
        signalingDelayLibrary = LoadLibraryExW(path, IntPtr.Zero, 8);
        Check(signalingDelayLibrary != IntPtr.Zero, "The selected native library could not be loaded (Win32 " + Marshal.GetLastWin32Error() + ").");
        StringBuilder loaded = new StringBuilder(32768);
        Check(GetModuleFileNameW(signalingDelayLibrary, loaded, loaded.Capacity) > 0 && Path.GetFullPath(loaded.ToString()).Equals(path, StringComparison.OrdinalIgnoreCase), "The native module does not match the selected fixture input.");
        Check(GetModuleHandleW("datachannel.dll") == signalingDelayLibrary, "The selected native library is not the module resolved by its import name.");
        using (SHA256 sha = SHA256.Create()) using (FileStream input = File.OpenRead(path))
            Console.WriteLine("NATIVE_INPUT_SHA256 " + BitConverter.ToString(sha.ComputeHash(input)).Replace("-", ""));
    }
    static IPEndPoint SignalingDelayEndpoint(string sdp)
    {
        foreach (string line in sdp.Split(new string[] { "\r\n" }, StringSplitOptions.RemoveEmptyEntries))
        {
            if (!line.StartsWith("a=candidate:", StringComparison.Ordinal)) continue;
            string[] fields = line.Split(new char[] { ' ' }, StringSplitOptions.RemoveEmptyEntries);
            IPAddress address; int port;
            if (fields.Length >= 8 && fields[2].Equals("UDP", StringComparison.OrdinalIgnoreCase) &&
                IPAddress.TryParse(fields[4], out address) && address.AddressFamily == AddressFamily.InterNetwork &&
                Int32.TryParse(fields[5], out port) && port > 0 && port <= 65535 && fields[6] == "typ" && fields[7] == "host")
                return new IPEndPoint(IPAddress.Loopback, port);
        }
        throw new Exception("The owned native peer did not gather an IPv4 UDP host candidate.");
    }
    static string SignalingDelayDescription(string sdp, int proxyPort)
    {
        StringBuilder rewritten = new StringBuilder(); bool inserted = false;
        string candidate = "a=candidate:1 1 UDP 2122260223 127.0.0.1 " + proxyPort + " typ host\r\n";
        foreach (string line in sdp.Split(new string[] { "\r\n" }, StringSplitOptions.RemoveEmptyEntries))
        {
            if (line.StartsWith("a=candidate:", StringComparison.Ordinal)) continue;
            if (line == "a=end-of-candidates") { rewritten.Append(candidate); inserted = true; }
            rewritten.Append(line).Append("\r\n");
        }
        if (!inserted) rewritten.Append(candidate);
        string result = rewritten.ToString(); PeerSignal.ValidateSdp(result); return result;
    }
    sealed class SignalingDelayProxy : IDisposable
    {
        readonly IPEndPoint hostEndpoint;
        readonly UdpClient hostProxy, viewerProxy;
        readonly Thread fromViewer, fromHost;
        volatile IPEndPoint viewerEndpoint;
        volatile bool forward, stopping;
        bool viewerStarted, hostStarted;
        int disposed, seen, dropped, forwarded, handshakes, firstError;
        internal int HostPort { get { return ((IPEndPoint)hostProxy.Client.LocalEndPoint).Port; } }
        internal int ViewerPort { get { return ((IPEndPoint)viewerProxy.Client.LocalEndPoint).Port; } }
        internal int Dropped { get { return Volatile.Read(ref dropped); } }
        internal int Forwarded { get { return Volatile.Read(ref forwarded); } }
        internal int Handshakes { get { return Volatile.Read(ref handshakes); } }
        internal int Error { get { return Volatile.Read(ref firstError); } }
        internal IPEndPoint Viewer { set { viewerEndpoint = value; } }
        internal bool Forward { set { forward = value; } }
        readonly Stopwatch clock = Stopwatch.StartNew();
        long lastFromViewer = -1, lastFromHost = -1, quietAtReply = -1;
        int statefulWindow;
        internal int StatefulWindow { set { statefulWindow = value; } }
        // How long the answering PC had been silent when the sharing PC first sent.
        internal long ViewerQuietMilliseconds { get { return Interlocked.Read(ref quietAtReply); } }
        internal SignalingDelayProxy(IPEndPoint host)
        {
            hostEndpoint = host;
            try
            {
                hostProxy = new UdpClient(new IPEndPoint(IPAddress.Loopback, 0));
                viewerProxy = new UdpClient(new IPEndPoint(IPAddress.Loopback, 0));
                hostProxy.Client.ReceiveTimeout = viewerProxy.Client.ReceiveTimeout = 250;
                fromViewer = new Thread(delegate() { Pump(hostProxy, viewerProxy, true); }) { IsBackground = true, Name = "signaling-delay-from-viewer" };
                fromHost = new Thread(delegate() { Pump(viewerProxy, hostProxy, false); }) { IsBackground = true, Name = "signaling-delay-from-host" };
                fromViewer.Start(); viewerStarted = true; fromHost.Start(); hostStarted = true;
            }
            catch { Dispose(); throw; }
        }
        void Pump(UdpClient incoming, UdpClient outgoing, bool viewerDirection)
        {
            while (!stopping)
            {
                try
                {
                    IPEndPoint sender = new IPEndPoint(IPAddress.Any, 0); byte[] bytes = incoming.Receive(ref sender);
                    Interlocked.Increment(ref seen);
                    IPEndPoint expected = viewerDirection ? viewerEndpoint : hostEndpoint;
                    IPEndPoint target = viewerDirection ? hostEndpoint : viewerEndpoint;
                    if (!forward || expected == null || target == null || !IPAddress.IsLoopback(sender.Address) || sender.Port != expected.Port)
                    { Interlocked.Increment(ref dropped); continue; }
                    if (statefulWindow > 0)
                    {
                        long now = clock.ElapsedMilliseconds;
                        if (viewerDirection) Interlocked.Exchange(ref lastFromViewer, now); else Interlocked.Exchange(ref lastFromHost, now);
                        long peerLast = viewerDirection ? Interlocked.Read(ref lastFromHost) : Interlocked.Read(ref lastFromViewer);
                        if (!viewerDirection && Interlocked.Read(ref quietAtReply) < 0) { long viewerLast = Interlocked.Read(ref lastFromViewer); Interlocked.Exchange(ref quietAtReply, viewerLast < 0 ? now : now - viewerLast); }
                        if (peerLast < 0 || now - peerLast > statefulWindow) { Interlocked.Increment(ref dropped); continue; }
                    }
                    // Opposite sockets preserve the proxy endpoint advertised in each description.
                    outgoing.Send(bytes, bytes.Length, target); Interlocked.Increment(ref forwarded);
                    if (bytes.Length >= 13 && bytes[0] == 22) Interlocked.Increment(ref handshakes);
                }
                catch (SocketException error) { if (!stopping && error.SocketErrorCode != SocketError.TimedOut) Interlocked.CompareExchange(ref firstError, error.ErrorCode, 0); }
                catch (ObjectDisposedException) { if (!stopping) Interlocked.CompareExchange(ref firstError, -1, 0); }
            }
        }
        internal void Report(int delay, bool earlyRoute, double seconds)
        {
            Console.WriteLine("SIGNALING_DELAY replyDelayMs=" + delay + " earlyRoute=" + earlyRoute + " elapsedSeconds=" + seconds.ToString("F1", CultureInfo.InvariantCulture) +
                " seen=" + Volatile.Read(ref seen) + " dropped=" + Dropped + " forwarded=" + Forwarded + " dtlsHandshakes=" + Handshakes + " proxyError=" + Error);
        }
        public void Dispose()
        {
            if (Interlocked.Exchange(ref disposed, 1) != 0) return;
            stopping = true;
            try { if (hostProxy != null) hostProxy.Close(); } finally { if (viewerProxy != null) viewerProxy.Close(); }
            bool viewerJoined = !viewerStarted || fromViewer.Join(3000);
            bool hostJoined = !hostStarted || fromHost.Join(3000);
            Check(viewerJoined && hostJoined, "An owned signaling-delay proxy thread did not join.");
        }
    }
    static void SignalingDelayBytes(PeerTransport sender, PeerTransport receiver, byte[] expected)
    {
        sender.Write(expected, 0, expected.Length); byte[] observed = new byte[expected.Length]; int count = 0;
        while (count < observed.Length) { int read = receiver.Read(observed, count, observed.Length - count); Check(read > 0, "The native binary stream ended during the delay fixture."); count += read; }
        for (int i = 0; i < expected.Length; ++i) Check(observed[i] == expected[i], "The native binary stream changed a byte after signaling delay.");
    }
    static void PeerSignalingDelay(int delayMilliseconds, bool earlyRoute) { PeerSignalingDelay(delayMilliseconds, earlyRoute, 0); }
    // statefulMilliseconds > 0 models a stateful firewall or NAT on each side: inbound
    // packets pass only if that side sent to the other within the window.
    static void PeerSignalingDelay(int delayMilliseconds, bool earlyRoute, int statefulMilliseconds)
    {
        Stopwatch total = Stopwatch.StartNew(); PeerTransport host = null, viewer = null; SignalingDelayProxy proxy = null;
        List<Task> waits = new List<Task>();
        try
        {
            host = new PeerTransport(false); viewer = new PeerTransport(false);
            Check(GetModuleHandleW("datachannel.dll") == signalingDelayLibrary, "The peer imports did not resolve to the selected native library.");
            string offer = host.CreateOffer(); proxy = new SignalingDelayProxy(SignalingDelayEndpoint(offer));
            string answer = viewer.CreateAnswer(SignalingDelayDescription(offer, proxy.HostPort));
            proxy.Viewer = SignalingDelayEndpoint(answer); proxy.StatefulWindow = statefulMilliseconds; proxy.Forward = earlyRoute || statefulMilliseconds > 0;
            Stopwatch manual = Stopwatch.StartNew(); bool selectedRoute = false; int reported = 0;
            while (manual.ElapsedMilliseconds < delayMilliseconds)
            {
                Check(host.CanRead && viewer.CanRead, "A native peer ended before the delayed manual reply was applied.");
                Check(proxy.Error == 0, "An owned UDP proxy failed (socket code " + proxy.Error + ").");
                if (earlyRoute && !selectedRoute) selectedRoute = viewer.RouteSummary().StartsWith("Direct P2P /", StringComparison.Ordinal);
                if (earlyRoute && manual.ElapsedMilliseconds >= 15000) Check(selectedRoute && proxy.Handshakes > 0, "The early-route fixture did not establish ICE and begin DTLS before the reply delay.");
                int seconds = (int)manual.Elapsed.TotalSeconds;
                if (seconds >= reported + 30) { reported = seconds; Console.WriteLine("SIGNALING_WAIT elapsedSeconds=" + seconds + " peersAlive=True earlyRoute=" + earlyRoute + " dtlsHandshakes=" + proxy.Handshakes); }
                Thread.Sleep(100);
            }
            Check(host.CanRead && viewer.CanRead, "A native peer ended at the manual reply deadline.");
            if (earlyRoute) Check(selectedRoute && proxy.Handshakes > 0, "The early-route delay did not exercise pending DTLS.");
            else if (delayMilliseconds > 0) Check(proxy.Forwarded == 0 && proxy.Dropped > 0, "The blocked-route delay did not suppress connectivity checks.");
            host.AcceptAnswer(SignalingDelayDescription(answer, proxy.ViewerPort)); proxy.Forward = true;
            Exception hostFailure = null, viewerFailure = null;
            int hostWait = earlyRoute || statefulMilliseconds > 0 ? 90000 : 10000;
            int viewerWait = earlyRoute ? Math.Max(1, 180000 - (int)manual.ElapsedMilliseconds) : statefulMilliseconds > 0 ? 90000 : 10000;
            waits.Add(Task.Run(delegate { try { host.WaitReady(hostWait); } catch (Exception error) { hostFailure = error; } }));
            waits.Add(Task.Run(delegate { try { viewer.WaitReady(viewerWait); } catch (Exception error) { viewerFailure = error; if (earlyRoute) viewer.Dispose(); } }));
            Check(Task.WaitAll(waits.ToArray(), Math.Max(hostWait, viewerWait) + 2500), "Native readiness tasks did not finish at their bounded deadlines.");
            Check(hostFailure == null && viewerFailure == null, "Native signaling delay failed (host=" + (hostFailure == null ? "ready" : hostFailure.GetType().Name) + ", viewer=" + (viewerFailure == null ? "ready" : viewerFailure.GetType().Name) + ").");
            host.ReadTimeout = viewer.ReadTimeout = host.WriteTimeout = viewer.WriteTimeout = 3000;
            SignalingDelayBytes(viewer, host, new byte[] { 19, 27, 33, 44, 58, 66, 77, 89 });
            SignalingDelayBytes(host, viewer, new byte[] { 89, 77, 66, 58, 44, 33, 27, 19 });
            Check(proxy.Error == 0, "An owned UDP proxy failed during duplex validation (socket code " + proxy.Error + ").");
            proxy.Report(delayMilliseconds, earlyRoute, total.Elapsed.TotalSeconds);
            if (statefulMilliseconds > 0) Console.WriteLine("SIGNALING_STATEFUL windowMs=" + statefulMilliseconds + " viewerQuietBeforeReplyMs=" + proxy.ViewerQuietMilliseconds);
            Console.WriteLine("SIGNALING_DUPLEX verifiedBytesPerDirection=8");
        }
        finally
        {
            try { if (proxy != null) proxy.Dispose(); }
            finally
            {
                try { if (viewer != null) viewer.Dispose(); }
                finally { if (host != null) host.Dispose(); Check(Task.WaitAll(waits.ToArray(), 5000), "Owned readiness tasks did not join after peer disposal."); }
            }
        }
        Check(!host.CanRead && !viewer.CanRead, "The delay fixture left a peer transport alive after cleanup.");
        Console.WriteLine("SIGNALING_CLEANUP ownedProxyThreadsJoined=True peersEnded=True readinessTasksJoined=True");
    }
}
