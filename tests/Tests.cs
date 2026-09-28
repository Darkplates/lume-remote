using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Drawing;
using System.Drawing.Imaging;
using System.IO;
using System.Net;
using System.Net.Security;
using System.Net.Sockets;
using System.Runtime.InteropServices;
using System.Security.Authentication;
using System.Threading;
using System.Threading.Tasks;
using System.Windows.Forms;
using LumeRemote;

static partial class Tests
{
    static int passed, failed, captured, approved, clipboard;
    static int relayPort;
    [STAThread] static int Main(string[] args)
    {
        Native.SetProcessDPIAware(); Application.EnableVisualStyles(); Control.CheckForIllegalCrossThreadCalls = true;
        if (args.Length == 2 && args[0] == "--portable-fixture") { PortableHostFixture(args[1]); return 0; }
        if (args.Length == 2 && args[0] == "--portable-peer-fixture") { PortablePeerFixture(args[1]); return 0; }
        if (args.Length == 2 && args[0] == "--portable-viewer") { PortableViewerFixture(args[1]); return 0; }
        if (args.Length == 2 && args[0] == "--portable-monitors-fixture") { PortableMonitorsFixture(args[1]); return 0; }
        if (args.Length == 2 && args[0] == "--portable-monitors-viewer") { PortableMonitorsViewer(args[1]); return 0; }
        if (args.Length == 3 && args[0] == "--portable-files-fixture") { PortableFilesFixture(args[1], args[2]); return 0; }
        if (args.Length == 2 && args[0] == "--portable-paired-fixture") { PortablePairedFixture(args[1]); return 0; }
        if (args.Length == 3 && args[0] == "--portable-files-viewer") { PortableFilesViewer(args[1], args[2]); return 0; }
        if (args.Length == 1 && args[0] == "--portable") { PortableChecks(); return failed == 0 ? 0 : 1; }
        if(args.Length==2 && args[0]=="--portable-audio-write"){PortableAudioWrite(args[1]);return 0;}
        if(args.Length==2 && args[0]=="--portable-audio-read"){PortableAudioRead(args[1]);return 0;}
        if(args.Length==2 && args[0]=="--portable-paired-viewer"){PortablePairedViewer(args[1]);return 0;}
        if (args.Length > 0 && args[0] == "--ui") { UiChecks(); Console.WriteLine("RESULT: " + passed + " passed, " + failed + " failed."); return failed == 0 ? 0 : 1; }
        if (args.Length > 0 && args[0] == "--files") { FileClipboardChecks(); Console.WriteLine("RESULT: " + passed + " passed, " + failed + " failed."); return failed == 0 ? 0 : 1; }
        if (args.Length > 0 && args[0] == "--resume") { ResumeChecks(); Console.WriteLine("RESULT: " + passed + " passed, " + failed + " failed."); return failed == 0 ? 0 : 1; }
        if (args.Length > 0 && args[0] == "--printing") { PrintingChecks(); Console.WriteLine("RESULT: " + passed + " passed, " + failed + " failed."); return failed == 0 ? 0 : 1; }
        if (args.Length > 0 && args[0] == "--print-spool") { Run("Owned Windows PDF print job renders through the spooler", PrintToPdfFixture); Console.WriteLine("RESULT: " + passed + " passed, " + failed + " failed."); return failed == 0 ? 0 : 1; }
        if (args.Length > 0 && args[0] == "--files-ui") { Run("Native remote file browser", FileBrowserUi); return failed == 0 ? 0 : 1; }
        if (args.Length == 2 && args[0] == "--ui-preview") { PreviewUi(args[1]); return 0; }
        if (args.Length == 1 && args[0] == "--native-capture") { Run("Real desktop capture reproduces an owned window pixel", NativeCaptureFixture); return failed == 0 ? 0 : 1; }
        if (args.Length > 0 && args[0] == "--connections") { ConnectionChecks(); Console.WriteLine("RESULT: " + passed + " passed, " + failed + " failed."); return failed == 0 ? 0 : 1; }
        if (args.Length == 2 && args[0] == "--idle-soak") { Run("Idle P2P session survives a blocked and minimized UI", delegate { IdleSoak(Int32.Parse(args[1])); }); return failed == 0 ? 0 : 1; }
        if (args.Length > 0 && args[0] == "--video") { VideoChecks(); Console.WriteLine("RESULT: " + passed + " passed, " + failed + " failed."); return failed == 0 ? 0 : 1; }
        if (args.Length > 0 && args[0] == "--video-benchmark") { VideoBenchmark(); return 0; }
        if (args.Length > 0 && args[0] == "--tools") { SessionToolChecks(); Console.WriteLine("RESULT: " + passed + " passed, " + failed + " failed."); return failed == 0 ? 0 : 1; }
        if (args.Length > 0 && args[0] == "--media") { MediaChecks(); Console.WriteLine("RESULT: " + passed + " passed, " + failed + " failed."); return failed == 0 ? 0 : 1; }
        if (args.Length > 0 && args[0] == "--collaboration") { CollaborationChecks(); Console.WriteLine("RESULT: " + passed + " passed, " + failed + " failed."); return failed == 0 ? 0 : 1; }
        bool safe = Array.IndexOf(args, "--safe") >= 0;
        if (args.Length > 0 && args[0] == "--benchmark") { Benchmark(); return 0; }
        if (args.Length > 0 && args[0] == "--profile") { ProfileStages(); return 0; }
        if (args.Length > 0 && args[0] == "--p2p-ui") { Run("Main app P2P approval UI", PeerMainApproval); return failed == 0 ? 0 : 1; }
        if (args.Length > 0 && args[0] == "--quality") { QualityChecks(); Console.WriteLine("RESULT: " + passed + " passed, " + failed + " failed."); return failed == 0 ? 0 : 1; }
        if (args.Length > 0 && args[0] == "--signal") { Run("Encrypted signaling rejects tampering and address substitution", SignalSecurity); Run("Public signaling delivers authenticated encrypted messages", PublicSignaling); Console.WriteLine("RESULT: " + passed + " passed, " + failed + " failed."); return failed == 0 ? 0 : 1; }
        if (args.Length > 0 && args[0] == "--paired") { Run("Protected settings and one-time pairing code", ProtectedSettings); Run("Pair once, reconnect automatically, then revoke the live session", PersistentPairing); Console.WriteLine("RESULT: " + passed + " passed, " + failed + " failed."); return failed == 0 ? 0 : 1; }
        if (args.Length > 0 && args[0] == "--installed-host") { Run("Installed service: real source capture, keyboard, mouse and revocation", InstalledHost); Console.WriteLine("RESULT: " + passed + " passed, " + failed + " failed."); return failed == 0 ? 0 : 1; }
        if (args.Length > 0 && args[0] == "--installed-files") { Run("Installed service transfers files using owner permissions", InstalledFiles); return failed == 0 ? 0 : 1; }
        if (args.Length > 0 && args[0] == "--p2p")
        {
            Run("Native WebRTC layout and ordered binary stream", PeerBytes);
            Run("P2P offer, signed reply and malformed-code limits", PeerCodes);
            Run("P2P pinned TLS delivers acknowledged desktop frames", PeerFrames);
            Run("P2P local rejection prevents capture", PeerDecline);
            Run("P2P wrong certificate pin cannot reach approval", delegate { PeerBadCredentials(true); });
            Run("P2P wrong invitation secret cannot reach approval", delegate { PeerBadCredentials(false); });
            if (args.Length > 1 && args[1] == "--stun")
            {
                Run("Public STUN discovers a server-reflexive candidate", PeerStun);
                Run("Main app P2P exchange displays its real approval dialog", PeerMainApproval);
            }
            Console.WriteLine("RESULT: " + passed + " passed, " + failed + " failed."); return failed == 0 ? 0 : 1;
        }
        if (args.Length == 2 && args[0] == "--render-ui") { RenderUi(args[1]); return 0; }
        int relayArgument = Array.IndexOf(args, "--relay-port"); if (relayArgument >= 0 && relayArgument + 1 < args.Length) relayPort = Int32.Parse(args[relayArgument + 1]);
        ResumeChecks();
        PortableChecks();
        PrintingChecks();
        SessionToolChecks();
        MediaChecks();
        CollaborationChecks();
        VideoChecks();
        FileClipboardChecks();
        ConnectionChecks();
        UiChecks();
        QualityChecks();
        Run("Invitation round trip and secret size", InvitationRoundTrip);
        Run("Invalid invitation and parser fuzz rejected", InvitationFailures);
        Run("TCP failure never advances to host approval", ConnectionRefused);
        Run("Secure approval stages preserve the capture gate", ConnectionStages);
        Run("Network diagnostics verify TLS without approval or capture", DiagnosticProbe);
        Run("Diagnostic reports exclude all invitation secrets", DiagnosticRedaction);
        Run("Viewer displays connection failure instead of waiting for approval", ViewerFailureUi);
        Run("Exact read handles fragmented streams", Fragmented);
        Run("Oversized, truncated and empty packets rejected", PacketLimits);
        Run("Packet text and trailing data validation", PacketText);
        Run("Session certificates have independent pins and private keys", Certificates);
        Run("First frame, unchanged frame and exact dirty tiles", DeltaFrames);
        Run("JPEG header dimensions validated before decode", ImageBounds);
        Run("Lossless Windows codec round trip and malformed blocks", LosslessBlocks);
        Run("Native input layout, mapping and stuck-key release", InputTracking);
        Run("Malformed input rejected before injection", BadInput);
        Run("Wrong certificate pin is rejected before approval", WrongPin);
        Run("Wrong session secret is rejected before capture", WrongSecret);
        Run("Local decline prevents screen capture", Declined);
        Run("Authenticated direct stream decodes and acknowledges frames", DirectFrames);
        Run("View-only session refuses input and clipboard", ViewOnly);
        Run("Repeated authentication failures trigger cooldown", BruteForce);
        Run("Stopping host promptly closes active sockets", StopHost);
        Run("Disconnect allows a new locally approved session", Reconnect);
        if (!safe) { Run("Native owned-window keyboard and mouse integration", NativeInputFixture); Run("Real desktop capture reproduces an owned window pixel", NativeCaptureFixture); }
        else Console.WriteLine("SKIP Physical desktop/input fixtures (--safe). No global input was injected.");
        Run("Native WebRTC layout and ordered binary stream", PeerBytes);
        Run("P2P offer, signed reply and malformed-code limits", PeerCodes);
        Run("P2P pinned TLS delivers acknowledged desktop frames", PeerFrames);
        Run("P2P local rejection prevents capture", PeerDecline);
        Run("P2P wrong certificate pin cannot reach approval", delegate { PeerBadCredentials(true); });
        Run("P2P wrong invitation secret cannot reach approval", delegate { PeerBadCredentials(false); });
        if (relayPort > 0) Run("End-to-end TLS and screen frames through local relay", RelayFrames);
        Console.WriteLine("RESULT: " + passed + " passed, " + failed + " failed.");
        return failed == 0 ? 0 : 1;
    }
    static void Run(string name, Action action)
    {
        try { action(); passed++; Console.WriteLine("PASS " + name); }
        catch (Exception e) { failed++; Console.WriteLine("FAIL " + name + ": " + e); }
    }
    static void Check(bool condition, string message) { if (!condition) throw new Exception(message); }
    static void Reject(Action action) { bool rejected = false; try { action(); } catch { rejected = true; } Check(rejected, "Expected rejection."); }
    static Invitation Sample()
    { return new Invitation { Host = "127.0.0.1", Port = 24816, Secret = Security.Token(32), Room = "", Fingerprint = new string('A', 64) }; }
    static void InvitationRoundTrip()
    {
        Invitation first = Sample(), second = Invitation.Parse(first.ToString());
        Check(first.Host == second.Host && first.Port == second.Port && Security.Equal(first.Secret, second.Secret) && first.Fingerprint == second.Fingerprint, "Invitation mismatch.");
        first.Room = Guid.NewGuid().ToString("N"); Check(Invitation.Parse(first.ToString()).Relay, "Relay lost.");
        Check(!Security.Equal("abc", "abcd") && !Security.Equal("abc", "abd") && Security.Equal("abc", "abc"), "Constant-time equality result.");
    }
    static void InvitationFailures()
    {
        string[] invalid = { "", "https://example.com", "lume://%", new string('x', 5000), "lume://" + Security.Base64(System.Text.Encoding.UTF8.GetBytes("1|0.0.0.0|0|||")) };
        foreach (string value in invalid) Reject(delegate { Invitation.Parse(value); });
        Random random = new Random(777);
        for (int i = 0; i < 500; i++) { byte[] bytes = new byte[random.Next(1, 512)]; random.NextBytes(bytes); Reject(delegate { Invitation.Parse("lume://" + Security.Base64(bytes)); }); }
    }
    static Invitation ClosedEndpoint()
    {
        TcpListener listener = new TcpListener(IPAddress.Loopback, 0); listener.Start();
        Invitation invite = Sample(); invite.Port = ((IPEndPoint)listener.LocalEndpoint).Port; listener.Stop(); return invite;
    }
    static void ConnectionRefused()
    {
        Invitation invite = ClosedEndpoint(); List<ConnectionStage> stages = new List<ConnectionStage>();
        using (ViewerConnection viewer = new ViewerConnection())
        {
            Exception failure = null;
            try { viewer.Connect(invite, "Connection failure test", stages.Add); } catch (Exception error) { failure = error; }
            Check(failure != null && stages.Count == 1 && stages[0] == ConnectionStage.Contacting, "Unreachable host incorrectly reached approval.");
            string message = ConnectionDiagnostics.Failure(viewer.Stage, invite, failure);
            Check(message.Contains(ConnectionDiagnostics.Endpoint(invite)) && message.Contains("approval has not been requested"), "Failure hid its endpoint or approval state.");
            string timeout = ConnectionDiagnostics.Failure(ConnectionStage.Contacting, invite, new TimeoutException());
            Check(timeout.Contains("No TCP response") && timeout.Contains("approval has not been requested"), "Timeout was mislabeled as approval.");
        }
    }
    static void PairPeers(PeerTransport first, PeerTransport second)
    {
        string offer = first.CreateOffer(), answer = second.CreateAnswer(offer); first.AcceptAnswer(answer);
        Task a = Task.Run(delegate { first.WaitReady(15000); }), b = Task.Run(delegate { second.WaitReady(15000); });
        Check(Task.WaitAll(new Task[] { a, b }, 18000), "P2P endpoints did not connect.");
        Console.WriteLine("PEER_ROUTE: " + first.RouteSummary());
    }
    static void PeerBytes()
    {
        Check(Marshal.SizeOf(typeof(Rtc.Configuration)) == 56, "WebRTC Win64 configuration layout is wrong.");
        using (PeerTransport first = new PeerTransport(false)) using (PeerTransport second = new PeerTransport(false))
        {
            PairPeers(first, second); byte[] expected = new byte[700000]; new Random(17).NextBytes(expected);
            Task send = Task.Run(delegate { first.Write(expected, 0, expected.Length); });
            byte[] observed = Wire.ReadExact(second, expected.Length); Check(send.Wait(10000), "P2P sender blocked.");
            Check(Convert.ToBase64String(expected) == Convert.ToBase64String(observed), "P2P stream lost or reordered data.");
            second.Write(new byte[] { 91, 92, 93 }, 0, 3); Check(Wire.ReadExact(first, 3)[2] == 93, "P2P return path failed.");
        }
    }
    static void PeerCodes()
    {
        using (PeerTransport first = new PeerTransport(false)) using (PeerTransport second = new PeerTransport(false))
        {
            PeerSignal offer = PeerSignal.Offer(Sample(), first.CreateOffer()), parsed = PeerSignal.Parse(offer.ToString());
            Check(parsed.Sdp == offer.Sdp && parsed.Session.Secret == offer.Session.Secret, "P2P invitation changed in transit.");
            PeerSignal reply = parsed.Reply(second.CreateAnswer(parsed.Sdp)); offer.VerifyReply(PeerSignal.Parse(reply.ToString()));
            reply.Sdp += "a=x-lume-test:tampered\r\n"; Reject(delegate { offer.VerifyReply(reply); });
            Reject(delegate { PeerSignal.Parse(new string('A', 65537)); }); Reject(delegate { PeerSignal.Parse("lume-p2p://invalid"); });
            Reject(delegate { PeerSignal.ValidateSdp("v=0\r\n"); });
            byte[] bomb = new byte[70000]; using (MemoryStream packed = new MemoryStream())
            {
                using (System.IO.Compression.GZipStream zip = new System.IO.Compression.GZipStream(packed, System.IO.Compression.CompressionMode.Compress, true)) zip.Write(bomb, 0, bomb.Length);
                string code = PeerSignal.OfferPrefix + Security.Base64(packed.ToArray()); Reject(delegate { PeerSignal.Parse(code); });
            }
        }
    }
    static void PeerFrames()
    {
        captured = approved = 0;
        using (HostService host = new HostService(delegate { Interlocked.Increment(ref captured); return new Synthetic(); }, Profile.All[1], false,
            delegate { Interlocked.Increment(ref approved); return true; }, delegate { }, delegate { }))
        using (PeerTransport first = new PeerTransport(false)) using (PeerTransport second = new PeerTransport(false))
        using (ViewerConnection viewer = new ViewerConnection()) using (FrameDecoder decoder = new FrameDecoder())
        {
            host.StartPeer(); PairPeers(first, second); host.AcceptPeer(first); viewer.ConnectPeer(second, host.Invite, "P2P integration test"); int frames = 0;
            Task receiver = Task.Run(delegate
            {
                try { viewer.Receive(delegate(Packet packet) { decoder.Apply(packet); viewer.Ack(decoder.Sequence); if (++frames == 5) viewer.Dispose(); }, delegate { }); }
                catch { if (frames < 5) throw; }
            });
            Check(receiver.Wait(15000), "No complete P2P screen stream.");
            Check(frames == 5 && captured == 1 && approved == 1 && decoder.Image.Width == 640, "P2P frame or approval count failed.");
        }
    }
    static void PeerDecline()
    {
        int sources = 0, requests = 0;
        using (HostService host = new HostService(delegate { sources++; return new Synthetic(); }, Profile.All[1], false,
            delegate { requests++; return false; }, delegate { }, delegate { }))
        using (PeerTransport first = new PeerTransport(false)) using (PeerTransport second = new PeerTransport(false))
        using (ViewerConnection viewer = new ViewerConnection())
        {
            host.StartPeer(); PairPeers(first, second); host.AcceptPeer(first);
            Reject(delegate { viewer.ConnectPeer(second, host.Invite, "P2P decline test"); });
            Check(requests == 1 && sources == 0, "P2P bypassed local consent.");
        }
    }
    static void PeerStun()
    {
        using (PeerTransport peer = new PeerTransport())
        {
            string sdp = peer.CreateOffer(); Check(sdp.Contains(" typ srflx"), "STUN did not discover a public candidate on this PC.");
            Console.WriteLine("STUN: server-reflexive candidate discovered; no address or invitation logged. This does not prove a two-router connection.");
        }
    }
    static void PeerBadCredentials(bool wrongPin)
    {
        int sources = 0, requests = 0;
        using (HostService host = new HostService(delegate { sources++; return new Synthetic(); }, Profile.All[1], false,
            delegate { requests++; return true; }, delegate { }, delegate { }))
        using (PeerTransport first = new PeerTransport(false)) using (PeerTransport second = new PeerTransport(false))
        using (ViewerConnection viewer = new ViewerConnection())
        {
            host.StartPeer(); PairPeers(first, second); host.AcceptPeer(first);
            Invitation bad = Invitation.Parse(host.Invite.ToString()); if (wrongPin) bad.Fingerprint = new string('0', 64); else bad.Secret = Security.Token(32);
            Reject(delegate { viewer.ConnectPeer(second, bad, "P2P invalid credential test"); });
            Check(requests == 0 && sources == 0, "Invalid P2P credentials reached consent or capture.");
        }
    }
    static object Field(object instance, string name)
    { return instance.GetType().GetField(name, System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic).GetValue(instance); }
    static void PumpUntil(Func<bool> done, int milliseconds, string error)
    {
        Stopwatch clock = Stopwatch.StartNew();
        while (!done() && clock.ElapsedMilliseconds < milliseconds) { Application.DoEvents(); Thread.Sleep(10); }
        Check(done(), error);
    }
    static void PeerMainApproval()
    {
        using (MainForm main = new MainForm())
        {
            Exception failure = null;
            main.Shown += async delegate
            {
                try
                {
                    Task starting = (Task)typeof(MainForm).GetMethod("StartSharing", System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic).Invoke(main, null);
                    Check(await Task.WhenAny(starting, Task.Delay(30000)) == starting, "Main app P2P setup did not finish."); await starting;
                    PeerHostForm pairing = null; foreach (Form child in main.OwnedForms) if (child is PeerHostForm) pairing = (PeerHostForm)child;
                    Check(pairing != null, "Main app did not present its P2P reply window.");
                    PeerSignal offer = (PeerSignal)Field(pairing, "offer");
                    PeerViewerForm controlling = new PeerViewerForm(offer); controlling.Show(main);
                    TextBox returnedReply = (TextBox)Field(controlling, "reply");
                    Stopwatch preparation = Stopwatch.StartNew();
                    while (returnedReply.Text.Length == 0 && preparation.ElapsedMilliseconds < 30000) await Task.Delay(20);
                    Check(returnedReply.Text.StartsWith(PeerSignal.ReplyPrefix, StringComparison.Ordinal), "The controlling window did not generate its reply.");
                    TextBox replyField = (TextBox)Field(pairing, "reply");
                    Check(!replyField.InvokeRequired, "The fixture lost its Windows Forms synchronization context.");
                    replyField.Text = returnedReply.Text;
                    bool approvalSeen = false;
                    using (System.Windows.Forms.Timer declineOwnFixture = new System.Windows.Forms.Timer { Interval = 50 })
                    {
                        declineOwnFixture.Tick += delegate
                        {
                            foreach (Form child in main.OwnedForms)
                                if (child is ConsentForm && child.Visible && child.Owner == main && child.Text == "Lume - Connection request")
                                { approvalSeen = true; child.DialogResult = DialogResult.No; child.Close(); break; }
                        };
                        declineOwnFixture.Start(); ((Button)Field(pairing, "apply")).PerformClick();
                        ViewerForm remote = null; Stopwatch connection = Stopwatch.StartNew();
                        while (connection.ElapsedMilliseconds < 20000)
                        {
                            foreach (Form window in Application.OpenForms) if (window is ViewerForm) remote = (ViewerForm)window;
                            if (approvalSeen && remote != null && remote.Text == "Lume - Disconnected") break;
                            await Task.Delay(20);
                        }
                        Check(approvalSeen, "The real host approval dialog did not appear.");
                        Check(controlling.IsDisposed && remote != null && remote.Text == "Lume - Disconnected", "The controlling window did not transfer to a viewer that enforces refusal.");
                        Check(Field(remote, "displayImage") == null, "A real desktop frame appeared after refusal.");
                        remote.Close();
                    }
                    Stopwatch end = Stopwatch.StartNew();
                    while (!((Button)Field(main, "start")).Enabled && end.ElapsedMilliseconds < 8000) await Task.Delay(20);
                    Check(((Button)Field(main, "start")).Enabled && ((TextBox)Field(main, "invitation")).Text.Length == 0, "Main app did not revoke the completed P2P invitation.");
                    Console.WriteLine("REAL_UI: Both P2P windows exchanged codes; viewer opened; local approval displayed; declined; invitation revoked. No desktop was approved for capture.");
                }
                catch (Exception error) { failure = error; }
                finally { main.ExitDashboard(); }
            };
            Application.Run(main);
            if (failure != null) throw failure;
        }
    }
    static void ConnectionStages()
    {
        int sources = 0; List<ConnectionStage> stages = new List<ConnectionStage>();
        using (ManualResetEvent requested = new ManualResetEvent(false))
        using (ManualResetEvent release = new ManualResetEvent(false))
        using (HostService host = new HostService(delegate { Interlocked.Increment(ref sources); return new Synthetic(); }, Profile.All[1], false,
            delegate { requested.Set(); return release.WaitOne(4000); }, delegate { }, delegate { }))
        using (ViewerConnection viewer = new ViewerConnection())
        {
            host.Start(IPAddress.Loopback, 0, "127.0.0.1");
            Task connect = Task.Run(delegate { viewer.Connect(host.Invite, "Approval stage test", delegate(ConnectionStage stage) { lock (stages) stages.Add(stage); }); });
            try
            {
                Check(requested.WaitOne(4000), "Approval request never reached the authenticated host.");
                Check(sources == 0 && !connect.IsCompleted, "Capture or connection completed before consent.");
                release.Set(); Check(connect.Wait(4000), "Approved connection did not complete.");
                Check(stages.Count == 4 && stages[0] == ConnectionStage.Contacting && stages[1] == ConnectionStage.Securing &&
                    stages[2] == ConnectionStage.RequestingApproval && stages[3] == ConnectionStage.Approved, "Connection phases were out of order.");
                Check(sources == 1, "Approved desktop was not opened.");
            }
            finally { release.Set(); }
        }
    }
    static void DiagnosticProbe()
    {
        using (HostService host = NewHost(false, true))
        {
            string result = ConnectionDiagnostics.Probe(host.Invite);
            Check(result.StartsWith("PASS:"), "Diagnostic could not verify the reachable TLS host: " + result);
            Check(captured == 0 && approved == 0, "Diagnostic requested approval or captured the desktop.");
            Invitation wrong = Invitation.Parse(host.Invite.ToString()); wrong.Fingerprint = new string('0', 64);
            Check(ConnectionDiagnostics.Probe(wrong).StartsWith("FAIL:"), "Diagnostic accepted the wrong certificate pin.");
            Check(captured == 0 && approved == 0, "Failed diagnostic crossed the consent gate.");
        }
    }
    static void DiagnosticRedaction()
    {
        Invitation invite = Sample(); invite.Room = Guid.NewGuid().ToString("N");
        string report = ConnectionDiagnostics.LocalReport(invite, false);
        Check(!report.Contains(invite.Secret) && !report.Contains(invite.Room) && !report.Contains(invite.Fingerprint) && !report.Contains(invite.ToString()), "Diagnostic leaked invitation material.");
        Check(report.Contains(ConnectionDiagnostics.Endpoint(invite)), "Diagnostic omitted the target endpoint.");
        Check(ConnectionDiagnostics.SameSubnet(IPAddress.Parse("192.168.1.4"), IPAddress.Parse("192.168.1.9"), IPAddress.Parse("255.255.255.0")), "Same-subnet classification failed.");
        Check(!ConnectionDiagnostics.SameSubnet(IPAddress.Parse("192.168.1.4"), IPAddress.Parse("192.168.2.9"), IPAddress.Parse("255.255.255.0")), "Different subnets were conflated.");
    }
    static void ViewerFailureUi()
    {
        using (ViewerForm viewer = new ViewerForm(ClosedEndpoint()))
        {
            viewer.Show(); Stopwatch wait = Stopwatch.StartNew();
            while (viewer.Text != "Lume - Disconnected" && wait.ElapsedMilliseconds < 6000) { Application.DoEvents(); Thread.Sleep(20); }
            RemoteCanvas canvas = (RemoteCanvas)Field(viewer, "canvas");
            Check(canvas != null && canvas.SessionEnded && canvas.StatusMessage.Contains("approval has not been requested"), "Viewer still claims to await approval after TCP failure.");
            Check(viewer.Text == "Lume - Disconnected", "Viewer did not end the failed attempt.");
            viewer.Close();
        }
    }
    static void Fragmented()
    {
        byte[] expected = new byte[1000]; new Random(1).NextBytes(expected);
        using (Stream stream = new FragmentStream(expected)) Check(Convert.ToBase64String(Wire.ReadExact(stream, expected.Length)) == Convert.ToBase64String(expected), "Fragmented data changed.");
    }
    static void PacketLimits()
    {
        foreach (int size in new int[] { 0, -1, Wire.MaxPacket + 1, Int32.MaxValue })
        using (MemoryStream data = new MemoryStream(BitConverter.GetBytes(size))) using (Wire wire = new Wire(data)) Reject(delegate { wire.Read(Wire.MaxPacket); });
        using (MemoryStream data = new MemoryStream(new byte[] { 5, 0, 0, 0, 1 })) using (Wire wire = new Wire(data)) Reject(delegate { wire.Read(100); });
    }
    static void PacketText()
    {
        using (Packet p = new Packet(Wire.Message(Kind.Notice, delegate(BinaryWriter w) { Wire.Text(w, "Hello \u20ac"); }))) { Check(p.Text(64) == "Hello \u20ac", "UTF-8 mismatch."); p.End(); }
        using (Packet p = new Packet(Wire.Message(Kind.Notice, delegate(BinaryWriter w) { w.Write(Int32.MaxValue); }))) Reject(delegate { p.Text(128); });
        using (Packet p = new Packet(new byte[] { (byte)Kind.Ping, 7 })) Reject(delegate { p.End(); });
    }
    static void Certificates()
    {
        using (var a = Security.Certificate()) using (var b = Security.Certificate())
        { Check(a.HasPrivateKey && b.HasPrivateKey, "Missing private key."); Check(Security.Pin(a).Length == 64 && Security.Pin(a) != Security.Pin(b), "Certificate pin reused."); }
    }
    static void DeltaFrames()
    {
        using (Bitmap image = new Bitmap(320, 240, PixelFormat.Format32bppRgb))
        using (FrameDecoder decoder = new FrameDecoder())
        {
            using (Graphics g = Graphics.FromImage(image)) g.Clear(Color.FromArgb(20, 50, 90));
            FrameEncoder encoder = new FrameEncoder(); byte[] first = encoder.Encode(image, 1, 90, false);
            using (Packet p = new Packet(first)) decoder.Apply(p);
            Check(decoder.Image.Width == 320 && Math.Abs(decoder.Image.GetPixel(150, 150).B - 90) < 5, "Frame did not decode.");
            Check(encoder.Encode(image, 2, 90, false) == null, "Static frame transmitted again.");
            using (Graphics g = Graphics.FromImage(image)) g.FillRectangle(Brushes.White, 100, 100, 10, 10);
            byte[] patch = encoder.Encode(image, 2, 90, false);
            using (Packet p = new Packet(patch)) decoder.Apply(p);
            Check(decoder.Image.GetPixel(104, 104).R > 235, "Delta patch missing.");
            Check(Math.Abs(decoder.Image.GetPixel(250, 200).B - 90) < 5, "Unchanged area corrupted.");
            Check(patch.Length < first.Length, "Small patch did not save bandwidth.");
        }
    }
    static void ImageBounds()
    {
        using (Bitmap image = new Bitmap(320, 240))
        using (MemoryStream bytes = new MemoryStream())
        { image.Save(bytes, ImageFormat.Jpeg); byte[] jpg = bytes.ToArray(); FrameDecoder.ValidateJpeg(jpg, 320, 240); Reject(delegate { FrameDecoder.ValidateJpeg(jpg, 10, 10); }); }
        using (FrameDecoder decoder = new FrameDecoder())
        using (Packet packet = new Packet(Wire.Message(Kind.Frame, delegate(BinaryWriter w) { w.Write(1); w.Write(100000); w.Write(100000); w.Write(1); }))) Reject(delegate { decoder.Apply(packet); });
        Reject(delegate { FrameDecoder.ValidateJpeg(new byte[] { 255, 216, 255, 217 }, 1, 1); });
    }
    static void InputTracking()
    {
        List<InputController.INPUT> sent = new List<InputController.INPUT>();
        using (InputController input = new InputController(SystemInformation.VirtualScreen, sent.Add))
        { input.Apply(0, 65535, 65535); input.Apply(4, 65, 0); input.Apply(1, 0, 0); input.Release(); }
        Check(Marshal.SizeOf(typeof(InputController.INPUT)) == 40, "Win64 input layout is wrong.");
        Check(sent.Count == 5 && sent[0].u.mouse.dx == 65535 && sent[0].u.mouse.dy == 65535, "Pointer or release count mismatch.");
        Check(sent[3].u.keyboard.flags == 2 && sent[4].u.mouse.flags == 4, "Stuck input not released.");
    }
    static void LosslessBlocks()
    {
        using (Bitmap bitmap = new Bitmap(320, 240, PixelFormat.Format32bppRgb))
        using (FrameDecoder decoder = new FrameDecoder())
        {
            using (Graphics g = Graphics.FromImage(bitmap)) { g.Clear(Color.FromArgb(12, 45, 67)); g.FillRectangle(Brushes.White, 110, 110, 30, 30); }
            FrameEncoder encoder = new FrameEncoder(); byte[] frame = encoder.Encode(bitmap, 1, 80, true, true);
            using (Packet packet = new Packet(frame)) decoder.Apply(packet);
            Check(decoder.Image.GetPixel(50, 50).ToArgb() == bitmap.GetPixel(50, 50).ToArgb() && decoder.Image.GetPixel(115, 115).R == 255, "Lossless frame changed pixels.");
            using (Graphics g = Graphics.FromImage(bitmap)) g.FillRectangle(Brushes.Teal, 110, 110, 30, 30);
            using (Packet packet = new Packet(encoder.Encode(bitmap, 2, 80, false, true))) decoder.Apply(packet);
            Check(decoder.Image.GetPixel(115, 115).ToArgb() == bitmap.GetPixel(115, 115).ToArgb(), "Lossless delta changed pixels.");
            Reject(delegate { FastCodec.Decode(new byte[] { 1, 2, 3, 4 }, bitmap, new Rectangle(0, 0, 96, 96)); });
        }
    }
    static void BadInput()
    {
        int calls = 0;
        using (InputController input = new InputController(new Rectangle(0, 0, 320, 240), delegate { calls++; }))
        { Reject(delegate { input.Apply(0, -1, 0); }); Reject(delegate { input.Apply(1, 3, 0); }); Reject(delegate { input.Apply(3, 1201, 0); }); Reject(delegate { input.Apply(4, 256, 0); }); Reject(delegate { input.Apply(255, 0, 0); }); }
        Check(calls == 0, "Invalid input reached injection.");
    }
    static HostService NewHost(bool allow, bool consent)
    {
        captured = approved = clipboard = 0;
        HostService host = new HostService(delegate { Interlocked.Increment(ref captured); return new Synthetic(); }, Profile.All[1], allow,
            delegate { Interlocked.Increment(ref approved); return consent; }, delegate { Interlocked.Increment(ref clipboard); }, delegate(string message) { if (message.StartsWith("Connection ended:")) Console.WriteLine("HOST: " + message); });
        host.Start(IPAddress.Loopback, 0, "127.0.0.1"); return host;
    }
    static void WrongPin()
    {
        using (HostService host = NewHost(false, true)) using (ViewerConnection viewer = new ViewerConnection())
        { Invitation invite = Invitation.Parse(host.Invite.ToString()); invite.Fingerprint = new string('0', 64); Reject(delegate { viewer.Connect(invite, "Test viewer"); }); Check(captured == 0 && approved == 0, "Capture or approval before pin verification."); }
    }
    static void WrongSecret()
    {
        using (HostService host = NewHost(false, true)) using (ViewerConnection viewer = new ViewerConnection())
        { Invitation invite = Invitation.Parse(host.Invite.ToString()); invite.Secret = Security.Token(32); Reject(delegate { viewer.Connect(invite, "Test viewer"); }); Check(captured == 0 && approved == 0, "Unauthenticated request reached capture or approval."); }
    }
    static void Declined()
    {
        using (HostService host = NewHost(false, false)) using (ViewerConnection viewer = new ViewerConnection())
        { Reject(delegate { viewer.Connect(host.Invite, "Test viewer"); }); Check(captured == 0 && approved == 1, "Consent gate failed."); }
    }
    static void ReadFrames(Invitation invite, int count)
    {
        using (ViewerConnection viewer = new ViewerConnection()) using (FrameDecoder decoder = new FrameDecoder())
        {
            viewer.Connect(invite, "Synthetic integration test"); int frames = 0;
            Task task = Task.Run(delegate
            {
                try { viewer.Receive(delegate(Packet packet) { decoder.Apply(packet); viewer.Ack(decoder.Sequence); if (++frames >= count) viewer.Dispose(); }, delegate { }); }
                catch (Exception) { if (frames < count) throw; }
            });
            if (!task.Wait(12000)) { viewer.Dispose(); throw new TimeoutException("No complete desktop stream."); }
            Check(frames >= count && decoder.Image.Width == 640 && decoder.Image.Height == 360, "Frame count or dimensions failed.");
        }
    }
    static void DirectFrames() { using (HostService host = NewHost(false, true)) ReadFrames(host.Invite, 8); }
    static void ViewOnly()
    {
        using (HostService host = NewHost(false, true)) using (ViewerConnection viewer = new ViewerConnection())
        {
            viewer.Connect(host.Invite, "Read-only test"); long before = viewer.Sent; viewer.Input(4, 65, 0); Check(before == viewer.Sent && !viewer.CanControl, "Viewer sent view-only input.");
            Reject(delegate { viewer.SendClipboard("should not arrive"); });
        }
        using (HostService host = NewHost(false, true))
        using (RawClient raw = new RawClient(host.Invite))
        {
            raw.Auth(host.Invite.Secret); using (Packet accepted = raw.Wire.Read(2048)) Check(accepted.Kind == Kind.Accepted, "No acceptance.");
            raw.Wire.Send(Kind.Clipboard, delegate(BinaryWriter w) { Wire.Text(w, "should not arrive"); });
            bool closed = false;
            try { for (int i = 0; i < 4; i++) using (Packet p = raw.Wire.Read(Wire.MaxPacket)) { } } catch { closed = true; }
            Check(closed && clipboard == 0, "Host accepted clipboard in view-only session.");
        }
    }
    static void BruteForce()
    {
        using (HostService host = NewHost(false, true))
        {
            Invitation bad = Invitation.Parse(host.Invite.ToString()); bad.Secret = Security.Token(32);
            for (int i = 0; i < 5; i++) using (ViewerConnection viewer = new ViewerConnection()) Reject(delegate { viewer.Connect(bad, "Bad key test"); });
            using (ViewerConnection viewer = new ViewerConnection()) Reject(delegate { viewer.Connect(host.Invite, "Cooldown test"); });
            Check(captured == 0 && approved == 0, "Cooldown failed.");
        }
    }
    static void StopHost()
    {
        using (HostService host = NewHost(false, true)) using (RawClient raw = new RawClient(host.Invite))
        {
            raw.Auth(host.Invite.Secret); using (Packet p = raw.Wire.Read(2048)) Check(p.Kind == Kind.Accepted, "Acceptance failed.");
            Stopwatch watch = Stopwatch.StartNew(); host.Dispose();
            Reject(delegate { while (true) using (Packet p = raw.Wire.Read(Wire.MaxPacket)) { } });
            Check(watch.ElapsedMilliseconds < 2500, "Stopping the host was slow.");
        }
    }
    static void Reconnect()
    {
        using (HostService host = NewHost(false, true))
        {
            ReadFrames(host.Invite, 2);
            for (int i = 0; i < 50 && host.HasSession; i++) Thread.Sleep(20);
            ReadFrames(host.Invite, 2); Check(approved == 2, "Reconnect skipped fresh approval.");
        }
    }
    static void RelayFrames()
    {
        using (ManualResetEvent ready = new ManualResetEvent(false))
        using (HostService host = new HostService(delegate { return new Synthetic(); }, Profile.All[1], false, delegate { return true; }, delegate { }, delegate(string s) { if (s.StartsWith("Relay ready")) ready.Set(); }))
        {
            host.StartRelay("127.0.0.1", relayPort); Check(ready.WaitOne(10000), "Relay never became ready."); ReadFrames(host.Invite, 8);
        }
    }
    static void NativeInputFixture()
    {
        using (Form fixture = new Form { Text = "Lume owned input test", StartPosition = FormStartPosition.CenterScreen, Size = new Size(420, 220), TopMost = true })
        using (TextBox text = new TextBox { Dock = DockStyle.Top, Name = "TestText" })
        using (Button button = new Button { Text = "Local input fixture", Bounds = new Rectangle(30, 70, 260, 40) })
        {
            fixture.Controls.Add(text); fixture.Controls.Add(button); int clicks = 0; button.Click += delegate { clicks++; };
            fixture.Show(); fixture.Activate(); text.Focus(); Application.DoEvents(); Thread.Sleep(100); Application.DoEvents();
            Check(GetForegroundWindow() == fixture.Handle && text.Focused, "Owned test window did not receive focus; no input injected.");
            Point original = Cursor.Position;
            using (InputController input = new InputController(SystemInformation.VirtualScreen))
            {
                try
                {
                    input.Apply(4, 65, 0); input.Apply(5, 65, 0);
                    for (int i = 0; i < 10; i++) { Application.DoEvents(); Thread.Sleep(10); }
                    Check(text.Text.Equals("a", StringComparison.OrdinalIgnoreCase), "Real Windows keyboard input was not received.");
                    Check(GetForegroundWindow() == fixture.Handle, "Focus changed; mouse injection was not attempted.");
                    Rectangle desktop = SystemInformation.VirtualScreen; Point target = button.PointToScreen(new Point(60, 20));
                    input.Apply(0, (int)((long)(target.X - desktop.X) * 65535 / (desktop.Width - 1)), (int)((long)(target.Y - desktop.Y) * 65535 / (desktop.Height - 1)));
                    input.Apply(1, 0, 0); input.Apply(2, 0, 0);
                    for (int i = 0; i < 10; i++) { Application.DoEvents(); Thread.Sleep(10); }
                    Check(clicks == 1, "Real Windows mouse input was not received.");
                }
                finally { Cursor.Position = original; }
            }
            fixture.Close();
        }
    }
    static void NativeCaptureFixture()
    {
        Color expected = Color.FromArgb(23, 117, 171);
        using (Form fixture = new Form { Text = "Lume owned capture test", BackColor = expected, StartPosition = FormStartPosition.CenterScreen, Size = new Size(420, 260), TopMost = true })
        {
            fixture.Show(); fixture.Activate(); Application.DoEvents();
            using (DesktopSource source = new DesktopSource(Screen.PrimaryScreen.Bounds, Profile.All[2]))
            {
                bool found = false;
                for (int attempt = 0; attempt < 20; attempt++)
                {
                    Application.DoEvents(); Thread.Sleep(50);
                    Bitmap bitmap = source.Capture(); Point point = fixture.PointToScreen(new Point(130, 130));
                    int x = (point.X - source.Bounds.X) * bitmap.Width / source.Bounds.Width, y = (point.Y - source.Bounds.Y) * bitmap.Height / source.Bounds.Height;
                    Color observed = bitmap.GetPixel(x, y);
                    if (observed.R == expected.R && observed.G == expected.G && observed.B == expected.B) { found = true; break; }
                }
                Check(found, "The captured image did not contain the expected window pixels.");
                Console.WriteLine("CAPTURE_BACKEND: " + source.Backend);
            }
            fixture.Close();
        }
    }
    static void Benchmark()
    {
        Console.WriteLine("LOCAL BENCHMARK - no competitor comparison; encoded images are not saved.");
        Process process = Process.GetCurrentProcess();
        foreach (Profile profile in Profile.All)
        using (DesktopSource source = new DesktopSource(Screen.PrimaryScreen.Bounds, profile))
        {
            FrameEncoder encoder = new FrameEncoder(); Stopwatch timer = Stopwatch.StartNew(); TimeSpan cpu = process.TotalProcessorTime; long bytes = 0; int frames = 0;
            for (int i = 0; i < 30; i++) { byte[] frame = encoder.Encode(source.Capture(), i + 1, profile.Quality, true, profile.Lossless); bytes += frame.Length; frames++; }
            timer.Stop(); process.Refresh();
            Console.WriteLine(String.Format(System.Globalization.CultureInfo.InvariantCulture, "{0} [{6}]: forced full-frame encode {1:0.0} fps, {2:0.00} ms/frame, {3:0.00} MB working set, {4:0.0}% normalized CPU, {5:0.00} Mbit/s at measured throughput", profile.Name, frames / timer.Elapsed.TotalSeconds, timer.Elapsed.TotalMilliseconds / frames, process.WorkingSet64 / 1048576.0, (process.TotalProcessorTime - cpu).TotalMilliseconds / timer.Elapsed.TotalMilliseconds / Environment.ProcessorCount * 100, bytes * 8.0 / timer.Elapsed.TotalSeconds / 1000000, source.Backend));
        }
        using (Synthetic source = new Synthetic(false))
        {
            FrameEncoder encoder = new FrameEncoder(); int packets = 0; long bytes = 0;
            for (int i = 0; i < 100; i++) { byte[] data = encoder.Encode(source.Capture(), i + 1, 80, false); if (data != null) { packets++; bytes += data.Length; } }
            Console.WriteLine("Static synthetic desktop: " + packets + " transmitted frame in 100 captures; " + bytes + " bytes.");
        }
    }
    static void ProfileStages()
    {
        Console.WriteLine("Screen: " + Screen.PrimaryScreen.Bounds + "; logical CPUs: " + Environment.ProcessorCount);
        using (DesktopSource source = new DesktopSource(Screen.PrimaryScreen.Bounds, Profile.All[1]))
        {
            FrameEncoder encoder = new FrameEncoder(); Stopwatch capture = new Stopwatch(), encode = new Stopwatch(), hash = new Stopwatch();
            for (int i = 0; i < 30; i++)
            {
                capture.Start(); Bitmap bitmap = source.Capture(); capture.Stop();
                hash.Start(); encoder.Changes(bitmap, false); hash.Stop();
                encode.Start(); encoder.Encode(bitmap, i + 1, 80, true, true); encode.Stop();
            }
            Console.WriteLine(String.Format(System.Globalization.CultureInfo.InvariantCulture, "{0}: capture {1:0.00} ms, hash {2:0.00} ms, hash+lossless {3:0.00} ms", source.Backend, capture.Elapsed.TotalMilliseconds / 30, hash.Elapsed.TotalMilliseconds / 30, encode.Elapsed.TotalMilliseconds / 30));
        }
    }
    static void QualityChecks()
    {
        Run("Source dimensions, 360p, source refresh and 180 FPS selection", delegate
        {
            StreamQuality quality = StreamQuality.Source;
            Check(quality.Dimensions(new Size(3440, 1440)) == new Size(3440, 1440) && quality.Limit(180) == 180, "Source was reduced or its refresh was capped.");
            quality.Height = 360; quality.Fps = 10; Check(quality.Dimensions(new Size(1920, 1080)) == new Size(640, 360) && quality.Limit(180) == 10, "360p/10 FPS was not respected.");
            quality.Fps = 180; Check(quality.Limit(60) == 180, "Explicit 180 FPS was capped to source refresh.");
            quality.Fps = -1; Check(quality.Limit(60) == -1, "Uncapped selection changed.");
            quality.Fps = 1001; Reject(quality.Validate); quality.Fps = 10; quality.Height = 119; Reject(quality.Validate);
            Reject(delegate { StreamQuality.Source.Dimensions(new Size(16384, 16384)); });
        });
        Run("Frame pipeline is bounded and acknowledgements are ordered", delegate
        {
            FrameWindow window = new FrameWindow();
            for (int i = 1; i <= 16; i++) { Check(window.HasSpace(1024 * 1024, 32), "Frame byte budget filled too early."); window.Add(i, 1024 * 1024); }
            Check(!window.HasSpace(1, 32), "Frame pipeline exceeded its byte budget."); Reject(delegate { window.Acknowledge(2); }); window.Acknowledge(1);
            Check(window.HasSpace(1024 * 1024, 32), "Acknowledgement did not release bytes."); Reject(delegate { window.Acknowledge(1); });
            for (int i = 2; i <= 16; i++) window.Acknowledge(i); Check(window.IsEmpty, "Frame queue leaked.");
            window.Add(17, 1); Check(!window.HasSpace(1, 1), "Legacy frame window exceeded one frame.");
        });
        Run("Source lossless stream preserves pixels and 360p dimensions", delegate
        {
            using (Bitmap original = new Bitmap(1280, 720, PixelFormat.Format32bppRgb)) using (FrameScaler scaler = new FrameScaler()) using (FrameDecoder decoder = new FrameDecoder())
            {
                using (Graphics g = Graphics.FromImage(original)) { g.Clear(Color.FromArgb(37, 89, 141)); g.FillRectangle(Brushes.White, 37, 51, 113, 47); }
                Bitmap source = scaler.Scale(original, StreamQuality.Source); Check(Object.ReferenceEquals(source, original), "Source pixels were resampled.");
                using (Packet packet = new Packet(new FrameEncoder().Encode(source, 1, 100, true, true))) decoder.Apply(packet);
                Check(decoder.Image.Size == original.Size && decoder.Image.GetPixel(40, 60).ToArgb() == original.GetPixel(40, 60).ToArgb() && decoder.Image.GetPixel(900, 600).ToArgb() == original.GetPixel(900, 600).ToArgb(), "Source lossless pixels changed.");
                Check(scaler.Scale(original, new StreamQuality { Height = 360, Fps = 10 }).Size == new Size(640, 360), "360p resized incorrectly.");
            }
        });
        Run("Live viewer switches 360p 10 FPS to source 180 without reconnecting", LiveQuality);
        Run("Source frame above the old 12 MB limit decodes without rescaling", LargeSourceFrame);
    }
    static void LargeSourceFrame()
    {
        using (Bitmap image = new Bitmap(2048, 2048, PixelFormat.Format32bppRgb)) using (FrameDecoder decoder = new FrameDecoder())
        {
            byte[] pixels = new byte[2048 * 2048 * 4]; new Random(301).NextBytes(pixels);
            BitmapData data = image.LockBits(new Rectangle(0, 0, 2048, 2048), ImageLockMode.WriteOnly, PixelFormat.Format32bppRgb);
            try { Marshal.Copy(pixels, 0, data.Scan0, pixels.Length); } finally { image.UnlockBits(data); }
            byte[] frame = new FrameEncoder().Encode(image, 1, 100, true, true);
            Check(frame.Length > 12 * 1024 * 1024, "Fixture did not exercise the previous block-size limit.");
            using (Packet packet = new Packet(frame)) decoder.Apply(packet);
            Check(decoder.Image.Size == image.Size && decoder.Image.GetPixel(1223, 1540) == image.GetPixel(1223, 1540), "Large source pixels changed.");
        }
    }
    static void SignalSecurity()
    {
        string key = Security.Token(32), a = SignalBroker.NewId(), b = SignalBroker.NewId();
        SignalEnvelope envelope = SignalCrypto.Seal(key, a, b, Guid.NewGuid().ToString("N"), Guid.NewGuid().ToString("N"), "connect", new SignalBody { code = "PRIVATE-CODE", name = "PRIVATE-COMPUTER" });
        Check(!JsonData.Encode(envelope).Contains("PRIVATE"), "Plaintext leaked into signaling.");
        Check(SignalCrypto.Open(key, a, b, envelope).code == "PRIVATE-CODE", "Encrypted signaling did not round trip.");
        Reject(delegate { SignalCrypto.Open(key, b, a, envelope); }); Reject(delegate { SignalCrypto.Open(Security.Token(32), a, b, envelope); });
        envelope.stage = "pair"; Reject(delegate { SignalCrypto.Open(key, a, b, envelope); }); envelope.stage = "connect";
        envelope.box = (envelope.box[0] == 'A' ? "B" : "A") + envelope.box.Substring(1); Reject(delegate { SignalCrypto.Open(key, a, b, envelope); });
    }
    static void ProtectedSettings()
    {
        string directory = Path.Combine(Path.GetTempPath(), "Lume-owned-settings-test-" + Guid.NewGuid().ToString("N"));
        TrustedStore store = new TrustedStore(directory, false); store.ChangeHost(delegate(HostPreferences host) { host.Enabled = true; });
        PairingCode code = store.CreatePairing(false, null); PairingCode parsed = PairingCode.Parse(code.ToString());
        Check(parsed.host == store.ReadHost().HostId && parsed.key == code.key, "Pairing code changed.");
        string protectedFile = System.Text.Encoding.UTF8.GetString(File.ReadAllBytes(store.HostFile)); Check(!protectedFile.Contains(code.key) && !protectedFile.Contains(code.host), "Protected settings contain plaintext capabilities.");
        parsed.expires = DateTime.UtcNow.AddMinutes(-1).Ticks; Reject(delegate { PairingCode.Parse(parsed.ToString()); });
        byte[] packet = WakeOnLan.Packet("12-34-56-78-9A-BC"); Check(packet.Length == 102 && packet[0] == 255 && packet[6] == 0x12 && packet[101] == 0xBC, "Invalid magic packet."); Reject(delegate { WakeOnLan.Normalize("FF-FF-FF-FF-FF-FF"); });
        store.ChangeHost(delegate(HostPreferences host) { host.Enabled = false; host.PairKey = host.PairId = null; host.PairExpires = 0; });
        File.Delete(store.HostFile); Directory.Delete(directory);
    }
    static void PersistentPairing()
    {
        string directory = Path.Combine(Path.GetTempPath(), "Lume-owned-pairing-test-" + Guid.NewGuid().ToString("N"));
        TrustedStore store = new TrustedStore(directory, false); store.ChangeHost(delegate(HostPreferences preferences) { preferences.Enabled = true; });
        int sources = 0;
        using (PersistentHost host = new PersistentHost(store, delegate { Interlocked.Increment(ref sources); return new Synthetic(); }, delegate(string state) { Console.WriteLine("PAIRED_STATE: " + state); }))
        using (CancellationTokenSource timeout = new CancellationTokenSource(120000))
        {
            Task running = host.Run(); Stopwatch ready = Stopwatch.StartNew(); while (!host.Ready && ready.ElapsedMilliseconds < 20000) Thread.Sleep(50); Check(host.Ready, "Persistent host did not register: " + host.State);
            PairingCode code = store.CreatePairing(false, null); SavedComputer computer = PairedClient.Pair(code, "Owned paired test", timeout.Token).GetAwaiter().GetResult();
            Check(store.ReadHost().PairKey == null && store.ReadHost().Controllers.Count == 1 && sources == 0, "Pairing did not consume its code or captured a desktop.");
            for (int attempt = 0; attempt < 2; attempt++)
            {
                if (attempt > 0) Thread.Sleep(500);
                using (PairedLink link = PairedClient.Connect(computer, delegate { }, timeout.Token).GetAwaiter().GetResult())
                using (ViewerConnection viewer = new ViewerConnection()) using (FrameDecoder decoder = new FrameDecoder())
                {
                    viewer.ConnectPeer(link.Peer, link.Invitation, "Owned saved viewer"); int frames = 0; bool revoked = false;
                    Task receiving = Task.Run(delegate
                    {
                        try { viewer.Receive(delegate(Packet packet) { decoder.Apply(packet); viewer.Ack(decoder.Sequence); frames++; if (frames == 3) { if (attempt == 0) viewer.Dispose(); else { store.ChangeHost(delegate(HostPreferences preferences) { preferences.Controllers.Clear(); }); revoked = true; } } }, delegate { }); }
                        catch { if (frames < 3) throw; }
                    });
                    Check(receiving.Wait(15000) && frames >= 3 && (attempt == 0 || revoked), "Saved P2P stream or live revocation failed.");
                }
            }
            Check(sources == 2 && store.ReadHost().Controllers.Count == 0, "Saved session authorization counts changed.");
            host.Dispose(); Check(running.Wait(10000), "Persistent host did not stop.");
            Console.WriteLine("PAIRED_FLOW: one-time enrollment, two automatically negotiated encrypted synthetic desktop sessions, live revocation. No real desktop captured.");
        }
        foreach (string file in Directory.GetFiles(directory)) File.Delete(file); Directory.Delete(directory);
    }
    static void InstalledHost()
    {
        TrustedStore store = TrustedStore.Machine; HostPreferences before = store.ReadHost();
        Check(PermanentAccess.Installed && before.Enabled && String.IsNullOrEmpty(before.PairKey), "An enabled installed host with no pending user pairing is required.");
        SavedComputer computer = null; string testId = null; Color expected = Color.FromArgb(41, 137, 193); Point originalCursor = Cursor.Position;
        using (CancellationTokenSource timeout = new CancellationTokenSource(120000))
        using (Form fixture = new Form { Text = "Lume owned service acceptance fixture", BackColor = expected, StartPosition = FormStartPosition.CenterScreen, Size = new Size(460, 280), TopMost = true })
        using (TextBox input = new TextBox { Dock = DockStyle.Top })
        using (Button button = new Button { Text = "Owned service input target", Bounds = new Rectangle(30, 70, 280, 40) })
        {
            int clicks = 0; button.Click += delegate { clicks++; }; fixture.Controls.Add(input); fixture.Controls.Add(button);
            fixture.Show(); fixture.Activate(); input.Focus(); Application.DoEvents();
            try
            {
                PairingCode code = store.CreatePairing(false, null); testId = code.id;
                Task<SavedComputer> pairing = PairedClient.Pair(code, "Owned service acceptance fixture", timeout.Token);
                PumpUntil(delegate { return pairing.IsCompleted; }, 30000, "Installed host pairing timed out."); computer = pairing.GetAwaiter().GetResult();
                Task<PairedLink> connecting = PairedClient.Connect(computer, delegate { }, timeout.Token);
                PumpUntil(delegate { return connecting.IsCompleted; }, 90000, "Installed host connection timed out.");
                using (PairedLink link = connecting.GetAwaiter().GetResult()) using (ViewerConnection viewer = new ViewerConnection()) using (FrameDecoder decoder = new FrameDecoder())
                {
                    Task authenticating = Task.Run(delegate { viewer.ConnectPeer(link.Peer, link.Invitation, "Owned service viewer"); });
                    PumpUntil(delegate { return authenticating.IsCompleted; }, 20000, "Installed host TLS timed out."); authenticating.GetAwaiter().GetResult();
                    Point sample = fixture.PointToScreen(new Point(340, 160)); Rectangle desktop = Screen.PrimaryScreen.Bounds;
                    bool captured = false;
                    Task receiving = Task.Run(delegate
                    {
                        try { viewer.Receive(delegate(Packet packet) { decoder.Apply(packet); viewer.Ack(decoder.Sequence); if (decoder.Image.Size == desktop.Size) { Color observed = decoder.Image.GetPixel(sample.X - desktop.X, sample.Y - desktop.Y); if (observed.R == expected.R && observed.G == expected.G && observed.B == expected.B) captured = true; } }, delegate { }); }
                        catch { if (!captured) throw; }
                    });
                    PumpUntil(delegate { return captured || receiving.IsCompleted; }, 20000, "Installed host did not deliver the fixture pixels.");
                    if (receiving.IsFaulted) receiving.GetAwaiter().GetResult(); Check(captured, "Installed service source pixels did not match.");
                    fixture.Activate(); input.Focus(); Application.DoEvents();
                    Check(GetForegroundWindow() == fixture.Handle && input.Focused, "Owned fixture lost focus; no service input was injected.");
                    viewer.Input(4, 65, 0); viewer.Input(5, 65, 0);
                    PumpUntil(delegate { return input.Text.Equals("a", StringComparison.OrdinalIgnoreCase); }, 5000, "Service keyboard input was not received by the owned fixture.");
                    Check(GetForegroundWindow() == fixture.Handle, "Owned fixture lost focus; no service mouse input was injected.");
                    Point target = button.PointToScreen(new Point(70, 20)); viewer.Input(0, (int)((long)(target.X - desktop.X) * 65535 / (desktop.Width - 1)), (int)((long)(target.Y - desktop.Y) * 65535 / (desktop.Height - 1))); viewer.Input(1, 0, 0); viewer.Input(2, 0, 0);
                    PumpUntil(delegate { return clicks == 1; }, 5000, "Service mouse input was not received by the owned fixture.");
                    store.ChangeHost(delegate(HostPreferences host) { host.Controllers.RemoveAll(c => c.Id == computer.Id); });
                    Check(receiving.Wait(10000), "Revoking the installed host did not end its active stream.");
                    Console.WriteLine("INSTALLED_SERVICE: LocalSystem worker, public encrypted pairing, native P2P/TLS, " + desktop.Width + "x" + desktop.Height + " source pixels, " + viewer.SourceRefresh + " Hz source metadata, owned-window keyboard/mouse, live revocation. No desktop images or credentials saved.");
                }
            }
            finally
            {
                if (testId != null) store.ChangeHost(delegate(HostPreferences host) { host.Controllers.RemoveAll(c => c.Id == testId); if (host.PairId == testId) { host.PairId = host.PairKey = null; host.PairExpires = 0; } });
                Cursor.Position = originalCursor; fixture.Close();
            }
        }
    }
    static void PublicSignaling()
    {
        using (SignalBroker first = new SignalBroker(SignalBroker.NewId())) using (SignalBroker second = new SignalBroker(SignalBroker.NewId()))
        {
            string key = Security.Token(32); TaskCompletionSource<bool> received = new TaskCompletionSource<bool>();
            first.Closed += delegate(string message) { Console.WriteLine("PROBE sender: " + message); }; second.Closed += delegate(string message) { Console.WriteLine("PROBE receiver: " + message); };
            first.Message += delegate(BrokerPacket packet) { Console.WriteLine("PROBE sender packet type: " + packet.type); };
            second.Message += delegate(BrokerPacket packet)
            {
                try { if (packet.payload == null) return; SignalBody body = SignalCrypto.Open(key, packet.src, second.Id, packet.payload); Check(body.message == "Lume protocol probe", "Unexpected probe payload."); received.TrySetResult(true); }
                catch (Exception error) { received.TrySetException(error); }
            };
            first.Start(Security.Token(32)).GetAwaiter().GetResult(); second.Start(Security.Token(32)).GetAwaiter().GetResult();
            SignalEnvelope envelope = SignalCrypto.Seal(key, first.Id, second.Id, Guid.NewGuid().ToString("N"), Guid.NewGuid().ToString("N"), "probe", new SignalBody { message = "Lume protocol probe" });
            first.Send(second.Id, envelope).GetAwaiter().GetResult(); Check(received.Task.Wait(15000) && received.Task.Result, "Public signaling delivery timed out.");
            Console.WriteLine("PUBLIC_SIGNAL: encrypted non-desktop probe delivered between two native clients. No identity, key or invitation logged.");
        }
    }
    static void LiveQuality()
    {
        using (HostService host = new HostService(delegate { return new LargeSynthetic(); }, Profile.All[3], false, delegate { return true; }, delegate { }, delegate { }))
        using (ViewerConnection viewer = new ViewerConnection()) using (FrameDecoder decoder = new FrameDecoder())
        {
            host.Start(IPAddress.Loopback, 0, "127.0.0.1"); viewer.Connect(host.Invite, "Live quality fixture");
            viewer.SetQuality(new StreamQuality { Height = 360, Fps = 10, Lossless = false, JpegQuality = 55 }); bool lowSeen = false, sourceSeen = false;
            Task receive = Task.Run(delegate
            {
                try { viewer.Receive(delegate(Packet packet)
                {
                    decoder.Apply(packet); viewer.Ack(decoder.Sequence); StreamQuality quality = viewer.CurrentQuality;
                    if (!lowSeen && quality != null && quality.Height == 360 && quality.Fps == 10 && !quality.Lossless && decoder.Image.Size == new Size(640, 360))
                    { lowSeen = true; viewer.SetQuality(new StreamQuality { Height = 0, Fps = 180, Lossless = true, JpegQuality = 100 }); }
                    else if (lowSeen && quality != null && quality.Height == 0 && quality.Fps == 180 && quality.Lossless && decoder.Image.Size == new Size(1280, 720))
                    { Check(decoder.Image.GetPixel(1000, 600).ToArgb() == Color.FromArgb(37, 89, 141).ToArgb(), "Restored source pixels differ."); sourceSeen = true; viewer.Dispose(); }
                }, delegate { }); } catch { if (!sourceSeen) throw; }
            });
            Check(receive.Wait(20000) && lowSeen && sourceSeen, "Live quality changes were not applied.");
        }
    }
    sealed class LargeSynthetic : IScreenSource
    {
        readonly Bitmap image = new Bitmap(1280, 720, PixelFormat.Format32bppRgb); int counter;
        public LargeSynthetic() { using (Graphics g = Graphics.FromImage(image)) g.Clear(Color.FromArgb(37, 89, 141)); }
        public Rectangle Bounds { get { return new Rectangle(0, 0, 1280, 720); } }
        public Bitmap Capture() { using (Graphics g = Graphics.FromImage(image)) g.FillRectangle((++counter % 2 == 0) ? Brushes.White : Brushes.Black, 40, 40, 80, 80); return image; }
        public void Dispose() { image.Dispose(); }
    }
    static void RenderUi(string directory)
    {
        Directory.CreateDirectory(directory);
        using (MainForm form = new MainForm())
        {
            form.Show(); Application.DoEvents();
            using (Bitmap image = new Bitmap(form.Width, form.Height)) { form.DrawToBitmap(image, new Rectangle(0, 0, form.Width, form.Height)); image.Save(Path.Combine(directory, "main-window.png"), ImageFormat.Png); }
            Console.WriteLine("Main UI rendered at " + form.Size + "; screen " + Screen.PrimaryScreen.Bounds);
            form.Close();
        }
        using (ConsentForm form = new ConsentForm(new PeerRequest { Name = "Test computer", Address = "127.0.0.1", Control = true }))
        {
            form.Show(); Application.DoEvents();
            using (Bitmap image = new Bitmap(form.Width, form.Height)) { form.DrawToBitmap(image, new Rectangle(0, 0, form.Width, form.Height)); image.Save(Path.Combine(directory, "approval-dialog.png"), ImageFormat.Png); }
            form.Close();
        }
        PeerSignal preview = new PeerSignal { Session = Sample() };
        using (PeerHostForm form = new PeerHostForm(preview, null, delegate { }, delegate { }))
        {
            form.Show(); Application.DoEvents();
            using (Bitmap image = new Bitmap(form.Width, form.Height)) { form.DrawToBitmap(image, new Rectangle(0, 0, form.Width, form.Height)); image.Save(Path.Combine(directory, "p2p-sharing.png"), ImageFormat.Png); }
            form.Close();
        }
        using (PeerViewerForm form = new PeerViewerForm(preview))
        {
            // Render layout only; the actual Shown flow is covered by PeerMainApproval.
            System.ComponentModel.EventHandlerList events = (System.ComponentModel.EventHandlerList)typeof(System.ComponentModel.Component).GetProperty("Events", System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic).GetValue(form, null);
            object shownKey = typeof(Form).GetField("EVENT_SHOWN", System.Reflection.BindingFlags.Static | System.Reflection.BindingFlags.NonPublic).GetValue(null);
            events.RemoveHandler(shownKey, events[shownKey]);
            form.Show(); Application.DoEvents();
            ((TextBox)Field(form, "reply")).Text = "[Private reply code omitted from this layout preview.]";
            ((Label)Field(form, "status")).Text = "Copy this reply and return it to the sharing PC. Waiting for its P2P negotiation...";
            ((Button)Field(form, "copy")).Enabled = true;
            form.PerformLayout(); Application.DoEvents();
            using (Bitmap image = new Bitmap(form.Width, form.Height)) { form.DrawToBitmap(image, new Rectangle(0, 0, form.Width, form.Height)); image.Save(Path.Combine(directory, "p2p-controlling.png"), ImageFormat.Png); }
            form.Close();
        }
        using (ViewerForm form = new ViewerForm(ClosedEndpoint()))
        {
            form.Show(); Stopwatch wait = Stopwatch.StartNew();
            while (form.Text != "Lume - Disconnected" && wait.ElapsedMilliseconds < 6000) { Application.DoEvents(); Thread.Sleep(20); }
            using (Bitmap image = new Bitmap(form.Width, form.Height)) { form.DrawToBitmap(image, new Rectangle(0, 0, form.Width, form.Height)); image.Save(Path.Combine(directory, "connection-failure.png"), ImageFormat.Png); }
            form.Close();
        }
        using (ConnectionDiagnosticsForm form = new ConnectionDiagnosticsForm(null, true))
        {
            form.Show(); Stopwatch wait = Stopwatch.StartNew();
            while (wait.ElapsedMilliseconds < 2000) { Application.DoEvents(); Thread.Sleep(20); }
            using (Bitmap image = new Bitmap(form.Width, form.Height)) { form.DrawToBitmap(image, new Rectangle(0, 0, form.Width, form.Height)); image.Save(Path.Combine(directory, "connection-diagnostics.png"), ImageFormat.Png); }
            form.Close();
        }
        using (StreamQualityForm form = new StreamQualityForm(StreamQuality.Source, 2560, 1440, 180))
        {
            form.Show(); Application.DoEvents();
            using (Bitmap image = new Bitmap(form.Width, form.Height)) { form.DrawToBitmap(image, new Rectangle(0, 0, form.Width, form.Height)); image.Save(Path.Combine(directory, "quality-settings.png"), ImageFormat.Png); }
            form.Close();
        }
        Console.WriteLine("Owned WinForms rendering completed. This is not a desktop screenshot from the UI automation tool.");
    }
    [DllImport("user32.dll")] static extern IntPtr GetForegroundWindow();
    sealed class Synthetic : IScreenSource
    {
        readonly Bitmap image = new Bitmap(640, 360, PixelFormat.Format32bppRgb); int counter; readonly bool animated;
        public Synthetic() : this(true) { }
        public Synthetic(bool animated) { this.animated = animated; using (Graphics g = Graphics.FromImage(image)) g.Clear(Color.FromArgb(12, 30, 60)); }
        public Rectangle Bounds { get { return new Rectangle(0, 0, 640, 360); } }
        public Bitmap Capture()
        { if (animated) using (Graphics g = Graphics.FromImage(image)) { g.FillRectangle((++counter % 2 == 0) ? Brushes.White : Brushes.Teal, 110, 110, 40, 40); } return image; }
        public void Dispose() { image.Dispose(); }
    }
    sealed class RawClient : IDisposable
    {
        readonly TcpClient client; public Wire Wire;
        public RawClient(Invitation invite)
        {
            client = Transport.Connect(invite.Host, invite.Port, 3000);
            SslStream tls = new SslStream(client.GetStream(), false, delegate(object sender, System.Security.Cryptography.X509Certificates.X509Certificate cert, System.Security.Cryptography.X509Certificates.X509Chain chain, SslPolicyErrors errors) { return cert != null && Security.Equal(Security.Pin(cert), invite.Fingerprint); });
            tls.ReadTimeout = 3000; tls.WriteTimeout = 3000; tls.AuthenticateAsClient("Lume Remote Session", null, SslProtocols.None, false); Wire = new Wire(tls);
        }
        public void Auth(string secret) { Wire.Send(Kind.Auth, delegate(BinaryWriter w) { w.Write(1); Wire.Text(w, secret); Wire.Text(w, "Protocol test"); }); }
        public void Dispose() { client.Close(); }
    }
    sealed class FragmentStream : MemoryStream
    {
        public FragmentStream(byte[] bytes) : base(bytes) { }
        public override int Read(byte[] buffer, int offset, int count) { return base.Read(buffer, offset, Math.Min(3, count)); }
    }
}
