using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Drawing;
using System.IO;
using System.Net;
using System.Net.Security;
using System.Net.Sockets;
using System.Security.Authentication;
using System.Security.Cryptography.X509Certificates;
using System.Threading;
using System.Threading.Tasks;

namespace LumeRemote
{
    public sealed class PeerRequest
    {
        public string Name, Address, CheckCode;
        public bool Control, Files;
    }

    // Counts rejected invitation secrets per remote address so one misbehaving source cannot lock out everyone else.
    internal sealed class AuthFailureTracker
    {
        sealed class Entry { public int Failures; public long CooldownUntil, Touched; }
        public const int Limit = 5, Capacity = 256;
        public static readonly TimeSpan Cooldown = TimeSpan.FromSeconds(30);
        readonly Dictionary<string, Entry> entries = new Dictionary<string, Entry>(StringComparer.Ordinal);
        public bool Blocked(string key)
        {
            lock (entries) { Entry entry; return entries.TryGetValue(key ?? "", out entry) && DateTime.UtcNow.Ticks < entry.CooldownUntil; }
        }
        public void Fail(string key)
        {
            key = key ?? ""; long now = DateTime.UtcNow.Ticks;
            lock (entries)
            {
                Entry entry;
                if (!entries.TryGetValue(key, out entry))
                {
                    if (entries.Count >= Capacity)
                    {
                        string oldest = null; long oldestTouched = long.MaxValue;
                        foreach (KeyValuePair<string, Entry> item in entries) if (item.Value.Touched < oldestTouched) { oldest = item.Key; oldestTouched = item.Value.Touched; }
                        entries.Remove(oldest);
                    }
                    entries.Add(key, entry = new Entry());
                }
                entry.Touched = now;
                if (++entry.Failures >= Limit) { entry.CooldownUntil = now + Cooldown.Ticks; entry.Failures = 0; }
            }
        }
        public int Count { get { lock (entries) return entries.Count; } }
    }

    public sealed class HostService : IDisposable
    {
        readonly X509Certificate2 certificate;
        readonly string secret = Security.Token(32);
        readonly Func<IScreenSource> sourceFactory;
        readonly Func<PeerRequest, bool> approve;
        readonly Action<string> status;
        readonly Action<string> clipboard;
        readonly Func<string, Task<bool>> clipboardRequest;
        readonly Func<RemoteFileAccess> fileAccessFactory;
        readonly Func<Task<string>> clipboardRead;
        readonly bool enableChat;
        readonly Func<Task<bool>> audioPermission;
        readonly Func<PowerAction, Task> powerRequest;
        readonly bool allowClipboardSync;
        readonly Func<Action, CancellationToken, Task<IDisposable>> microphonePermission;
        readonly Profile profile;
        readonly bool allowControl;
        readonly object clientsGate = new object();
        readonly List<IDisposable> clients = new List<IDisposable>();
        readonly ManualResetEvent stopped = new ManualResetEvent(false);
        TcpListener listener;
        volatile bool disposed;
        int active, handshakes;
        readonly AuthFailureTracker failures = new AuthFailureTracker();
        internal int PreAuthDeadlineMilliseconds = 15000;
        const string RelayFailureKey = "relay", PeerFailureKey = "peer";
        public Invitation Invite { get; private set; }
        internal Func<Rectangle, InputController> InputFactory { get; set; }
        public event Action PeerEnded;
        public event Action<Exception> SessionEnded;
        // Raised on a session thread once a viewer is approved and accepted (name, keyboard and mouse allowed).
        public event Action<string, bool> SessionStarted;
        public bool HasSession { get { return Interlocked.CompareExchange(ref active, 0, 0) != 0; } }
        public HostService(Func<IScreenSource> sourceFactory, Profile profile, bool allowControl, Func<PeerRequest, bool> approve, Action<string> clipboard, Action<string> status,
            Func<string, Task<bool>> clipboardRequest = null, Func<RemoteFileAccess> fileAccessFactory = null, Func<Task<string>> clipboardRead = null, bool enableChat = false, Func<Task<bool>> audioPermission = null, Func<PowerAction, Task> powerRequest = null, bool allowClipboardSync = false, Func<Action, CancellationToken, Task<IDisposable>> microphonePermission = null)
        {
            this.sourceFactory = sourceFactory; this.profile = profile; this.allowControl = allowControl;
            this.approve = approve; this.clipboard = clipboard; this.status = status; certificate = Security.Certificate();
            this.clipboardRequest = clipboardRequest; this.fileAccessFactory = fileAccessFactory; this.clipboardRead = clipboardRead; this.enableChat = enableChat; this.audioPermission = audioPermission; this.powerRequest = powerRequest; this.allowClipboardSync = allowClipboardSync;
            this.microphonePermission = microphonePermission;
        }
        // Guests see only the display the owner chose; paired computers may switch displays.
        public bool AllowMonitorSwitching = true;
        public void Start(IPAddress bind, int port, string advertisedHost)
        {
            listener = new TcpListener(bind, port); listener.Start(4);
            Invite = new Invitation { Host = advertisedHost, Port = ((IPEndPoint)listener.LocalEndpoint).Port, Fingerprint = Security.Pin(certificate), Secret = secret, Room = "" };
            BackgroundWork.Run((Action)AcceptLoop); status("Ready. Share your invitation with the person you trust.");
        }
        public void StartRelay(string host, int port)
        {
            Invite = new Invitation { Host = host, Port = port, Fingerprint = Security.Pin(certificate), Secret = secret, Room = Guid.NewGuid().ToString("N") };
            BackgroundWork.Run((Action)RelayLoop);
        }
        public void StartPeer()
        {
            Invite = new Invitation { Host = "peer", Port = 1, Fingerprint = Security.Pin(certificate), Secret = secret, Room = "" };
            status("Discovering a direct P2P route...");
        }
        public void AcceptPeer(PeerTransport stream)
        {
            lock (clientsGate) { if (disposed) { stream.Dispose(); return; } clients.Add(stream); }
            BackgroundWork.Run(delegate
            {
                try { ServeStream(stream, stream.Dispose, stream.RouteSummary(), PeerFailureKey, true); }
                finally
                {
                    lock (clientsGate) clients.Remove(stream); stream.Dispose();
                    Action finished = PeerEnded; if (finished != null) finished();
                }
            });
        }
        bool Track(TcpClient client)
        {
            lock (clientsGate) { if (disposed) { client.Close(); return false; } clients.Add(client); return true; }
        }
        void Untrack(TcpClient client) { lock (clientsGate) clients.Remove(client); client.Close(); }
        void AcceptLoop()
        {
            while (!disposed)
            {
                try
                {
                    TcpClient client = listener.AcceptTcpClient();
                    if (HasSession || failures.Blocked(RemoteKey(client))) { client.Close(); continue; }
                    if (Interlocked.Increment(ref handshakes) > 2) { Interlocked.Decrement(ref handshakes); client.Close(); continue; }
                    if (!Track(client)) { Interlocked.Decrement(ref handshakes); break; }
                    BackgroundWork.Run(delegate { try { Serve(client, false); } finally { Interlocked.Decrement(ref handshakes); Untrack(client); } });
                }
                catch (Exception error) { if (!disposed) status("Listener: " + error.Message); }
            }
        }
        void RelayLoop()
        {
            while (!disposed)
            {
                TcpClient client = null;
                try
                {
                    if (failures.Blocked(RelayFailureKey)) { if (stopped.WaitOne(1000)) break; continue; }
                    status("Connecting to your relay...");
                    client = Transport.Connect(Invite.Host, Invite.Port, 8000);
                    if (!Track(client)) break;
                    Transport.JoinRelay(client, "H", Invite.Room);
                    status("Relay ready. Waiting for a viewer.");
                    client.ReceiveTimeout = 0;
                    int paired = client.GetStream().ReadByte();
                    if (paired != 1) throw new IOException("Relay pairing was interrupted.");
                    Serve(client, true);
                }
                catch (Exception error) { if (!disposed) status("Relay: " + error.Message + " Retrying..."); }
                finally { if (client != null) Untrack(client); }
                if (stopped.WaitOne(1500)) break;
            }
        }
        void Serve(TcpClient client, bool relay)
        {
            Transport.Configure(client);
            string remote = relay ? RelayFailureKey : RemoteKey(client);
            ServeStream(client.GetStream(), client.Close, relay ? "Through your relay" : remote, remote, relay);
        }
        static string RemoteKey(TcpClient client)
        {
            try
            {
                IPAddress address = ((IPEndPoint)client.Client.RemoteEndPoint).Address;
                if (address.IsIPv4MappedToIPv6) address = address.MapToIPv4();
                return address.ToString();
            }
            catch (Exception) { return "unknown"; }
        }
        void ServeStream(Stream transport, Action closeTransport, string address, string failureKey, bool relay)
        {
            bool ownsSession = false;
            Exception sessionError = null;
            // Absolute pre-authentication deadline: per-read timeouts alone let a slow-drip client hold a handshake slot indefinitely.
            int preAuth = 0;
            Timer preAuthDeadline = new Timer(delegate { if (Interlocked.CompareExchange(ref preAuth, 2, 0) == 0) try { closeTransport(); } catch (Exception) { } }, null, PreAuthDeadlineMilliseconds, Timeout.Infinite);
            try
            {
                using (SslStream tls = new SslStream(transport, false))
                {
                    tls.ReadTimeout = 10000; tls.WriteTimeout = 10000;
                    tls.AuthenticateAsServer(certificate, false, SslProtocols.None, false); Security.CheckTls(tls);
                    Wire wire = new Wire(tls);
                    string name; int version;
                    using (Packet packet = wire.Read(2048))
                    {
                        if (packet.Kind != Kind.Auth) throw new InvalidDataException("Authentication required.");
                        version = packet.Reader.ReadInt32(); string supplied = packet.Text(128); name = packet.Text(128); packet.End();
                        if ((version < 1 || version > 4) || !Security.Equal(secret, supplied))
                        {
                            failures.Fail(failureKey);
                            wire.Send(Kind.Denied, delegate(BinaryWriter w) { Wire.Text(w, "Invitation rejected. Ask for the current invitation."); }); return;
                        }
                        if (name.Length == 0 || HasControlChars(name)) throw new InvalidDataException("Invalid peer name.");
                    }
                    if (Interlocked.CompareExchange(ref preAuth, 1, 0) != 0) throw new TimeoutException("Authentication took too long.");
                    preAuthDeadline.Dispose();
                    if (Interlocked.CompareExchange(ref active, 1, 0) != 0) { wire.Send(Kind.Denied, delegate(BinaryWriter w) { Wire.Text(w, "This desktop is already in a session."); }); return; }
                    ownsSession = true;
                    bool allowFiles = allowControl && version >= 3 && fileAccessFactory != null;
                    if (disposed || !approve(new PeerRequest { Name = name, Address = address, Control = allowControl, Files = allowFiles }) || disposed)
                    { wire.Send(Kind.Denied, delegate(BinaryWriter w) { Wire.Text(w, "The host declined the request."); }); return; }
                    tls.ReadTimeout = 30000; tls.WriteTimeout = 15000;
                    using (IScreenSource source = sourceFactory())
                    using (InputController input = InputFactory == null ? new InputController(source.Bounds) : InputFactory(source.Bounds))
                    using (AutoResetEvent acknowledged = new AutoResetEvent(false))
                    using (FrameScaler scaler = new FrameScaler())
                    using (FramePacer pacer = new FramePacer())
                    using (AdaptiveFrameEncoder encoder = new AdaptiveFrameEncoder())
                    using (FileTransfer files = allowFiles ? new FileTransfer(wire, true, fileAccessFactory(), delegate { closeTransport(); }, version >= 4, version >= 4, version >= 4) : null)
                    {
                        IAdaptiveScreenSource adaptive = source as IAdaptiveScreenSource;
                        IMonitorSource monitors = AllowMonitorSwitching ? source as IMonitorSource : null;
                        IDisplayRefreshSource display = source as IDisplayRefreshSource;
                        SessionState state = new SessionState { LastAck = Stopwatch.GetTimestamp() };
                        SessionTools tools = null; HostSessionChat chat = null; SessionAudio hostAudio = null; HostAnnotations annotations = enableChat && allowControl ? new HostAnnotations() : null;
                        HostVoiceController hostVoice = allowControl && microphonePermission != null && MediaNative.Version >= 2 ? new HostVoiceController(wire, microphonePermission) : null;
                        SessionCapabilities capabilities = allowFiles ? SessionCapabilities.Folders : SessionCapabilities.None;
                        capabilities |= SessionCapabilities.PortableImages;
                        if (files != null && files.Resume) capabilities |= SessionCapabilities.FileResume;
                        if (files != null && files.NetworkFolders) capabilities |= SessionCapabilities.NetworkFolders;
                        if (allowControl && clipboardRead != null) capabilities |= SessionCapabilities.ClipboardRead;
                        if (allowControl && allowClipboardSync && clipboardRead != null && clipboardRequest != null) capabilities |= SessionCapabilities.ClipboardSync;
                        if (monitors != null) capabilities |= SessionCapabilities.Monitors;
                        if (enableChat) capabilities |= SessionCapabilities.Chat;
                        if (annotations != null) capabilities |= SessionCapabilities.Annotations;
                        if (allowControl && powerRequest != null) capabilities |= SessionCapabilities.Power;
                        if (allowControl && audioPermission != null && MediaNative.Available) capabilities |= SessionCapabilities.Audio;
                        if (hostVoice != null) capabilities |= SessionCapabilities.Voice;
                        if (version >= 4)
                        {
                            tools = new SessionTools(wire, delegate(Exception error) { state.Error = error; state.Ended = true; closeTransport(); }, async delegate(SessionTool op, Packet body)
                            {
                                if (state.Ended || disposed) throw new OperationCanceledException();
                                if (op == SessionTool.PortableImages)
                                { bool enabled = body.Reader.ReadBoolean(); body.End(); Volatile.Write(ref state.PortableImages, enabled ? 1 : 0); return SessionTools.Payload(); }
                                if (op == SessionTool.Voice)
                                { bool enabled = body.Reader.ReadBoolean(); int generation = body.Reader.ReadInt32(); body.End(); if (hostVoice == null) throw new InvalidOperationException("Voice calling is unavailable in this session."); await hostVoice.Set(enabled, generation).ConfigureAwait(false); return SessionTools.Payload(); }
                                if (op == SessionTool.ClipboardWrite)
                                {
                                    string text = body.Text(262144); body.End();
                                    if ((capabilities & SessionCapabilities.ClipboardSync) == 0) throw new InvalidOperationException("Clipboard sync requires a paired computer.");
                                    if (!await clipboardRequest(text).ConfigureAwait(false)) throw new InvalidOperationException("Clipboard write was declined."); return SessionTools.Payload();
                                }
                                if (op == SessionTool.ClipboardRead)
                                {
                                    body.End(); if ((capabilities & SessionCapabilities.ClipboardRead) == 0) throw new InvalidOperationException("Remote clipboard access is unavailable in this session.");
                                    string value = await clipboardRead().ConfigureAwait(false);
                                    if (System.Text.Encoding.UTF8.GetByteCount(value) > 262144) throw new InvalidOperationException("Clipboard text exceeds 256 KiB.");
                                    return SessionTools.Payload(delegate(BinaryWriter w) { Wire.Text(w, value); });
                                }
                                if (op == SessionTool.Monitors)
                                {
                                    body.End(); if (monitors == null) throw new InvalidOperationException("Monitor selection is unavailable.");
                                    RemoteMonitor[] list = monitors.Monitors();
                                    return SessionTools.Payload(delegate(BinaryWriter w) { w.Write(list.Length); foreach (RemoteMonitor item in list) item.Write(w); });
                                }
                                if (op == SessionTool.SelectMonitor)
                                {
                                    string id = body.Text(256); body.End(); if (monitors == null) throw new InvalidOperationException("Monitor selection is unavailable.");
                                    MonitorChange request = new MonitorChange { Id = id };
                                    if (Interlocked.CompareExchange(ref state.Monitor, request, null) != null) throw new InvalidOperationException("Wait for the current monitor change.");
                                    return await request.Done.Task.ConfigureAwait(false);
                                }
                                if (op == SessionTool.Annotation)
                                {
                                    int epoch = body.Reader.ReadInt32(); Point[] points = AnnotationWire.Read(body);
                                    if (annotations == null) throw new InvalidOperationException("Annotations are unavailable in this session.");
                                    Rectangle bounds; lock (state.InputGate) { if (epoch != state.Epoch) throw new InvalidOperationException("The display changed. Draw again on the current display."); bounds = source.Bounds; }
                                    await annotations.Draw(bounds, points).ConfigureAwait(false); return SessionTools.Payload();
                                }
                                if (op == SessionTool.Power)
                                {
                                    PowerAction action = (PowerAction)body.Reader.ReadByte(); body.End();
                                    if (!Enum.IsDefined(typeof(PowerAction), action)) throw new InvalidDataException("Invalid power action.");
                                    if ((capabilities & SessionCapabilities.Power) == 0) throw new InvalidOperationException("Power actions require an authorized paired computer.");
                                    await powerRequest(action).ConfigureAwait(false); return SessionTools.Payload();
                                }
                                if (op == SessionTool.Audio)
                                {
                                    bool enabled = body.Reader.ReadBoolean(); int generation = body.Reader.ReadInt32(); body.End();
                                    if (generation < 1) throw new InvalidDataException("Invalid audio generation.");
                                    lock (state.InputGate) { if (hostAudio != null) { hostAudio.Dispose(); hostAudio = null; } }
                                    if (enabled)
                                    {
                                        if ((capabilities & SessionCapabilities.Audio) == 0 || !await audioPermission().ConfigureAwait(false)) throw new InvalidOperationException("The host did not allow system audio.");
                                        SessionAudio starting;
                                        lock (state.InputGate)
                                        {
                                        if (state.Ended || disposed) throw new OperationCanceledException();
                                        starting = hostAudio = new SessionAudio(false, delegate(byte[] bytes) { if (!state.Ended && !disposed) wire.Send(Kind.Audio, delegate(BinaryWriter w) { w.Write(generation); w.Write(bytes.Length); w.Write(bytes); }); },
                                            delegate { if (!state.Ended && !disposed) try { wire.Send(Kind.Notice, delegate(BinaryWriter w) { Wire.Text(w, "System audio stopped. Check the remote audio device, then enable audio again."); }); } catch { } });
                                        }
                                        await starting.Ready.ConfigureAwait(false);
                                    }
                                    return SessionTools.Payload();
                                }
                                if (op == SessionTool.Chat)
                                {
                                    string value = body.Text(8192); body.End(); if (chat == null) throw new InvalidOperationException("Chat is unavailable in this session.");
                                    await chat.Receive(value).ConfigureAwait(false); return SessionTools.Payload();
                                }
                                throw new InvalidOperationException("This action is unavailable on this host.");
                            });
                            if (hostVoice != null) tools.Arrived = delegate(SessionTool op, byte[] payload) { if (op == SessionTool.Voice) hostVoice.Preview(payload); };
                            if (enableChat) chat = new HostSessionChat(name, async delegate(string text) { await tools.Request(SessionTool.Chat, delegate(BinaryWriter w) { Wire.Text(w, text); }).ConfigureAwait(false); });
                        }
                        try
                        {
                        int sourceHz = adaptive == null ? 60 : adaptive.RefreshRate;
                        StreamQuality selectedQuality = StreamQuality.FromProfile(profile, source.Bounds);
                        wire.Send(Kind.Accepted, delegate(BinaryWriter w) { w.Write(allowControl); Wire.Text(w, Environment.MachineName); w.Write(source.Bounds.Width); w.Write(source.Bounds.Height); if (version >= 2) { w.Write(version); w.Write(sourceHz); } if (version >= 3) w.Write(allowFiles); if (version >= 4) w.Write((ulong)capabilities); });
                        status("Connected to " + name + (allowControl ? " - keyboard and mouse enabled." : " - view only."));
                        Action<string, bool> started = SessionStarted; if (started != null) try { started(name, allowControl); } catch (Exception) { }
                        Task receiver = BackgroundWork.Run(delegate
                        {
                            try
                            {
                                Stopwatch flood = Stopwatch.StartNew(); int messages = 0; long lastClipboard = 0; Task<bool> pendingClipboard = null;
                                while (!disposed && !state.Ended)
                                {
                                    using (Packet packet = wire.Read(270000))
                                    {
                                        if (flood.ElapsedMilliseconds >= 1000) { messages = 0; flood.Restart(); }
                                        if (++messages > 1200) throw new InvalidDataException("Input rate exceeded.");
                                        switch (packet.Kind)
                                        {
                                            case Kind.Ack:
                                                int sequence = packet.Reader.ReadInt32(); packet.End();
                                                state.Window.Acknowledge(sequence); Interlocked.Exchange(ref state.LastAck, Stopwatch.GetTimestamp());
                                                acknowledged.Set(); break;
                                            case Kind.Quality:
                                                if (version < 2) throw new InvalidDataException("Update both PCs to change stream quality.");
                                                StreamQuality requested = StreamQuality.Read(packet.Reader, version); packet.End(); Volatile.Write(ref state.Quality, requested); break;
                                            case Kind.Input:
                                                int epoch = version >= 4 ? packet.Reader.ReadInt32() : 1;
                                                byte action = packet.Reader.ReadByte(); int a = packet.Reader.ReadInt32(), b = packet.Reader.ReadInt32(); packet.End();
                                                if (!allowControl) throw new InvalidDataException("This session is view only.");
                                                Interlocked.Exchange(ref state.LastInput, DateTime.UtcNow.Ticks);
                                                try { lock (state.InputGate) { if (epoch == state.Epoch) input.Apply(action, a, b); } }
                                                catch (InvalidOperationException error) { wire.Send(Kind.Notice, delegate(BinaryWriter w) { Wire.Text(w, error.Message); }); }
                                                break;
                                            case Kind.Release: packet.End(); input.Release(); break;
                                            case Kind.Clipboard:
                                                string text = packet.Text(262144); packet.End();
                                                if (!allowControl) throw new InvalidDataException("Clipboard is unavailable in view-only sessions.");
                                                if ((pendingClipboard != null && !pendingClipboard.IsCompleted) || DateTime.UtcNow.Ticks - lastClipboard < TimeSpan.TicksPerSecond)
                                                { wire.Send(Kind.Notice, delegate(BinaryWriter w) { Wire.Text(w, "Clipboard request already sent. Wait a moment before retrying."); }); break; }
                                                lastClipboard = DateTime.UtcNow.Ticks;
                                                try
                                                {
                                                    if (clipboardRequest == null) { clipboard(text); wire.Send(Kind.Notice, delegate(BinaryWriter w) { Wire.Text(w, "Clipboard text sent to the host."); }); }
                                                    else
                                                    {
                                                        pendingClipboard = clipboardRequest(text);
                                                        pendingClipboard.ContinueWith(delegate(Task<bool> completed)
                                                        {
                                                            string receipt = completed.IsFaulted ? "Clipboard could not be copied. It may be busy; try again." : completed.IsCanceled || !completed.Result ? "Clipboard request declined." : "Clipboard copied on the remote PC.";
                                                            if (completed.IsFaulted) { var observed = completed.Exception; }
                                                            if (!disposed && !state.Ended) try { wire.Send(Kind.Notice, delegate(BinaryWriter w) { Wire.Text(w, receipt); }); } catch { }
                                                        });
                                                    }
                                                }
                                                catch (Exception) { wire.Send(Kind.Notice, delegate(BinaryWriter w) { Wire.Text(w, "Clipboard is busy. Try again shortly."); }); }
                                                break;
                                            case Kind.Tools:
                                            case Kind.ToolReply:
                                                if (tools == null) throw new InvalidDataException("Session tools were not negotiated."); tools.Handle(packet); break;
                                            case Kind.Files:
                                                if (files == null) throw new InvalidDataException("File access was not authorized for this session.");
                                                files.Handle(packet); break;
                                            case Kind.Voice:
                                                if (version < 4 || hostVoice == null) throw new InvalidDataException("Voice was not authorized for this session."); hostVoice.Receive(packet); break;
                                            case Kind.Ping:
                                                long stamp = packet.Reader.ReadInt64(); packet.End(); wire.Send(Kind.Pong, delegate(BinaryWriter w) { w.Write(stamp); }); break;
                                            case Kind.Goodbye: packet.End(); return;
                                            default: throw new InvalidDataException("Unexpected client packet.");
                                        }
                                    }
                                }
                            }
                            catch (Exception error) { state.Error = error; }
                            finally { state.Ended = true; try { acknowledged.Set(); } catch (ObjectDisposedException) { } closeTransport(); }
                        });
                        Stopwatch ticks = Stopwatch.StartNew(); long lastChange = 0, lastReport = 0, bytesAtReport = 0;
                        double captureMs = 0, encodeMs = 0, waitMs = 0, sendMs = 0; int checks = 0, encodeChecks = 0;
                        IFrameChangeSource changes = source as IFrameChangeSource;
                        int sequenceNumber = 0, intervalFrames = 0; bool reportQuality = version >= 2, forceFrame = true;
                        int[] lastCursor = new int[] { -1, -1, -1 };
                        try
                        {
                            while (!disposed && !state.Ended)
                            {
                                double start = ticks.Elapsed.TotalMilliseconds;
                                MonitorChange change = Volatile.Read(ref state.Monitor);
                                if (change != null && state.Window.IsEmpty)
                                {
                                    try
                                    {
                                        lock (state.InputGate) { monitors.Select(change.Id); input.UpdateBounds(source.Bounds); state.Epoch++; }
                                        sourceHz = adaptive == null ? 60 : adaptive.RefreshRate;
                                        encoder.Dispose(); forceFrame = true; reportQuality = true;
                                        change.Done.TrySetResult(SessionTools.Payload(delegate(BinaryWriter w) { w.Write(state.Epoch); w.Write(source.Bounds.Width); w.Write(source.Bounds.Height); w.Write(sourceHz); }));
                                    }
                                    catch (Exception error) { change.Done.TrySetException(error); }
                                    finally { Interlocked.CompareExchange(ref state.Monitor, null, change); }
                                }
                                else if (change != null)
                                {
                                    if (WaitHandle.WaitAny(new WaitHandle[] { acknowledged, stopped }, 15000) == WaitHandle.WaitTimeout) throw new TimeoutException("The viewer stopped acknowledging frames.");
                                    continue;
                                }
                                if (display != null)
                                {
                                    // Same display, new resolution or position: normalized viewer input stays valid, so the
                                    // monitor epoch is unchanged (a new epoch is only announced by an explicit selection).
                                    bool resized; lock (state.InputGate) { resized = display.RefreshBounds(); if (resized) input.UpdateBounds(source.Bounds); }
                                    if (resized) { sourceHz = adaptive == null ? 60 : adaptive.RefreshRate; encoder.Dispose(); forceFrame = true; reportQuality = true; }
                                }
                                StreamQuality requested = Interlocked.Exchange(ref state.Quality, null);
                                if (requested != null) { selectedQuality = requested; forceFrame = true; reportQuality = true; }
                                bool portableImages = Volatile.Read(ref state.PortableImages) != 0;
                                if (selectedQuality.PortableImages != portableImages) { selectedQuality.PortableImages = portableImages; forceFrame = true; }
                                if (adaptive != null) adaptive.Configure(selectedQuality);
                                int rate = selectedQuality.Limit(sourceHz);
                                int frameLimit = version < 2 ? 1 : rate < 0 ? 32 : Math.Max(2, Math.Min(32, (int)Math.Ceiling(rate * 0.15)));
                                if (!state.Window.IsEmpty && (Stopwatch.GetTimestamp() - Interlocked.Read(ref state.LastAck)) / (double)Stopwatch.Frequency > 15)
                                    throw new TimeoutException("The viewer stopped acknowledging frames.");
                                int[] cursor = DesktopSource.CursorState(source.Bounds);
                                if (cursor[0] != lastCursor[0] || cursor[1] != lastCursor[1] || cursor[2] != lastCursor[2])
                                { wire.Send(Kind.Cursor, delegate(BinaryWriter w) { w.Write(cursor[0]); w.Write(cursor[1]); w.Write(cursor[2]); }); lastCursor = cursor; }
                                double stageStart = ticks.Elapsed.TotalMilliseconds;
                                Bitmap captured = scaler.Scale(source.Capture(), selectedQuality); checks++; captureMs += ticks.Elapsed.TotalMilliseconds - stageStart;
                                byte[] frame = null; stageStart = ticks.Elapsed.TotalMilliseconds;
                                if (forceFrame || changes == null || changes.FrameChanged || encoder.PendingOutput)
                                {
                                    encodeChecks++;
                                    try { frame = encoder.Encode(captured, sequenceNumber + 1, selectedQuality, forceFrame, sourceHz); }
                                    catch (Exception error)
                                    {
                                        if (!selectedQuality.Video || error is OutOfMemoryException) throw;
                                        encoder.Dispose(); selectedQuality = selectedQuality.Copy(); selectedQuality.Video = false; selectedQuality.Lossless = true;
                                        reportQuality = true; forceFrame = true;
                                        wire.Send(Kind.Notice, delegate(BinaryWriter w) { Wire.Text(w, "H.264 is unavailable for these settings. Using lossless image updates. " + error.Message); });
                                        frame = encoder.Encode(captured, sequenceNumber + 1, selectedQuality, true, sourceHz);
                                    }
                                    encodeMs += ticks.Elapsed.TotalMilliseconds - stageStart;
                                }
                                if (frame != null || !selectedQuality.Video) forceFrame = false;
                                if (reportQuality && version >= 2)
                                { wire.Send(Kind.QualityInfo, delegate(BinaryWriter w) { selectedQuality.Write(w, version); w.Write(captured.Width); w.Write(captured.Height); w.Write(sourceHz); }); reportQuality = false; }
                                if (frame != null)
                                {
                                    stageStart = ticks.Elapsed.TotalMilliseconds;
                                    while (!state.Window.HasSpace(frame.Length, frameLimit) && !state.Ended && !disposed)
                                    { int ready = WaitHandle.WaitAny(new WaitHandle[] { acknowledged, stopped }, 15000); if (ready == WaitHandle.WaitTimeout) throw new TimeoutException("The viewer stopped acknowledging frames."); if (ready == 1) break; }
                                    if (disposed || state.Ended) break;
                                    waitMs += ticks.Elapsed.TotalMilliseconds - stageStart; stageStart = ticks.Elapsed.TotalMilliseconds;
                                    if (state.Window.IsEmpty) Interlocked.Exchange(ref state.LastAck, Stopwatch.GetTimestamp());
                                    if (version >= 4)
                                    {
                                        byte[] tagged = new byte[frame.Length + 4]; tagged[0] = frame[0]; Buffer.BlockCopy(BitConverter.GetBytes(state.Epoch), 0, tagged, 1, 4); Buffer.BlockCopy(frame, 1, tagged, 5, frame.Length - 1); frame = tagged;
                                    }
                                    state.Window.Add(++sequenceNumber, frame.Length); wire.Send(frame);
                                    sendMs += ticks.Elapsed.TotalMilliseconds - stageStart;
                                    lastChange = ticks.ElapsedMilliseconds; intervalFrames++;
                                }
                                long now = ticks.ElapsedMilliseconds;
                                bool idle = !encoder.PendingOutput && now - lastChange > 2000 && DateTime.UtcNow.Ticks - Interlocked.Read(ref state.LastInput) > TimeSpan.TicksPerSecond * 2;
                                if (now - lastReport >= 2000)
                                {
                                    double seconds = (now - lastReport) / 1000.0;
                                    StreamMetrics metrics = new StreamMetrics { CaptureMilliseconds = captureMs / Math.Max(1, checks), EncodeMilliseconds = encodeMs / Math.Max(1, encodeChecks), WaitMilliseconds = waitMs / Math.Max(1, intervalFrames), SendMilliseconds = sendMs / Math.Max(1, intervalFrames), ChecksPerSecond = checks / seconds, Idle = idle, Hardware = encoder.Hardware, Backend = encoder.Backend };
                                    if (version >= 3) wire.Send(Kind.StreamMetrics, metrics.Write);
                                    status(String.Format(System.Globalization.CultureInfo.InvariantCulture, "Session active | {0:0.0} updates/s{5} | {1:0.00} Mbit/s | {2} x {3} / {4} | capture {6:0.0} ms / encode {7:0.0} ms / wait {8:0.0} ms / send {9:0.0} ms | {10}", intervalFrames / seconds, (wire.Sent - bytesAtReport) * 8.0 / seconds / 1000000, captured.Width, captured.Height, selectedQuality.Description, idle ? " (idle)" : "", metrics.CaptureMilliseconds, metrics.EncodeMilliseconds, metrics.WaitMilliseconds, metrics.SendMilliseconds, display == null ? metrics.Backend : metrics.Backend + " | capture " + display.Backend));
                                    lastReport = now; bytesAtReport = wire.Sent; intervalFrames = 0;
                                    captureMs = encodeMs = waitMs = sendMs = 0; checks = encodeChecks = 0;
                                }
                                double budget = idle ? 250.0 : rate < 0 ? 0 : 1000.0 / rate;
                                if (pacer.Wait(Math.Max(0, budget - (ticks.Elapsed.TotalMilliseconds - start)), stopped)) break;
                            }
                        }
                        finally { state.Ended = true; closeTransport(); input.Release(); receiver.Wait(2000); }
                        sessionError = state.Error;
                        if (state.Error != null && !disposed) status("Session ended: " + state.Error.Message);
                        }
                        finally
                        {
                            lock (state.InputGate) { state.Ended = true; if (hostAudio != null) hostAudio.Dispose(); }
                            if (hostVoice != null) hostVoice.Dispose();
                            if (state.Monitor != null) state.Monitor.Done.TrySetCanceled();
                            if (tools != null) tools.Dispose(); if (chat != null) chat.Dispose(); if (annotations != null) annotations.Dispose();
                        }
                    }
                }
            }
            catch (Exception error) { if (Volatile.Read(ref preAuth) == 2) error = new TimeoutException("Authentication took too long."); sessionError = error; if (!disposed) status("Connection ended: " + error.Message); }
            finally
            {
                preAuthDeadline.Dispose();
                if (ownsSession)
                {
                    Interlocked.Exchange(ref active, 0);
                    Action<Exception> finished = SessionEnded; if (finished != null) finished(disposed ? new OperationCanceledException() : sessionError);
                }
                if (!disposed && !relay) status("Ready for a new session. Local approval is always required.");
            }
        }
        public static bool HasControlChars(string name) { foreach (char c in name) if (Char.IsControl(c)) return true; return false; }
        public void Dispose()
        {
            if (disposed) return; disposed = true; stopped.Set(); if (listener != null) listener.Stop();
            lock (clientsGate) foreach (IDisposable client in clients) client.Dispose();
            // Keep the certificate alive until all TLS workers have unwound.
            Task.Run(delegate { for (int i = 0; i < 200; i++) { lock (clientsGate) { if (clients.Count == 0) { certificate.Dispose(); return; } } Thread.Sleep(50); } });
        }
        sealed class MonitorChange { public string Id; public readonly TaskCompletionSource<byte[]> Done = new TaskCompletionSource<byte[]>(TaskCreationOptions.RunContinuationsAsynchronously); }
        sealed class SessionState { public readonly object InputGate = new object(); public int Epoch = 1, PortableImages; public MonitorChange Monitor; public volatile bool Ended; public Exception Error; public long LastInput, LastAck; public StreamQuality Quality; public readonly FrameWindow Window = new FrameWindow(); }
    }

    public sealed class ViewerConnection : IDisposable
    {
        public int SourceWidth { get; private set; }
        public int SourceHeight { get; private set; }
        public int SourceRefresh { get; private set; }
        public int StreamWidth { get; private set; }
        public int StreamHeight { get; private set; }
        public StreamQuality CurrentQuality { get; private set; }
        public int ProtocolVersion { get; private set; }
        public StreamMetrics Metrics { get; private set; }
        public FileTransfer Files { get; private set; }
        public SessionTools Tools { get; private set; }
        public SessionCapabilities Capabilities { get; private set; }
        internal string FileResumeKey { get; set; }
        public Func<string, Task> ChatReceived;
        public Action<byte[]> SystemAudioReceived;
        SessionAudio audio; int audioGeneration;
        SessionVoice voice; int voiceGeneration;
        public bool VoiceEnabled { get { SessionVoice current = Volatile.Read(ref voice); return current != null && current.Active; } }
        public bool AudioEnabled { get { SessionAudio current = Volatile.Read(ref audio); return current != null && current.IsRunning; } }
        int monitorEpoch = 1;
        public int FrameEpoch { get; private set; }
        public int MonitorEpoch { get { return Volatile.Read(ref monitorEpoch); } }
        TcpClient client;
        Stream peerStream;
        Wire wire;
        readonly object lifetime = new object();
        SessionHeartbeat heartbeat;
        Exception failure;
        public Exception Failure { get { return Volatile.Read(ref failure); } }
        volatile bool disposed;
        public bool CanControl { get; private set; }
        public string RemoteName { get; private set; }
        public long Received { get { return wire == null ? 0 : Interlocked.Read(ref wire.Received); } }
        public long Sent { get { return wire == null ? 0 : Interlocked.Read(ref wire.Sent); } }
        public int RttMilliseconds { get; private set; }
        public ConnectionStage Stage { get; private set; }
        void Report(ConnectionStage stage, Action<ConnectionStage> progress)
        { Stage = stage; if (progress != null) progress(stage); }
        public void Connect(Invitation invite, string name, Action<ConnectionStage> progress = null)
        {
            Report(ConnectionStage.Contacting, progress);
            client = Transport.Connect(invite.Host, invite.Port, 10000);
            if (disposed) { client.Close(); throw new OperationCanceledException(); }
            if (invite.Relay) { Report(ConnectionStage.PairingRelay, progress); Transport.JoinRelay(client, "V", invite.Room); }
            ConnectSecure(client.GetStream(), invite, name, progress);
        }
        public void ConnectPeer(PeerTransport transport, Invitation invite, string name, Action<ConnectionStage> progress = null)
        {
            peerStream = transport;
            if (disposed) { transport.Dispose(); throw new OperationCanceledException(); }
            ConnectSecure(transport, invite, name, progress);
        }
        void ConnectSecure(Stream transport, Invitation invite, string name, Action<ConnectionStage> progress)
        {
            Report(ConnectionStage.Securing, progress);
            SslStream tls = new SslStream(transport, false, delegate(object sender, X509Certificate cert, X509Chain chain, SslPolicyErrors errors)
            { return cert != null && Security.Equal(Security.Pin(cert), invite.Fingerprint); });
            tls.ReadTimeout = 15000; tls.WriteTimeout = 15000;
            tls.AuthenticateAsClient("lume-remote", null, SslProtocols.None, false); Security.CheckTls(tls);
            using (X509Certificate2 identity = new X509Certificate2(tls.RemoteCertificate))
                ProtocolVersion = identity.GetNameInfo(X509NameType.SimpleName, false) == "Lume Remote Session v4" ? 4 : identity.GetNameInfo(X509NameType.SimpleName, false) == "Lume Remote Session v3" ? 3 : 2;
            wire = new Wire(tls);
            wire.Send(Kind.Auth, delegate(BinaryWriter w) { w.Write(ProtocolVersion); Wire.Text(w, invite.Secret); Wire.Text(w, name); });
            Report(ConnectionStage.RequestingApproval, progress);
            tls.ReadTimeout = 75000;
            using (Packet packet = wire.Read(2048))
            {
                if (packet.Kind == Kind.Denied) { string error = packet.Text(1024); packet.End(); throw new AuthenticationException(error); }
                if (packet.Kind != Kind.Accepted) throw new InvalidDataException("Unexpected handshake response.");
                CanControl = packet.Reader.ReadBoolean(); RemoteName = packet.Text(256);
                int width = packet.Reader.ReadInt32(), height = packet.Reader.ReadInt32();
                if (packet.Reader.ReadInt32() != ProtocolVersion) throw new InvalidDataException("The host returned a different protocol version.");
                SourceRefresh = packet.Reader.ReadInt32(); bool allowFiles = ProtocolVersion >= 3 && packet.Reader.ReadBoolean();
                if (ProtocolVersion >= 4) Capabilities = (SessionCapabilities)packet.Reader.ReadUInt64(); packet.End(); SourceWidth = width; SourceHeight = height;
                if (allowFiles && !CanControl) throw new InvalidDataException("View-only sessions cannot grant file access.");
                if (allowFiles) Files = new FileTransfer(wire, false, new RemoteFileAccess(null, FileResumeKey), Abort, ProtocolVersion >= 4 && (Capabilities & SessionCapabilities.Folders) != 0, ProtocolVersion >= 4 && (Capabilities & SessionCapabilities.FileResume) != 0, ProtocolVersion >= 4 && (Capabilities & SessionCapabilities.NetworkFolders) != 0);
                if (width < 1 || height < 1 || width > 32768 || height > 32768) throw new InvalidDataException("Invalid remote desktop size.");
                if (SourceRefresh < 1 || SourceRefresh > 1000) throw new InvalidDataException("Invalid source refresh rate.");
            }
            if (ProtocolVersion >= 4) Tools = new SessionTools(wire, Abort, async delegate(SessionTool op, Packet body)
            {
                if (op != SessionTool.Chat || (Capabilities & SessionCapabilities.Chat) == 0) throw new InvalidDataException("Unexpected host action.");
                string value = body.Text(8192); body.End(); var handler = ChatReceived;
                if (handler == null) throw new InvalidOperationException("The viewer chat is not available.");
                await handler(value).ConfigureAwait(false); return SessionTools.Payload();
            });
            tls.ReadTimeout = 30000;
            if (Tools != null && (Capabilities & SessionCapabilities.PreferPortableImages) != 0 && (Capabilities & SessionCapabilities.PortableImages) != 0)
                Tools.Request(SessionTool.PortableImages, delegate(BinaryWriter w) { w.Write(true); }).ContinueWith(delegate(Task<byte[]> done) { if (done.IsFaulted) { var observed = done.Exception; Abort(observed.GetBaseException()); } }, TaskContinuationOptions.OnlyOnFaulted);
            lock (lifetime)
            {
                if (disposed) throw new OperationCanceledException();
                heartbeat = new SessionHeartbeat(Ping, Abort);
            }
            Report(ConnectionStage.Approved, progress);
        }
        public void Receive(Action<Packet> onFrame, Action<string> onNotice, Action<int, int, int> onCursor = null)
        {
            while (!disposed)
            {
                using (Packet packet = wire.Read(Wire.MaxPacket))
                {
                    switch (packet.Kind)
                    {
                        case Kind.Frame: FrameEpoch = ProtocolVersion >= 4 ? packet.Reader.ReadInt32() : 1; if (FrameEpoch < 1) throw new InvalidDataException("Invalid frame monitor."); onFrame(packet); break;
                        case Kind.Cursor:
                            int x = packet.Reader.ReadInt32(), y = packet.Reader.ReadInt32(), shape = packet.Reader.ReadInt32(); packet.End();
                            if (x < 0 || y < 0 || x > 65535 || y > 65535 || shape < 0 || shape > 4) throw new InvalidDataException("Invalid cursor state.");
                            if (onCursor != null) onCursor(x, y, shape); break;
                        case Kind.Pong:
                            long stamp = packet.Reader.ReadInt64(); packet.End();
                            RttMilliseconds = (int)Math.Max(0, Math.Min(60000, (DateTime.UtcNow.Ticks - stamp) / TimeSpan.TicksPerMillisecond)); break;
                        case Kind.Notice: string message = packet.Text(2048); packet.End(); onNotice(message); break;
                        case Kind.QualityInfo:
                            StreamQuality quality = StreamQuality.Read(packet.Reader, ProtocolVersion); int streamWidth = packet.Reader.ReadInt32(), streamHeight = packet.Reader.ReadInt32(), sourceHz = packet.Reader.ReadInt32(); packet.End();
                            if (streamWidth < 1 || streamHeight < 1 || streamWidth > StreamQuality.MaxDimension || streamHeight > StreamQuality.MaxDimension || (long)streamWidth * streamHeight > StreamQuality.MaxPixels || sourceHz < 1 || sourceHz > 1000) throw new InvalidDataException("Invalid stream quality response.");
                            CurrentQuality = quality; StreamWidth = streamWidth; StreamHeight = streamHeight; SourceRefresh = sourceHz; break;
                        case Kind.StreamMetrics:
                            if (ProtocolVersion < 3) throw new InvalidDataException("Stream metrics were not negotiated.");
                            StreamMetrics metrics = StreamMetrics.Read(packet.Reader); packet.End(); Metrics = metrics; break;
                        case Kind.Goodbye: packet.End(); return;
                        case Kind.Audio:
                            if ((Capabilities & SessionCapabilities.Audio) == 0) throw new InvalidDataException("Audio was not negotiated.");
                            int generation = packet.Reader.ReadInt32(), length = packet.Reader.ReadInt32();
                            if (length < 5 || length > AudioBlock.Maximum + 256 || length != packet.Reader.BaseStream.Length - packet.Reader.BaseStream.Position) throw new InvalidDataException("Invalid audio packet.");
                            byte[] samples = packet.Reader.ReadBytes(length); packet.End(); SessionAudio target = Volatile.Read(ref audio);
                            if (target != null && generation == Volatile.Read(ref audioGeneration)) { byte[] pcm = AudioBlock.Decode(samples); target.ReceivePcm(pcm); var recordedAudio = SystemAudioReceived; if (recordedAudio != null) recordedAudio(pcm); } break;
                        case Kind.Voice:
                            if ((Capabilities & SessionCapabilities.Voice) == 0) throw new InvalidDataException("Voice was not negotiated."); int callGeneration; byte[] voiceBlock = SessionVoice.Read(packet, out callGeneration); SessionVoice call = Volatile.Read(ref voice); if (call != null && callGeneration == Volatile.Read(ref voiceGeneration)) call.Receive(voiceBlock); break;
                        case Kind.VoiceEnded:
                            if ((Capabilities & SessionCapabilities.Voice) == 0) throw new InvalidDataException("Voice was not negotiated."); int endedGeneration = packet.Reader.ReadInt32(); packet.End(); if (endedGeneration < 1) throw new InvalidDataException("Invalid voice generation."); if (endedGeneration == Volatile.Read(ref voiceGeneration)) { SessionVoice endedCall = Interlocked.Exchange(ref voice, null); if (endedCall != null) endedCall.Dispose(); onNotice("Voice call ended. The microphone is off."); } break;
                        case Kind.Tools:
                        case Kind.ToolReply:
                            if (Tools == null) throw new InvalidDataException("Session tools were not negotiated."); Tools.Handle(packet); break;
                        case Kind.Files:
                            if (Files == null) throw new InvalidDataException("File access was not negotiated."); Files.Handle(packet); break;
                        default: throw new InvalidDataException("Unexpected host packet.");
                    }
                }
            }
        }
        public void Ack(int sequence) { wire.Send(Kind.Ack, delegate(BinaryWriter w) { w.Write(sequence); }); }
        public void SetQuality(StreamQuality quality)
        {
            if (disposed || wire == null) throw new InvalidOperationException("Connect before changing quality."); quality.Validate();
            if (quality.Video && (ProtocolVersion < 3 || !VideoDecoder.Available())) { quality = quality.Copy(); quality.Video = false; quality.Lossless = true; }
            StreamQuality selected = quality; wire.Send(Kind.Quality, delegate(BinaryWriter writer) { selected.Write(writer, ProtocolVersion); });
        }
        public void Ping() { if (!disposed && wire != null) wire.Send(Kind.Ping, delegate(BinaryWriter w) { w.Write(DateTime.UtcNow.Ticks); }); }
        public void Input(byte action, int a, int b) { Input(action, a, b, MonitorEpoch); }
        public void Input(byte action, int a, int b, int expectedEpoch)
        {
            if (CanControl && !disposed && expectedEpoch != 0 && expectedEpoch == MonitorEpoch)
                wire.Send(Kind.Input, delegate(BinaryWriter w) { if (ProtocolVersion >= 4) w.Write(expectedEpoch); w.Write(action); w.Write(a); w.Write(b); });
        }
        public void Release() { if (wire != null && !disposed) wire.Send(Kind.Release, null); }
        public void SendClipboard(string text)
        {
            if (!CanControl) throw new InvalidOperationException("This session is view only.");
            if (System.Text.Encoding.UTF8.GetByteCount(text) > 262144) throw new InvalidOperationException("Clipboard text must be smaller than 256 KiB per action.");
            wire.Send(Kind.Clipboard, delegate(BinaryWriter w) { Wire.Text(w, text); });
        }
        public async Task Annotate(Point[] points, int epoch)
        {
            Require(SessionCapabilities.Annotations); if (points.Length > 128) throw new InvalidOperationException("Annotation is too long.");
            using (Packet response = new Packet(await Tools.Request(SessionTool.Annotation, delegate(BinaryWriter w) { w.Write(epoch); w.Write(points.Length); foreach (Point point in points) { w.Write(point.X); w.Write(point.Y); } }).ConfigureAwait(false))) response.End();
        }
        public async Task Power(PowerAction action)
        {
            Require(SessionCapabilities.Power);
            using (Packet response = new Packet(await Tools.Request(SessionTool.Power, delegate(BinaryWriter w) { w.Write((byte)action); }).ConfigureAwait(false))) response.End();
        }
        public async Task SetAudio(bool enabled)
        {
            Require(SessionCapabilities.Audio); SessionAudio previous = Interlocked.Exchange(ref audio, null); if (previous != null) previous.Dispose();
            int generation = Interlocked.Increment(ref audioGeneration);
            SessionAudio next = null;
            try
            {
                if (enabled) { next = new SessionAudio(true, null, delegate { }); await next.Ready.ConfigureAwait(false); lock (lifetime) { if (disposed) throw new OperationCanceledException(); audio = next; } }
                using (Packet response = new Packet(await Tools.Request(SessionTool.Audio, delegate(BinaryWriter w) { w.Write(enabled); w.Write(generation); }).ConfigureAwait(false))) response.End();
            }
            catch { if (next != null) next.Dispose(); Interlocked.CompareExchange(ref audio, null, next); throw; }
        }
        public async Task SetVoice(bool enabled)
        {
            Require(SessionCapabilities.Voice); SessionVoice previous = Interlocked.Exchange(ref voice, null); if (previous != null) previous.Dispose(); int generation = Interlocked.Increment(ref voiceGeneration); SessionVoice next = null;
            try
            {
                if (enabled)
                {
                    next = new SessionVoice(delegate(byte[] bytes) { if (!disposed && generation == Volatile.Read(ref voiceGeneration)) wire.Send(Kind.Voice, delegate(BinaryWriter w) { w.Write(generation); w.Write(bytes.Length); w.Write(bytes); }); }, delegate { if (generation != Volatile.Read(ref voiceGeneration)) return; var current = Interlocked.CompareExchange(ref voice, null, next); if (current == next && next != null) next.Dispose(); Task cleanup = Tools.Request(SessionTool.Voice, delegate(BinaryWriter w) { w.Write(false); w.Write(generation); }); cleanup.ContinueWith(delegate(Task done) { var observed = done.Exception; }, TaskContinuationOptions.OnlyOnFaulted); });
                    await next.PlaybackReady.ConfigureAwait(false); lock (lifetime) { if (disposed) throw new OperationCanceledException(); voice = next; }
                }
                using (Packet reply = new Packet(await Tools.Request(SessionTool.Voice, delegate(BinaryWriter w) { w.Write(enabled); w.Write(generation); }).ConfigureAwait(false))) reply.End();
                if (enabled) { if (voice != next) throw new OperationCanceledException("The remote microphone was stopped."); await next.StartMicrophone().ConfigureAwait(false); }
            }
            catch
            {
                if (next != null) next.Dispose(); Interlocked.CompareExchange(ref voice, null, next);
                if (enabled && !disposed) { Task cleanup = Tools.Request(SessionTool.Voice, delegate(BinaryWriter w) { w.Write(false); w.Write(generation); }); Task observation = cleanup.ContinueWith(delegate(Task done) { var observed = done.Exception; }, TaskContinuationOptions.OnlyOnFaulted); }
                throw;
            }
        }
        public async Task SetClipboardText(string text)
        {
            Require(SessionCapabilities.ClipboardSync);
            if (System.Text.Encoding.UTF8.GetByteCount(text) > 262144) throw new InvalidOperationException("Clipboard text exceeds 256 KiB.");
            using (Packet response = new Packet(await Tools.Request(SessionTool.ClipboardWrite, delegate(BinaryWriter w) { Wire.Text(w, text); }).ConfigureAwait(false))) response.End();
        }
        public async Task<string> GetClipboard()
        {
            Require(SessionCapabilities.ClipboardRead);
            using (Packet p = new Packet(await Tools.Request(SessionTool.ClipboardRead).ConfigureAwait(false))) { string text = p.Text(262144); p.End(); return text; }
        }
        public async Task<RemoteMonitor[]> GetMonitors()
        {
            Require(SessionCapabilities.Monitors);
            using (Packet p = new Packet(await Tools.Request(SessionTool.Monitors).ConfigureAwait(false)))
            {
                int count = p.Reader.ReadInt32(); if (count < 1 || count > 32) throw new InvalidDataException("Invalid display count.");
                RemoteMonitor[] result = new RemoteMonitor[count]; for (int i = 0; i < count; i++) result[i] = RemoteMonitor.Read(p); p.End(); return result;
            }
        }
        public async Task SelectMonitor(string id)
        {
            Require(SessionCapabilities.Monitors);
            Interlocked.Exchange(ref monitorEpoch, 0); await Task.Run((Action)Release).ConfigureAwait(false);
            try
            {
                using (Packet p = new Packet(await Tools.Request(SessionTool.SelectMonitor, delegate(BinaryWriter w) { Wire.Text(w, id); }).ConfigureAwait(false)))
                {
                    int epoch = p.Reader.ReadInt32(), width = p.Reader.ReadInt32(), height = p.Reader.ReadInt32(), hz = p.Reader.ReadInt32(); p.End();
                    if (epoch < 2 || width < 1 || height < 1 || width > 32768 || height > 32768 || hz < 1 || hz > 1000) throw new InvalidDataException("Invalid monitor selection.");
                    SourceWidth = width; SourceHeight = height; SourceRefresh = hz; Interlocked.Exchange(ref monitorEpoch, epoch);
                }
            }
            catch { throw; } // Keep input disabled after an ambiguous/failed switch; choose a display again.
        }
        public async Task SendChat(string text)
        {
            Require(SessionCapabilities.Chat); if (System.Text.Encoding.UTF8.GetByteCount(text) > 8192) throw new InvalidOperationException("Keep chat messages below 8 KiB.");
            using (Packet p = new Packet(await Tools.Request(SessionTool.Chat, delegate(BinaryWriter w) { Wire.Text(w, text); }).ConfigureAwait(false))) p.End();
        }
        public void Require(SessionCapabilities feature)
        { if (disposed || Tools == null || (Capabilities & feature) == 0) throw new InvalidOperationException("This action is unavailable in this session. Update both PCs and check session permissions."); }
        public void Abort(Exception error) { Interlocked.CompareExchange(ref failure, error, null); Dispose(); }
        public void Dispose()
        {
            lock (lifetime) { disposed = true; if (heartbeat != null) { heartbeat.Dispose(); heartbeat = null; } }
            if (client != null) client.Close(); if (peerStream != null) peerStream.Dispose();
            if (Files != null) Files.Dispose(); if (Tools != null) Tools.Dispose(); SessionAudio sound = Interlocked.Exchange(ref audio, null); if (sound != null) sound.Dispose();
            SessionVoice call = Interlocked.Exchange(ref voice, null); if (call != null) call.Dispose();
        }
    }
}
