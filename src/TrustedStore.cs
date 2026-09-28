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
        public string HostFile { get { return Path.Combine(DirectoryPath, "host.dat"); } }
        public TrustedStore(string directory, bool machine) { DirectoryPath = Path.GetFullPath(directory); scope = machine ? DataProtectionScope.LocalMachine : DataProtectionScope.CurrentUser; }
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
        void Write(string path, object value)
        {
            Directory.CreateDirectory(DirectoryPath); byte[] plain = Encoding.UTF8.GetBytes(JsonData.Encode(value));
            byte[] protectedBytes; try { protectedBytes = ProtectedData.Protect(plain, Entropy, scope); } finally { Array.Clear(plain, 0, plain.Length); }
            string temporary = path + "." + Guid.NewGuid().ToString("N") + ".tmp";
            try { File.WriteAllBytes(temporary, protectedBytes); if (File.Exists(path)) File.Replace(temporary, path, null); else File.Move(temporary, path); }
            finally { if (File.Exists(temporary)) File.Delete(temporary); }
        }
        public HostPreferences ReadHost() { HostPreferences preferences = Read<HostPreferences>(HostFile); preferences.Validate(); return preferences; }
        public void ChangeHost(Action<HostPreferences> change)
        {
            string lockName; using (SHA256 hash = SHA256.Create()) lockName = "Global\\LumeSettings-" + BitConverter.ToString(hash.ComputeHash(Encoding.UTF8.GetBytes(DirectoryPath.ToUpperInvariant()))).Replace("-", "");
            MutexSecurity security = new MutexSecurity();
            string owner = File.Exists(HostFile) ? ReadHost().OwnerSid : WindowsIdentity.GetCurrent().User.Value;
            security.AddAccessRule(new MutexAccessRule(new SecurityIdentifier(owner), MutexRights.FullControl, AccessControlType.Allow));
            security.AddAccessRule(new MutexAccessRule(new SecurityIdentifier(WellKnownSidType.LocalSystemSid, null), MutexRights.FullControl, AccessControlType.Allow));
            security.AddAccessRule(new MutexAccessRule(new SecurityIdentifier(WellKnownSidType.BuiltinAdministratorsSid, null), MutexRights.FullControl, AccessControlType.Allow));
            bool created;
            using (System.Threading.Mutex mutex = new System.Threading.Mutex(false, lockName, out created, security))
            {
                bool taken = false;
                try { try { taken = mutex.WaitOne(5000); } catch (System.Threading.AbandonedMutexException) { taken = true; } if (!taken) throw new IOException("Computer settings are busy. Try again."); HostPreferences preferences = ReadHost(); change(preferences); preferences.Validate(); Write(HostFile, preferences); }
                finally { if (taken) mutex.ReleaseMutex(); }
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
            PairingCode code = null;
            ChangeHost(delegate(HostPreferences host)
            {
                if (!host.Enabled) throw new InvalidOperationException("Enable permanent access first.");
                host.PairId = Guid.NewGuid().ToString("N"); host.PairKey = Security.Token(32); host.PairExpires = DateTime.UtcNow.AddMinutes(15).Ticks; host.PairWakeOnly = wakeOnly; host.PairMac = wakeOnly ? WakeOnLan.Normalize(mac) : null;
                code = new PairingCode { host = host.HostId, id = host.PairId, key = host.PairKey, expires = host.PairExpires, name = Environment.MachineName, wake = wakeOnly, mac = host.PairMac };
            }); return code;
        }
    }
}
