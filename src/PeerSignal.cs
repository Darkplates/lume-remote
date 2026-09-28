using System;
using System.IO;
using System.IO.Compression;
using System.Security.Cryptography;
using System.Text;

namespace LumeRemote
{
    public sealed class PeerSignal
    {
        public const string OfferPrefix = "lume-p2p://", ReplyPrefix = "lume-reply://";
        public string Id, Sdp, Signature;
        public Invitation Session;
        public bool IsReply;
        public static PeerSignal Offer(Invitation session, string sdp)
        { ValidateSdp(sdp); return new PeerSignal { Id = Security.Token(16), Session = session, Sdp = sdp }; }
        public PeerSignal Reply(string sdp)
        {
            if (IsReply || Session == null) throw new InvalidOperationException("An invitation is required.");
            ValidateSdp(sdp); return new PeerSignal { IsReply = true, Id = Id, Sdp = sdp, Signature = Sign(Session.Secret, Id, sdp) };
        }
        public void VerifyReply(PeerSignal reply)
        {
            if (IsReply || reply == null || !reply.IsReply || !Security.Equal(Id, reply.Id) || !Security.Equal(reply.Signature, Sign(Session.Secret, Id, reply.Sdp)))
                throw new InvalidDataException("This reply does not match the current private invitation. Copy a fresh reply from the controlling PC.");
        }
        static string Sign(string secret, string id, string sdp)
        { using (HMACSHA256 hmac = new HMACSHA256(Security.Unbase64(secret))) return Security.Base64(hmac.ComputeHash(Encoding.UTF8.GetBytes(id + "\n" + sdp))); }
        public static void ValidateSdp(string value)
        {
            if (value == null || value.Length < 64 || value.Length > 30000 || !value.StartsWith("v=0\r\n", StringComparison.Ordinal) ||
                !value.Contains("m=application ") || !value.Contains("a=fingerprint:sha-256 ") || !value.Contains("a=ice-ufrag:") || !value.Contains("a=ice-pwd:"))
                throw new InvalidDataException("Invalid P2P connection description.");
            foreach (char c in value) if (c > 126 || (c < 32 && c != '\r' && c != '\n' && c != '\t')) throw new InvalidDataException("Invalid P2P description characters.");
        }
        public override string ToString()
        {
            byte[] raw = Wire.Message((Kind)(IsReply ? 91 : 90), delegate(BinaryWriter writer)
            {
                writer.Write(1); Wire.Text(writer, Id); Wire.Text(writer, IsReply ? Signature : Session.ToString()); Wire.Text(writer, Sdp);
            });
            using (MemoryStream compressed = new MemoryStream())
            {
                using (GZipStream gzip = new GZipStream(compressed, CompressionMode.Compress, true)) gzip.Write(raw, 0, raw.Length);
                return (IsReply ? ReplyPrefix : OfferPrefix) + Security.Base64(compressed.ToArray());
            }
        }
        public static PeerSignal Parse(string value)
        {
            if (value == null || value.Length > 65536) throw new FormatException("The P2P code is missing or too large.");
            value = value.Trim(); bool reply = value.StartsWith(ReplyPrefix, StringComparison.Ordinal);
            string prefix = reply ? ReplyPrefix : OfferPrefix;
            if (!value.StartsWith(prefix, StringComparison.Ordinal)) throw new FormatException("Paste the complete P2P invitation or reply code.");
            string encoded = value.Substring(prefix.Length).Replace('-', '+').Replace('_', '/');
            byte[] compressed = Convert.FromBase64String(encoded.PadRight((encoded.Length + 3) / 4 * 4, '='));
            using (MemoryStream source = new MemoryStream(compressed))
            using (GZipStream gzip = new GZipStream(source, CompressionMode.Decompress))
            using (MemoryStream unpacked = new MemoryStream())
            {
                byte[] buffer = new byte[4096]; int count;
                while ((count = gzip.Read(buffer, 0, buffer.Length)) > 0)
                { if (unpacked.Length + count > 65536) throw new InvalidDataException("The P2P code expands beyond its limit."); unpacked.Write(buffer, 0, count); }
                if (unpacked.Length < 5) throw new InvalidDataException("The P2P code is incomplete.");
                using (Packet packet = new Packet(unpacked.ToArray()))
                {
                    if ((int)packet.Kind != (reply ? 91 : 90) || packet.Reader.ReadInt32() != 1) throw new InvalidDataException("Unsupported P2P code version.");
                    PeerSignal signal = new PeerSignal { IsReply = reply, Id = packet.Text(64) };
                    if (Security.Unbase64(signal.Id).Length != 16 || signal.Id.Length != 22) throw new InvalidDataException("Invalid P2P session identifier.");
                    string security = packet.Text(4096); signal.Sdp = packet.Text(30000); packet.End(); ValidateSdp(signal.Sdp);
                    if (reply) { if (security.Length != 43 || Security.Unbase64(security).Length != 32) throw new InvalidDataException("Invalid reply signature."); signal.Signature = security; }
                    else { signal.Session = Invitation.Parse(security); if (signal.Session.Relay) throw new InvalidDataException("A P2P code cannot contain a relay invitation."); }
                    return signal;
                }
            }
        }
    }
}
