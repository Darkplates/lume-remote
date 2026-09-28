using System;
using System.Collections.Generic;
using System.Drawing;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Threading;
using System.Threading.Tasks;
using System.Windows.Forms;

namespace LumeRemote
{
    public sealed class PairedLink : IDisposable
    {
        public PeerTransport Peer; public Invitation Invitation;
        public void Dispose() { if (Peer != null) Peer.Dispose(); }
    }
    public sealed class PairedClient
    {
        static async Task<T> Await<T>(Task<T> task, int timeout, CancellationToken cancellation)
        {
            Task limit = Task.Delay(timeout, cancellation);
            if (await Task.WhenAny(task, limit).ConfigureAwait(false) != task) { cancellation.ThrowIfCancellationRequested(); throw new TimeoutException("The saved computer did not answer. It may be asleep, off, or disconnected."); }
            return await task.ConfigureAwait(false);
        }
        public static async Task<SavedComputer> Pair(PairingCode code, string clientName, CancellationToken cancellation)
        {
            HostPreferences.ValidateName(clientName); string newKey = Security.Token(32), request = Guid.NewGuid().ToString("N");
            using (SignalBroker broker = new SignalBroker(SignalBroker.NewId()))
            using (cancellation.Register(broker.Dispose))
            {
                TaskCompletionSource<SignalBody> reply = new TaskCompletionSource<SignalBody>();
                broker.Closed += delegate(string message) { reply.TrySetException(new IOException(message)); };
                broker.Message += delegate(BrokerPacket packet)
                {
                    try
                    {
                        if (packet.type == "EXPIRE") { reply.TrySetException(new IOException("The host is offline. Keep permanent access enabled there while pairing.")); return; }
                        SignalEnvelope envelope = packet.payload;
                        if (packet.src != code.host || envelope == null || envelope.route != code.id || envelope.request != request) return;
                        if (envelope.stage == "paired") reply.TrySetResult(SignalCrypto.Open(newKey, packet.src, broker.Id, envelope));
                        else if (envelope.stage == "error") reply.TrySetException(new IOException(SignalCrypto.Open(code.key, packet.src, broker.Id, envelope).message));
                    }
                    catch (CryptographicException) { }
                    catch (Exception error) { reply.TrySetException(error); }
                };
                await broker.Start(Security.Token(32)).ConfigureAwait(false);
                await broker.Send(code.host, SignalCrypto.Seal(code.key, broker.Id, code.host, code.id, request, "pair", new SignalBody { name = clientName, key = newKey })).ConfigureAwait(false);
                SignalBody accepted = await Await(reply.Task, 20000, cancellation).ConfigureAwait(false);
                SavedComputer computer = new SavedComputer { HostId = code.host, Id = code.id, Name = accepted.name, Key = newKey, WakeOnly = code.wake, WakeMac = code.wake ? code.mac : accepted.mac };
                computer.Validate(); return computer;
            }
        }
        public static async Task<PairedLink> Connect(SavedComputer computer, Action<string> status, CancellationToken cancellation)
        {
            computer.Validate(); if (computer.WakeOnly) throw new InvalidOperationException("This pairing only allows wake packets, not desktop access.");
            string request = Guid.NewGuid().ToString("N"); PeerTransport peer = null;
            using (SignalBroker broker = new SignalBroker(SignalBroker.NewId()))
            using (cancellation.Register(broker.Dispose))
            {
                TaskCompletionSource<SignalBody> reply = new TaskCompletionSource<SignalBody>();
                broker.Closed += delegate(string message) { reply.TrySetException(new IOException(message)); };
                broker.Message += delegate(BrokerPacket packet)
                {
                    try
                    {
                        if (packet.type == "EXPIRE") { reply.TrySetException(new IOException("The saved computer is offline or its host service is not connected.")); return; }
                        SignalEnvelope envelope = packet.payload;
                        if (packet.src != computer.HostId || envelope == null || envelope.route != computer.Id || envelope.request != request) return;
                        SignalBody body = SignalCrypto.Open(computer.Key, packet.src, broker.Id, envelope);
                        if (envelope.stage == "offer") reply.TrySetResult(body); else if (envelope.stage == "error") reply.TrySetException(new IOException(body.message));
                    }
                    catch (CryptographicException) { }
                    catch (Exception error) { reply.TrySetException(error); }
                };
                try
                {
                    status("Finding " + computer.Name + "..."); await broker.Start(Security.Token(32)).ConfigureAwait(false);
                    await broker.Send(computer.HostId, SignalCrypto.Seal(computer.Key, broker.Id, computer.HostId, computer.Id, request, "connect", new SignalBody())).ConfigureAwait(false);
                    SignalBody offerBody = await Await(reply.Task, 35000, cancellation).ConfigureAwait(false); PeerSignal offer = PeerSignal.Parse(offerBody.code);
                    status("Connecting directly to " + computer.Name + "...");
                    peer = await Task.Run(delegate { return new PeerTransport(); }, cancellation).ConfigureAwait(false);
                    using (cancellation.Register(peer.Dispose))
                    {
                        string answer = await Task.Run(delegate { return peer.CreateAnswer(offer.Sdp); }, cancellation).ConfigureAwait(false);
                        await broker.Send(computer.HostId, SignalCrypto.Seal(computer.Key, broker.Id, computer.HostId, computer.Id, request, "answer", new SignalBody { code = offer.Reply(answer).ToString() })).ConfigureAwait(false);
                        await Task.Run(delegate { peer.WaitReady(90000); }, cancellation).ConfigureAwait(false); cancellation.ThrowIfCancellationRequested();
                        PairedLink result = new PairedLink { Peer = peer, Invitation = offer.Session }; peer = null; return result;
                    }
                }
                finally { if (peer != null) peer.Dispose(); }
            }
        }
        public static async Task Wake(SavedComputer helper, string targetMac, CancellationToken cancellation)
        {
            helper.Validate(); targetMac = WakeOnLan.Normalize(targetMac);
            if (!helper.WakeOnly || targetMac != helper.WakeMac) throw new InvalidOperationException("This wake helper is not paired for that target MAC address.");
            string request = Guid.NewGuid().ToString("N");
            using (SignalBroker broker = new SignalBroker(SignalBroker.NewId())) using (cancellation.Register(broker.Dispose))
            {
                TaskCompletionSource<SignalBody> reply = new TaskCompletionSource<SignalBody>();
                broker.Closed += delegate(string message) { reply.TrySetException(new IOException(message)); };
                broker.Message += delegate(BrokerPacket packet)
                {
                    try
                    {
                        if (packet.type == "EXPIRE") { reply.TrySetException(new IOException("The wake helper is offline.")); return; }
                        SignalEnvelope envelope = packet.payload;
                        if (packet.src != helper.HostId || envelope == null || envelope.route != helper.Id || envelope.request != request) return;
                        SignalBody body = SignalCrypto.Open(helper.Key, packet.src, broker.Id, envelope);
                        if (envelope.stage == "woke") reply.TrySetResult(body); else if (envelope.stage == "error") reply.TrySetException(new IOException(body.message));
                    }
                    catch (CryptographicException) { }
                    catch (Exception error) { reply.TrySetException(error); }
                };
                await broker.Start(Security.Token(32)).ConfigureAwait(false);
                await broker.Send(helper.HostId, SignalCrypto.Seal(helper.Key, broker.Id, helper.HostId, helper.Id, request, "wake", new SignalBody { mac = targetMac })).ConfigureAwait(false);
                await Await(reply.Task, 20000, cancellation).ConfigureAwait(false);
            }
        }
    }

    public sealed class PersistentHost : IDisposable
    {
        readonly TrustedStore store;
        readonly Func<HostPreferences, IScreenSource> sourceFactory;
        readonly Action<string> report;
        readonly CancellationTokenSource stopped = new CancellationTokenSource();
        readonly SemaphoreSlim changed = new SemaphoreSlim(0, 1);
        readonly object gate = new object();
        readonly Dictionary<string, long> recent = new Dictionary<string, long>();
        SignalBroker broker;
        ActiveSession active;
        FileSystemWatcher watcher;
        int messageCount; long messageWindow = DateTime.UtcNow.Ticks;
        DateTime lastStatusWrite;
        public bool Ready { get; private set; }
        public string State { get; private set; }
        public PersistentHost(TrustedStore store, Func<HostPreferences, IScreenSource> sourceFactory, Action<string> report)
        { this.store = store; this.sourceFactory = sourceFactory; this.report = report ?? delegate { }; }
        void Status(string message)
        {
            State = message; report(message);
            try { File.WriteAllText(Path.Combine(store.DirectoryPath, "status.txt"), DateTime.UtcNow.ToString("u") + "\r\n" + message, new System.Text.UTF8Encoding(false)); lastStatusWrite = DateTime.UtcNow; } catch (IOException) { } catch (UnauthorizedAccessException) { }
        }
        void Pulse() { try { if (changed.CurrentCount == 0) changed.Release(); } catch (SemaphoreFullException) { } }
        public async Task Run()
        {
            Directory.CreateDirectory(store.DirectoryPath);
            watcher = new FileSystemWatcher(store.DirectoryPath, "host.dat") { NotifyFilter = NotifyFilters.LastWrite | NotifyFilters.FileName | NotifyFilters.Size, EnableRaisingEvents = true };
            watcher.Changed += delegate { Pulse(); }; watcher.Created += delegate { Pulse(); }; watcher.Renamed += delegate { Pulse(); };
            DateTime retry = DateTime.MinValue;
            try
            {
                while (!stopped.IsCancellationRequested)
                {
                    try
                    {
                        HostPreferences preferences = store.ReadHost();
                        lock (gate)
                        {
                            if (active != null && (!preferences.Enabled || !preferences.Controllers.Any(p => !p.WakeOnly && p.Id == active.Controller.Id && Security.Equal(p.Key, active.Controller.Key)))) { active.Dispose(); active = null; }
                        }
                        if (!preferences.Enabled)
                        { DisconnectBroker(); if (State != "Permanent access is disabled.") Status("Permanent access is disabled."); }
                        else if (broker == null && DateTime.UtcNow >= retry)
                        {
                            SignalBroker opening = new SignalBroker(preferences.HostId);
                            opening.Message += delegate(BrokerPacket packet) { Receive(opening, packet); };
                            opening.Closed += delegate(string reason) { if (broker == opening) { SessionLog.Write(store.DirectoryPath, "host", "signaling_disconnected"); DisconnectBroker(); Status(reason + " Reconnecting..."); Pulse(); } };
                            try { broker = opening; await opening.Start(preferences.BrokerToken).ConfigureAwait(false); Ready = true; Status("Permanent access ready. Paired computers can connect."); }
                            catch { if (broker == opening) broker = null; opening.Dispose(); Ready = false; throw; }
                        }
                    }
                    catch (Exception error) { if (stopped.IsCancellationRequested) break; retry = DateTime.UtcNow.AddSeconds(10); Status("Host waiting: " + error.Message); }
                    if (State != null && DateTime.UtcNow - lastStatusWrite > TimeSpan.FromSeconds(15)) Status(State);
                    await changed.WaitAsync(2000, stopped.Token).ConfigureAwait(false);
                }
            }
            catch (OperationCanceledException) { }
            finally { if (watcher != null) watcher.Dispose(); DisconnectBroker(); lock (gate) { if (active != null) active.Dispose(); active = null; } }
        }
        void DisconnectBroker() { SignalBroker previous = broker; broker = null; Ready = false; if (previous != null) previous.Dispose(); }
        bool Fresh(string nonce)
        {
            lock (gate)
            {
                long now = DateTime.UtcNow.Ticks;
                foreach (string key in recent.Where(p => p.Value < now).Select(p => p.Key).ToArray()) recent.Remove(key);
                if (recent.ContainsKey(nonce) || recent.Count >= 256) return false;
                recent[nonce] = now + TimeSpan.TicksPerMinute * 5; return true;
            }
        }
        void Receive(SignalBroker channel, BrokerPacket packet)
        {
            try
            {
                if (stopped.IsCancellationRequested || packet == null || packet.payload == null || packet.src == null || !packet.src.StartsWith("lume-", StringComparison.Ordinal) || !Invitation.IsHex(packet.src.Substring(5), 32)) return;
                lock (gate) { long now = DateTime.UtcNow.Ticks; if (now - messageWindow >= TimeSpan.TicksPerSecond) { messageWindow = now; messageCount = 0; } if (++messageCount > 30) return; }
                HostPreferences preferences = store.ReadHost(); if (!preferences.Enabled) return;
                SignalEnvelope envelope = packet.payload;
                if (envelope.stage == "pair")
                {
                    if (envelope.route != preferences.PairId || String.IsNullOrEmpty(preferences.PairKey) || preferences.PairExpires < DateTime.UtcNow.Ticks) return;
                    SignalBody body = SignalCrypto.Open(preferences.PairKey, packet.src, channel.Id, envelope);
                    if (!Fresh(packet.src + ":" + envelope.request)) return;
                    HostPreferences.ValidateKey(envelope.route, body.key); HostPreferences.ValidateName(body.name);
                    store.ChangeHost(delegate(HostPreferences current)
                    {
                        if (!current.Enabled || current.PairExpires < DateTime.UtcNow.Ticks || current.PairId != envelope.route || !Security.Equal(current.PairKey, preferences.PairKey)) throw new InvalidDataException("The pairing code expired or was already used.");
                        if (current.Controllers.Count >= 32) throw new InvalidOperationException("Remove an unused paired computer first.");
                        current.Controllers.Add(new TrustedController { Id = envelope.route, Key = body.key, Name = body.name, WakeOnly = current.PairWakeOnly, AllowedMac = current.PairMac });
                        current.PairId = current.PairKey = current.PairMac = null; current.PairExpires = 0;
                    });
                    Send(channel, packet, body.key, "paired", new SignalBody { name = Environment.MachineName, mac = preferences.PairWakeOnly ? preferences.PairMac : WakeOnLan.EthernetMac() });
                    Status("Paired with " + body.name + ". The one-time code is now revoked."); return;
                }
                TrustedController controller = preferences.Controllers.FirstOrDefault(p => p.Id == envelope.route); if (controller == null) return;
                SignalBody request = SignalCrypto.Open(controller.Key, packet.src, channel.Id, envelope);
                if (envelope.stage == "answer")
                {
                    lock (gate) { if (active != null && active.Request == envelope.request && active.Remote == packet.src && active.Controller.Id == controller.Id) active.Answer.TrySetResult(request); } return;
                }
                if (!Fresh(packet.src + ":" + envelope.request)) return;
                if (envelope.stage == "wake")
                {
                    if (!controller.WakeOnly || WakeOnLan.Normalize(request.mac) != controller.AllowedMac) throw new InvalidOperationException("This pairing is not authorized to wake that device.");
                    WakeOnLan.Send(controller.AllowedMac); Send(channel, packet, controller.Key, "woke", new SignalBody { message = "The local wake packet was sent. Power-on still depends on the target hardware and firmware." }); Status("An authorized wake packet was sent."); return;
                }
                if (envelope.stage != "connect" || controller.WakeOnly) return;
                ActiveSession session = new ActiveSession { Controller = controller, Request = envelope.request, Remote = packet.src };
                lock (gate)
                {
                    if (active != null) { Send(channel, packet, controller.Key, "error", new SignalBody { message = "This PC already has a session or connection attempt." }); return; }
                    active = session;
                }
                Task.Run(delegate { return Connect(channel, packet, preferences, session); });
            }
            catch (Exception) { /* Unauthenticated/malformed requests cannot prompt, capture or reveal keys. */ }
        }
        void Send(SignalBroker channel, BrokerPacket request, string key, string stage, SignalBody body)
        {
            SignalEnvelope envelope = SignalCrypto.Seal(key, channel.Id, request.src, request.payload.route, request.payload.request, stage, body);
            Task send = channel.Send(request.src, envelope); send.ContinueWith(delegate(Task failed) { var ignored = failed.Exception; }, TaskContinuationOptions.OnlyOnFaulted);
        }
        async Task Connect(SignalBroker channel, BrokerPacket request, HostPreferences preferences, ActiveSession session)
        {
            try
            {
                Status("Connecting to paired computer " + session.Controller.Name + "...");
                HostService createdHost = new HostService(delegate { return sourceFactory(preferences); }, Profile.All[3], true,
                    delegate { HostPreferences current = store.ReadHost(); return !session.Disposed && current.Enabled && current.Controllers.Any(p => !p.WakeOnly && p.Id == session.Controller.Id && Security.Equal(p.Key, session.Controller.Key)); },
                    delegate { }, Status, delegate(string text) { new RemoteFileAccess(preferences.OwnerSid).Run(delegate { }); return ClipboardAccess.Write(text); }, delegate { return new RemoteFileAccess(preferences.OwnerSid, session.Controller.Key, delegate { return store.ReadHost().NetworkFolders.ToArray(); }); }, async delegate { var access = new RemoteFileAccess(preferences.OwnerSid); access.Run(delegate { }); string text = await ClipboardAccess.Read().ConfigureAwait(false); access.Run(delegate { }); return text; }, true, delegate { new RemoteFileAccess(preferences.OwnerSid).Run(delegate { }); return Task.FromResult(true); }, delegate(PowerAction action) { new RemoteFileAccess(preferences.OwnerSid).Run(delegate { }); return RemotePower.Request(action); }, true, async delegate(Action stop, CancellationToken cancellation) { var access = new RemoteFileAccess(preferences.OwnerSid); access.Run(delegate { }); IDisposable grant = await HostVoiceConsent.Request(stop, cancellation).ConfigureAwait(false); try { access.Run(delegate { }); return grant; } catch { if (grant != null) grant.Dispose(); throw; } });
                if (!session.Attach(createdHost)) return;
                createdHost.SessionEnded += delegate(Exception error) { SessionLog.Write(store.DirectoryPath, "host", SessionLog.Reason(error), error); };
                SessionLog.Write(store.DirectoryPath, "host", "paired_connection_requested");
                session.Host.StartPeer(); if (!session.Attach(new PeerTransport())) return;
                PeerSignal offer = PeerSignal.Offer(session.Host.Invite, session.Peer.CreateOffer());
                if (session.Disposed) return;
                await channel.Send(request.src, SignalCrypto.Seal(session.Controller.Key, channel.Id, request.src, request.payload.route, session.Request, "offer", new SignalBody { code = offer.ToString() })).ConfigureAwait(false);
                Task limit = Task.Delay(90000, stopped.Token);
                if (await Task.WhenAny(session.Answer.Task, limit).ConfigureAwait(false) != session.Answer.Task) throw new TimeoutException("The paired viewer did not complete P2P negotiation.");
                PeerSignal answer = PeerSignal.Parse((await session.Answer.Task.ConfigureAwait(false)).code); offer.VerifyReply(answer);
                session.Peer.AcceptAnswer(answer.Sdp); session.Peer.WaitReady(90000);
                TaskCompletionSource<bool> ended = new TaskCompletionSource<bool>(); session.Host.PeerEnded += delegate { ended.TrySetResult(true); };
                session.Host.AcceptPeer(session.Peer);
                await Task.WhenAny(ended.Task, session.Stopped.Task).ConfigureAwait(false);
            }
            catch (Exception error) { if (!session.Disposed && !stopped.IsCancellationRequested) { Status("Saved connection ended: " + error.Message); try { Send(channel, request, session.Controller.Key, "error", new SignalBody { message = error.Message }); } catch { } } }
            finally
            {
                session.Dispose(); lock (gate) if (active == session) active = null;
                if (!stopped.IsCancellationRequested && Ready) Status("Permanent access ready. Paired computers can reconnect.");
            }
        }
        public void Dispose() { stopped.Cancel(); Pulse(); DisconnectBroker(); lock (gate) { if (active != null) active.Dispose(); } }
        sealed class ActiveSession : IDisposable
        {
            public TrustedController Controller; public string Request, Remote; public HostService Host; public PeerTransport Peer;
            public readonly TaskCompletionSource<SignalBody> Answer = new TaskCompletionSource<SignalBody>();
            public readonly TaskCompletionSource<bool> Stopped = new TaskCompletionSource<bool>();
            int disposed;
            readonly object ownership = new object();
            public bool Disposed { get { return Volatile.Read(ref disposed) != 0; } }
            public bool Attach(HostService host) { lock (ownership) { if (Disposed) { host.Dispose(); return false; } Host = host; return true; } }
            public bool Attach(PeerTransport peer) { lock (ownership) { if (Disposed) { peer.Dispose(); return false; } Peer = peer; return true; } }
            public void Dispose() { lock (ownership) { if (Interlocked.Exchange(ref disposed, 1) != 0) return; Answer.TrySetCanceled(); if (Host != null) Host.Dispose(); if (Peer != null) Peer.Dispose(); Stopped.TrySetResult(true); } }
        }
    }
}
