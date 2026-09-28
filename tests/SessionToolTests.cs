using System;
using System.Drawing;
using System.IO;
using System.Net;
using System.Threading;
using System.Threading.Tasks;
using LumeRemote;

static partial class Tests
{
    static void SessionToolChecks()
    {
        Run("Clipboard read uses STA, enforces size and survives failure", ClipboardReader);
        Run("Clipboard requests preserve frames while waiting for permission", ClipboardPull);
        Run("Unadvertised clipboard requests are denied without stopping video", ToolPermissions);
        Run("Live monitor selection tags frames and preserves dimensions", MonitorSelection);
        Run("Protocol 3 viewers keep their exact handshake and frame layout", VersionThreeViewer);
        Run("Nested and empty folders transfer both ways without overwriting", FolderRoundTrip);
        Run("Malformed session replies and request envelopes are bounded", ToolBounds);
    }
    static void ClipboardReader()
    {
        bool sta = false;
        string value = Await(ClipboardAccess.Read(delegate { sta = Thread.CurrentThread.GetApartmentState() == ApartmentState.STA; return "fixture \u00e1"; }));
        Check(sta && value == "fixture \u00e1", "Clipboard reader lost apartment or Unicode.");
        Thread.Sleep(20); Reject(delegate { Await(ClipboardAccess.Read(delegate { return new string('x', 262145); })); });
        Thread.Sleep(20); Check(Await(ClipboardAccess.Read(delegate { return "recovered"; })) == "recovered", "Read failure left clipboard busy.");
    }
    static void ClipboardPull()
    {
        var consent = new TaskCompletionSource<string>(TaskCreationOptions.RunContinuationsAsynchronously);
        using (var host = new HostService(delegate { return new Synthetic(true); }, Profile.All[1], true, delegate { return true; }, delegate { }, delegate { }, null, null, delegate { return consent.Task; }))
        using (var viewer = new ViewerConnection())
        {
            host.Start(IPAddress.Loopback, 0, "127.0.0.1"); viewer.Connect(host.Invite, "Clipboard pull fixture");
            int frames = 0; Task receiver = Task.Run(delegate { try { using (var decoder = new FrameDecoder()) viewer.Receive(delegate(Packet p) { decoder.Apply(p); viewer.Ack(decoder.Sequence); Interlocked.Increment(ref frames); }, delegate { }); } catch { } });
            Task<string> text = viewer.GetClipboard(); Spin(delegate { return frames >= 3; }, 6000, "Clipboard prompt blocked desktop frames.");
            Check(!text.IsCompleted, "Clipboard was read before consent."); consent.SetResult("approved fixture"); Check(Await(text) == "approved fixture", "Clipboard pull changed data.");
            Check(host.HasSession, "Clipboard pull ended desktop session."); viewer.Dispose(); receiver.Wait(3000);
        }
    }
    static void ToolPermissions()
    {
        using (var fixture = new FilesFixture(false))
        {
            Check((fixture.Viewer.Capabilities & SessionCapabilities.ClipboardRead) == 0, "View-only acquired clipboard read.");
            Reject(delegate { Await(fixture.Viewer.GetClipboard()); });
            Reject(delegate { Await(fixture.Viewer.Tools.Request(SessionTool.ClipboardRead)); });
            Check(fixture.Host.HasSession, "Unsupported operation ended the session.");
        }
        using (var host = new HostService(delegate { return new Synthetic(); }, Profile.All[1], false, delegate { return true; }, delegate { }, delegate { }, null, null, null, true))
        using (var viewer = new ViewerConnection())
        {
            host.Start(IPAddress.Loopback, 0, "127.0.0.1"); viewer.Connect(host.Invite, "View-only collaboration fixture");
            Check((viewer.Capabilities & SessionCapabilities.Chat) != 0 && (viewer.Capabilities & (SessionCapabilities.Annotations | SessionCapabilities.Power | SessionCapabilities.ClipboardSync)) == 0, "View-only chat granted control tools.");
            var receiver = Task.Run(delegate { try { using (var decoder = new FrameDecoder()) viewer.Receive(delegate(Packet p) { decoder.Apply(p); viewer.Ack(decoder.Sequence); }, delegate { }); } catch { } });
            Reject(delegate { Await(viewer.Tools.Request(SessionTool.Annotation, delegate(System.IO.BinaryWriter w) { w.Write(1); w.Write(0); })); });
            Check(host.HasSession, "Denied drawing stopped view-only video."); viewer.Dispose(); receiver.Wait(3000);
        }
    }
    sealed class SyntheticMonitors : IScreenSource, IAdaptiveScreenSource, IFrameChangeSource, IMonitorSource
    {
        Bitmap image; int selected;
        public SyntheticMonitors() { Select("one"); }
        public Rectangle Bounds { get { return selected == 0 ? new Rectangle(0, 0, 640, 360) : new Rectangle(-800, -20, 800, 450); } }
        public int RefreshRate { get { return selected == 0 ? 60 : 144; } }
        public bool FrameChanged { get { return true; } }
        public Bitmap Capture() { return image; }
        public void Configure(StreamQuality quality) { }
        public void Select(string id)
        {
            if (id != "one" && id != "two") throw new InvalidOperationException("Display disconnected.");
            selected = id == "one" ? 0 : 1; if (image != null) image.Dispose(); image = new Bitmap(Bounds.Width, Bounds.Height);
            using (Graphics g = Graphics.FromImage(image)) g.Clear(selected == 0 ? Color.Navy : Color.Teal);
        }
        public RemoteMonitor[] Monitors() { return new[] { new RemoteMonitor { Id = "one", Name = "One", Bounds = new Rectangle(0, 0, 640, 360), Refresh = 60, Selected = selected == 0 }, new RemoteMonitor { Id = "two", Name = "Two", Bounds = new Rectangle(-800, -20, 800, 450), Refresh = 144, Selected = selected == 1 } }; }
        public void Dispose() { if (image != null) image.Dispose(); }
    }
    static void MonitorSelection()
    {
        using (var host = new HostService(delegate { return new SyntheticMonitors(); }, Profile.All[3], false, delegate { return true; }, delegate { }, delegate { }))
        using (var viewer = new ViewerConnection())
        {
            host.Start(IPAddress.Loopback, 0, "127.0.0.1"); viewer.Connect(host.Invite, "Monitor fixture");
            int epoch = 0, width = 0; Exception fault = null;
            Task receiver = Task.Run(delegate { try { using (var decoder = new FrameDecoder()) viewer.Receive(delegate(Packet p) { decoder.Apply(p); width = decoder.Image.Width; epoch = viewer.FrameEpoch; viewer.Ack(decoder.Sequence); }, delegate { }); } catch (Exception error) { fault = error; } });
            RemoteMonitor[] displays = Await(viewer.GetMonitors()); Check(displays.Length == 2 && displays[1].Bounds.X == -800, "Display metadata was lost.");
            viewer.SelectMonitor("two").GetAwaiter().GetResult(); Spin(delegate { return epoch == 2 && width == 800; }, 5000, "New display frame/epoch did not arrive.");
            Check(viewer.SourceWidth == 800 && viewer.SourceHeight == 450 && viewer.SourceRefresh == 144, "Source controls retained old display metadata.");
            Reject(delegate { viewer.SelectMonitor("missing").GetAwaiter().GetResult(); }); Check(viewer.MonitorEpoch == 0, "Ambiguous display change left input active.");
            viewer.SelectMonitor("one").GetAwaiter().GetResult(); Spin(delegate { return epoch == 3 && width == 640; }, 5000, "Display selection did not recover.");
            Check(fault == null, "Display change killed decoder."); viewer.Dispose(); receiver.Wait(3000);
        }
    }
    static void VersionThreeViewer()
    {
        using (var host = NewHost(false, true)) using (var raw = new RawClient(host.Invite)) using (var decoder = new FrameDecoder())
        {
            raw.Wire.Send(Kind.Auth, delegate(System.IO.BinaryWriter w) { w.Write(3); Wire.Text(w, host.Invite.Secret); Wire.Text(w, "Version 3 fixture"); });
            using (Packet p = raw.Wire.Read(2048)) { Check(p.Kind == Kind.Accepted, "Legacy host declined."); p.Reader.ReadBoolean(); p.Text(256); p.Reader.ReadInt32(); p.Reader.ReadInt32(); Check(p.Reader.ReadInt32() == 3, "Protocol changed."); p.Reader.ReadInt32(); p.Reader.ReadBoolean(); p.End(); }
            bool frame = false; while (!frame) using (Packet p = raw.Wire.Read(Wire.MaxPacket)) if (p.Kind == Kind.Frame) { decoder.Apply(p); raw.Wire.Send(Kind.Ack, delegate(System.IO.BinaryWriter w) { w.Write(decoder.Sequence); }); frame = true; }
            Check(decoder.Image != null, "Legacy frame format changed.");
        }
    }
    static void FolderRoundTrip()
    {
        using (var fixture = new FilesFixture())
        {
            string local = Path.Combine(fixture.Root, "local"), remote = Path.Combine(fixture.Root, "remote"), tree = Path.Combine(local, "folder fixture");
            Directory.CreateDirectory(Path.Combine(tree, "empty")); Directory.CreateDirectory(Path.Combine(tree, "nested"));
            File.WriteAllText(Path.Combine(tree, "nested", "unicode.txt"), "File content \u00e1\u03bb"); File.WriteAllBytes(Path.Combine(tree, "zero"), new byte[0]);
            for (int i = 0; i < 32; i++) File.WriteAllText(Path.Combine(tree, "small-" + i + ".txt"), "Batch receipt fixture " + i);
            string uploaded = Await(fixture.Viewer.Files.UploadFolder(tree, remote, CancellationToken.None));
            Check(Directory.Exists(Path.Combine(uploaded, "empty")) && SameFile(Path.Combine(tree, "nested", "unicode.txt"), Path.Combine(uploaded, "nested", "unicode.txt")), "Nested or empty folder missing.");
            string download = Await(fixture.Viewer.Files.DownloadFolder(uploaded, local, CancellationToken.None));
            Check(download != tree && Directory.Exists(Path.Combine(download, "empty")) && SameFile(Path.Combine(tree, "nested", "unicode.txt"), Path.Combine(download, "nested", "unicode.txt")), "Folder overwrite or round-trip failure.");
            for (int i = 0; i < 32; i++) Check(SameFile(Path.Combine(tree, "small-" + i + ".txt"), Path.Combine(download, "small-" + i + ".txt")), "A consecutive file changed during the folder round trip.");
            using (var cancel = new CancellationTokenSource()) { cancel.Cancel(); Reject(delegate { Await(fixture.Viewer.Files.UploadFolder(tree, remote, cancel.Token)); }); }
            Check(Directory.GetDirectories(remote).Length == 1 && fixture.Host.HasSession, "Cancelled folder created data or killed desktop.");
        }
    }
    static void ToolBounds()
    {
        using (var memory = new MemoryStream()) using (var tools = new SessionTools(new Wire(memory), delegate { }))
        {
            using (var packet = new Packet(Wire.Message(Kind.ToolReply, delegate(System.IO.BinaryWriter w) { w.Write(1L); w.Write(true); Wire.Text(w, ""); w.Write(Int32.MaxValue); }))) Reject(delegate { tools.Handle(packet); });
            using (var packet = new Packet(Wire.Message(Kind.Tools, delegate(System.IO.BinaryWriter w) { w.Write(1L); w.Write((byte)SessionTool.Chat); Wire.Text(w, "unsolicited"); }))) Reject(delegate { tools.Handle(packet); });
        }
        using (var failed = new ManualResetEvent(false)) using (var memory = new MemoryStream())
        {
            Exception problem = null;
            using (var tools = new SessionTools(new Wire(memory), delegate(Exception error) { problem = error; failed.Set(); }, delegate(SessionTool op, Packet body) { body.Reader.ReadInt32(); return Task.FromResult(SessionTools.Payload()); }))
            using (var packet = new Packet(Wire.Message(Kind.Tools, delegate(System.IO.BinaryWriter w) { w.Write(1L); w.Write((byte)SessionTool.Chat); })))
            { tools.Handle(packet); Check(failed.WaitOne(3000) && problem is EndOfStreamException, "Truncated tool payload was not rejected as a protocol failure."); }
        }
    }
}
