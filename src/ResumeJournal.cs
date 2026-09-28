using System;
using System.IO;
using System.Security.Cryptography;
using System.Text;

namespace LumeRemote
{
    // Metadata is authenticated with the paired-computer key; that key is never stored here.
    // Prefix bytes are rehashed and compared with the sender before any suffix is accepted.
    internal static class ResumeJournal
    {
        public const int HeaderLength = 80;
        public static byte[] Header(string key, string folder, string name, long length, byte[] digest)
        {
            byte[] secret = Security.Unbase64(key);
            if (secret.Length != 32 || digest == null || digest.Length != 32 || length < 0) throw new InvalidDataException("Invalid resume identity.");
            byte[] fields;
            using (MemoryStream data = new MemoryStream()) using (BinaryWriter writer = new BinaryWriter(data))
            { writer.Write(Encoding.ASCII.GetBytes("LUMER01\0")); writer.Write(length); writer.Write(digest); fields = data.ToArray(); }
            byte[] identity = Encoding.UTF8.GetBytes("Lume paired transfer resume v1\0" + folder.TrimEnd('\\').ToUpperInvariant() + "\\" + name.ToUpperInvariant());
            byte[] tag;
            try { using (HMACSHA256 hmac = new HMACSHA256(secret)) { hmac.TransformBlock(identity, 0, identity.Length, null, 0); hmac.TransformFinalBlock(fields, 0, fields.Length); tag = hmac.Hash; } }
            finally { Array.Clear(secret, 0, secret.Length); }
            byte[] header = new byte[HeaderLength]; Buffer.BlockCopy(fields, 0, header, 0, fields.Length); Buffer.BlockCopy(tag, 0, header, fields.Length, tag.Length); return header;
        }
        public static string Name(byte[] header) { return ".lume-resume-" + BitConverter.ToString(header, 48, 32).Replace("-", "").ToLowerInvariant() + ".part"; }
        public static bool Equal(byte[] first, byte[] second) { return first != null && second != null && Security.Equal(Convert.ToBase64String(first), Convert.ToBase64String(second)); }
        public static bool InternalName(string name) { return (name.Length == 82 && name.EndsWith(".part", StringComparison.Ordinal) || name.Length == 83 && name.EndsWith(".state", StringComparison.Ordinal)) && name.StartsWith(".lume-resume-", StringComparison.Ordinal) && Invitation.IsHex(name.Substring(13, 64), 64); }
    }
}
