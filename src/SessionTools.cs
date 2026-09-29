using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.IO;
using System.Threading;
using System.Threading.Tasks;

namespace LumeRemote
{
    [Flags] public enum SessionCapabilities : ulong
    {
        None = 0, ClipboardRead = 1, Monitors = 2, Chat = 4, Power = 8, Folders = 16, Audio = 32, Annotations = 64, ClipboardSync = 128, FileResume = 256, NetworkFolders = 512, Voice = 1024, PortableImages = 2048, PreferPortableImages = 4096
    }
    public enum SessionTool : byte { ClipboardRead = 1, Monitors = 2, SelectMonitor = 3, Chat = 4, Power = 5, Audio = 6, Annotation = 7, ClipboardWrite = 8, Voice = 9, PortableImages = 10 }

    // Separate bounded work queue: a local consent dialog must never stop ACKs or heartbeats.
    public sealed class SessionTools : IDisposable
    {
        readonly Wire wire;
        readonly Action<Exception> failed;
        readonly Func<SessionTool, Packet, Task<byte[]>> handler;
        readonly BlockingCollection<Action> work = new BlockingCollection<Action>(8);
        readonly Dictionary<long, TaskCompletionSource<byte[]>> pending = new Dictionary<long, TaskCompletionSource<byte[]>>();
        readonly object gate = new object();
        readonly TaskCompletionSource<bool> stopped = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        long nextId;
        volatile bool disposed;
        // Runs on the receive thread before a request joins the sequential queue, so a request
        // can withdraw an earlier one that is still waiting for a local prompt. Must not block.
        internal Action<SessionTool, byte[]> Arrived { get; set; }
        public SessionTools(Wire wire, Action<Exception> failed, Func<SessionTool, Packet, Task<byte[]>> handler = null)
        {
            this.wire = wire; this.failed = failed; this.handler = handler;
            if (handler != null) BackgroundWork.Run(delegate { foreach (Action action in work.GetConsumingEnumerable()) if (!disposed) action(); });
        }
        public static byte[] Payload(Action<BinaryWriter> write = null) { return Wire.Message(Kind.Tools, write); }
        public async Task<byte[]> Request(SessionTool op, Action<BinaryWriter> write = null)
        {
            long id = Interlocked.Increment(ref nextId);
            TaskCompletionSource<byte[]> result = new TaskCompletionSource<byte[]>(TaskCreationOptions.RunContinuationsAsynchronously);
            byte[] payload = Payload(write); if (payload.Length > 262200) throw new InvalidOperationException("This request is too large.");
            lock (gate) { if (disposed) throw new OperationCanceledException(); if (pending.Count >= 4) throw new InvalidOperationException("Wait for the current session action to finish."); pending.Add(id, result); }
            try
            {
                await Task.Run(delegate { wire.Send(Kind.Tools, delegate(BinaryWriter w) { w.Write(id); w.Write((byte)op); w.Write(payload, 1, payload.Length - 1); }); }).ConfigureAwait(false);
                if (await Task.WhenAny(result.Task, Task.Delay(75000)).ConfigureAwait(false) != result.Task) throw new TimeoutException("The session action did not respond.");
                return await result.Task.ConfigureAwait(false);
            }
            finally { lock (gate) pending.Remove(id); }
        }
        public void Handle(Packet packet)
        {
            if (packet.Kind == Kind.ToolReply)
            {
                if (packet.Reader.BaseStream.Length > 263200) throw new InvalidDataException("Session response is too large.");
                long id = packet.Reader.ReadInt64(); bool success = packet.Reader.ReadBoolean();
                string error = packet.Text(1024); int length = packet.Reader.ReadInt32();
                if (length < 1 || length > 262200 || length > packet.Reader.BaseStream.Length - packet.Reader.BaseStream.Position) throw new InvalidDataException("Invalid session response.");
                byte[] data = packet.Reader.ReadBytes(length); packet.End();
                if (id <= 0 || data[0] != (byte)Kind.Tools) throw new InvalidDataException("Invalid session response identifier.");
                TaskCompletionSource<byte[]> result; lock (gate) pending.TryGetValue(id, out result);
                if (result != null) { if (success) result.TrySetResult(data); else result.TrySetException(new InvalidOperationException(error)); }
                return;
            }
            if (packet.Kind != Kind.Tools || handler == null || packet.Reader.BaseStream.Length > 262220) throw new InvalidDataException("Unexpected session request.");
            long request = packet.Reader.ReadInt64(); SessionTool op = (SessionTool)packet.Reader.ReadByte();
            if (request <= 0 || !Enum.IsDefined(typeof(SessionTool), op)) throw new InvalidDataException("Invalid session request.");
            int remaining = checked((int)(packet.Reader.BaseStream.Length - packet.Reader.BaseStream.Position));
            byte[] payload = new byte[remaining + 1]; payload[0] = (byte)Kind.Tools; packet.Reader.Read(payload, 1, remaining); packet.End();
            Action<SessionTool, byte[]> arrived = Arrived; if (arrived != null && !disposed) arrived(op, payload);
            Action action = delegate
            {
                byte[] reply = Payload(); string error = "";
                try
                {
                    using (Packet body = new Packet(payload))
                    {
                        Task<byte[]> handling = handler(op, body);
                        if (Task.WaitAny(handling, stopped.Task) == 1) { handling.ContinueWith(delegate(Task<byte[]> done) { var observed = done.Exception; }, TaskContinuationOptions.OnlyOnFaulted); return; }
                        reply = handling.GetAwaiter().GetResult();
                    }
                }
                catch (InvalidDataException malformed) { failed(malformed); return; }
                catch (EndOfStreamException truncated) { failed(truncated); return; }
                catch (System.Text.DecoderFallbackException invalidText) { failed(invalidText); return; }
                catch (Exception problem) { error = problem is OperationCanceledException ? "The action was cancelled." : problem.Message; if (System.Text.Encoding.UTF8.GetByteCount(error) > 1000) error = "The session action failed. Try again."; }
                if (!disposed) try { wire.Send(Kind.ToolReply, delegate(BinaryWriter w) { w.Write(request); w.Write(error.Length == 0); Wire.Text(w, error); w.Write(reply.Length); w.Write(reply); }); }
                catch (Exception problem) { failed(problem); }
            };
            // A backlog behind an unanswered local prompt is not a protocol error: answer busy
            // instead of ending the desktop session. Each reply is bounded like the request.
            bool queued;
            try { queued = disposed || work.TryAdd(action); }
            catch (InvalidOperationException) { if (!disposed) throw; return; }
            if (!queued)
            {
                const string busy = "The remote PC is still answering a previous request. Try again.";
                wire.Send(Kind.ToolReply, delegate(BinaryWriter w) { byte[] empty = Payload(); w.Write(request); w.Write(false); Wire.Text(w, busy); w.Write(empty.Length); w.Write(empty); });
            }
        }
        public void Dispose()
        {
            lock (gate) { if (disposed) return; disposed = true; foreach (var result in pending.Values) result.TrySetCanceled(); pending.Clear(); }
            stopped.TrySetResult(true); work.CompleteAdding();
        }
    }
}
