using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Drawing;
using System.Drawing.Imaging;
using System.IO;
using System.Security.Cryptography;
using System.Reflection;
using System.Threading;
using System.Threading.Tasks;
using System.Web.Script.Serialization;
using LumeRemote;

// This separate executable uses production protocol code, never production UI consent.
// Only synthetic frames, injected input/clipboard and owned files are permitted.
internal static class TwoPcScenario
{
    static readonly object gate = new object();
    static readonly Dictionary<string, object> receipt = new Dictionary<string, object>();
    static readonly Stopwatch clock = Stopwatch.StartNew();
    static string root, role, mode, stage = "starting";
    static int approvals, sources, captures, inputs, reads, writes;
    static string clipboard = "Lume two-PC synthetic host clipboard";
    const string GuestClipboard = "Lume two-PC synthetic viewer clipboard";

    static void Record(string key, object value) { lock (gate) receipt[key] = value; }
    sealed class PhaseProbe : IDisposable
    {
        readonly Timer timer;
        readonly PeerTransport peer;
        readonly List<Dictionary<string, object>> trace = new List<Dictionary<string, object>>();
        string previous;
        public PhaseProbe(PeerTransport transport)
        {
            peer = transport;
            string expected = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "datachannel.dll"), loaded = null;
            foreach (ProcessModule module in Process.GetCurrentProcess().Modules) if (module.ModuleName.Equals("datachannel.dll", StringComparison.OrdinalIgnoreCase)) loaded = module.FileName;
            Check(loaded != null && Path.GetFullPath(loaded).Equals(Path.GetFullPath(expected), StringComparison.OrdinalIgnoreCase), "native module provenance");
            Record("NativeSha256", Hash(File.ReadAllBytes(loaded)));
            timer = new Timer(delegate { Observe(); }, null, 0, 1000);
        }
        void Observe()
        {
            lock (gate)
            {
                bool checking = Flag("checking"), routed = Flag("routed"), secured = Flag("secured"), opened = Flag("opened"), ended = Flag("ended");
                string phase = peer.Phase, current = phase + checking + routed + secured + opened + ended;
                receipt["PeerChecking"] = checking; receipt["PeerRouted"] = routed; receipt["PeerSecured"] = secured;
                receipt["PeerOpened"] = opened; receipt["PeerEnded"] = ended; receipt["PeerPhase"] = phase;
                receipt["PeerCanRead"] = peer.CanRead;
                if (current != previous)
                {
                    trace.Add(new Dictionary<string, object> { { "Seconds", Math.Round(clock.Elapsed.TotalSeconds, 3) }, { "Checking", checking }, { "Routed", routed }, { "Secured", secured }, { "Opened", opened }, { "Ended", ended }, { "Phase", phase } });
                    receipt["PhaseTrace"] = trace; previous = current; Status(stage);
                }
            }
        }
        bool Flag(string name) { return (bool)typeof(PeerTransport).GetField(name, BindingFlags.Instance | BindingFlags.NonPublic).GetValue(peer); }
        public void Dispose() { using (ManualResetEvent joined = new ManualResetEvent(false)) { timer.Dispose(joined); joined.WaitOne(); } }
    }

    static void Status(string value)
    {
        lock (gate)
        {
            stage = value; receipt["Stage"] = stage; receipt["Seconds"] = Math.Round(clock.Elapsed.TotalSeconds, 3);
            receipt["Approvals"] = Volatile.Read(ref approvals); receipt["Sources"] = Volatile.Read(ref sources);
            receipt["Captures"] = Volatile.Read(ref captures); receipt["InjectedInput"] = Volatile.Read(ref inputs);
            receipt["ClipboardReads"] = Volatile.Read(ref reads); receipt["ClipboardWrites"] = Volatile.Read(ref writes);
            string path = Path.Combine(root, "status.json"), temp = path + ".new";
            File.WriteAllText(temp, new JavaScriptSerializer().Serialize(receipt));
            if (File.Exists(path)) File.Replace(temp, path, null); else File.Move(temp, path);
        }
    }
    static void Check(bool test, string label) { if (!test) throw new InvalidOperationException(label); }
    static T Complete<T>(Task<T> task, int seconds)
    {
        if (!task.Wait(TimeSpan.FromSeconds(seconds))) throw new TimeoutException();
        return task.GetAwaiter().GetResult();
    }
    static void Complete(Task task, int seconds)
    {
        if (!task.Wait(TimeSpan.FromSeconds(seconds))) throw new TimeoutException();
        task.GetAwaiter().GetResult();
    }
    static string PrivateRead(string path, int seconds)
    {
        Stopwatch wait = Stopwatch.StartNew();
        while (!File.Exists(path)) { if (wait.Elapsed.TotalSeconds > seconds) throw new TimeoutException(); Thread.Sleep(100); }
        string text = File.ReadAllText(path); File.Delete(path); return text;
    }
    static void PrivateWrite(string path, string value)
    {
        string temp = path + ".new"; File.WriteAllText(temp, value); File.Move(temp, path);
    }
    static byte[] Payload(int seed) { byte[] bytes = new byte[65536]; new Random(seed).NextBytes(bytes); return bytes; }
    static string Hash(byte[] bytes) { using (SHA256 hash = SHA256.Create()) return BitConverter.ToString(hash.ComputeHash(bytes)).Replace("-", ""); }
    static bool Match(string path, int seed) { return File.Exists(path) && Hash(File.ReadAllBytes(path)) == Hash(Payload(seed)); }

    sealed class SyntheticScreen : IScreenSource
    {
        readonly Bitmap image = new Bitmap(640, 360, PixelFormat.Format32bppRgb);
        public Rectangle Bounds { get { return new Rectangle(0, 0, 640, 360); } }
        public Bitmap Capture()
        {
            Check(Volatile.Read(ref approvals) == 1, "capture before fixture approval");
            int count = Interlocked.Increment(ref captures);
            using (Graphics graphics = Graphics.FromImage(image)) graphics.Clear(Color.FromArgb(12, 30, 60 + count % 100));
            return image;
        }
        public void Dispose() { image.Dispose(); }
    }

    static void Host()
    {
        string files = Path.Combine(root, "files"); Directory.CreateDirectory(files);
        File.WriteAllBytes(Path.Combine(files, "host-payload.bin"), Payload(20260930));
        using (ManualResetEvent ended = new ManualResetEvent(false))
        using (ManualResetEvent replyReady = new ManualResetEvent(false))
        using (PeerTransport peer = new PeerTransport())
        using (PhaseProbe probe = new PhaseProbe(peer))
        using (HostService host = new HostService(
            delegate { Check(Volatile.Read(ref approvals) == 1, "source before fixture approval"); Interlocked.Increment(ref sources); return new SyntheticScreen(); },
            new Profile { Name = "Synthetic fixture", MaxWidth = 640, Fps = 5, Quality = 80, Lossless = true }, true,
            delegate { Interlocked.Increment(ref approvals); return true; }, delegate { }, delegate { },
            delegate(string value) { Interlocked.Exchange(ref clipboard, value); Interlocked.Increment(ref writes); return Task.FromResult(true); },
            delegate { return new RemoteFileAccess(); },
            delegate { Interlocked.Increment(ref reads); return Task.FromResult(Volatile.Read(ref clipboard)); },
            allowClipboardSync: true))
        {
            host.InputFactory = delegate(Rectangle bounds) { return new InputController(bounds, delegate { Interlocked.Increment(ref inputs); }); };
            host.PeerEnded += delegate { ended.Set(); };
            host.StartPeer(); Status("creating-offer");
            PeerSignal offer = PeerSignal.Offer(host.Invite, peer.CreateOffer()), reply = null;
            SignalBroker broker = null;
            try
            {
                if (mode == "automatic") broker = Complete(GuestRendezvous.Listen(offer, delegate(PeerSignal value) { reply = value; replyReady.Set(); }), 30);
                PrivateWrite(Path.Combine(root, "private-offer.tmp"), offer.ToString()); Status("offer-ready");
                if (mode == "automatic") { Check(replyReady.WaitOne(90000), "automatic reply timeout"); Record("AutomaticReplyReceived", true); }
                else reply = PeerSignal.Parse(PrivateRead(Path.Combine(root, "private-reply.tmp"), 230));
                Status("verifying-reply"); offer.VerifyReply(reply); Record("ReplyVerified", true);
                peer.AcceptAnswer(reply.Sdp); Status("waiting-route"); peer.WaitReady(90000);
                Record("Route", peer.RouteSummary()); Status("secure-channel-ready"); host.AcceptPeer(peer);
                Check(ended.WaitOne(120000), "viewer completion timeout"); Status("checking-owned-effects");
                Check(approvals == 1 && sources == 1 && captures >= 5, "frame authorization counts");
                Check(inputs == 2 && writes == 1 && reads == 2 && clipboard == GuestClipboard, "input/clipboard effects");
                Check(Match(Path.Combine(files, "viewer-payload.bin"), 20260931), "uploaded file mismatch");
                Record("UploadedSha256", Hash(File.ReadAllBytes(Path.Combine(files, "viewer-payload.bin"))));
                Record("SyntheticOnly", true); Record("Passed", true); Status("passed");
            }
            finally { if (broker != null) broker.Dispose(); }
        }
    }

    static void Viewer(string remoteRoot, int delaySeconds, int idleSeconds)
    {
        string upload = Path.Combine(root, "viewer-payload.bin"), downloads = Path.Combine(root, "downloads");
        File.WriteAllBytes(upload, Payload(20260931)); Directory.CreateDirectory(downloads);
        using (PeerTransport peer = new PeerTransport())
        using (PhaseProbe probe = new PhaseProbe(peer))
        using (ViewerConnection viewer = new ViewerConnection())
        using (FrameDecoder decoder = new FrameDecoder())
        using (ManualResetEvent enoughFrames = new ManualResetEvent(false))
        {
            Status("reading-offer"); PeerSignal offer = PeerSignal.Parse(PrivateRead(Path.Combine(root, "private-offer.tmp"), 30));
            bool beforeAnswer = Environment.GetEnvironmentVariable("LUME_FIXTURE_DELAY_BEFORE_ANSWER") == "1";
            Record("ReplyDelaySeconds", delaySeconds); Record("DelayBeforeAnswer", beforeAnswer);
            if (delaySeconds > 0 && beforeAnswer) { Status("waiting-before-answer"); Thread.Sleep(delaySeconds * 1000); }
            Status("creating-answer"); PeerSignal reply = offer.Reply(peer.CreateAnswer(offer.Sdp));
            if (delaySeconds > 0 && !beforeAnswer) { Status("holding-reply"); Thread.Sleep(delaySeconds * 1000); }
            if (mode == "automatic") { Status("delivering-reply"); Check(Complete(GuestRendezvous.Deliver(offer, reply, CancellationToken.None), 30), "automatic reply not acknowledged"); Record("AutomaticAcknowledgement", true); }
            else PrivateWrite(Path.Combine(root, "private-reply.tmp"), reply.ToString());
            Status("waiting-route"); peer.WaitReady(90000); Record("Route", peer.RouteSummary());
            viewer.ConnectPeer(peer, offer.Session, "Two-PC synthetic acceptance", delegate(ConnectionStage value) { Status("application-" + value.ToString()); });
            Check(viewer.CanControl && viewer.Files != null, "capability negotiation");
            int frames = 0; Exception receiveError = null;
            Task receiver = Task.Run(delegate
            {
                try { viewer.Receive(delegate(Packet packet) { decoder.Apply(packet); viewer.Ack(decoder.Sequence); if (Interlocked.Increment(ref frames) >= 5) enoughFrames.Set(); }, delegate { }); }
                catch (Exception error) { receiveError = error; enoughFrames.Set(); }
            });
            try
            {
                Status("receiving-synthetic-frames"); Check(enoughFrames.WaitOne(20000) && frames >= 5 && receiveError == null, "frame receipt");
                Check(decoder.Image != null && decoder.Image.Width == 640 && decoder.Image.Height == 360, "frame dimensions");
                Status("injected-input-and-clipboard"); viewer.Input(4, 65, 0); viewer.Input(5, 65, 0);
                Check(Complete(viewer.GetClipboard(), 20) == "Lume two-PC synthetic host clipboard", "host clipboard value");
                Complete(viewer.SetClipboardText(GuestClipboard), 20); Check(Complete(viewer.GetClipboard(), 20) == GuestClipboard, "viewer clipboard write");
                Status("uploading-owned-file"); Complete(viewer.Files.Upload(upload, Path.Combine(remoteRoot, "files"), CancellationToken.None), 45);
                Status("downloading-owned-file"); string download = Complete(viewer.Files.Download(Path.Combine(remoteRoot, "files", "host-payload.bin"), downloads, CancellationToken.None), 45);
                Check(Match(download, 20260930), "downloaded file mismatch"); Record("DownloadedSha256", Hash(File.ReadAllBytes(download)));
                Status("connected-idle-observation"); Record("IdleSeconds", idleSeconds); int before = frames;
                Thread.Sleep(idleSeconds * 1000); Check(viewer.Failure == null && receiveError == null && !receiver.IsCompleted && frames > before, "session did not remain active");
                Record("Frames", frames); Record("SyntheticOnly", true); Record("Passed", true); Status("passed");
            }
            finally { viewer.Dispose(); Check(receiver.Wait(10000), "receiver cleanup timeout"); Record("ReceiverJoined", true); Status(stage); }
        }
    }

    static int Main(string[] args)
    {
        if (args.Length != 6) return 64;
        role = args[0]; mode = args[1]; root = Path.GetFullPath(args[2]);
        Directory.CreateDirectory(root); receipt["Role"] = role; receipt["Mode"] = mode; receipt["Passed"] = false;
        Environment.SetEnvironmentVariable("LUME_MANUAL_GUEST_REPLY", mode == "manual" ? "1" : null);
        using (Timer watchdog = new Timer(delegate { Record("Passed", false); Record("ErrorType", "FixtureWatchdog"); Status("deadline-expired"); Environment.Exit(2); }, null, 420000, Timeout.Infinite))
        {
            try
            {
                if (role == "host") Host(); else if (role == "viewer") Viewer(args[3], Int32.Parse(args[4]), Int32.Parse(args[5])); else return 64;
                Record("ExitCode", 0); Status("passed"); Console.WriteLine("PASS two-PC " + mode + " " + role); return 0;
            }
            catch (Exception error)
            {
                Record("Passed", false); Record("FailedStage", stage); Record("ErrorType", error.GetType().Name);
                Record("ExitCode", 1); Status("failed"); Console.WriteLine("FAIL two-PC " + mode + " " + role + " " + error.GetType().Name); return 1;
            }
            finally
            {
                foreach (string name in new[] { "private-offer.tmp", "private-reply.tmp", "private-offer.tmp.new", "private-reply.tmp.new" })
                    try { File.Delete(Path.Combine(root, name)); } catch { }
            }
        }
    }
}
