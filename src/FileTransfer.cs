using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Security.Cryptography;
using System.Threading;
using System.Threading.Tasks;

namespace LumeRemote
{
    internal enum FileOp : byte { List = 1, Listing, Get, Put, Offer, Chunk, Finish, Ack, Cancel, Result, MakeDirectory, GetResume, PutResume, OfferResume, ResumeReady, Preparing, Roots }
    public sealed class FileProgress
    {
        public string Name, Direction;
        public long Bytes, Total;
    }
    public sealed class FileTransfer : IDisposable
    {
        const int ChunkSize = 65536, WindowSize = 8 * ChunkSize;
        readonly Wire wire;
        readonly bool server;
        public bool Folders { get; private set; }
        public bool Resume { get; private set; }
        public bool NetworkFolders { get; private set; }
        public event Action<FileProgress> ProgressChanged;
        readonly Dictionary<string, TaskCompletionSource<string>> operations = new Dictionary<string, TaskCompletionSource<string>>();
        readonly RemoteFileAccess access;
        readonly Action<Exception> failed;
        readonly object gate = new object();
        readonly BlockingCollection<Action> work = new BlockingCollection<Action>(32);
        readonly Dictionary<string, TaskCompletionSource<RemoteFileList>> lists = new Dictionary<string, TaskCompletionSource<RemoteFileList>>();
        readonly Task worker;
        readonly System.Threading.Timer expiry;
        Transfer sending, receiving;
        volatile bool disposed;
        volatile bool transportLost;
        sealed class Transfer
        {
            public string Id, Name, Folder;
            public long Length, Sent, Ack, Activity = Stopwatch.GetTimestamp();
            public bool Ready, Cancelled, Resumable;
            public long ResumeOffset, PreparationSent;
            public byte[] PrefixHash;
            public IncomingFile Incoming;
            public readonly TaskCompletionSource<string> Done = new TaskCompletionSource<string>(TaskCreationOptions.RunContinuationsAsynchronously);
        }
        public FileTransfer(Wire wire, bool server, RemoteFileAccess access, Action<Exception> failed, bool folders = false, bool resume = false, bool networkFolders = false)
        {
            this.wire = wire; this.server = server; Folders = folders; this.access = access; this.failed = failed; Resume = resume && access.ResumeKey != null;
            NetworkFolders = networkFolders && (!server || access.HasSharedRoots);
            worker = BackgroundWork.Run(delegate
            {
                try { foreach (Action action in work.GetConsumingEnumerable()) { if (!disposed) action(); } }
                finally { Transfer remaining; lock (gate) { remaining = receiving; receiving = null; } if (remaining != null && remaining.Incoming != null) { if (!remaining.Cancelled) remaining.Incoming.Suspend(); remaining.Incoming.Dispose(); } }
            });
            expiry = new System.Threading.Timer(delegate
            {
                string id = null; lock (gate) if (receiving != null && (Stopwatch.GetTimestamp() - receiving.Activity) / (double)Stopwatch.Frequency > 30) id = receiving.Id;
                if (id != null) try { Queue(id, delegate { EndIncoming(id, new IOException("File transfer stalled. Retry the file."), true); SendResult(id, false, "File transfer stalled. Retry the file."); }); } catch (Exception error) { failed(error); }
            }, null, 5000, 5000);
        }
        public FileProgress Progress
        {
            get { lock (gate) { Transfer item = sending ?? receiving; return item == null ? null : new FileProgress { Name = item.Name, Direction = sending != null ? "Sending" : "Receiving", Total = item.Length, Bytes = sending != null ? item.Ack : item.Incoming == null ? 0 : item.Incoming.Position }; } }
        }
        void Send(FileOp op, string id, Action<BinaryWriter> write = null)
        {
            if (disposed) throw new OperationCanceledException("The session ended.");
            try { wire.Send(Kind.Files, delegate(BinaryWriter w) { w.Write((byte)op); Wire.Text(w, id); if (write != null) write(w); }); }
            catch (Exception error) { transportLost = true; failed(error); throw; }
        }
        void SendResult(string id, bool success, string message)
        { Send(FileOp.Result, id, delegate(BinaryWriter w) { w.Write(success); Wire.Text(w, message.Length > 480 ? message.Substring(0, 480) : message); }); }
        void Queue(string id, Action action)
        {
            if (disposed) return;
            Action guarded = delegate
            {
                try { action(); }
                catch (Exception error)
                {
                    EndIncoming(id, error);
                    if (!disposed) try { SendResult(id, false, "File operation failed: " + error.Message); } catch { }
                }
            };
            try { if (!work.TryAdd(guarded)) throw new InvalidDataException("Too many pending file requests."); }
            catch (InvalidOperationException) { if (!disposed) throw; }
        }
        public async Task<RemoteFileList> List(string path, int page = 0)
        {
            if (server) throw new InvalidOperationException();
            string id = Guid.NewGuid().ToString("N"); TaskCompletionSource<RemoteFileList> completion = new TaskCompletionSource<RemoteFileList>(TaskCreationOptions.RunContinuationsAsynchronously);
            lock (gate) { if (disposed) throw new OperationCanceledException(); if (lists.Count >= 4) throw new InvalidOperationException("Wait for the current folder to load."); lists.Add(id, completion); }
            try
            {
                Send(NetworkFolders && path.Length == 0 ? FileOp.Roots : FileOp.List, id, delegate(BinaryWriter w) { Wire.Text(w, path); w.Write(page); });
                if (await Task.WhenAny(completion.Task, Task.Delay(30000)).ConfigureAwait(false) != completion.Task) throw new TimeoutException("The remote folder did not respond.");
                return await completion.Task.ConfigureAwait(false);
            }
            finally { lock (gate) lists.Remove(id); }
        }
        public async Task<string> CreateFolder(string parent, string name, bool unique)
        {
            if (server || !Folders) throw new InvalidOperationException("Update both PCs to transfer folders.");
            RemoteFileAccess.CheckName(name); string id = Guid.NewGuid().ToString("N");
            var result = new TaskCompletionSource<string>(TaskCreationOptions.RunContinuationsAsynchronously);
            lock (gate) { if (disposed) throw new OperationCanceledException(); if (operations.Count >= 4) throw new InvalidOperationException("Wait for the current folder action."); operations.Add(id, result); }
            try
            {
                await Task.Run(delegate { Send(FileOp.MakeDirectory, id, delegate(BinaryWriter w) { Wire.Text(w, parent); Wire.Text(w, name); w.Write(unique); }); }).ConfigureAwait(false);
                if (await Task.WhenAny(result.Task, Task.Delay(30000)).ConfigureAwait(false) != result.Task) throw new TimeoutException("The remote folder did not respond.");
                return RemoteFileAccess.CheckRemotePath(await result.Task.ConfigureAwait(false), NetworkFolders);
            }
            finally { lock (gate) operations.Remove(id); }
        }
        public Task<string> UploadFolder(string local, string remoteParent, CancellationToken cancel)
        {
            if (!Folders) throw new InvalidOperationException("Update both PCs to transfer folders.");
            return Task.Run(async delegate { return await UploadTree(RemoteFileAccess.CheckPath(local.TrimEnd('\\')), remoteParent, cancel, 0).ConfigureAwait(false); });
        }
        async Task<string> UploadTree(string local, string parent, CancellationToken cancel, int depth)
        {
            cancel.ThrowIfCancellationRequested(); if (depth > 64) throw new IOException("This folder tree is too deep.");
            string destination = await CreateFolder(parent, Path.GetFileName(local), depth == 0).ConfigureAwait(false);
            foreach (string entry in Directory.EnumerateFileSystemEntries(local))
            {
                cancel.ThrowIfCancellationRequested(); RemoteFileAccess.CheckPath(entry);
                if (Directory.Exists(entry)) await UploadTree(entry, destination, cancel, depth + 1).ConfigureAwait(false);
                else await Upload(entry, destination, cancel).ConfigureAwait(false);
            }
            return destination;
        }
        public Task<string> DownloadFolder(string remote, string localParent, CancellationToken cancel)
        {
            if (!Folders) throw new InvalidOperationException("Update both PCs to transfer folders.");
            return Task.Run(async delegate { return await DownloadTree(remote.TrimEnd('\\'), localParent, cancel, 0).ConfigureAwait(false); });
        }
        async Task<string> DownloadTree(string remote, string parent, CancellationToken cancel, int depth)
        {
            cancel.ThrowIfCancellationRequested(); if (depth > 64) throw new IOException("This folder tree is too deep.");
            string destination = access.CreateFolder(parent, Path.GetFileName(remote), depth == 0);
            int page = 0;
            do
            {
                RemoteFileList list = await List(remote, page++).ConfigureAwait(false);
                foreach (RemoteFileEntry entry in list.Entries)
                {
                    cancel.ThrowIfCancellationRequested(); string source = Path.Combine(remote, entry.Name);
                    if (entry.Directory) await DownloadTree(source, destination, cancel, depth + 1).ConfigureAwait(false);
                    else await Download(source, destination, cancel).ConfigureAwait(false);
                }
                if (!list.More) break;
            } while (true);
            return destination;
        }
        public Task<string> Upload(string localPath, string remoteFolder, CancellationToken cancel)
        {
            if (server) throw new InvalidOperationException();
            Transfer transfer = NewSending(Path.GetFileName(localPath)); transfer.Resumable = Resume;
            return SendFile(transfer, localPath, remoteFolder, false, cancel);
        }
        public async Task<string> Download(string remotePath, string localFolder, CancellationToken cancel)
        {
            if (server) throw new InvalidOperationException();
            string name = Path.GetFileName(remotePath); RemoteFileAccess.CheckName(name);
            Transfer transfer = new Transfer { Id = Guid.NewGuid().ToString("N"), Name = name, Folder = localFolder, Resumable = Resume };
            lock (gate) { CheckAvailable(); receiving = transfer; }
            try
            {
                using (cancel.Register(delegate { Cancel(transfer.Id); }))
                { cancel.ThrowIfCancellationRequested(); Send(Resume ? FileOp.GetResume : FileOp.Get, transfer.Id, delegate(BinaryWriter w) { Wire.Text(w, remotePath); }); return await transfer.Done.Task.ConfigureAwait(false); }
            }
            finally { if (!disposed) Queue(transfer.Id, delegate { EndIncoming(transfer.Id, new OperationCanceledException()); }); }
        }
        void CheckAvailable()
        { if (disposed) throw new OperationCanceledException(); if (sending != null || receiving != null) throw new InvalidOperationException("Finish or cancel the current transfer first."); }
        Transfer NewSending(string name, string id = null)
        {
            RemoteFileAccess.CheckName(name); Transfer item = new Transfer { Id = id ?? Guid.NewGuid().ToString("N"), Name = name };
            lock (gate) { CheckAvailable(); sending = item; } return item;
        }
        async Task<string> SendFile(Transfer item, string path, string remoteFolder, bool offer, CancellationToken cancel)
        {
            try
            {
                return await BackgroundWork.Run(delegate
                {
                    using (cancel.Register(delegate { Cancel(item.Id); }))
                    using (FileStream file = access.OpenRead(path))
                    using (SHA256 hash = SHA256.Create())
                    {
                        cancel.ThrowIfCancellationRequested(); item.Length = file.Length;
                        byte[] identity = null;
                        if (item.Resumable) { using (SHA256 whole = SHA256.Create()) { HashPrefix(file, whole, item.Length, item); whole.TransformFinalBlock(new byte[0], 0, 0); identity = whole.Hash; } access.Run(delegate { file.Position = 0; }); }
                        Send(item.Resumable ? (offer ? FileOp.OfferResume : FileOp.PutResume) : (offer ? FileOp.Offer : FileOp.Put), item.Id, delegate(BinaryWriter w) { if (!offer) Wire.Text(w, remoteFolder); Wire.Text(w, item.Name); w.Write(item.Length); if (identity != null) w.Write(identity); });
                        Wait(item, delegate { return item.Ready; });
                        if (item.Resumable)
                        {
                            using (SHA256 prefix = SHA256.Create()) { HashPrefix(file, prefix, item.ResumeOffset, item, hash); prefix.TransformFinalBlock(new byte[0], 0, 0); if (!ResumeJournal.Equal(prefix.Hash, item.PrefixHash)) throw new InvalidDataException("The saved prefix changed. Retry to start a fresh verified transfer."); }
                            lock (gate) { item.Sent = item.Ack = item.ResumeOffset; item.Activity = Stopwatch.GetTimestamp(); }
                        }
                        byte[] buffer = new byte[ChunkSize];
                        while (item.Sent < item.Length)
                        {
                            Wait(item, delegate { return item.Sent - item.Ack < WindowSize; });
                            int count = access.Run(delegate { return file.Read(buffer, 0, (int)Math.Min(buffer.Length, item.Length - item.Sent)); });
                            if (count == 0) throw new IOException("The source file changed while sending.");
                            hash.TransformBlock(buffer, 0, count, null, 0); long offset;
                            lock (gate) { if (item.Cancelled) throw new OperationCanceledException(); offset = item.Sent; item.Sent += count; }
                            Send(FileOp.Chunk, item.Id, delegate(BinaryWriter w) { w.Write(offset); w.Write(count); w.Write(buffer, 0, count); });
                        }
                        Wait(item, delegate { return item.Ack == item.Length; }); hash.TransformFinalBlock(new byte[0], 0, 0);
                        Send(FileOp.Finish, item.Id, delegate(BinaryWriter w) { w.Write(hash.Hash); });
                        Wait(item, delegate { return item.Done.Task.IsCompleted; });
                        return item.Done.Task.GetAwaiter().GetResult();
                    }
                }).ConfigureAwait(false);
            }
            catch (Exception error)
            {
                if (!disposed) try { SendResult(item.Id, false, error is OperationCanceledException ? "Transfer cancelled." : "File transfer failed: " + error.Message); } catch { }
                throw;
            }
            finally { lock (gate) { if (sending == item) sending = null; } }
        }
        void Preparing(Transfer item)
        {
            if (disposed || item.Cancelled) throw new OperationCanceledException();
            long now = Stopwatch.GetTimestamp();
            Interlocked.Exchange(ref item.Activity, now);
            if (now - item.PreparationSent >= Stopwatch.Frequency) { item.PreparationSent = now; Send(FileOp.Preparing, item.Id); }
        }
        void HashPrefix(FileStream file, SHA256 hash, long length, Transfer item, SHA256 second = null)
        {
            byte[] buffer = new byte[ChunkSize]; long remaining = length;
            while (remaining > 0) { Preparing(item); int count = access.Run(delegate { return file.Read(buffer, 0, (int)Math.Min(buffer.Length, remaining)); }); if (count == 0) throw new EndOfStreamException(); hash.TransformBlock(buffer, 0, count, null, 0); if (second != null) second.TransformBlock(buffer, 0, count, null, 0); remaining -= count; }
        }
        void Wait(Transfer item, Func<bool> ready)
        {
            lock (gate)
            {
                while (true)
                {
                    if (disposed || item.Cancelled) throw new OperationCanceledException("Transfer cancelled.");
                    if (item.Done.Task.IsFaulted) item.Done.Task.GetAwaiter().GetResult();
                    if (ready()) return;
                    if ((Stopwatch.GetTimestamp() - item.Activity) / (double)Stopwatch.Frequency > 30) throw new TimeoutException("The file transfer stopped responding.");
                    Monitor.Wait(gate, 1000);
                }
            }
        }
        public void CancelAll()
        { string id; lock (gate) id = sending != null ? sending.Id : receiving != null ? receiving.Id : null; if (id != null) Cancel(id); }
        void Cancel(string id)
        {
            if (disposed || transportLost) return;
            lock (gate) { if (sending != null && sending.Id == id) sending.Cancelled = true; if (receiving != null && receiving.Id == id) { receiving.Cancelled = true; receiving.Done.TrySetCanceled(); } Monitor.PulseAll(gate); }
            Queue(id, delegate { EndIncoming(id, new OperationCanceledException()); });
            if (!disposed) try { Send(FileOp.Cancel, id); } catch { }
        }
        // A stall or a sender-side failure looks like a network loss, so a paired resumable
        // partial is kept (Suspend is a no-op otherwise); retry verifies its prefix.
        void EndIncoming(string id, Exception error, bool preserve = false)
        {
            Transfer item;
            lock (gate)
            {
                if (receiving == null || receiving.Id != id) return;
                item = receiving;
                if (error is OperationCanceledException) item.Done.TrySetCanceled(); else item.Done.TrySetException(error);
                receiving = null;
            }
            if (item.Incoming != null) { if ((disposed || transportLost || preserve) && !item.Cancelled) item.Incoming.Suspend(); item.Incoming.Dispose(); }
        }
        public bool Handle(Packet packet)
        {
            if (packet.Kind != Kind.Files) return false;
            if (packet.Reader.BaseStream.Length > 262144) throw new InvalidDataException("File packet is too large.");
            FileOp op = (FileOp)packet.Reader.ReadByte(); string id = packet.Text(32);
            if (!Invitation.IsHex(id, 32)) throw new InvalidDataException("Invalid file request identifier.");
            switch (op)
            {
                case FileOp.List:
                case FileOp.Roots:
                    if (!server) throw new InvalidDataException("Unexpected file browser request.");
                    string directory = packet.Text(4096); int page = packet.Reader.ReadInt32(); packet.End();
                    bool roots = op == FileOp.Roots; if (roots && (!NetworkFolders || directory.Length != 0 || page != 0)) throw new InvalidDataException("Network roots were not negotiated.");
                    Queue(id, delegate
                    {
                        RemoteFileList list = access.List(directory, page, roots);
                        Send(FileOp.Listing, id, delegate(BinaryWriter w)
                        {
                            Wire.Text(w, list.Path); w.Write(list.Page); w.Write(list.More); w.Write(list.Entries.Count);
                            foreach (RemoteFileEntry entry in list.Entries) { Wire.Text(w, entry.Name); w.Write(entry.Directory); w.Write(entry.Length); }
                        });
                    }); break;
                case FileOp.Listing:
                    if (server) throw new InvalidDataException("Unexpected folder response.");
                    RemoteFileList response = new RemoteFileList { Path = packet.Text(4096), Page = packet.Reader.ReadInt32(), More = packet.Reader.ReadBoolean() };
                    int count = packet.Reader.ReadInt32(); if (count < 0 || count > 200 || response.Page < 0 || response.Page > 10000) throw new InvalidDataException("Invalid folder response.");
                    for (int i = 0; i < count; i++)
                    {
                        RemoteFileEntry entry = new RemoteFileEntry { Name = packet.Text(1024), Directory = packet.Reader.ReadBoolean(), Length = packet.Reader.ReadInt64() };
                        if (response.Path.Length != 0) RemoteFileAccess.CheckName(entry.Name);
                        else { if (!entry.Directory) throw new InvalidDataException("Invalid root response."); RemoteFileAccess.CheckRemotePath(entry.Name, NetworkFolders); if (!entry.Name.StartsWith("\\\\", StringComparison.Ordinal) && entry.Name.Length != 3) throw new InvalidDataException("Invalid drive response."); }
                        if (entry.Length < 0) throw new InvalidDataException("Invalid file size."); response.Entries.Add(entry);
                    }
                    packet.End(); lock (gate) { TaskCompletionSource<RemoteFileList> pending; if (lists.TryGetValue(id, out pending)) pending.TrySetResult(response); } break;
                case FileOp.MakeDirectory:
                    if (!server || !Folders) throw new InvalidDataException("Folder creation was not negotiated.");
                    string parent = packet.Text(4096), folderName = packet.Text(1024); bool unique = packet.Reader.ReadBoolean(); packet.End(); RemoteFileAccess.CheckName(folderName);
                    Queue(id, delegate { SendResult(id, true, access.CreateFolder(parent, folderName, unique)); }); break;
                case FileOp.Get:
                case FileOp.GetResume:
                    if (!server) throw new InvalidDataException("Unrequested download.");
                    bool resumeGet = op == FileOp.GetResume; if (resumeGet && !Resume) throw new InvalidDataException("Transfer resume was not negotiated.");
                    string path = packet.Text(4096); packet.End();
                    Queue(id, delegate
                    {
                        Transfer outgoing = NewSending(Path.GetFileName(path), id); outgoing.Resumable = resumeGet;
                        SendFile(outgoing, path, null, true, CancellationToken.None).ContinueWith(delegate(Task<string> task) { var observed = task.Exception; }, TaskContinuationOptions.OnlyOnFaulted);
                    }); break;
                case FileOp.Put:
                case FileOp.Offer:
                case FileOp.PutResume:
                case FileOp.OfferResume:
                    bool put = op == FileOp.Put || op == FileOp.PutResume, resumeOffer = op == FileOp.PutResume || op == FileOp.OfferResume; if (put != server) throw new InvalidDataException("Unrequested file offer.");
                    if (resumeOffer && !Resume) throw new InvalidDataException("Transfer resume was not negotiated.");
                    string folder = put ? packet.Text(4096) : null, name = packet.Text(1024); long length = packet.Reader.ReadInt64(); byte[] identity = resumeOffer ? packet.Reader.ReadBytes(32) : null; packet.End(); RemoteFileAccess.CheckName(name);
                    if (identity != null && identity.Length != 32) throw new InvalidDataException("Invalid source digest.");
                    if (length < 0) throw new InvalidDataException("Invalid file size.");
                    Queue(id, delegate
                    {
                        Transfer item;
                        lock (gate)
                        {
                            if (put) { CheckAvailable(); receiving = new Transfer { Id = id, Name = name, Folder = folder, Length = length, Resumable = resumeOffer }; }
                            else if (receiving == null || receiving.Id != id || receiving.Name != name || receiving.Incoming != null || receiving.Cancelled || receiving.Resumable != resumeOffer) throw new InvalidDataException("Unexpected file offer.");
                            item = receiving; item.Length = length;
                        }
                        item.Incoming = new IncomingFile(access, item.Folder, name, length, identity, delegate { Preparing(item); }); item.Activity = Stopwatch.GetTimestamp();
                        if (resumeOffer) Send(FileOp.ResumeReady, id, delegate(BinaryWriter w) { w.Write(item.Incoming.Position); w.Write(item.Incoming.PrefixHash); });
                        else Send(FileOp.Ack, id, delegate(BinaryWriter w) { w.Write(0L); });
                    }); break;
                case FileOp.Preparing:
                    packet.End(); if (!Resume) throw new InvalidDataException("Transfer resume was not negotiated.");
                    lock (gate) { if (sending != null && sending.Id == id && sending.Resumable) sending.Activity = Stopwatch.GetTimestamp(); if (receiving != null && receiving.Id == id && receiving.Resumable) receiving.Activity = Stopwatch.GetTimestamp(); Monitor.PulseAll(gate); } break;
                case FileOp.ResumeReady:
                    long resumeOffset = packet.Reader.ReadInt64(); byte[] prefixHash = packet.Reader.ReadBytes(32); packet.End();
                    if (!Resume || resumeOffset < 0 || prefixHash.Length != 32) throw new InvalidDataException("Invalid resume response.");
                    lock (gate) { if (sending != null && sending.Id == id) { if (!sending.Resumable || sending.Ready || resumeOffset > sending.Length) throw new InvalidDataException("Unexpected resume response."); sending.ResumeOffset = resumeOffset; sending.PrefixHash = prefixHash; sending.Ready = true; sending.Activity = Stopwatch.GetTimestamp(); Monitor.PulseAll(gate); } } break;
                case FileOp.Chunk:
                    long offset = packet.Reader.ReadInt64(); int size = packet.Reader.ReadInt32();
                    if (offset < 0 || size < 1 || size > ChunkSize || packet.Reader.BaseStream.Length - packet.Reader.BaseStream.Position != size) throw new InvalidDataException("Invalid file chunk.");
                    byte[] bytes = packet.Reader.ReadBytes(size); packet.End();
                    Queue(id, delegate
                    {
                        Transfer item;
                        lock (gate)
                        {
                            if (receiving == null || receiving.Id != id || receiving.Cancelled) return;
                            if (receiving.Incoming == null) throw new InvalidDataException("File was not accepted.");
                            item = receiving;
                        }
                        item.Incoming.Append(offset, bytes); long position = item.Incoming.Position; Interlocked.Exchange(ref item.Activity, Stopwatch.GetTimestamp());
                        Send(FileOp.Ack, id, delegate(BinaryWriter w) { w.Write(position); });
                    }); break;
                case FileOp.Finish:
                    byte[] digest = packet.Reader.ReadBytes(32); packet.End(); if (digest.Length != 32) throw new InvalidDataException("Invalid file digest.");
                    Queue(id, delegate
                    {
                        Transfer item;
                        lock (gate)
                        {
                            if (receiving == null || receiving.Id != id || receiving.Cancelled || receiving.Incoming == null) return;
                            item = receiving;
                        }
                        string saved = item.Incoming.Complete(digest); item.Incoming.Dispose();
                        SendResult(id, true, saved);
                        // Publish the receipt before waking a folder/batch continuation.
                        // Otherwise its next Get can overtake this transfer's Result.
                        lock (gate) { receiving = null; item.Done.TrySetResult(saved); }
                    }); break;
                case FileOp.Ack:
                    long received = packet.Reader.ReadInt64(); packet.End();
                    FileProgress report = null;
                    lock (gate)
                    {
                        if (sending != null && sending.Id == id)
                        {
                            if ((sending.Resumable && !sending.Ready) || received < sending.Ack || received > sending.Sent) throw new InvalidDataException("Invalid file acknowledgement.");
                            sending.Ready = true; sending.Ack = received; sending.Activity = Stopwatch.GetTimestamp(); Monitor.PulseAll(gate);
                            report = new FileProgress { Name = sending.Name, Direction = "Sending", Bytes = received, Total = sending.Length };
                        }
                    }
                    var progress = ProgressChanged; if (report != null && progress != null) progress(report); break;
                case FileOp.Cancel:
                    packet.End(); lock (gate) { if (sending != null && sending.Id == id) sending.Cancelled = true; if (receiving != null && receiving.Id == id) receiving.Cancelled = true; Monitor.PulseAll(gate); }
                    Queue(id, delegate { EndIncoming(id, new OperationCanceledException()); }); break;
                case FileOp.Result:
                    bool success = packet.Reader.ReadBoolean(); string message = packet.Text(2048); packet.End();
                    lock (gate)
                    {
                        if (sending != null && sending.Id == id)
                        {
                            if (success && sending.Ack != sending.Length) throw new InvalidDataException("Premature file completion.");
                            if (success) sending.Done.TrySetResult(message); else sending.Done.TrySetException(new IOException(message));
                            // The next request can arrive before SendFile's async finally.
                            // This transfer has its terminal receipt and can release the slot now.
                            sending = null;
                            Monitor.PulseAll(gate);
                        }
                        TaskCompletionSource<string> operation; if (operations.TryGetValue(id, out operation)) { if (success) operation.TrySetResult(message); else operation.TrySetException(new IOException(message)); }
                        TaskCompletionSource<RemoteFileList> listing;
                        if (lists.TryGetValue(id, out listing)) listing.TrySetException(new IOException(message));
                    }
                    if (!success) Queue(id, delegate { EndIncoming(id, new IOException(message), true); }); break;
                default: throw new InvalidDataException("Unknown file operation.");
            }
            return true;
        }
        public void Dispose()
        {
            lock (gate)
            {
                if (disposed) return; disposed = true; expiry.Dispose();
                if (sending != null) { sending.Cancelled = true; sending.Done.TrySetCanceled(); }
                if (receiving != null) { receiving.Done.TrySetCanceled(); }
                foreach (var operation in operations.Values) operation.TrySetCanceled(); operations.Clear();
                foreach (var item in lists.Values) item.TrySetCanceled(); lists.Clear(); Monitor.PulseAll(gate); work.CompleteAdding();
            }
        }
    }
}
