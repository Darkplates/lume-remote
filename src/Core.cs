using System;
using System.IO;
using System.Net;
using System.Net.Sockets;
using System.Net.Security;
using System.Security.Authentication;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using System.Text;
using System.Threading;

namespace LumeRemote
{
    public static class Security
    {
        public static string Token(int bytes)
        {
            byte[] value = new byte[bytes];
            using (RandomNumberGenerator rng = RandomNumberGenerator.Create()) rng.GetBytes(value);
            return Base64(value);
        }
        public static string Base64(byte[] value) { return Convert.ToBase64String(value).TrimEnd('=').Replace('+', '-').Replace('/', '_'); }
        public static byte[] Unbase64(string value)
        {
            if (value == null || value.Length > 8192) throw new FormatException("Invalid invitation length.");
            value = value.Replace('-', '+').Replace('_', '/');
            return Convert.FromBase64String(value.PadRight((value.Length + 3) / 4 * 4, '='));
        }
        public static bool Equal(string a, string b)
        {
            if (a == null || b == null) return false;
            int result = a.Length ^ b.Length;
            int count = Math.Max(a.Length, b.Length);
            for (int i = 0; i < count; i++) result |= (i < a.Length ? a[i] : 0) ^ (i < b.Length ? b[i] : 0);
            return result == 0;
        }
        public static string Pin(X509Certificate certificate)
        {
            using (SHA256 hash = SHA256.Create()) return BitConverter.ToString(hash.ComputeHash(certificate.GetRawCertData())).Replace("-", "");
        }
        public static X509Certificate2 Certificate(int protocol = 4)
        {
            using (RSA rsa = new RSACng(2048))
            {
                CertificateRequest request = new CertificateRequest(protocol >= 4 ? "CN=Lume Remote Session v4" : protocol >= 3 ? "CN=Lume Remote Session v3" : "CN=Lume Remote Session", rsa, HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1);
                request.CertificateExtensions.Add(new X509BasicConstraintsExtension(false, false, 0, true));
                request.CertificateExtensions.Add(new X509KeyUsageExtension(X509KeyUsageFlags.DigitalSignature | X509KeyUsageFlags.KeyEncipherment, true));
                using (X509Certificate2 generated = request.CreateSelfSigned(DateTimeOffset.UtcNow.AddMinutes(-5), DateTimeOffset.UtcNow.AddDays(7)))
                    // Schannel on .NET Framework needs a user key container. Without
                    // PersistKeySet the temporary container is removed on disposal.
                    return new X509Certificate2(generated.Export(X509ContentType.Pfx), (string)null, X509KeyStorageFlags.UserKeySet);
            }
        }
        public static void CheckTls(SslStream stream)
        {
            if (!stream.IsEncrypted || !stream.IsSigned || stream.CipherStrength < 128 ||
                (int)stream.SslProtocol < (int)SslProtocols.Tls12)
                throw new AuthenticationException("TLS 1.2 or newer with encryption is required.");
        }
    }

    public sealed class Invitation
    {
        public string Host, Secret, Fingerprint, Room;
        public int Port;
        public bool Relay { get { return !String.IsNullOrEmpty(Room); } }
        public override string ToString()
        {
            return "lume://" + Security.Base64(Encoding.UTF8.GetBytes(String.Join("|", new string[] { "1", Host, Port.ToString(System.Globalization.CultureInfo.InvariantCulture), Room ?? "", Secret, Fingerprint })));
        }
        public static Invitation Parse(string text)
        {
            if (text == null) throw new FormatException("Paste a Lume invitation first.");
            text = text.Trim();
            if (!text.StartsWith("lume://", StringComparison.Ordinal) || text.Length > 4096) throw new FormatException("This is not a Lume invitation.");
            string[] p = new UTF8Encoding(false, true).GetString(Security.Unbase64(text.Substring(7))).Split('|');
            int port;
            if (p.Length != 6 || p[0] != "1" || !Int32.TryParse(p[2], out port) || port < 1 || port > 65535 ||
                p[1].Length > 253 || Uri.CheckHostName(p[1]) == UriHostNameType.Unknown || p[1] == "0.0.0.0" || p[1] == "::")
                throw new FormatException("The invitation has an invalid address or version.");
            if (Security.Unbase64(p[4]).Length != 32 || p[4].Length != 43 || !IsHex(p[5], 64) || (p[3] != "" && !IsHex(p[3], 32)))
                throw new FormatException("The invitation has an invalid security key.");
            return new Invitation { Host = p[1], Port = port, Room = p[3], Secret = p[4], Fingerprint = p[5].ToUpperInvariant() };
        }
        public static bool IsHex(string value, int length)
        {
            if (value == null || value.Length != length) return false;
            foreach (char c in value) if (!Uri.IsHexDigit(c)) return false;
            return true;
        }
    }

    public enum Kind : byte { Auth = 1, Accepted = 2, Denied = 3, Frame = 4, Ack = 5, Input = 6, Release = 7, Clipboard = 8, Ping = 9, Pong = 10, Goodbye = 11, Notice = 12, Cursor = 13, Quality = 14, QualityInfo = 15, StreamMetrics = 16, Files = 17, Tools = 18, ToolReply = 19, Audio = 20, Voice = 21, VoiceEnded = 22 }

    public sealed class Packet : IDisposable
    {
        public readonly Kind Kind;
        public readonly BinaryReader Reader;
        public Packet(byte[] data) { Kind = (Kind)data[0]; Reader = new BinaryReader(new MemoryStream(data, 1, data.Length - 1, false), Encoding.UTF8); }
        public string Text(int maxBytes)
        {
            int length = Reader.ReadInt32();
            if (length < 0 || length > maxBytes || length > Reader.BaseStream.Length - Reader.BaseStream.Position) throw new InvalidDataException("Invalid text length.");
            byte[] data = Reader.ReadBytes(length);
            return new UTF8Encoding(false, true).GetString(data);
        }
        public void End() { if (Reader.BaseStream.Position != Reader.BaseStream.Length) throw new InvalidDataException("Unexpected packet data."); }
        public void Dispose() { Reader.Dispose(); }
    }

    public sealed class Wire : IDisposable
    {
        public const int MaxPacket = 144 * 1024 * 1024;
        readonly Stream stream;
        readonly object gate = new object();
        public long Sent, Received;
        public Wire(Stream stream) { this.stream = stream; }
        public static void Text(BinaryWriter writer, string value)
        {
            byte[] data = Encoding.UTF8.GetBytes(value ?? ""); writer.Write(data.Length); writer.Write(data);
        }
        public static byte[] Message(Kind kind, Action<BinaryWriter> write)
        {
            using (MemoryStream buffer = new MemoryStream())
            {
                using (BinaryWriter writer = new BinaryWriter(buffer, Encoding.UTF8, true)) { writer.Write((byte)kind); if (write != null) write(writer); }
                if (buffer.Length > MaxPacket) throw new InvalidDataException("Packet is too large.");
                return buffer.ToArray();
            }
        }
        public void Send(Kind kind, Action<BinaryWriter> write) { Send(Message(kind, write)); }
        public void Send(byte[] data)
        {
            if (data.Length < 1 || data.Length > MaxPacket) throw new InvalidDataException("Invalid packet size.");
            lock (gate)
            {
                byte[] size = BitConverter.GetBytes(data.Length); stream.Write(size, 0, 4); stream.Write(data, 0, data.Length); stream.Flush();
                Interlocked.Add(ref Sent, data.Length + 4);
            }
        }
        public Packet Read(int maximum)
        {
            byte[] head = ReadExact(stream, 4);
            int size = BitConverter.ToInt32(head, 0);
            if (size < 1 || size > Math.Min(maximum, MaxPacket)) throw new InvalidDataException("Invalid packet size.");
            byte[] data = ReadExact(stream, size); Interlocked.Add(ref Received, size + 4); return new Packet(data);
        }
        public static byte[] ReadExact(Stream stream, int count)
        {
            byte[] bytes = new byte[count]; int offset = 0;
            while (offset < count) { int n = stream.Read(bytes, offset, count - offset); if (n == 0) throw new EndOfStreamException("The connection was closed."); offset += n; }
            return bytes;
        }
        public void Dispose() { stream.Dispose(); }
    }

    public static class Transport
    {
        public static TcpClient Connect(string address, int port, int milliseconds)
        {
            TcpClient client = new TcpClient();
            try
            {
                IAsyncResult pending = client.BeginConnect(address, port, null, null);
                using (WaitHandle handle = pending.AsyncWaitHandle)
                { if (!handle.WaitOne(milliseconds)) throw new TimeoutException("Connection timed out. Check the address, relay and firewall."); }
                client.EndConnect(pending); Configure(client); return client;
            }
            catch { client.Close(); throw; }
        }
        public static void Configure(TcpClient client)
        {
            client.NoDelay = true; client.SendTimeout = 15000; client.ReceiveTimeout = 15000;
            client.Client.SetSocketOption(SocketOptionLevel.Socket, SocketOptionName.KeepAlive, true);
        }
        public static void JoinRelay(TcpClient client, string role, string room)
        {
            byte[] line = Encoding.ASCII.GetBytes(RelayHeader(role, room, role == "H" ? Environment.GetEnvironmentVariable("LUME_RELAY_TOKEN") : null));
            client.GetStream().Write(line, 0, line.Length);
            int response = client.GetStream().ReadByte();
            if (response != 1) throw new IOException(response == 2 ? "The relay has no waiting host. Ask for a fresh invitation or retry shortly." : "The relay rejected the connection. If it requires an operator token, set LUME_RELAY_TOKEN on the sharing PC.");
        }
        // A relay started with an operator token requires it from sharing PCs (relay/relay.py).
        // Viewers join by room only. The token is never logged.
        internal static string RelayHeader(string role, string room, string token)
        {
            if (String.IsNullOrEmpty(token)) return "LUME1 " + role + " " + room + "\n";
            if (token.Length < 16 || token.Length > 64) throw new InvalidDataException("LUME_RELAY_TOKEN must be 16-64 characters.");
            foreach (char c in token) if (!(c >= 'A' && c <= 'Z' || c >= 'a' && c <= 'z' || c >= '0' && c <= '9' || c == '.' || c == '_' || c == '~' || c == '-')) throw new InvalidDataException("LUME_RELAY_TOKEN may contain only letters, digits, '.', '_', '~' and '-'.");
            return "LUME1 " + role + " " + room + " " + token + "\n";
        }
    }
}
