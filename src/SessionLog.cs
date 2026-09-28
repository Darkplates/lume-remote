using System;
using System.IO;
using System.Security.Authentication;
using System.Text;

namespace LumeRemote
{
    public static class SessionLog
    {
        const long Limit = 256 * 1024;
        static readonly object gate = new object();
        public static string UserDirectory { get { return Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "LumeRemote", "Diagnostics"); } }
        public static string Reason(Exception error)
        {
            if (error == null) return "remote_ended";
            if (error is AuthenticationException) return "authentication_failed";
            if (error is InvalidDataException) return "invalid_protocol";
            if (error is OperationCanceledException) return "cancelled";
            if (error is TimeoutException || error.Message.IndexOf("timed out", StringComparison.OrdinalIgnoreCase) >= 0) return "transport_timeout";
            if (error is EndOfStreamException) return "remote_closed";
            if (error is IOException) return "transport_failed";
            return "session_failed";
        }
        // Only fixed event codes, error types and HRESULTs are persisted. No peer names,
        // network addresses, invitation codes, credentials, SDP or exception messages.
        public static void Write(string directory, string side, string eventCode, Exception error = null)
        {
            if (String.IsNullOrEmpty(directory)) return;
            if (!Code(side) || !Code(eventCode)) throw new ArgumentException("Invalid diagnostic event code.");
            try
            {
                lock (gate)
                {
                    Directory.CreateDirectory(directory);
                    string path = Path.Combine(directory, "connections.log"), previous = path + ".previous";
                    if (File.Exists(path) && new FileInfo(path).Length >= Limit)
                    { File.Copy(path, previous, true); File.WriteAllText(path, "", new UTF8Encoding(false)); }
                    string type = error == null ? "none" : error.GetType().Name;
                    if (!Code(type)) type = "Exception";
                    File.AppendAllText(path, DateTime.UtcNow.ToString("o") + " " + side + " " + eventCode + " " + type + " " + (error == null ? "0" : error.HResult.ToString("X8")) + "\r\n", new UTF8Encoding(false));
                }
            }
            catch (IOException) { } catch (UnauthorizedAccessException) { } catch (System.Security.SecurityException) { }
        }
        static bool Code(string value)
        {
            if (String.IsNullOrEmpty(value) || value.Length > 64) return false;
            foreach (char c in value) if (!(c >= 'A' && c <= 'Z') && !(c >= 'a' && c <= 'z') && c != '_') return false;
            return true;
        }
    }
}
