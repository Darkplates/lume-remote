using System;
using System.Collections.Concurrent;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Net;
using System.Security.Cryptography;
using System.Threading;
using System.Threading.Tasks;
using System.Windows.Forms;
using LumeRemote;

static partial class Tests
{
    static void FileClipboardChecks()
    {
        Run("Clipboard uses STA and reports a busy clipboard without crashing", ClipboardApartment);
        Run("Rapid clipboard actions and busy callbacks preserve the desktop session", ClipboardRepeat);
        Run("File paths reject traversal, device names, streams, links and wrong owners", FilePathSecurity);
        Run("File hashes, chunk offsets and size are verified before final publication", FileIntegrity);
        Run("Authenticated file upload and download preserve bytes and existing files", FileRoundTrip);
        Run("File cancellation cleans partial copies and leaves the desktop connected", FileCancellation);
        Run("View-only and legacy sessions do not acquire file access", FilePermission);
        Run("File browser loads remote folders and closes without ending the session", FileBrowserUi);
    }
    sealed class FilesFixture : IDisposable
    {
        public readonly string Root = Path.Combine(Path.GetTempPath(), "lume-files-test-" + Guid.NewGuid().ToString("N"));
        public readonly ViewerConnection Viewer = new ViewerConnection();
        public readonly HostService Host;
        public readonly ConcurrentQueue<string> Notices = new ConcurrentQueue<string>();
        readonly Task receiver;
        readonly bool keepRoot;
        public int Frames;
        public FilesFixture(bool control = true, Func<string, Task<bool>> clipboardAction = null, bool peer = false, string resumeKey = null, string root = null, bool keepRoot = false, Func<string[]> shares = null)
        {
            if (root != null) Root = root; this.keepRoot = keepRoot; Viewer.FileResumeKey = resumeKey;
            Directory.CreateDirectory(Root); Directory.CreateDirectory(Path.Combine(Root, "remote")); Directory.CreateDirectory(Path.Combine(Root, "local"));
            Host = new HostService(delegate { return new Synthetic(false); }, Profile.All[1], control, delegate { return true; }, delegate { }, delegate { }, clipboardAction, delegate { return new RemoteFileAccess(null, resumeKey, shares); });
            if (peer)
            {
                Host.StartPeer(); PeerTransport first = new PeerTransport(false), second = new PeerTransport(false); PairPeers(first, second); Host.AcceptPeer(first); Viewer.ConnectPeer(second, Host.Invite, "File test");
            }
            else { Host.Start(IPAddress.Loopback, 0, "127.0.0.1"); Viewer.Connect(Host.Invite, "File test"); }
            receiver = Task.Run(delegate
            {
                try { using (FrameDecoder decoder = new FrameDecoder()) Viewer.Receive(delegate(Packet packet) { decoder.Apply(packet); Interlocked.Increment(ref Frames); Viewer.Ack(decoder.Sequence); }, Notices.Enqueue); }
                catch (Exception) { }
            });
            Spin(delegate { return Frames > 0; }, 8000, "File fixture received no desktop.");
        }
        public void Dispose()
        {
            Viewer.Dispose(); Host.Dispose(); receiver.Wait(3000);
            if (keepRoot) return;
            for (int i = 0; i < 30; i++) { try { if (Directory.Exists(Root)) Directory.Delete(Root, true); break; } catch (IOException) { Thread.Sleep(100); } }
        }
    }
    static void Spin(Func<bool> ready, int milliseconds, string message)
    { Stopwatch clock = Stopwatch.StartNew(); while (!ready() && clock.ElapsedMilliseconds < milliseconds) Thread.Sleep(10); Check(ready(), message); }
    static T Await<T>(Task<T> task, int timeout = 20000)
    { Check(Task.WhenAny(task, Task.Delay(timeout)).GetAwaiter().GetResult() == task, "Timed out waiting for operation."); return task.GetAwaiter().GetResult(); }
    static byte[] HashFile(string path) { using (SHA256 hash = SHA256.Create()) using (FileStream file = File.OpenRead(path)) return hash.ComputeHash(file); }
    static bool SameFile(string a, string b) { return Convert.ToBase64String(HashFile(a)) == Convert.ToBase64String(HashFile(b)); }
    static void ClipboardApartment()
    {
        bool sta = false; string observed = null;
        Check(Await(ClipboardAccess.Write("clipboard fixture", delegate(string value) { sta = Thread.CurrentThread.GetApartmentState() == ApartmentState.STA; observed = value; })) && sta && observed == "clipboard fixture", "Clipboard writer did not run on STA.");
        using (ManualResetEvent entered = new ManualResetEvent(false)) using (ManualResetEvent finish = new ManualResetEvent(false))
        {
            Task<bool> pending = ClipboardAccess.Write("first", delegate { entered.Set(); finish.WaitOne(3000); });
            Check(entered.WaitOne(1000), "Clipboard worker did not start.");
            Reject(delegate { ClipboardAccess.Write("second", delegate { }); }); finish.Set(); Check(Await(pending), "Clipboard worker did not finish.");
        }
        Thread.Sleep(20); Reject(delegate { Await(ClipboardAccess.Write("busy", delegate { throw new System.Runtime.InteropServices.ExternalException(); })); });
    }
    static void ClipboardRepeat()
    {
        int written = 0;
        var copied = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        using (FilesFixture fixture = new FilesFixture(true, delegate(string text) { Interlocked.Increment(ref written); if (text == "busy") throw new InvalidOperationException(); return text == "first" ? copied.Task : Task.FromResult(true); }))
        {
            fixture.Viewer.SendClipboard("first"); fixture.Viewer.SendClipboard("second");
            Spin(delegate { return fixture.Notices.Any(item => item.Contains("already sent")); }, 3000, "Clipboard duplicate response missing.");
            copied.SetResult(true); Spin(delegate { return fixture.Notices.Any(item => item.Contains("copied on")); }, 3000, "Clipboard completion receipt missing.");
            Check(written == 1 && fixture.Host.HasSession, "A duplicate clipboard action closed the session or wrote twice.");
            Thread.Sleep(1100); fixture.Viewer.SendClipboard("busy");
            Spin(delegate { return fixture.Notices.Any(item => item.Contains("busy")); }, 3000, "Busy clipboard error was not reported.");
            Thread.Sleep(1100); fixture.Viewer.SendClipboard("recovered"); Spin(delegate { return written == 3; }, 3000, "Clipboard did not recover.");
            Check(fixture.Host.HasSession && fixture.Viewer.Failure == null, "Clipboard error killed the session.");
        }
    }
    static void FilePathSecurity()
    {
        foreach (string name in new[] { "..", ".", "../escape", "C:\\escape", "x:y", "NUL", "COM1.txt", "LPT\u00b2.log", "CONOUT$", "trailing.", "trailing ", "bad\0file" }) Reject(delegate { RemoteFileAccess.CheckName(name); });
        foreach (string path in new[] { "..\\x", "\\\\server\\share\\x", "\\\\?\\C:\\x", "C:\\safe\\..\\x", "C:\\safe\\x:stream", "C:/file" }) Reject(delegate { RemoteFileAccess.CheckPath(path); });
        Reject(delegate { new RemoteFileAccess("S-1-5-18").Run(delegate { return true; }); });
        RemoteFileAccess.CheckName("photo 01 - \u00e1.txt");
        string root = Path.Combine(Path.GetTempPath(), "lume-links-test-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        try
        {
            string target = Path.Combine(root, "target"), link = Path.Combine(root, "link"); Directory.CreateDirectory(target);
            ProcessStartInfo info = new ProcessStartInfo("cmd.exe", "/d /c mklink /J \"" + link + "\" \"" + target + "\"") { UseShellExecute = false, CreateNoWindow = true, RedirectStandardOutput = true, RedirectStandardError = true };
            using (Process process = Process.Start(info)) { process.StandardOutput.ReadToEnd(); process.StandardError.ReadToEnd(); process.WaitForExit(); Check(process.ExitCode == 0, "Could not create the owned junction fixture."); }
            try { Reject(delegate { RemoteFileAccess.CheckPath(Path.Combine(link, "file.txt")); }); }
            finally { Directory.Delete(link); }
        }
        finally { Directory.Delete(root, true); }
    }
    static void FileIntegrity()
    {
        string root = Path.Combine(Path.GetTempPath(), "lume-hash-test-" + Guid.NewGuid().ToString("N")); Directory.CreateDirectory(root);
        try
        {
            using (IncomingFile file = new IncomingFile(new RemoteFileAccess(), root, "test.bin", 3))
            {
                Reject(delegate { file.Append(1, new byte[] { 1 }); }); Reject(delegate { file.Append(0, new byte[4]); });
                file.Append(0, new byte[] { 1, 2, 3 }); Reject(delegate { file.Complete(new byte[32]); });
                Check(!File.Exists(Path.Combine(root, "test.bin")), "Corrupted transfer became a final file.");
            }
            Check(Directory.GetFiles(root).Length == 0, "Bad-hash partial file leaked.");
            using (IncomingFile file = new IncomingFile(new RemoteFileAccess(), root, "large.bin", (long)Int32.MaxValue + 1))
            { Check(file.Length > Int32.MaxValue, "File size was truncated to 32 bits."); Reject(delegate { file.Complete(new byte[32]); }); }
        }
        finally { Directory.Delete(root, true); }
    }
    static void FileRoundTrip()
    {
        using (FilesFixture fixture = new FilesFixture(true, null, true))
        {
            string local = Path.Combine(fixture.Root, "source \u00e1.bin"), remote = Path.Combine(fixture.Root, "remote"), destination = Path.Combine(fixture.Root, "local");
            byte[] payload = new byte[8 * 1024 * 1024 + 23]; new Random(307).NextBytes(payload); File.WriteAllBytes(local, payload);
            string saved = Await(fixture.Viewer.Files.Upload(local, remote, CancellationToken.None)); Check(SameFile(local, saved), "P2P upload changed the bytes.");
            string second = Await(fixture.Viewer.Files.Upload(local, remote, CancellationToken.None)); Check(saved != second && SameFile(local, saved) && SameFile(local, second), "A filename collision overwrote an existing file.");
            RemoteFileList list = Await(fixture.Viewer.Files.List(remote)); Check(list.Entries.Count == 2 && list.Entries.All(item => item.Length == payload.Length), "Remote browser returned incorrect sizes.");
            string received = Await(fixture.Viewer.Files.Download(saved, destination, CancellationToken.None)); Check(SameFile(saved, received), "P2P download changed the bytes.");
            string empty = Path.Combine(fixture.Root, "empty.txt"); File.WriteAllBytes(empty, new byte[0]);
            string emptyRemote = Await(fixture.Viewer.Files.Upload(empty, remote, CancellationToken.None));
            string emptyLocal = Await(fixture.Viewer.Files.Download(emptyRemote, destination, CancellationToken.None)); Check(new FileInfo(emptyLocal).Length == 0, "Empty file transfer failed.");
            Reject(delegate { Await(fixture.Viewer.Files.Download(Path.Combine(remote, "missing.txt"), destination, CancellationToken.None)); });
            Check(fixture.Host.HasSession && fixture.Viewer.Failure == null, "A file error ended the desktop.");
            Check(!Directory.GetFiles(remote, "*.part").Any() && !Directory.GetFiles(destination, "*.part").Any(), "Completed transfers left partial files.");
        }
    }
    static void FileCancellation()
    {
        using (FilesFixture fixture = new FilesFixture()) using (CancellationTokenSource cancelled = new CancellationTokenSource())
        {
            string local = Path.Combine(fixture.Root, "large.bin"), remote = Path.Combine(fixture.Root, "remote");
            using (FileStream file = File.Create(local)) file.SetLength(128L * 1024 * 1024);
            long acknowledged = 0;
            Action<FileProgress> stopAfterAck = delegate(FileProgress state) { if (state.Bytes > 0) { Interlocked.Exchange(ref acknowledged, state.Bytes); cancelled.Cancel(); } };
            fixture.Viewer.Files.ProgressChanged += stopAfterAck;
            Task<string> pending = fixture.Viewer.Files.Upload(local, remote, cancelled.Token);
            Reject(delegate { Await(pending); }); fixture.Viewer.Files.ProgressChanged -= stopAfterAck;
            Check(Interlocked.Read(ref acknowledged) > 0 && cancelled.IsCancellationRequested, "Cancellation did not follow an acknowledged chunk.");
            Spin(delegate { return Directory.GetFiles(remote).Length == 0; }, 5000, "Cancelled partial file leaked.");
            Check(fixture.Host.HasSession && fixture.Viewer.Failure == null, "Cancelling a transfer disconnected the desktop.");
            string next = Path.Combine(fixture.Root, "after.txt"); File.WriteAllText(next, "still connected");
            string saved = Await(fixture.Viewer.Files.Upload(next, remote, CancellationToken.None)); Check(SameFile(next, saved), "Transfer did not recover after cancellation.");
            int before = fixture.Frames; fixture.Viewer.SetQuality(new StreamQuality { Height = 360, Fps = 10, Lossless = true });
            Spin(delegate { return fixture.Frames > before; }, 5000, "Desktop frames stopped after a cancelled transfer.");
        }
    }
    static void FilePermission()
    {
        using (FilesFixture fixture = new FilesFixture(false)) Check(fixture.Viewer.Files == null, "View-only session obtained file access.");
        using (HostService host = NewHost(true, true)) using (RawClient raw = new RawClient(host.Invite))
        {
            raw.Auth(host.Invite.Secret); using (Packet packet = raw.Wire.Read(2048)) Check(packet.Kind == Kind.Accepted, "Legacy acceptance failed.");
            raw.Wire.Send(Kind.Files, delegate(BinaryWriter w) { w.Write((byte)FileOp.List); Wire.Text(w, Guid.NewGuid().ToString("N")); Wire.Text(w, ""); w.Write(0); });
            bool ended = false;
            try { for (int i = 0; i < 20; i++) using (Packet packet = raw.Wire.Read(Wire.MaxPacket)) { if (packet.Kind == Kind.Frame) { int sequence = packet.Reader.ReadInt32(); raw.Wire.Send(Kind.Ack, delegate(BinaryWriter w) { w.Write(sequence); }); } } }
            catch (IOException) { ended = true; }
            Check(ended, "Unnegotiated file packets were accepted.");
        }
    }
    static void FileBrowserUi()
    {
        using (FilesFixture fixture = new FilesFixture()) using (FileTransferForm form = new FileTransferForm(fixture.Viewer.Files, "Test PC"))
        {
            File.WriteAllText(Path.Combine(fixture.Root, "remote", "example.txt"), "file browser fixture");
            form.Show(); Application.DoEvents(); PumpUntil(delegate { return !((bool)Field(form, "loading")) && ((ListView)Field(form, "entries")).Items.Count > 0; }, 5000, "Drive browser did not load.");
            // DoEvents restores its caller's context; reproduce a real UI callback.
            SynchronizationContext.SetSynchronizationContext(new WindowsFormsSynchronizationContext());
            ((TextBox)Field(form, "path")).Text = Path.Combine(fixture.Root, "remote"); FindButton(form, "Go").PerformClick();
            try { PumpUntil(delegate { return !((bool)Field(form, "loading")) && (string)Field(form, "directory") == Path.Combine(fixture.Root, "remote"); }, 5000, "Remote folder did not load."); }
            catch { Console.WriteLine("BROWSER: loading=" + Field(form, "loading") + "; status=" + ((Label)Field(form, "status")).Text); throw; }
            Check(((ListView)Field(form, "entries")).Items.Count == 1 && ((ListView)Field(form, "entries")).Items[0].Text == "example.txt", "Browser did not show the real remote fixture file: " + ((Label)Field(form, "status")).Text);
            form.Close(); Check(fixture.Host.HasSession, "Closing the file browser ended the session.");
        }
    }
    static void InstalledFiles()
    {
        TrustedStore store = TrustedStore.Machine; HostPreferences before = store.ReadHost();
        Check(PermanentAccess.Installed && before.Enabled && String.IsNullOrEmpty(before.PairKey), "An enabled installed host with no pending pairing is required.");
        string statusFile = Path.Combine(store.DirectoryPath, "status.txt");
        Check(!File.ReadAllText(statusFile).Contains("Session active"), "An existing user session is active. It was not interrupted.");
        string root = Path.Combine(Path.GetTempPath(), "lume-installed-files-" + Guid.NewGuid().ToString("N")); Directory.CreateDirectory(root);
        string testId = null, denied = Path.Combine(root, "denied.txt"); System.Security.AccessControl.FileSecurity originalAcl = null;
        try
        {
            string remote = Path.Combine(root, "remote"), local = Path.Combine(root, "local"); Directory.CreateDirectory(remote); Directory.CreateDirectory(local);
            string source = Path.Combine(root, "source.bin"); byte[] bytes = new byte[1024 * 1024 + 17]; new Random(197).NextBytes(bytes); File.WriteAllBytes(source, bytes);
            File.WriteAllText(denied, "An owner ACL denial must also deny the installed SYSTEM file server."); originalAcl = File.GetAccessControl(denied);
            System.Security.AccessControl.FileSecurity restriction = File.GetAccessControl(denied);
            restriction.AddAccessRule(new System.Security.AccessControl.FileSystemAccessRule(System.Security.Principal.WindowsIdentity.GetCurrent().User, System.Security.AccessControl.FileSystemRights.ReadData, System.Security.AccessControl.AccessControlType.Deny));
            File.SetAccessControl(denied, restriction);
            using (CancellationTokenSource timeout = new CancellationTokenSource(90000))
            {
                PairingCode code = store.CreatePairing(false, null); testId = code.id;
                SavedComputer computer = Await(PairedClient.Pair(code, "Owned file acceptance fixture", timeout.Token), 30000);
                using (PairedLink link = Await(PairedClient.Connect(computer, delegate { }, timeout.Token), 60000)) using (ViewerConnection viewer = new ViewerConnection())
                {
                    viewer.ConnectPeer(link.Peer, link.Invitation, "Owned file acceptance viewer"); Check(viewer.Files != null, "Update the installed host to 0.4 before this acceptance check.");
                    Task receiving = Task.Run(delegate
                    {
                        try { using (FrameDecoder decoder = new FrameDecoder()) viewer.Receive(delegate(Packet packet) { decoder.Apply(packet); viewer.Ack(decoder.Sequence); }, delegate { }); }
                        catch (IOException) { } catch (ObjectDisposedException) { }
                    });
                    try
                    {
                        string uploaded = Await(viewer.Files.Upload(source, remote, timeout.Token)); Check(SameFile(source, uploaded), "Installed-host upload bytes differ.");
                        string downloaded = Await(viewer.Files.Download(uploaded, local, timeout.Token)); Check(SameFile(source, downloaded), "Installed-host download bytes differ.");
                        Reject(delegate { Await(viewer.Files.Download(denied, local, timeout.Token)); });
                        Check(!File.Exists(Path.Combine(local, "denied.txt")), "SYSTEM file access bypassed the owner's read denial.");
                        Check(Await(viewer.Files.List(remote)).Entries.Count == 1, "File access did not recover after ACL denial.");
                        Console.WriteLine("INSTALLED_FILES: SHA-256 upload/download verified; signed-in owner ACL denial enforced; desktop transport stayed active. Only fixture files used; no clipboard or global input changed.");
                    }
                    finally { viewer.Dispose(); receiving.Wait(5000); }
                }
            }
        }
        finally
        {
            if (testId != null) store.ChangeHost(delegate(HostPreferences host) { host.Controllers.RemoveAll(item => item.Id == testId); if (host.PairId == testId) { host.PairId = host.PairKey = null; host.PairExpires = 0; } });
            if (originalAcl != null) File.SetAccessControl(denied, originalAcl);
            if (Directory.Exists(root)) Directory.Delete(root, true);
        }
    }
}
