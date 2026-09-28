using System;
using System.IO;
using System.Security.Cryptography;
using System.Threading;
using System.Threading.Tasks;
using LumeRemote;

static partial class Tests
{
    sealed class ReceiptStream : MemoryStream
    {
        public readonly ManualResetEvent ReceiptEntered = new ManualResetEvent(false), ReleaseReceipt = new ManualResetEvent(false), FinishSent = new ManualResetEvent(false);
        public bool BlockReceipt;
        public override void Write(byte[] buffer, int offset, int count)
        {
            if (count > 4 && buffer[offset] == (byte)Kind.Files)
            {
                if (buffer[offset + 1] == (byte)FileOp.Result && BlockReceipt)
                { ReceiptEntered.Set(); if (!ReleaseReceipt.WaitOne(5000)) throw new IOException("Receipt fixture timed out."); }
                if (buffer[offset + 1] == (byte)FileOp.Finish) FinishSent.Set();
            }
            base.Write(buffer, offset, count);
        }
        protected override void Dispose(bool disposing)
        { if (disposing) { ReleaseReceipt.Set(); } base.Dispose(disposing); }
    }
    static Packet FilePacket(FileOp op, string id, Action<BinaryWriter> payload)
    { return new Packet(Wire.Message(Kind.Files, delegate(BinaryWriter w) { w.Write((byte)op); Wire.Text(w, id); if (payload != null) payload(w); })); }
    static string TransferId(FileTransfer files, string direction)
    { object item = Field(files, direction); return (string)item.GetType().GetField("Id").GetValue(item); }
    static void FileCompletionOrdering()
    {
        string root = Path.Combine(Path.GetTempPath(), "lume-receipt-test-" + Guid.NewGuid().ToString("N")); Directory.CreateDirectory(root);
        try
        {
            using (var stream = new ReceiptStream { BlockReceipt = true }) using (var files = new FileTransfer(new Wire(stream), false, new RemoteFileAccess(), delegate { }))
            {
                Task<string> download = files.Download(Path.Combine(root, "empty.bin"), root, CancellationToken.None);
                string id = TransferId(files, "receiving");
                using (var packet = FilePacket(FileOp.Offer, id, delegate(BinaryWriter w) { Wire.Text(w, "empty.bin"); w.Write(0L); })) files.Handle(packet);
                byte[] digest; using (SHA256 hash = SHA256.Create()) digest = hash.ComputeHash(new byte[0]);
                using (var packet = FilePacket(FileOp.Finish, id, delegate(BinaryWriter w) { w.Write(digest); })) files.Handle(packet);
                try
                {
                    Check(stream.ReceiptEntered.WaitOne(3000), "Completion receipt was not sent.");
                    Check(!download.IsCompleted, "A batch can send its next request before the completion receipt.");
                }
                finally { stream.ReleaseReceipt.Set(); }
                Check(File.Exists(Await(download)), "Receipt release lost the completed file.");
            }
            using (var stream = new ReceiptStream()) using (var files = new FileTransfer(new Wire(stream), false, new RemoteFileAccess(), delegate { }))
            {
                string source = Path.Combine(root, "empty.bin");
                Task<string> first = files.Upload(source, root, CancellationToken.None);
                string id = TransferId(files, "sending");
                using (var packet = FilePacket(FileOp.Ack, id, delegate(BinaryWriter w) { w.Write(0L); })) files.Handle(packet);
                Check(stream.FinishSent.WaitOne(3000), "Sender did not finish the empty file.");
                Task<string> next;
                // Hold the sender worker at its gate so its async finally cannot hide the race.
                lock (Field(files, "gate"))
                {
                    using (var packet = FilePacket(FileOp.Result, id, delegate(BinaryWriter w) { w.Write(true); Wire.Text(w, source); })) files.Handle(packet);
                    next = files.Upload(source, root, CancellationToken.None);
                }
                Check(Await(first) == source, "First transfer lost its receipt.");
                files.Dispose(); Reject(delegate { Await(next); });
            }
        }
        finally { Directory.Delete(root, true); }
    }
}
