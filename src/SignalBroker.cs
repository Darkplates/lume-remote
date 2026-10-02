using System;
using System.Collections.Generic;
using System.IO;
using System.Net.WebSockets;
using System.Security.Cryptography;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using System.Web.Script.Serialization;

namespace LumeRemote
{
    public static class JsonData
    {
        public static string Encode(object value) { return new JavaScriptSerializer { MaxJsonLength = 196608, RecursionLimit = 16 }.Serialize(value); }
        public static T Decode<T>(string value)
        { if (value == null || value.Length > 196608) throw new InvalidDataException("The message exceeds its size limit."); return new JavaScriptSerializer { MaxJsonLength = 196608, RecursionLimit = 16 }.Deserialize<T>(value); }
    }
    public sealed class SignalEnvelope { public int v = 1; public string route, request, stage, box; }
    public sealed class BrokerPacket { public string type, src, dst; public SignalEnvelope payload; }
    sealed class BrokerSdp { public string type, sdp; }
    sealed class BrokerPayload
    {
        public string type = "data", connectionId, label = "lume-encrypted", serialization = "raw", browser = "Lume";
        public bool reliable = true;
        public BrokerSdp sdp;
        public SignalEnvelope metadata;
    }
    sealed class BrokerWirePacket { public string type, dst; public string src { get; set; } public BrokerPayload payload; }
    public sealed class SignalBody
    {
        public long created;
        public string name, key, code, message, mac;
    }
    public static class SignalCrypto
    {
        static byte[] Derive(string key, string purpose)
        { using (HMACSHA256 hmac = new HMACSHA256(Security.Unbase64(key))) return hmac.ComputeHash(Encoding.ASCII.GetBytes("Lume signaling v1 / " + purpose)); }
        static byte[] Header(string source, string destination, SignalEnvelope envelope)
        { return Encoding.UTF8.GetBytes("1\n" + source + "\n" + destination + "\n" + envelope.route + "\n" + envelope.request + "\n" + envelope.stage + "\n"); }
        static byte[] Authenticate(byte[] key, byte[] header, byte[] data)
        {
            using (HMACSHA256 hmac = new HMACSHA256(key))
            { hmac.TransformBlock(header, 0, header.Length, null, 0); hmac.TransformFinalBlock(data, 0, data.Length); return hmac.Hash; }
        }
        public static SignalEnvelope Seal(string key, string source, string destination, string route, string request, string stage, SignalBody body)
        {
            SignalEnvelope envelope = new SignalEnvelope { route = route, request = request, stage = stage };
            body.created = DateTime.UtcNow.Ticks; byte[] plain = Encoding.UTF8.GetBytes(JsonData.Encode(body)); if (plain.Length > 98304) throw new InvalidDataException("Signaling payload is too large.");
            using (Aes aes = Aes.Create())
            {
                aes.Key = Derive(key, "encryption"); aes.GenerateIV(); aes.Mode = CipherMode.CBC; aes.Padding = PaddingMode.PKCS7;
                byte[] encrypted; using (ICryptoTransform transform = aes.CreateEncryptor()) encrypted = transform.TransformFinalBlock(plain, 0, plain.Length);
                byte[] authenticated = new byte[16 + encrypted.Length]; Buffer.BlockCopy(aes.IV, 0, authenticated, 0, 16); Buffer.BlockCopy(encrypted, 0, authenticated, 16, encrypted.Length);
                byte[] tag = Authenticate(Derive(key, "authentication"), Header(source, destination, envelope), authenticated);
                byte[] result = new byte[authenticated.Length + tag.Length]; Buffer.BlockCopy(authenticated, 0, result, 0, authenticated.Length); Buffer.BlockCopy(tag, 0, result, authenticated.Length, tag.Length);
                envelope.box = Security.Base64(result); Array.Clear(plain, 0, plain.Length); return envelope;
            }
        }
        public static SignalBody Open(string key, string source, string destination, SignalEnvelope envelope)
        {
            if (envelope == null || envelope.v != 1 || !Invitation.IsHex(envelope.route, 32) || !Invitation.IsHex(envelope.request, 32) || envelope.stage == null || envelope.stage.Length > 20 || envelope.box == null || envelope.box.Length > 140000)
                throw new InvalidDataException("Invalid encrypted signaling envelope.");
            string encoded = envelope.box.Replace('-', '+').Replace('_', '/'); byte[] packed = Convert.FromBase64String(encoded.PadRight((encoded.Length + 3) / 4 * 4, '='));
            if (packed.Length < 64 || (packed.Length - 48) % 16 != 0) throw new InvalidDataException("Invalid encrypted signaling length.");
            byte[] authenticated = new byte[packed.Length - 32], supplied = new byte[32]; Buffer.BlockCopy(packed, 0, authenticated, 0, authenticated.Length); Buffer.BlockCopy(packed, authenticated.Length, supplied, 0, 32);
            byte[] expected = Authenticate(Derive(key, "authentication"), Header(source, destination, envelope), authenticated);
            if (!Security.Equal(Security.Base64(supplied), Security.Base64(expected))) throw new CryptographicException("Signaling authentication failed.");
            using (Aes aes = Aes.Create())
            {
                aes.Key = Derive(key, "encryption"); byte[] iv = new byte[16]; Buffer.BlockCopy(authenticated, 0, iv, 0, 16); aes.IV = iv; aes.Mode = CipherMode.CBC; aes.Padding = PaddingMode.PKCS7;
                byte[] plain; using (ICryptoTransform transform = aes.CreateDecryptor()) plain = transform.TransformFinalBlock(authenticated, 16, authenticated.Length - 16);
                try
                {
                    SignalBody body = JsonData.Decode<SignalBody>(new UTF8Encoding(false, true).GetString(plain));
                    long now = DateTime.UtcNow.Ticks;
                    if (body == null || body.created < now - TimeSpan.TicksPerMinute * 5 || body.created > now + TimeSpan.TicksPerMinute * 5) throw new InvalidDataException("The request expired. Check the clock on both PCs.");
                    return body;
                }
                finally { Array.Clear(plain, 0, plain.Length); }
            }
        }
    }
    public sealed class SignalBroker : IDisposable
    {
        public const string DefaultEndpoint = "wss://0.peerjs.com/peerjs";
        readonly ClientWebSocket socket = new ClientWebSocket();
        readonly CancellationTokenSource stopped = new CancellationTokenSource();
        readonly SemaphoreSlim sending = new SemaphoreSlim(1, 1);
        public readonly string Id;
        public event Action<BrokerPacket> Message;
        public event Action<string> Closed;
        int disposed, failed, closing;
        readonly TaskCompletionSource<bool> readEnded = new TaskCompletionSource<bool>();
        public SignalBroker(string id) { if (!id.StartsWith("lume-", StringComparison.Ordinal) || !Invitation.IsHex(id.Substring(5), 32)) throw new ArgumentException("Invalid signaling identity."); Id = id; }
        public static string NewId() { return "lume-" + Guid.NewGuid().ToString("N"); }
        public async Task Start(string token, string endpoint = DefaultEndpoint)
        {
            Uri uri = new Uri(endpoint); if (uri.Scheme != "wss" || uri.UserInfo.Length != 0 || !String.IsNullOrEmpty(uri.Query) || !String.IsNullOrEmpty(uri.Fragment)) throw new InvalidDataException("A secure WebSocket signaling endpoint is required.");
            socket.Options.KeepAliveInterval = TimeSpan.FromSeconds(20);
            using (CancellationTokenSource timeout = CancellationTokenSource.CreateLinkedTokenSource(stopped.Token))
            {
                timeout.CancelAfter(15000);
                await socket.ConnectAsync(new Uri(endpoint + "?key=peerjs&id=" + Uri.EscapeDataString(Id) + "&token=" + Uri.EscapeDataString(token)), timeout.Token).ConfigureAwait(false);
                BrokerPacket opened = await Read(timeout.Token).ConfigureAwait(false);
                if (opened == null || opened.type != "OPEN") throw new IOException(opened != null && opened.type == "ID-TAKEN" ? "Another Lume host is already using this saved computer. Close duplicate hosts." : "The signaling service did not accept the connection.");
            }
            StartLoops();
        }
        void StartLoops() { Task.Run((Func<Task>)ReadLoop); Task.Run((Func<Task>)Heartbeat); }
        async Task<BrokerPacket> Read(CancellationToken cancellation)
        {
            byte[] block = new byte[8192];
            using (MemoryStream buffer = new MemoryStream())
            {
                WebSocketReceiveResult part;
                do
                {
                    part = await socket.ReceiveAsync(new ArraySegment<byte>(block), cancellation).ConfigureAwait(false);
                    if (part.MessageType == WebSocketMessageType.Close) throw new IOException("The signaling connection closed (" + part.CloseStatus + "): " + part.CloseStatusDescription);
                    if (part.MessageType != WebSocketMessageType.Text || buffer.Length + part.Count > 196608) throw new InvalidDataException("Invalid signaling packet.");
                    buffer.Write(block, 0, part.Count);
                } while (!part.EndOfMessage);
                BrokerWirePacket packet = JsonData.Decode<BrokerWirePacket>(new UTF8Encoding(false, true).GetString(buffer.ToArray()));
                return new BrokerPacket { type = packet.type, src = packet.src, dst = packet.dst, payload = packet.payload == null ? null : packet.payload.metadata };
            }
        }
        async Task ReadLoop()
        {
            try
            {
                while (!stopped.IsCancellationRequested)
                {
                    BrokerPacket packet = await Read(stopped.Token).ConfigureAwait(false);
                    Action<BrokerPacket> received = Message;
                    if (packet != null && received != null) received(packet);
                }
            }
            catch (Exception error) { Failed(error); }
            finally { readEnded.TrySetResult(true); }
        }
        async Task Heartbeat()
        {
            try { while (!stopped.IsCancellationRequested) { await Task.Delay(5000, stopped.Token).ConfigureAwait(false); await SendRaw(new BrokerPacket { type = "HEARTBEAT" }).ConfigureAwait(false); } }
            catch (Exception error) { Failed(error); }
        }
        void Failed(Exception error)
        {
            if (stopped.IsCancellationRequested || Volatile.Read(ref closing) != 0 || Interlocked.Exchange(ref failed, 1) != 0) return;
            socket.Abort();
            Action<string> closed = Closed; if (closed != null) closed("Signaling disconnected: " + error.Message);
        }
        public Task Send(string destination, SignalEnvelope envelope)
        { return SendRaw(new BrokerPacket { type = envelope.stage == "answer" || envelope.stage == "paired" || envelope.stage == "woke" ? "ANSWER" : "OFFER", dst = destination, payload = envelope }); }
        async Task SendRaw(BrokerPacket packet)
        {
            BrokerWirePacket wire = new BrokerWirePacket { type = packet.type, dst = packet.dst };
            if (packet.payload != null)
            {
                // PeerServer validates its SDP carrier. This shell contains no addresses,
                // credentials or real SDP; the native negotiation remains encrypted metadata.
                wire.payload = new BrokerPayload { connectionId = "dc_" + packet.payload.request, metadata = packet.payload,
                    sdp = new BrokerSdp { type = packet.type == "ANSWER" ? "answer" : "offer", sdp = "v=0\r\no=- 0 0 IN IP4 127.0.0.1\r\ns=Lume encrypted signaling\r\nt=0 0\r\nm=application 9 UDP/DTLS/SCTP webrtc-datachannel\r\nc=IN IP4 0.0.0.0\r\na=mid:0\r\na=sctp-port:5000\r\n" } };
            }
            byte[] bytes = Encoding.UTF8.GetBytes(JsonData.Encode(wire)); if (bytes.Length > 196608) throw new InvalidDataException("Signaling packet is too large.");
            using (CancellationTokenSource timeout = CancellationTokenSource.CreateLinkedTokenSource(stopped.Token))
            {
                timeout.CancelAfter(15000);
                await sending.WaitAsync(timeout.Token).ConfigureAwait(false);
                try { await socket.SendAsync(new ArraySegment<byte>(bytes), WebSocketMessageType.Text, true, timeout.Token).ConfigureAwait(false); }
                finally { sending.Release(); }
            }
        }
        // Ends the connection after the messages already sent, so a last message (for example a
        // refusal) reaches the service. Dispose alone resets the socket, which can drop it.
        public async Task Close(int milliseconds)
        {
            if (Volatile.Read(ref disposed) != 0 || Interlocked.Exchange(ref closing, 1) != 0) { Dispose(); return; }
            try
            {
                using (CancellationTokenSource limit = new CancellationTokenSource(milliseconds))
                {
                    await sending.WaitAsync(limit.Token).ConfigureAwait(false);
                    try { await socket.CloseOutputAsync(WebSocketCloseStatus.NormalClosure, "", limit.Token).ConfigureAwait(false); }
                    finally { sending.Release(); }
                    await Task.WhenAny(readEnded.Task, Task.Delay(milliseconds, limit.Token)).ConfigureAwait(false);
                }
            }
            catch (Exception) { }
            finally { Dispose(); }
        }
        public void Dispose() { if (Interlocked.Exchange(ref disposed, 1) != 0) return; stopped.Cancel(); socket.Abort(); socket.Dispose(); }
    }
}
