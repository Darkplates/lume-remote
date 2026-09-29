using System;
using System.IO;
using System.Threading;
using System.Threading.Tasks;

namespace LumeRemote
{
    internal sealed class SessionVoice : IDisposable
    {
        readonly SessionAudio playback;
        readonly Action<byte[]> send;
        readonly Action<Exception> failed;
        readonly object gate = new object();
        SessionAudio microphone;
        bool disposed;
        public SessionVoice(Action<byte[]> send, Action<Exception> failed) { this.send = send; this.failed = failed; playback = new SessionAudio(true, null, failed); }
        public Task PlaybackReady { get { return playback.Ready; } }
        public bool Active { get { lock (gate) return !disposed && microphone != null && microphone.IsRunning && playback.IsRunning; } }
        public async Task StartMicrophone()
        {
            SessionAudio started;
            lock (gate) { if (disposed) throw new OperationCanceledException(); if (microphone != null) throw new InvalidOperationException("Microphone is already active."); started = microphone = new SessionAudio(false, send, failed, true); }
            await started.Ready.ConfigureAwait(false);
        }
        public void Receive(byte[] bytes) { playback.Receive(bytes); }
        public void Dispose() { lock (gate) { if (disposed) return; disposed = true; if (microphone != null) microphone.Dispose(); playback.Dispose(); } }
        public static byte[] Read(Packet packet, out int generation)
        {
            generation = packet.Reader.ReadInt32(); int length = packet.Reader.ReadInt32();
            if (generation < 1 || length < 5 || length > AudioBlock.Maximum + 256 || length != packet.Reader.BaseStream.Length - packet.Reader.BaseStream.Position) throw new InvalidDataException("Invalid voice packet.");
            byte[] block = packet.Reader.ReadBytes(length); packet.End(); return block;
        }
    }
    internal sealed class HostVoiceController : IDisposable
    {
        readonly Wire wire;
        readonly Func<Action, CancellationToken, Task<IDisposable>> consent;
        readonly CancellationTokenSource stopping = new CancellationTokenSource();
        readonly object gate = new object();
        SessionVoice voice; IDisposable grant; int generation, latestRequest; bool disposed;
        // Cancels the open "Allow call" prompt when its request is withdrawn or superseded.
        CancellationTokenSource pendingRequest;
        public HostVoiceController(Wire wire, Func<Action, CancellationToken, Task<IDisposable>> consent) { this.wire = wire; this.consent = consent; }
        public async Task Set(bool enabled, int nextGeneration)
        {
            if (nextGeneration < 1) throw new InvalidDataException("Invalid voice generation.");
            lock (gate) { if (nextGeneration < latestRequest) { if (enabled) throw new InvalidDataException("Stale voice request."); return; } latestRequest = nextGeneration; }
            Stop(false);
            if (!enabled) return;
            CancellationTokenSource request = new CancellationTokenSource(), superseded;
            lock (gate)
            {
                if (disposed || nextGeneration < latestRequest) { request.Dispose(); throw new OperationCanceledException(); }
                generation = nextGeneration; superseded = pendingRequest; pendingRequest = request;
            }
            CancelQuietly(superseded);
            try
            {
                IDisposable approved; bool withdrawn;
                try
                {
                    using (var requestTimeout = CancellationTokenSource.CreateLinkedTokenSource(stopping.Token, request.Token))
                    { requestTimeout.CancelAfter(60000); approved = await consent(delegate { Stop(true, nextGeneration); }, requestTimeout.Token).ConfigureAwait(false); }
                }
                finally { lock (gate) { if (pendingRequest == request) pendingRequest = null; withdrawn = request.IsCancellationRequested; } request.Dispose(); }
                // A withdrawn request never starts the microphone, even if Allow raced the withdrawal.
                if (withdrawn) { if (approved != null) approved.Dispose(); throw new OperationCanceledException("The remote microphone request was withdrawn."); }
                if (approved == null) throw new InvalidOperationException("The remote microphone request was declined.");
                SessionVoice next;
                lock (gate)
                {
                    if (disposed || generation != nextGeneration) { approved.Dispose(); throw new OperationCanceledException(); }
                    grant = approved;
                    next = voice = new SessionVoice(delegate(byte[] bytes) { lock (gate) { if (disposed || generation != nextGeneration) return; } wire.Send(Kind.Voice, delegate(BinaryWriter w) { w.Write(nextGeneration); w.Write(bytes.Length); w.Write(bytes); }); }, delegate { Stop(true, nextGeneration); });
                }
                await next.PlaybackReady.ConfigureAwait(false); await next.StartMicrophone().ConfigureAwait(false);
            }
            catch { Stop(false, nextGeneration); throw; }
        }
        public void Receive(Packet packet)
        { int packetGeneration; byte[] bytes = SessionVoice.Read(packet, out packetGeneration); lock (gate) { if (!disposed && generation == packetGeneration && voice != null) voice.Receive(bytes); } }
        // Called on the receive thread when a Voice request arrives, before it waits in the
        // sequential tool queue: any newer generation (a withdrawal or a new call) dismisses the
        // prompt that is still open. Malformed bodies are left for the queued handler to reject.
        public void Preview(byte[] payload)
        {
            if (payload == null || payload.Length != 6) return;
            int nextGeneration = BitConverter.ToInt32(payload, 2); CancellationTokenSource superseded = null;
            lock (gate) { if (pendingRequest != null && nextGeneration > generation) { superseded = pendingRequest; pendingRequest = null; } }
            CancelQuietly(superseded);
        }
        void Stop(bool notify, int expectedGeneration = 0)
        {
            SessionVoice previous; IDisposable oldGrant; int previousGeneration; CancellationTokenSource withdrawn;
            lock (gate) { if (expectedGeneration != 0 && generation != expectedGeneration) return; previous = voice; voice = null; oldGrant = grant; grant = null; previousGeneration = generation; generation = 0; withdrawn = pendingRequest; pendingRequest = null; }
            CancelQuietly(withdrawn);
            if (previous != null) previous.Dispose(); if (oldGrant != null) oldGrant.Dispose();
            if (notify && previousGeneration > 0) try { wire.Send(Kind.VoiceEnded, delegate(BinaryWriter w) { w.Write(previousGeneration); }); } catch { }
        }
        static void CancelQuietly(CancellationTokenSource source)
        { if (source != null) try { source.Cancel(); } catch (ObjectDisposedException) { } catch (AggregateException) { } }
        public void Dispose() { lock (gate) { if (disposed) return; disposed = true; } stopping.Cancel(); Stop(false); }
    }
}
