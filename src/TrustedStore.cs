using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Security.AccessControl;
using System.Security.Cryptography;
using System.Security.Principal;
using System.Text;

namespace LumeRemote
{
    public sealed class TrustedController
    {
        public string Id, Name, Key, AllowedMac;
        public bool WakeOnly;
        public override string ToString() { return Name + (WakeOnly ? " (wake only)" : " (desktop control)"); }
    }
    public sealed class HostPreferences
    {
        public int Version = 1, Display;
        public string HostId = SignalBroker.NewId(), BrokerToken = Security.Token(32), OwnerSid = WindowsIdentity.GetCurrent().User.Value;
        public bool Enabled, KeepAwake = true;
        public string PairId, PairKey, PairMac;
        public bool PairWakeOnly;
        public long PairExpires;
        public List<TrustedController> Controllers = new List<TrustedController>();
        public List<string> NetworkFolders = new List<string>();
        public void Validate()
        {
            if (Version != 1 || HostId == null || !HostId.StartsWith("lume-", StringComparison.Ordinal) || !Invitation.IsHex(HostId.Substring(5), 32) || Security.Unbase64(BrokerToken).Length != 32 || Display < 0 || Display > 32 || Controllers == null || Controllers.Count > 32) throw new InvalidDataException("Invalid permanent-host settings.");
            new SecurityIdentifier(OwnerSid);
            if (NetworkFolders == null || NetworkFolders.Count > 32) throw new InvalidDataException("Invalid network-folder settings.");
            HashSet<string> roots = new HashSet<string>(StringComparer.OrdinalIgnoreCase); foreach (string root in NetworkFolders) if (!roots.Add(RemoteFileAccess.CheckShareRoot(root))) throw new InvalidDataException("Duplicate network folder.");
            HashSet<string> ids = new HashSet<string>();
            foreach (TrustedController controller in Controllers)
            {
                ValidateKey(controller.Id, controller.Key); ValidateName(controller.Name); if (!ids.Add(controller.Id)) throw new InvalidDataException("Duplicate paired computer.");
                if (controller.WakeOnly) WakeOnLan.Normalize(controller.AllowedMac);
            }
            if (!String.IsNullOrEmpty(PairKey)) { ValidateKey(PairId, PairKey); if (PairWakeOnly) WakeOnLan.Normalize(PairMac); }
        }
        public static void ValidateKey(string id, string key) { if (!Invitation.IsHex(id, 32) || key == null || key.Length != 43 || Security.Unbase64(key).Length != 32) throw new InvalidDataException("Invalid computer key."); }
        public static void ValidateName(string name) { if (String.IsNullOrWhiteSpace(name) || name.Length > 80 || HostService.HasControlChars(name)) throw new InvalidDataException("Choose a computer name of 1-80 printable characters."); }
    }
    public sealed class SavedComputer
    {
        public string HostId, Id, Name, Key, WakeMac, WakeHelperId;
        public bool WakeOnly;
        public StreamQuality Quality = StreamQuality.Source;
        public override string ToString() { return Name + (WakeOnly ? " (wake helper)" : ""); }
        public void Validate()
        {
            HostPreferences.ValidateKey(Id, Key); HostPreferences.ValidateName(Name);
            if (HostId == null || !HostId.StartsWith("lume-", StringComparison.Ordinal) || !Invitation.IsHex(HostId.Substring(5), 32)) throw new InvalidDataException("Invalid saved host.");
            if (Quality == null) Quality = StreamQuality.Source; Quality.Validate();
            if (!String.IsNullOrEmpty(WakeMac)) WakeMac = WakeOnLan.Normalize(WakeMac);
            if (!String.IsNullOrEmpty(WakeHelperId) && !Invitation.IsHex(WakeHelperId, 32)) throw new InvalidDataException("Invalid wake helper.");
        }
    }
    public sealed class SavedPreferences { public int Version = 1; public List<SavedComputer> Computers = new List<SavedComputer>(); }
    // A declarative, owner-initiated change to the protected host settings. The
    // non-elevated owner dashboard cannot write host.dat any more (it is
    // SYSTEM-owned, owner read-only); it sends one of these over the SYSTEM
    // worker's control named pipe, which authenticates the caller as the owner,
    // validates and applies it synchronously. The schema deliberately cannot set
    // OwnerSid, HostId or BrokerToken.
    public sealed class HostRequest
    {
        public int Version = 1;
        public string Op;                 // enable | keepawake | revoke | pair | clearpair | folders
        public bool Flag;                 // enable / keepawake value
        public string ControllerId;       // revoke
        public bool WakeOnly;             // pair
        public string Mac, PairId, PairKey; // pair
        public long PairExpires;          // pair
        public List<string> Folders;      // folders
        public void Validate()
        {
            if (Version != 1 || Op == null) throw new InvalidDataException("Invalid settings request.");
            switch (Op)
            {
                case "enable": case "keepawake": case "clearpair": break;
                case "revoke": if (!Invitation.IsHex(ControllerId, 32)) throw new InvalidDataException("Invalid controller id."); break;
                case "pair":
                    HostPreferences.ValidateKey(PairId, PairKey);
                    if (PairExpires < DateTime.UtcNow.Ticks || PairExpires > DateTime.UtcNow.AddHours(1).Ticks) throw new InvalidDataException("Invalid pairing expiry.");
                    if (WakeOnly) Mac = WakeOnLan.Normalize(Mac); else Mac = null;
                    break;
                case "folders":
                    if (Folders == null || Folders.Count > 32) throw new InvalidDataException("Invalid network-folder request.");
                    HashSet<string> roots = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
                    foreach (string root in Folders) if (!roots.Add(RemoteFileAccess.CheckShareRoot(root))) throw new InvalidDataException("Duplicate network folder.");
                    break;
                default: throw new InvalidDataException("Unknown settings request.");
            }
        }
    }
    // Exclusive lock backed by a file inside the ACL-protected Host directory.
    // Only SYSTEM and Administrators can open it, so an unprivileged local user
    // cannot pre-create or hold it to block disable/revocation (unlike a named
    // kernel object in the global namespace).
    sealed class HostLock : IDisposable
    {
        FileStream stream;
        HostLock(FileStream stream) { this.stream = stream; }
        public static HostLock Acquire(string path, int timeoutMs)
        {
            Directory.CreateDirectory(Path.GetDirectoryName(path));
            DateTime deadline = DateTime.UtcNow.AddMilliseconds(timeoutMs);
            for (;;)
            {
                try { return new HostLock(new FileStream(path, FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None)); }
                catch (IOException) { if (DateTime.UtcNow >= deadline) throw new IOException("Computer settings are busy. Try again."); System.Threading.Thread.Sleep(100); }
            }
        }
        public void Dispose() { if (stream != null) { stream.Dispose(); stream = null; } }
    }
    public sealed class PairingCode
    {
        public string host, id, key, name, mac;
        public bool wake;
        public long expires;
        public override string ToString() { return (wake ? "lume-wake://" : "lume-pair://") + Security.Base64(Encoding.UTF8.GetBytes(JsonData.Encode(this))); }
        public static PairingCode Parse(string text)
        {
            if (text == null || text.Length > 4096) throw new FormatException("Paste a complete one-time pairing code."); text = text.Trim();
            bool wake = text.StartsWith("lume-wake://", StringComparison.Ordinal); string prefix = wake ? "lume-wake://" : "lume-pair://";
            if (!text.StartsWith(prefix, StringComparison.Ordinal)) throw new FormatException("Paste a lume-pair:// or lume-wake:// code.");
            PairingCode code = JsonData.Decode<PairingCode>(new UTF8Encoding(false, true).GetString(Security.Unbase64(text.Substring(prefix.Length))));
            if (code == null || code.wake != wake || code.host == null || !code.host.StartsWith("lume-", StringComparison.Ordinal) || !Invitation.IsHex(code.host.Substring(5), 32)) throw new InvalidDataException("Invalid pairing code.");
            HostPreferences.ValidateKey(code.id, code.key); HostPreferences.ValidateName(code.name);
            if (code.expires < DateTime.UtcNow.Ticks || code.expires > DateTime.UtcNow.AddHours(1).Ticks) throw new InvalidDataException("This one-time code expired. Create a new code on the sharing PC.");
            if (code.wake) code.mac = WakeOnLan.Normalize(code.mac); return code;
        }
    }
    public sealed class TrustedStore
    {
        public static string UserDirectory { get { return Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "LumeRemote"); } }
        public static string MachineDirectory { get { return Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData), "LumeRemote", "Host"); } }
        public readonly string DirectoryPath;
        readonly DataProtectionScope scope;
        readonly bool machine;
        public string HostFile { get { return Path.Combine(DirectoryPath, "host.dat"); } }
        public TrustedStore(string directory, bool machine) { DirectoryPath = Path.GetFullPath(directory); this.machine = machine; scope = machine ? DataProtectionScope.LocalMachine : DataProtectionScope.CurrentUser; }
        public static TrustedStore Machine { get { return new TrustedStore(MachineDirectory, true); } }
        public static TrustedStore User { get { return new TrustedStore(UserDirectory, false); } }
        byte[] Entropy { get { return Encoding.UTF8.GetBytes("Lume Remote protected owner settings v1"); } }
        T Read<T>(string path) where T : new()
        {
            byte[] encrypted = null;
            // A replacement can temporarily make File.Exists return false. Do not
            // turn that transient state into a new identity or a disabled host.
            for (int attempt = 0; encrypted == null; attempt++)
            {
                try
                {
                    using (FileStream file = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete))
                    {
                        if (file.Length < 32 || file.Length > 262144) throw new InvalidDataException("Protected settings have an invalid size.");
                        encrypted = new byte[(int)file.Length]; int read = 0;
                        while (read < encrypted.Length) { int count = file.Read(encrypted, read, encrypted.Length - read); if (count == 0) throw new EndOfStreamException(); read += count; }
                    }
                }
                catch (FileNotFoundException) { if (attempt >= 8) return new T(); }
                catch (DirectoryNotFoundException) { if (attempt >= 8) return new T(); }
                catch (IOException) { if (attempt >= 8) throw; }
                if (encrypted == null) System.Threading.Thread.Sleep(25);
            }
            byte[] plain = ProtectedData.Unprotect(encrypted, Entropy, scope);
            try { return JsonData.Decode<T>(new UTF8Encoding(false, true).GetString(plain)); } finally { Array.Clear(plain, 0, plain.Length); }
        }
        // The SYSTEM-only serialization lock and the owner control pipe both live
        // in / are named from the protected Host directory, so only SYSTEM and
        // Administrators can contend for them.
        public string LockFile { get { return Path.Combine(DirectoryPath, "host.lock"); } }
        public string ControlPipeName { get { using (SHA256 hash = SHA256.Create()) return "LumeRemoteHostControl-" + BitConverter.ToString(hash.ComputeHash(Encoding.UTF8.GetBytes(DirectoryPath.ToUpperInvariant()))).Replace("-", ""); } }
        // Refuse to write through a reparse point. The owner can no longer create
        // objects in the protected directory, but this stays as defence in depth.
        public static void CheckNoReparse(string path)
        {
            string item = path;
            while (!String.IsNullOrEmpty(item))
            {
                if ((File.Exists(item) || Directory.Exists(item)) && (File.GetAttributes(item) & FileAttributes.ReparsePoint) != 0) throw new UnauthorizedAccessException("Refusing to use a reparse point: " + item);
                item = Path.GetDirectoryName(item);
            }
        }
        void Write(string path, object value)
        {
            Directory.CreateDirectory(DirectoryPath); CheckNoReparse(path); byte[] plain = Encoding.UTF8.GetBytes(JsonData.Encode(value));
            byte[] protectedBytes; try { protectedBytes = ProtectedData.Protect(plain, Entropy, scope); } finally { Array.Clear(plain, 0, plain.Length); }
            string temporary = path + "." + Guid.NewGuid().ToString("N") + ".tmp";
            try { File.WriteAllBytes(temporary, protectedBytes); if (File.Exists(path)) File.Replace(temporary, path, null); else File.Move(temporary, path); }
            finally { if (File.Exists(temporary)) File.Delete(temporary); }
        }
        public HostPreferences ReadHost() { HostPreferences preferences = Read<HostPreferences>(HostFile); preferences.Validate(); return preferences; }
        static bool CanWriteHost()
        {
            using (WindowsIdentity identity = WindowsIdentity.GetCurrent()) return identity.IsSystem || new WindowsPrincipal(identity).IsInRole(WindowsBuiltInRole.Administrator);
        }
        // Privileged direct write of host.dat (SYSTEM worker and the elevated
        // installer only), serialized by the protected lock file.
        public void ApplyDirect(Action<HostPreferences> change) { ApplyDirect(change, 10000); }
        public void ApplyDirect(Action<HostPreferences> change, int lockTimeoutMs)
        {
            using (HostLock.Acquire(LockFile, lockTimeoutMs)) { HostPreferences preferences = ReadHost(); change(preferences); preferences.Validate(); Write(HostFile, preferences); }
        }
        // Translate a validated owner request into a host.dat mutation. OwnerSid,
        // HostId and BrokerToken are never assigned here.
        static Action<HostPreferences> Mutation(HostRequest request)
        {
            return delegate(HostPreferences host)
            {
                switch (request.Op)
                {
                    case "enable": host.Enabled = request.Flag; if (!host.Enabled) { host.PairId = host.PairKey = host.PairMac = null; host.PairExpires = 0; } break;
                    case "keepawake": host.KeepAwake = request.Flag; break;
                    case "clearpair": host.PairId = host.PairKey = host.PairMac = null; host.PairExpires = 0; break;
                    case "revoke": host.Controllers.RemoveAll(delegate(TrustedController c) { return c.Id == request.ControllerId; }); break;
                    case "pair":
                        if (!host.Enabled) throw new InvalidOperationException("Enable permanent access first.");
                        host.PairId = request.PairId; host.PairKey = request.PairKey; host.PairExpires = request.PairExpires; host.PairWakeOnly = request.WakeOnly; host.PairMac = request.WakeOnly ? request.Mac : null; break;
                    case "folders": host.NetworkFolders = request.Folders; break;
                }
            };
        }
        // Backward-compatible privileged direct write. Callers without write access
        // to the protected host.dat (a standard-user owner) cannot use it; they go
        // through Change/Disable and the control pipe instead.
        public void ChangeHost(Action<HostPreferences> change) { ApplyDirect(change); }
        // SYSTEM side: validate and apply an owner request.
        public void ApplyRequest(HostRequest request) { request.Validate(); ApplyDirect(Mutation(request)); }
        // Owner-facing change. Privileged callers apply directly; the non-elevated
        // owner dashboard sends the request to the SYSTEM worker's control pipe and
        // gets a synchronous success/error result.
        public void Change(HostRequest request)
        {
            request.Validate();
            // The per-user store is owned by the user; only the machine store is
            // SYSTEM-owned and therefore requires the control pipe for a
            // non-privileged owner.
            if (!machine || CanWriteHost()) { ApplyRequest(request); return; }
            SendControl(request);
        }
        void SendControl(HostRequest request)
        {
            using (System.IO.Pipes.NamedPipeClientStream client = new System.IO.Pipes.NamedPipeClientStream(".", ControlPipeName, System.IO.Pipes.PipeDirection.InOut, System.IO.Pipes.PipeOptions.None, TokenImpersonationLevel.Impersonation))
            {
                try { client.Connect(4000); }
                catch (TimeoutException) { throw new IOException("The Lume host service is not running on this PC, so the change was not applied. Open Lume there, or use Disable/Remove on that PC."); }
                // Any local user can create a pipe with this name while the worker is not
                // listening. Requests can carry a pairing secret, so only talk to a pipe
                // owned by SYSTEM or Administrators, which a standard user cannot fake.
                SecurityIdentifier pipeOwner = client.GetAccessControl().GetOwner(typeof(SecurityIdentifier)) as SecurityIdentifier;
                if (pipeOwner == null || !(pipeOwner.IsWellKnown(WellKnownSidType.LocalSystemSid) || pipeOwner.IsWellKnown(WellKnownSidType.BuiltinAdministratorsSid)))
                    throw new UnauthorizedAccessException("The settings channel is not owned by the Lume host service. The change was not sent.");
                WriteFrame(client, Encoding.UTF8.GetBytes(JsonData.Encode(request)));
                byte[] replyBytes = ReadFrame(client, 8192); string[] reply = new UTF8Encoding(false, true).GetString(replyBytes).Split(new char[] { '\n' }, 2);
                if (reply.Length == 0 || reply[0] != "ok") throw new InvalidOperationException(reply.Length > 1 && reply[1].Length > 0 ? reply[1] : "The host service rejected the change.");
            }
        }
        // Length-prefixed (4-byte little-endian) framing for the control pipe.
        internal static void WriteFrame(Stream stream, byte[] payload)
        {
            byte[] header = BitConverter.GetBytes(payload.Length); if (!BitConverter.IsLittleEndian) Array.Reverse(header);
            stream.Write(header, 0, 4); stream.Write(payload, 0, payload.Length); stream.Flush();
        }
        internal static byte[] ReadFrame(Stream stream, int limit)
        {
            byte[] header = ReadExact(stream, 4); int length = BitConverter.ToInt32(BitConverter.IsLittleEndian ? header : Reversed(header), 0);
            if (length < 0 || length > limit) throw new InvalidDataException("The control message exceeds its size limit.");
            return ReadExact(stream, length);
        }
        static byte[] Reversed(byte[] value) { byte[] copy = (byte[])value.Clone(); Array.Reverse(copy); return copy; }
        static byte[] ReadExact(Stream stream, int count)
        {
            byte[] buffer = new byte[count]; int read = 0;
            while (read < count) { int got = stream.Read(buffer, read, count - read); if (got == 0) throw new EndOfStreamException(); read += got; }
            return buffer;
        }
        // Parse a request received on the control pipe (already caller-authenticated).
        public static HostRequest ReadRequestJson(byte[] payload)
        {
            HostRequest request = JsonData.Decode<HostRequest>(new UTF8Encoding(false, true).GetString(payload)); if (request == null) throw new InvalidDataException("Empty settings request."); request.Validate(); return request;
        }
        // Robust, privilege-aware disable. Owner sends it over the pipe; a
        // privileged caller (the elevated --disable-host uninstall path) writes
        // directly and, if the lock is momentarily unavailable, still forces the
        // host off so revocation is never blocked.
        public void Disable()
        {
            HostRequest request = new HostRequest { Op = "enable", Flag = false };
            if (machine && !CanWriteHost()) { SendControl(request); return; }
            try { request.Validate(); ApplyDirect(Mutation(request), 3000); }
            catch (IOException)
            {
                HostPreferences host = ReadHost(); host.Enabled = false; host.PairId = host.PairKey = host.PairMac = null; host.PairExpires = 0; host.Validate(); Write(HostFile, host);
            }
        }
        public SavedPreferences ReadSaved()
        {
            SavedPreferences preferences = Read<SavedPreferences>(Path.Combine(DirectoryPath, "computers.dat"));
            if (preferences == null || preferences.Version != 1 || preferences.Computers == null || preferences.Computers.Count > 64) throw new InvalidDataException("Invalid saved-computer list.");
            foreach (SavedComputer computer in preferences.Computers) computer.Validate(); return preferences;
        }
        public void SaveComputers(SavedPreferences preferences) { foreach (SavedComputer computer in preferences.Computers) computer.Validate(); if (preferences.Computers.Count > 64) throw new InvalidDataException("Too many saved computers."); Write(Path.Combine(DirectoryPath, "computers.dat"), preferences); }
        public PairingCode CreatePairing(bool wakeOnly, string mac)
        {
            HostPreferences host = ReadHost();
            if (!host.Enabled) throw new InvalidOperationException("Enable permanent access first.");
            // The owner generates the one-time pairing secret locally (it can read
            // HostId and has LocalMachine DPAPI) and asks SYSTEM to persist it, so
            // the displayed code always equals the stored code.
            string pairId = Guid.NewGuid().ToString("N"), pairKey = Security.Token(32), normalizedMac = wakeOnly ? WakeOnLan.Normalize(mac) : null;
            long expires = DateTime.UtcNow.AddMinutes(15).Ticks;
            Change(new HostRequest { Op = "pair", WakeOnly = wakeOnly, Mac = normalizedMac, PairId = pairId, PairKey = pairKey, PairExpires = expires });
            return new PairingCode { host = host.HostId, id = pairId, key = pairKey, expires = expires, name = Environment.MachineName, wake = wakeOnly, mac = normalizedMac };
        }
    }
}
