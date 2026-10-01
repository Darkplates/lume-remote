using System;
using System.IO;
using System.Security.Cryptography;
using System.Text;
using System.Threading;
using System.Threading.Tasks;

namespace LumeRemote
{
    // Short-code pairing: a person types eight digits shown on the other PC instead of copying
    // the long one-time pairing code. The digits only let the two PCs find each other through
    // the public broker. Authenticity comes from an ephemeral P-256 key agreement with a hash
    // commitment and a six-digit comparison that both people confirm. Only then does the
    // sharing PC send its existing one-time pairing code, encrypted under the agreed key, and
    // the joining PC continues with the unchanged PairedClient.Pair flow.
    public static class ShortPairing
    {
        public const int CodeDigits = 8;
        const int KeyTimeout = 20000, CodeTimeout = 180000;
        public static string NewCode()
        {
            StringBuilder digits = new StringBuilder(CodeDigits); byte[] one = new byte[1];
            using (RandomNumberGenerator rng = RandomNumberGenerator.Create())
                while (digits.Length < CodeDigits) { rng.GetBytes(one); if (one[0] < 250) digits.Append((char)('0' + one[0] % 10)); }
            return digits.ToString();
        }
        // Accepts "4829 1307", "4829-1307" or "48291307"; null when it is not a short code.
        public static string Normalize(string text)
        {
            if (text == null) return null;
            StringBuilder digits = new StringBuilder(CodeDigits);
            foreach (char c in text.Trim())
            {
                if (c >= '0' && c <= '9') { if (digits.Length == CodeDigits) return null; digits.Append(c); }
                else if (c != ' ' && c != '-') return null;
            }
            return digits.Length == CodeDigits ? digits.ToString() : null;
        }
        public static string Format(string code) { return code.Substring(0, 4) + " " + code.Substring(4); }
        static byte[] Derive(string code, string purpose)
        {
            using (HMACSHA256 hmac = new HMACSHA256(Encoding.ASCII.GetBytes("Lume short pairing v1")))
                return hmac.ComputeHash(Encoding.ASCII.GetBytes(purpose + "\n" + code));
        }
        static string Hex(byte[] value, int bytes) { StringBuilder hex = new StringBuilder(bytes * 2); for (int i = 0; i < bytes; i++) hex.Append(value[i].ToString("x2")); return hex.ToString(); }
        internal static string HostId(string code) { return "lume-" + Hex(Derive(code, "host"), 16); }
        internal static string Route(string code) { return Hex(Derive(code, "route"), 16); }
        // Only hides the exchange from casual observers; the digits are not a secret key.
        internal static string EnvelopeKey(string code) { return Security.Base64(Derive(code, "envelope")); }
        internal static bool ValidPeer(string id) { return id != null && id.StartsWith("lume-", StringComparison.Ordinal) && Invitation.IsHex(id.Substring(5), 32); }
        internal static string ClaimedName(string name)
        {
            if (String.IsNullOrWhiteSpace(name)) return "Unnamed PC";
            StringBuilder clean = new StringBuilder(); foreach (char c in name) if (!Char.IsControl(c)) clean.Append(c);
            string value = clean.ToString().Trim(); return value.Length == 0 ? "Unnamed PC" : value.Length > 64 ? value.Substring(0, 64) : value;
        }
        static async Task<T> Within<T>(Task<T> task, int milliseconds, CancellationToken cancellation, string message)
        {
            if (await Task.WhenAny(task, Task.Delay(milliseconds, cancellation)).ConfigureAwait(false) != task) { cancellation.ThrowIfCancellationRequested(); throw new TimeoutException(message); }
            return await task.ConfigureAwait(false);
        }

        // Joining PC: returns the long one-time pairing code after both people confirmed.
        public static async Task<string> Join(string code, string thisName, Func<string, string, Task<bool>> confirm, Action<string> status, CancellationToken cancellation)
        {
            if (Normalize(code) != code) throw new ArgumentException("Enter the eight-digit code shown on the other PC.");
            string target = HostId(code), route = Route(code), request = Guid.NewGuid().ToString("N"), envelope = EnvelopeKey(code);
            using (PairingHandshake handshake = new PairingHandshake())
            using (SignalBroker broker = new SignalBroker(SignalBroker.NewId()))
            using (cancellation.Register(broker.Dispose))
            {
                TaskCompletionSource<SignalBody> offered = new TaskCompletionSource<SignalBody>(), delivered = new TaskCompletionSource<SignalBody>();
                string sessionKey = null; object gate = new object();
                broker.Closed += delegate(string message) { IOException error = new IOException("The pairing service disconnected. Try again."); offered.TrySetException(error); delivered.TrySetException(error); };
                broker.Message += delegate(BrokerPacket packet)
                {
                    try
                    {
                        if (packet.type == "EXPIRE")
                        {
                            offered.TrySetException(new IOException("No PC is showing this code. Check the digits, and keep Pair another PC open on the other PC."));
                            delivered.TrySetException(new IOException("The other PC closed its pairing window. Start again from Pair another PC."));
                            return;
                        }
                        SignalEnvelope sealedBody = packet.payload;
                        if (packet.src != target || sealedBody == null || sealedBody.route != route || sealedBody.request != request) return;
                        if (sealedBody.stage == "key") offered.TrySetResult(SignalCrypto.Open(envelope, packet.src, broker.Id, sealedBody));
                        else if (sealedBody.stage == "code") { string agreed; lock (gate) agreed = sessionKey; if (agreed != null) delivered.TrySetResult(SignalCrypto.Open(agreed, packet.src, broker.Id, sealedBody)); }
                        else if (sealedBody.stage == "reject")
                        {
                            SignalCrypto.Open(envelope, packet.src, broker.Id, sealedBody);
                            OperationCanceledException stopped = new OperationCanceledException("The other PC stopped the pairing because the numbers did not match. Nothing was shared.");
                            offered.TrySetException(stopped); delivered.TrySetException(stopped);
                        }
                        else if (sealedBody.stage == "busy") offered.TrySetException(new IOException("Another PC is already pairing with this code. Choose Pair another PC again on the other PC."));
                    }
                    // Anyone can address this broker ID: ignore unauthenticated or malformed messages.
                    catch (CryptographicException) { }
                    catch (InvalidDataException) { }
                    catch (FormatException) { }
                    catch (Exception error) { offered.TrySetException(error); }
                };
                status("Finding the other PC...");
                await broker.Start(Security.Token(32)).ConfigureAwait(false);
                await broker.Send(target, SignalCrypto.Seal(envelope, broker.Id, target, route, request, "commit", new SignalBody { message = Security.Base64(handshake.Commitment()) })).ConfigureAwait(false);
                SignalBody offer = await Within(offered.Task, KeyTimeout, cancellation, "The other PC did not answer. Check the digits and keep Pair another PC open there.").ConfigureAwait(false);
                byte[] offerPublic = PairingHandshake.Bytes(offer.key, PairingHandshake.PublicKeyLength), offerNonce = PairingHandshake.Bytes(offer.code, PairingHandshake.NonceLength);
                // Derive first, so an early confirmation from the other PC can already be opened.
                handshake.Complete(route, offerPublic, offerNonce, handshake.PublicKey, handshake.Nonce, false);
                lock (gate) sessionKey = handshake.SessionKey;
                await broker.Send(target, SignalCrypto.Seal(envelope, broker.Id, target, route, request, "reveal", new SignalBody { key = Security.Base64(handshake.PublicKey), code = Security.Base64(handshake.Nonce), name = thisName })).ConfigureAwait(false);
                status("Compare the numbers on both PCs.");
                if (!await confirm(handshake.Sas, ClaimedName(offer.name)).ConfigureAwait(false))
                {
                    try { await broker.Send(target, SignalCrypto.Seal(envelope, broker.Id, target, route, request, "reject", new SignalBody())).ConfigureAwait(false); } catch (Exception) { }
                    throw new OperationCanceledException("Pairing stopped because the numbers did not match. Nothing was shared.");
                }
                status("Waiting for the other PC to confirm...");
                SignalBody result = await Within(delivered.Task, CodeTimeout, cancellation, "The other PC did not confirm in time. Start again from Pair another PC.").ConfigureAwait(false);
                PairingCode.Parse(result.code);
                return result.code;
            }
        }
    }

    // Sharing PC: holds the short code while its Pair another PC window is open.
    public sealed class ShortPairingOffer : IDisposable
    {
        public readonly string Code;
        // Raised on a broker thread: the comparison number and the joining PC's claimed name.
        public event Action<string, string> Ready;
        public event Action<string> Failed;
        readonly string pairingCode, thisName, route, envelope;
        readonly SignalBroker broker;
        readonly PairingHandshake handshake = new PairingHandshake();
        readonly object gate = new object();
        string peer, request;
        byte[] commitment;
        bool revealed, verified, sent;
        int disposed;
        ShortPairingOffer(string pairingCode, string thisName)
        {
            this.pairingCode = pairingCode; this.thisName = thisName; Code = ShortPairing.NewCode();
            route = ShortPairing.Route(Code); envelope = ShortPairing.EnvelopeKey(Code); broker = new SignalBroker(ShortPairing.HostId(Code));
            broker.Message += Receive;
            broker.Closed += delegate { Fail("The pairing service disconnected. Use the full code instead."); };
        }
        public static async Task<ShortPairingOffer> Start(string pairingCode, string thisName)
        {
            long expires = PairingCode.Parse(pairingCode).expires;
            ShortPairingOffer offer = new ShortPairingOffer(pairingCode, thisName);
            try
            {
                await offer.broker.Start(Security.Token(32)).ConfigureAwait(false);
                // The digits never outlive the one-time code they carry.
                int remaining = (int)Math.Max(1000, Math.Min(Int32.MaxValue, (expires - DateTime.UtcNow.Ticks) / TimeSpan.TicksPerMillisecond));
                Task expiry = Task.Delay(remaining).ContinueWith(delegate { offer.Fail("This code expired. Close this window and choose Pair another PC again."); offer.Dispose(); });
                return offer;
            }
            catch { offer.Dispose(); throw; }
        }
        void Send(string destination, string requestId, string stage, SignalBody body, string key)
        {
            Task sending = broker.Send(destination, SignalCrypto.Seal(key, broker.Id, destination, route, requestId, stage, body));
            sending.ContinueWith(delegate(Task failed) { var ignored = failed.Exception; }, TaskContinuationOptions.OnlyOnFaulted);
        }
        void Receive(BrokerPacket packet)
        {
            try
            {
                SignalEnvelope sealedBody = packet.payload;
                if (sealedBody == null || sealedBody.route != route || !ShortPairing.ValidPeer(packet.src)) return;
                if (sealedBody.stage == "commit")
                {
                    SignalBody body = SignalCrypto.Open(envelope, packet.src, broker.Id, sealedBody);
                    byte[] value = PairingHandshake.Bytes(body.message, PairingHandshake.CommitmentLength);
                    lock (gate)
                    {
                        // One joining PC per code: later requests learn that it is taken.
                        if (peer != null) { if (packet.src != peer) Send(packet.src, sealedBody.request, "busy", new SignalBody(), envelope); return; }
                        peer = packet.src; request = sealedBody.request; commitment = value;
                    }
                    Send(peer, request, "key", new SignalBody { key = Security.Base64(handshake.PublicKey), code = Security.Base64(handshake.Nonce), name = thisName }, envelope);
                    return;
                }
                string from, requestId; lock (gate) { from = peer; requestId = request; }
                if (packet.src != from || sealedBody.request != requestId) return;
                // From here on the sender is the pinned joining PC: its failures end this attempt.
                try
                {
                    if (sealedBody.stage == "reveal")
                    {
                        SignalBody body = SignalCrypto.Open(envelope, packet.src, broker.Id, sealedBody);
                        lock (gate) { if (revealed) return; revealed = true; }
                        byte[] joinPublic = PairingHandshake.Bytes(body.key, PairingHandshake.PublicKeyLength), joinNonce = PairingHandshake.Bytes(body.code, PairingHandshake.NonceLength);
                        if (!PairingHandshake.Opens(commitment, joinPublic, joinNonce)) { Fail("The other PC did not prove its key. Nothing was shared; start again."); return; }
                        handshake.Complete(route, handshake.PublicKey, handshake.Nonce, joinPublic, joinNonce, true);
                        lock (gate) verified = true;
                        Action<string, string> ready = Ready; if (ready != null) ready(handshake.Sas, ShortPairing.ClaimedName(body.name));
                    }
                    else if (sealedBody.stage == "reject")
                    {
                        SignalCrypto.Open(envelope, packet.src, broker.Id, sealedBody);
                        Fail(CodeSent ? "The other PC reported that the numbers do not match after the code was sent. The pairing code is being cancelled; start again." :
                            "The other PC reported that the numbers do not match. Nothing was shared.");
                    }
                }
                catch (Exception) { lock (gate) { if (!revealed) return; } Fail("The pairing exchange failed. Nothing was shared; start again."); }
            }
            // Anyone who knows the digits can send here: ignore whatever does not come from the pinned PC.
            catch (Exception) { }
        }
        public bool CodeSent { get { lock (gate) return sent; } }
        // The owner saw different numbers: tell the joining PC, then stop.
        public void Reject()
        {
            string destination, requestId; lock (gate) { destination = peer; requestId = request; }
            if (destination != null) Send(destination, requestId, "reject", new SignalBody(), envelope);
            Dispose();
        }
        // The owner confirmed that both PCs show the same number.
        public void Confirm()
        {
            string destination, requestId;
            lock (gate) { if (!verified || sent || Volatile.Read(ref disposed) != 0) return; sent = true; destination = peer; requestId = request; }
            Send(destination, requestId, "code", new SignalBody { code = pairingCode }, handshake.SessionKey);
        }
        void Fail(string message) { if (Volatile.Read(ref disposed) != 0) return; Action<string> failed = Failed; if (failed != null) failed(message); }
        public void Dispose()
        {
            if (Interlocked.Exchange(ref disposed, 1) != 0) return;
            // Give a just-confirmed code a moment to leave before closing the broker connection.
            bool linger; lock (gate) linger = sent || peer != null;
            Task.Delay(linger ? 3000 : 0).ContinueWith(delegate { broker.Dispose(); handshake.Dispose(); });
        }
    }

    // Transport-independent cryptographic core, so it can be tested without the broker.
    public sealed class PairingHandshake : IDisposable
    {
        public const int PublicKeyLength = 64, NonceLength = 32, CommitmentLength = 32;
        readonly ECDiffieHellman key = ECDiffieHellman.Create(ECCurve.NamedCurves.nistP256);
        public readonly byte[] PublicKey, Nonce = new byte[NonceLength];
        public string Sas { get; private set; }
        public string SessionKey { get; private set; }
        public PairingHandshake()
        {
            ECParameters parameters = key.ExportParameters(false);
            PublicKey = new byte[PublicKeyLength]; Buffer.BlockCopy(parameters.Q.X, 0, PublicKey, 0, 32); Buffer.BlockCopy(parameters.Q.Y, 0, PublicKey, 32, 32);
            using (RandomNumberGenerator rng = RandomNumberGenerator.Create()) rng.GetBytes(Nonce);
        }
        internal static byte[] Bytes(string value, int length)
        {
            byte[] bytes = Security.Unbase64(value ?? "");
            if (bytes.Length != length) throw new InvalidDataException("Invalid pairing handshake value.");
            return bytes;
        }
        static byte[] Hash(string label, params byte[][] parts)
        {
            using (SHA256 sha = SHA256.Create())
            {
                byte[] head = Encoding.ASCII.GetBytes(label + "\n"); sha.TransformBlock(head, 0, head.Length, null, 0);
                foreach (byte[] part in parts) sha.TransformBlock(part, 0, part.Length, null, 0);
                sha.TransformFinalBlock(new byte[0], 0, 0); return sha.Hash;
            }
        }
        // The joining PC commits to its key before it sees the sharing PC's key, so a
        // relay in the middle cannot search for keys that make both numbers match.
        public byte[] Commitment() { return Hash("Lume pairing commitment v1", PublicKey, Nonce); }
        public static bool Opens(byte[] commitment, byte[] publicKey, byte[] nonce)
        {
            if (commitment == null || commitment.Length != CommitmentLength) return false;
            byte[] expected = Hash("Lume pairing commitment v1", publicKey, nonce); int difference = 0;
            for (int i = 0; i < CommitmentLength; i++) difference |= expected[i] ^ commitment[i];
            return difference == 0;
        }
        public void Complete(string route, byte[] offerPublic, byte[] offerNonce, byte[] joinPublic, byte[] joinNonce, bool offering)
        {
            byte[] otherPublic = offering ? joinPublic : offerPublic;
            byte[] x = new byte[32], y = new byte[32]; Buffer.BlockCopy(otherPublic, 0, x, 0, 32); Buffer.BlockCopy(otherPublic, 32, y, 0, 32);
            byte[] shared;
            using (ECDiffieHellman other = ECDiffieHellman.Create(new ECParameters { Curve = ECCurve.NamedCurves.nistP256, Q = new ECPoint { X = x, Y = y } })) shared = key.DeriveKeyFromHash(other.PublicKey, HashAlgorithmName.SHA256);
            byte[] transcript = Hash("Lume pairing transcript v1", Encoding.ASCII.GetBytes(route), offerPublic, offerNonce, joinPublic, joinNonce);
            try
            {
                using (HMACSHA256 hmac = new HMACSHA256(shared))
                {
                    byte[] sas = hmac.ComputeHash(Combine(Encoding.ASCII.GetBytes("comparison\n"), transcript));
                    uint number = ((uint)sas[0] << 24 | (uint)sas[1] << 16 | (uint)sas[2] << 8 | sas[3]) % 1000000;
                    string digits = number.ToString("000000"); Sas = digits.Substring(0, 3) + " " + digits.Substring(3);
                    SessionKey = Security.Base64(hmac.ComputeHash(Combine(Encoding.ASCII.GetBytes("session key\n"), transcript)));
                }
            }
            finally { Array.Clear(shared, 0, shared.Length); }
        }
        static byte[] Combine(byte[] a, byte[] b) { byte[] c = new byte[a.Length + b.Length]; Buffer.BlockCopy(a, 0, c, 0, a.Length); Buffer.BlockCopy(b, 0, c, a.Length, b.Length); return c; }
        public void Dispose() { key.Dispose(); }
    }
}
