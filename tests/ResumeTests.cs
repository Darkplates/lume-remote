using System;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Threading;
using System.Threading.Tasks;
using LumeRemote;

static partial class Tests
{
    static void ResumeChecks()
    {
        Run("Resume journals authenticate metadata and isolate paired computers", ResumeIdentity);
        Run("Interrupted upload reconnects and sends only the verified suffix", ResumeUpload);
        Run("Downloads reuse verified prefixes and preserve existing final files", ResumeDownload);
        Run("Corrupted prefixes are discarded without closing the desktop", ResumeCorruption);
        Run("Explicit cancellation discards resumable state and allows another transfer", ResumeCancel);
        Run("Only owner-configured network roots are advertised and authorized", NetworkRootPolicy);
        Run("Interrupted downloads preserve state when their disconnected UI cancels", ResumeDownloadDisconnect);
    }
    static void ResumeIdentity()
    {
        string root = Path.Combine(Path.GetTempPath(), "lume-resume-identity-" + Guid.NewGuid().ToString("N")); Directory.CreateDirectory(root);
        string key = Security.Token(32); byte[] bytes = { 1, 2, 3, 4, 5 }, digest; using (SHA256 hash = SHA256.Create()) digest = hash.ComputeHash(bytes);
        var access = new RemoteFileAccess(null, key);
        try
        {
            using (var first = new IncomingFile(access, root, "data.bin", bytes.Length, digest)) { first.Append(0, new byte[] { 1, 2, 3 }); first.Suspend(); }
            string state = Directory.GetFiles(root, "*.state").Single(), part = Directory.GetFiles(root, "*.part").Single();
            Check(File.ReadAllBytes(part).SequenceEqual(new byte[] { 1, 2, 3 }), "Partial bytes were lost.");
            Check(access.List(root, 0).Entries.Count == 0, "Internal transfer state leaked into the file browser.");
            using (var other = new IncomingFile(new RemoteFileAccess(null, Security.Token(32)), root, "data.bin", bytes.Length, digest)) Check(other.Position == 0, "A different pair resumed another pair's transfer.");
            Check(File.Exists(part) && File.Exists(state), "Another pair discarded the original state.");
            byte[] valid = File.ReadAllBytes(state), invalid = (byte[])valid.Clone(); invalid[20] ^= 1; File.WriteAllBytes(state, invalid);
            Reject(delegate { using (var bad = new IncomingFile(access, root, "data.bin", bytes.Length, digest)) { } });
            Check(!File.Exists(Path.Combine(root, "data.bin")), "Invalid state became a final file."); File.WriteAllBytes(state, valid);
            using (var resumed = new IncomingFile(access, root, "data.bin", bytes.Length, digest))
            { Check(resumed.Position == 3, "Valid checkpoint did not resume."); resumed.Append(3, new byte[] { 4, 5 }); Check(File.ReadAllBytes(resumed.Complete(digest)).SequenceEqual(bytes), "Resumed content changed."); }
            Check(Directory.GetFiles(root).Length == 1, "Successful completion kept its journal.");
        }
        finally { Directory.Delete(root, true); }
    }
    static bool PartialClosed(string folder)
    {
        string[] files = Directory.GetFiles(folder, "*.part"); if (files.Length != 1) return false;
        try { using (var file = new FileStream(files[0], FileMode.Open, FileAccess.Read, FileShare.None)) return file.Length > 0; } catch (IOException) { return false; }
    }
    static void ResumeUpload()
    {
        string root = Path.Combine(Path.GetTempPath(), "lume-resume-upload-" + Guid.NewGuid().ToString("N")); Directory.CreateDirectory(root);
        string key = Security.Token(32), source = Path.Combine(root, "source.bin"), remote = Path.Combine(root, "remote");
        byte[] bytes = new byte[16 * 1024 * 1024 + 31]; new Random(5621).NextBytes(bytes); File.WriteAllBytes(source, bytes);
        try
        {
            using (var first = new FilesFixture(resumeKey: key, root: root, keepRoot: true))
            {
                Check(first.Viewer.Files.Resume, "Paired resume was not negotiated."); int cut = 0;
                first.Viewer.Files.ProgressChanged += delegate(FileProgress progress) { if (progress.Bytes > 0 && Interlocked.Exchange(ref cut, 1) == 0) first.Viewer.Dispose(); };
                Reject(delegate { Await(first.Viewer.Files.Upload(source, remote, CancellationToken.None)); });
                Spin(delegate { return PartialClosed(remote); }, 8000, "Transport loss did not preserve a closed partial file.");
            }
            long offset = new FileInfo(Directory.GetFiles(remote, "*.part").Single()).Length; Check(offset > 0 && offset < bytes.Length, "The interrupted transfer was not partial.");
            using (var second = new FilesFixture(resumeKey: key, root: root, keepRoot: true))
            {
                long firstAck = -1; second.Viewer.Files.ProgressChanged += delegate(FileProgress progress) { Interlocked.CompareExchange(ref firstAck, progress.Bytes, -1); };
                string saved = Await(second.Viewer.Files.Upload(source, remote, CancellationToken.None));
                Check(SameFile(source, saved) && firstAck > offset, "The transfer restarted or changed bytes instead of resuming.");
                Check(!Directory.GetFiles(remote).Any(p => p.EndsWith(".part") || p.EndsWith(".state")), "Completion kept partial state.");
            }
        }
        finally { Directory.Delete(root, true); }
    }
    static void SeedPartial(string key, string source, string folder, int length, bool corrupt)
    {
        byte[] prefix = new byte[length]; using (var input = File.OpenRead(source)) { int offset = 0; while (offset < prefix.Length) offset += input.Read(prefix, offset, prefix.Length - offset); }
        using (var incoming = new IncomingFile(new RemoteFileAccess(null, key), folder, Path.GetFileName(source), new FileInfo(source).Length, HashFile(source)))
        {
            for (int position = 0; position < prefix.Length; position += 65536) { int count = Math.Min(65536, prefix.Length - position); byte[] chunk = new byte[count]; Buffer.BlockCopy(prefix, position, chunk, 0, count); incoming.Append(position, chunk); }
            incoming.Suspend();
        }
        if (corrupt) { string part = Directory.GetFiles(folder, "*.part").Single(); using (var file = File.Open(part, FileMode.Open, FileAccess.Write)) file.WriteByte((byte)(prefix[0] ^ 1)); }
    }
    static void ResumeDownload()
    {
        string key = Security.Token(32);
        using (var fixture = new FilesFixture(resumeKey: key))
        {
            string source = Path.Combine(fixture.Root, "remote", "source.bin"), destination = Path.Combine(fixture.Root, "local");
            byte[] bytes = new byte[3 * 1024 * 1024 + 21]; new Random(76).NextBytes(bytes); File.WriteAllBytes(source, bytes);
            File.WriteAllText(Path.Combine(destination, "source.bin"), "existing file"); SeedPartial(key, source, destination, 1024 * 1024, false);
            long receivedBefore = fixture.Viewer.Received; string saved = Await(fixture.Viewer.Files.Download(source, destination, CancellationToken.None));
            Check(SameFile(source, saved) && File.ReadAllText(Path.Combine(destination, "source.bin")) == "existing file", "Download resume changed data or overwrote an existing file.");
            Check(fixture.Viewer.Received - receivedBefore < bytes.Length, "Download did not skip its prefix.");
        }
    }
    static void ResumeCorruption()
    {
        string key = Security.Token(32);
        using (var fixture = new FilesFixture(resumeKey: key))
        {
            string source = Path.Combine(fixture.Root, "source.bin"), remote = Path.Combine(fixture.Root, "remote"); byte[] bytes = new byte[512 * 1024]; new Random(92).NextBytes(bytes); File.WriteAllBytes(source, bytes);
            SeedPartial(key, source, remote, 131072, true); Reject(delegate { Await(fixture.Viewer.Files.Upload(source, remote, CancellationToken.None)); });
            Spin(delegate { return Directory.GetFiles(remote).Length == 0; }, 5000, "Corrupted partial state was not removed.");
            Check(fixture.Host.HasSession && fixture.Viewer.Failure == null, "Resume rejection closed the desktop.");
            Check(SameFile(source, Await(fixture.Viewer.Files.Upload(source, remote, CancellationToken.None))), "Fresh retry after corruption did not recover.");
        }
    }
    static void ResumeCancel()
    {
        using (var fixture = new FilesFixture(resumeKey: Security.Token(32))) using (var cancel = new CancellationTokenSource())
        {
            string source = Path.Combine(fixture.Root, "cancel.bin"), remote = Path.Combine(fixture.Root, "remote"); using (var file = File.Create(source)) file.SetLength(16 * 1024 * 1024);
            fixture.Viewer.Files.ProgressChanged += delegate(FileProgress progress) { if (progress.Bytes > 0) cancel.Cancel(); };
            Reject(delegate { Await(fixture.Viewer.Files.Upload(source, remote, cancel.Token)); });
            Spin(delegate { return Directory.GetFiles(remote).Length == 0; }, 5000, "Explicit cancellation kept resumable state.");
            Check(fixture.Host.HasSession && fixture.Viewer.Failure == null, "Cancellation closed the desktop.");
        }
    }
    static void NetworkRootPolicy()
    {
        string[] roots = { "\\\\fixture-host\\share\\allowed" }; var access = new RemoteFileAccess(null, null, delegate { return roots; });
        Check(access.AuthorizedSharePath("\\\\FIXTURE-HOST\\share\\allowed\\file.txt") == "\\\\FIXTURE-HOST\\share\\allowed\\file.txt", "Case-insensitive configured root was rejected.");
        foreach (string path in new[] { "\\\\fixture-host\\share\\allowed-other", "\\\\fixture-host\\share", "\\\\another-host\\share\\allowed", "\\\\?\\C:\\folder", "\\\\fixture-host\\share\\allowed\\..\\outside", "\\\\fixture-host\\share\\allowed\\file:stream", "\\\\fixture-host\\share\\allowed\\CON" }) Reject(delegate { access.AuthorizedSharePath(path); });
        Check(access.List("", 0).Entries.All(e => !e.Name.StartsWith("\\\\")), "Legacy listing exposed a new root layout.");
        Check(access.List("", 0, true).Entries.Any(e => e.Name == roots[0]), "Configured root was not listed.");
        using (var fixture = new FilesFixture(shares: delegate { return roots; }))
        {
            Check(fixture.Viewer.Files.NetworkFolders && Await(fixture.Viewer.Files.List("")).Entries.Any(e => e.Name == roots[0]), "Network roots were not negotiated end to end.");
            roots = new string[0]; Reject(delegate { access.AuthorizedSharePath("\\\\fixture-host\\share\\allowed\\file.txt"); });
            Check(Await(fixture.Viewer.Files.List("")).Entries.All(e => !e.Name.StartsWith("\\\\")), "Removed root was still advertised.");
            Check(fixture.Host.HasSession, "Updating configured roots closed the desktop.");
        }
        HostPreferences old = JsonData.Decode<HostPreferences>("{}"); Check(old.NetworkFolders != null && old.NetworkFolders.Count == 0, "Old host settings did not default to no shared network roots.");
        Console.WriteLine("SMB: path policy and negotiated root listing only; no physical SMB share was contacted.");
    }
    static void ResumeDownloadDisconnect()
    {
        string root = Path.Combine(Path.GetTempPath(), "lume-resume-download-" + Guid.NewGuid().ToString("N")); Directory.CreateDirectory(root);
        string key = Security.Token(32), source = Path.Combine(root, "remote", "large.bin"), local = Path.Combine(root, "local");
        try
        {
            using (var fixture = new FilesFixture(resumeKey: key, root: root, keepRoot: true)) using (var cancelledUi = new CancellationTokenSource())
            {
                using (var file = File.Create(source)) file.SetLength(64 * 1024 * 1024);
                var transfer = fixture.Viewer.Files.Download(source, local, cancelledUi.Token);
                Spin(delegate { FileProgress p = fixture.Viewer.Files.Progress; return p != null && p.Bytes > 0; }, 6000, "Download made no progress.");
                fixture.Viewer.Dispose(); cancelledUi.Cancel(); Reject(delegate { Await(transfer); });
                Spin(delegate { return PartialClosed(local); }, 8000, "Disconnected UI cancellation deleted the resumable download.");
            }
            long prefix = new FileInfo(Directory.GetFiles(local, "*.part").Single()).Length; Check(prefix > 0 && prefix < new FileInfo(source).Length, "Download did not stop partway.");
            using (var fixture = new FilesFixture(resumeKey: key, root: root, keepRoot: true)) Check(SameFile(source, Await(fixture.Viewer.Files.Download(source, local, CancellationToken.None))), "Interrupted download did not resume correctly.");
        }
        finally { Directory.Delete(root, true); }
    }
}
