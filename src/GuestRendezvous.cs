using System;
using System.IO;
using System.Security.Cryptography;
using System.Text;
using System.Threading;
using System.Threading.Tasks;

namespace LumeRemote
{
    // Optional automatic return of a guest's P2P reply through the public signaling broker.
    // Copying the reply back by hand can take minutes, while the controlling PC's route
    // checks start at once; delivering it in seconds lets both PCs check routes together.
    // The broker identity and route are derived from the private invitation secret, so the
    // invitation format is unchanged and older viewers keep the manual reply exchange.
    // Messages are encrypted and authenticated with that secret: the broker only sees
    // random-looking identifiers and timing, never the reply, addresses or keys.
    public static class GuestRendezvous
    {
        public const int DeliveryTimeout = 20000;
        // Set LUME_MANUAL_GUEST_REPLY=1 to keep guest replies entirely manual (no broker contact).
        internal static bool Enabled = Environment.GetEnvironmentVariable("LUME_MANUAL_GUEST_REPLY") != "1";
        static string Derive(string secret, string purpose)
        {
            using (HMACSHA256 hmac = new HMACSHA256(Security.Unbase64(secret)))
            {
                byte[] hash = hmac.ComputeHash(Encoding.ASCII.GetBytes("Lume guest rendezvous v1 / " + purpose));
                StringBuilder hex = new StringBuilder(32); for (int i = 0; i < 16; i++) hex.Append(hash[i].ToString("x2")); return hex.ToString();
            }
        }
        public static string HostId(Invitation session) { return "lume-" + Derive(session.Secret, "host"); }
        public static string Route(Invitation session) { return Derive(session.Secret, "route"); }
        static bool ValidPeer(string id) { return id != null && id.StartsWith("lume-", StringComparison.Ordinal) && Invitation.IsHex(id.Substring(5), 32); }

        // Sharing PC: accepts the first authenticated reply that matches this invitation.
        public static async Task<SignalBroker> Listen(PeerSignal offer, Action<PeerSignal> received)
        {
            if (offer == null || offer.IsReply || offer.Session == null) throw new ArgumentException("An invitation is required.");
            Invitation session = offer.Session; string route = Route(session); int delivered = 0;
            SignalBroker broker = new SignalBroker(HostId(session));
            broker.Message += delegate(BrokerPacket packet)
            {
                // Anyone can address this broker ID: ignore anything unauthenticated or malformed.
                try
                {
                    SignalEnvelope envelope = packet.payload;
                    if (packet.type == "EXPIRE" || envelope == null || envelope.route != route || envelope.stage != "answer" || !ValidPeer(packet.src) || Volatile.Read(ref delivered) != 0) return;
                    SignalBody body = SignalCrypto.Open(session.Secret, packet.src, broker.Id, envelope);
                    PeerSignal reply = PeerSignal.Parse(body.code); offer.VerifyReply(reply);
                    if (Interlocked.Exchange(ref delivered, 1) != 0) return;
                    Task sent = broker.Send(packet.src, SignalCrypto.Seal(session.Secret, broker.Id, packet.src, route, envelope.request, "received", new SignalBody()));
                    sent.ContinueWith(delegate(Task failed) { var ignored = failed.Exception; }, TaskContinuationOptions.OnlyOnFaulted);
                    received(reply);
                }
                catch (Exception) { }
            };
            try { await broker.Start(Security.Token(32)).ConfigureAwait(false); return broker; }
            catch { broker.Dispose(); throw; }
        }

        // Controlling PC: true only when the sharing PC confirmed it received this reply.
        public static async Task<bool> Deliver(PeerSignal offer, PeerSignal reply, CancellationToken cancellation)
        {
            if (offer == null || offer.IsReply || offer.Session == null || reply == null || !reply.IsReply) throw new ArgumentException("An invitation and its reply are required.");
            Invitation session = offer.Session; string route = Route(session), target = HostId(session), request = Guid.NewGuid().ToString("N");
            using (SignalBroker broker = new SignalBroker(SignalBroker.NewId()))
            using (cancellation.Register(broker.Dispose))
            {
                TaskCompletionSource<bool> confirmed = new TaskCompletionSource<bool>();
                broker.Closed += delegate { confirmed.TrySetResult(false); };
                broker.Message += delegate(BrokerPacket packet)
                {
                    try
                    {
                        // The broker reports EXPIRE when the sharing PC is not listening (for example an older version).
                        if (packet.type == "EXPIRE") { confirmed.TrySetResult(false); return; }
                        SignalEnvelope envelope = packet.payload;
                        if (packet.src != target || envelope == null || envelope.route != route || envelope.request != request || envelope.stage != "received") return;
                        SignalCrypto.Open(session.Secret, packet.src, broker.Id, envelope); confirmed.TrySetResult(true);
                    }
                    catch (Exception) { }
                };
                await broker.Start(Security.Token(32)).ConfigureAwait(false);
                await broker.Send(target, SignalCrypto.Seal(session.Secret, broker.Id, target, route, request, "answer", new SignalBody { code = reply.ToString() })).ConfigureAwait(false);
                Task limit = Task.Delay(DeliveryTimeout, cancellation);
                if (await Task.WhenAny(confirmed.Task, limit).ConfigureAwait(false) != confirmed.Task) { cancellation.ThrowIfCancellationRequested(); return false; }
                return confirmed.Task.Result;
            }
        }
    }
}
