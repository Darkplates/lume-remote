using System;
using System.Drawing;
using System.IO;
using System.Net;
using System.Threading;
using System.Threading.Tasks;
using LumeRemote;

static partial class Tests
{
    static void PortableMonitorsFixture(string file)
    {
        string path = Path.GetFullPath(file); if (File.Exists(path)) throw new IOException("Fixture output exists.");
        using (var host = new HostService(delegate { return new SyntheticMonitors(); }, Profile.All[3], false, delegate { return true; }, delegate { }, delegate { }))
        {
            host.Start(IPAddress.Loopback, 0, "127.0.0.1"); File.WriteAllText(path, host.Invite.ToString());
            try { var clock = System.Diagnostics.Stopwatch.StartNew(); while (!File.Exists(path + ".stop") && clock.Elapsed.TotalSeconds < 90) Thread.Sleep(25); }
            finally { File.Delete(path); }
        }
    }
    static void PortableMonitorsViewer(string file)
    {
        using (var viewer = new ViewerConnection())
        {
            viewer.Connect(Invitation.Parse(File.ReadAllText(file)), "Synthetic display viewer"); Check(!viewer.CanControl, "Fixture must be view-only.");
            viewer.SetQuality(StreamQuality.Source); int width = 0, epoch = 0, colour = 0; Exception fault = null;
            Task receive = Task.Run(delegate { try { using (var decoder = new FrameDecoder()) viewer.Receive(delegate(Packet p) { decoder.Apply(p); width = decoder.Image.Width; colour = decoder.Image.GetPixel(20,20).ToArgb(); epoch = viewer.FrameEpoch; viewer.Ack(decoder.Sequence); }, delegate { }); } catch (Exception e) { fault = e; } });
            var list = Await(viewer.GetMonitors()); Check(list.Length == 2 && list[1].Bounds.X == -800 && list[1].Refresh == 144, "Portable display metadata changed.");
            viewer.SelectMonitor("two").GetAwaiter().GetResult(); Spin(delegate { return epoch == 2 && width == 800 && colour == Color.Teal.ToArgb(); }, 10000, "Portable selected display did not arrive.");
            Reject(delegate { viewer.SelectMonitor("missing").GetAwaiter().GetResult(); });
            viewer.SelectMonitor("one").GetAwaiter().GetResult(); Spin(delegate { return epoch == 3 && width == 640 && colour == Color.Navy.ToArgb(); }, 10000, "Portable display recovery failed.");
            Check(fault == null, "Portable display selection stopped frames."); viewer.Dispose(); receive.Wait(3000);
            Console.WriteLine("PASS Windows viewer / Rust host: display list, negative origin, source pixels, epoch changes, missing-display recovery. Synthetic only.");
        }
    }
    static void PortablePairedViewer(string file)
    {
        var saved=PairedClient.Pair(PairingCode.Parse(File.ReadAllText(file)),"Owned Windows viewer",CancellationToken.None).GetAwaiter().GetResult();
        File.WriteAllText(Path.ChangeExtension(file,"paired"),"paired");Spin(delegate{return File.Exists(Path.ChangeExtension(file,"confirmed"));},15000,"Portable host did not confirm pairing without capture.");
        try{for(int i=0;i<2;i++){
            using(var link=PairedClient.Connect(saved,delegate{},CancellationToken.None).GetAwaiter().GetResult())using(var viewer=new ViewerConnection()){
                viewer.ConnectPeer(link.Peer,link.Invitation,"Owned Windows viewer");int frames=0;
                Task receive=Task.Run(delegate{try{using(var decoder=new FrameDecoder())viewer.Receive(delegate(Packet p){decoder.Apply(p);viewer.Ack(decoder.Sequence);Interlocked.Increment(ref frames);},delegate{});}catch(IOException){}catch(ObjectDisposedException){} });
                Spin(delegate{return frames>0;},10000,"Portable host delivered no approved frame.");
                if(i==1){File.WriteAllText(Path.ChangeExtension(file,"revoke"),"revoke");Check(receive.Wait(10000),"Portable host revocation left the connection alive.");}
                viewer.Dispose();Check(receive.Wait(3000),"Windows receive worker did not stop.");
            }Thread.Sleep(1000);
        }
        Reject(delegate{using(var link=PairedClient.Connect(saved,delegate{},CancellationToken.None).GetAwaiter().GetResult()) {}});
        Console.WriteLine("PASS Windows viewer: portable-host pairing, two P2P connections, live revocation and rejected key reuse.");
        }finally{File.WriteAllText(Path.ChangeExtension(file,"stop"),"stop");}
    }
    static void PortableFilesViewer(string file, string directory)
    {
        string root = Path.GetFullPath(directory); if (Directory.Exists(root)) throw new IOException("Use a new isolated destination."); Directory.CreateDirectory(root);
        using (var viewer = new ViewerConnection())
        {
            viewer.Connect(Invitation.Parse(File.ReadAllText(file)), "Owned portable file fixture"); Check(viewer.Files != null && viewer.Files.Folders, "Portable file capabilities missing.");
            int frames = 0; var receive = Task.Run(delegate { try { using (var decoder = new FrameDecoder()) viewer.Receive(delegate(Packet p) { decoder.Apply(p); viewer.Ack(decoder.Sequence); Interlocked.Increment(ref frames); }, delegate { }); } catch { } });
            RemoteFileList roots = viewer.Files.List("").GetAwaiter().GetResult(); Check(roots.Entries.Count == 1 && roots.Entries[0].Name == "R:\\", "Shared folder root differs.");
            Reject(delegate { viewer.Files.List("R:\\..\\").GetAwaiter().GetResult(); });
            byte[] payload = new byte[1048699]; for (int i = 0; i < payload.Length; i++) payload[i] = (byte)(i * 31 + 7);
            string local = Path.Combine(root, "upload.bin"); File.WriteAllBytes(local, payload);
            string folder = viewer.Files.CreateFolder("R:\\", "roundtrip", true).GetAwaiter().GetResult();
            viewer.Files.Upload(local, folder, CancellationToken.None).GetAwaiter().GetResult();
            string download = viewer.Files.Download("R:\\payload.bin", root, CancellationToken.None).GetAwaiter().GetResult();
            Check(Convert.ToBase64String(File.ReadAllBytes(download)) == Convert.ToBase64String(payload), "Portable download changed bytes.");
            string tree = viewer.Files.DownloadFolder(folder, root, CancellationToken.None).GetAwaiter().GetResult();
            Check(Convert.ToBase64String(File.ReadAllBytes(Path.Combine(tree, "upload.bin"))) == Convert.ToBase64String(payload), "Portable folder download changed bytes.");
            string second = viewer.Files.CreateFolder("R:\\", "roundtrip", true).GetAwaiter().GetResult(); Check(second != folder, "Portable folder collision overwrote data.");
            Spin(delegate { return frames > 0; }, 8000, "Files stopped video."); viewer.Dispose(); receive.Wait(3000);
            Console.WriteLine("PASS Windows viewer / portable host: scoped root, traversal denial, SHA-256 upload/download, folders, collisions and video.");
        }
    }
    static void PortableFilesFixture(string file, string directory)
    {
        string path = Path.GetFullPath(file), root = Path.GetFullPath(directory);
        if (File.Exists(path) || Directory.Exists(root)) throw new IOException("Fixture output already exists.");
        Directory.CreateDirectory(root); byte[] payload = new byte[1048699]; for (int i = 0; i < payload.Length; i++) payload[i] = (byte)(i * 31 + 7);
        File.WriteAllBytes(Path.Combine(root, "payload.bin"), payload); File.WriteAllBytes(Path.Combine(root, "empty.txt"), new byte[0]);
        for (int i = 0; i < 205; i++) File.WriteAllText(Path.Combine(root, "page-" + i.ToString("D3") + ".txt"), "fixture");
        using (var host = new HostService(delegate { return new Synthetic(); }, Profile.All[1], true, delegate { return true; }, delegate { }, delegate { }, null, delegate { return new RemoteFileAccess(); }))
        {
            host.InputFactory = delegate(Rectangle bounds) { return new InputController(bounds, delegate { }); };
            host.Start(IPAddress.Loopback, 0, "127.0.0.1"); File.WriteAllText(path + ".root", root); File.WriteAllText(path, host.Invite.ToString());
            try { var timer = System.Diagnostics.Stopwatch.StartNew(); while (!File.Exists(path + ".stop") && timer.Elapsed.TotalSeconds < 180) Thread.Sleep(50); }
            finally { File.Delete(path); File.Delete(path + ".root"); }
        }
    }
    static void PortablePairedFixture(string file)
    {
        string path = Path.GetFullPath(file); if (File.Exists(path)) throw new IOException("Fixture output already exists.");
        string files = path + ".files"; Directory.CreateDirectory(files); File.WriteAllText(path + ".root", files);
        string root = path + ".host"; var store = new TrustedStore(root, false);
        store.ChangeHost(delegate(HostPreferences p) { p.Enabled = true; }); int sources = 0;
        using (var host = new PersistentHost(store, delegate { Interlocked.Increment(ref sources); return new Synthetic(); }, delegate { }))
        {
            Task running = host.Run(); Spin(delegate { return host.Ready; }, 25000, "Isolated host did not register.");
            File.WriteAllText(path, store.CreatePairing(false, null).ToString());
            try {
                var timer = System.Diagnostics.Stopwatch.StartNew(); bool checkedPair = false, revoked = false;
                while (!File.Exists(path + ".stop") && timer.Elapsed.TotalSeconds < 180) {
                    if (File.Exists(path + ".paired") && !checkedPair) { Check(store.ReadHost().PairKey == null && store.ReadHost().Controllers.Count == 1 && sources == 0, "Pairing did not consume the code before capture."); File.WriteAllText(path + ".confirmed", "ok"); checkedPair = true; }
                    if (File.Exists(path + ".revoke") && !revoked) { store.ChangeHost(delegate(HostPreferences p) { p.Controllers.Clear(); }); revoked = true; }
                    Thread.Sleep(50);
                }
                Check(checkedPair && revoked && sources >= 2, "Pairing fixture did not verify reconnect and revocation.");
                Console.WriteLine("PASS Owned portable pairing: one-time code, no capture during pairing, reconnect twice, explicit revocation.");
            } finally { host.Dispose(); running.Wait(5000); File.Delete(path); store.ChangeHost(delegate(HostPreferences p) { p.Enabled = false; p.Controllers.Clear(); p.PairKey = p.PairId = null; p.PairExpires = 0; }); }
        }
    }
    static void PortableChecks()
    {
        Run("Portable PNG validates dimensions and preserves source pixels", PortablePixels);
        Run("Portable image negotiation preserves legacy wire fields", PortableNegotiation);
    }
    static void PortablePixels()
    {
        using (Bitmap image = new Bitmap(64, 48)) using (Graphics g = Graphics.FromImage(image)) using (var decoder = new FrameDecoder())
        {
            g.Clear(Color.Teal); g.FillRectangle(Brushes.Gold, 0, 0, 25, 14);
            byte[] bytes = new FrameEncoder().Encode(image, 1, 90, true, true, true);
            using (var packet = new Packet(bytes)) decoder.Apply(packet);
            Check(decoder.Image.GetPixel(7, 7).ToArgb() == Color.Gold.ToArgb() && decoder.Image.GetPixel(40, 40).ToArgb() == Color.Teal.ToArgb(), "Portable source changed pixels.");
            using (var png = new MemoryStream()) { image.Save(png, System.Drawing.Imaging.ImageFormat.Png); byte[] block = png.ToArray(); FrameDecoder.ValidatePng(block, 64, 48); Reject(delegate { FrameDecoder.ValidatePng(block, 65, 48); }); block[0] = 0; Reject(delegate { FrameDecoder.ValidatePng(block, 64, 48); }); }
        }
    }
    static void PortableNegotiation()
    {
        using (var host = new HostService(delegate { return new Synthetic(); }, Profile.All[1], false, delegate { return true; }, delegate { }, delegate { }))
        using (var viewer = new ViewerConnection())
        {
            host.Start(IPAddress.Loopback, 0, "127.0.0.1"); viewer.Connect(host.Invite, "Portable negotiation fixture");
            Check((viewer.Capabilities & SessionCapabilities.PortableImages) != 0, "Portable images were not advertised.");
            int frames = 0; var receiver = Task.Run(delegate { try { using (var decoder = new FrameDecoder()) viewer.Receive(delegate(Packet p) { decoder.Apply(p); viewer.Ack(decoder.Sequence); Interlocked.Increment(ref frames); }, delegate { }); } catch { } });
            Await(viewer.Tools.Request(SessionTool.PortableImages, delegate(BinaryWriter w) { w.Write(true); })); viewer.SetQuality(StreamQuality.Source);
            Spin(delegate { return frames >= 2; }, 8000, "Negotiated portable source produced no frames.");
            viewer.Dispose(); receiver.Wait(3000);
        }
    }
    static void PortableHostFixture(string file)
    {
        string path = Path.GetFullPath(file); if (File.Exists(path)) throw new IOException("Fixture invitation output already exists.");
        using (var host = new HostService(delegate { return new Synthetic(); }, Profile.All[1], false,
            delegate { Console.WriteLine("FIXTURE Approved synthetic view-only session."); return true; }, delegate { },
            delegate(string status) { if (status.StartsWith("Connected to ") || status.StartsWith("Connection ended: ")) Console.WriteLine("FIXTURE " + status); }))
        {
            host.Start(IPAddress.Loopback, 0, "127.0.0.1"); File.WriteAllText(path, host.Invite.ToString());
            try { var timer = System.Diagnostics.Stopwatch.StartNew(); while (!File.Exists(path + ".stop") && timer.Elapsed.TotalSeconds < 90) Thread.Sleep(100); }
            finally { File.Delete(path); }
        }
    }
    static void PortablePeerFixture(string file)
    {
        string path = Path.GetFullPath(file); if (File.Exists(path)) throw new IOException("Fixture invitation output already exists.");
        using (var peer = new PeerTransport(false))
        using (var host = new HostService(delegate { return new Synthetic(); }, Profile.All[1], false, delegate { return true; }, delegate { }, delegate { }))
        {
            host.StartPeer(); PeerSignal offer = PeerSignal.Offer(host.Invite, peer.CreateOffer()); File.WriteAllText(path, offer.ToString());
            try
            {
                var timer = System.Diagnostics.Stopwatch.StartNew(); while (!File.Exists(path + ".reply") && timer.Elapsed.TotalSeconds < 40) Thread.Sleep(50);
                PeerSignal reply = PeerSignal.Parse(File.ReadAllText(path + ".reply")); offer.VerifyReply(reply); peer.AcceptAnswer(reply.Sdp); peer.WaitReady(15000); host.AcceptPeer(peer);
                while (!File.Exists(path + ".stop") && timer.Elapsed.TotalSeconds < 75) Thread.Sleep(100);
            }
            finally { File.Delete(path); if (File.Exists(path + ".reply")) File.Delete(path + ".reply"); }
        }
    }
    static void PortableViewerFixture(string file)
    {
        string text = File.ReadAllText(file); Invitation invite = Invitation.Parse(text);
        using (var viewer = new ViewerConnection()) using (var decoder = new FrameDecoder())
        {
            viewer.Connect(invite, "Windows interoperability fixture"); Check(!viewer.CanControl, "The fixture must be view-only.");
            viewer.SetQuality(StreamQuality.Source); int frames = 0; bool exact = false;
            viewer.Receive(delegate(Packet packet) {
                decoder.Apply(packet); viewer.Ack(decoder.Sequence); frames++;
                if (decoder.Image.GetPixel(10, 10).R == 70 && decoder.Image.GetPixel(10, 10).G == 120 && decoder.Image.GetPixel(10, 10).B == 190) exact = true;
                if (frames >= 2 && exact) viewer.Dispose();
            }, delegate { });
            Check(frames >= 2 && exact, "Portable host did not deliver exact source pixels."); Console.WriteLine("PASS Windows viewer decoded source pixels from the Rust TLS host. Synthetic loopback only.");
        }
    }
}
