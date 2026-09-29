using System;
using System.Collections.Generic;
using System.IO;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using LumeRemote;

// Regression tests for the permanent-access hardening: owner requests can never
// change ownership, the settings lock is a protected file (not a squattable
// global object), disable survives a held lock, and the control-pipe wire format
// is bounded. These use a per-user store so they run without administrator rights.
static partial class Tests
{
    static void HardeningChecks()
    {
        Run("Owner requests never change ownership or identity", RequestPolicy);
        Run("Settings lock is a protected file, not a global object", SettingsFileLockSerializes);
        Run("Disable survives a held settings lock", DisableSurvivesHeldLock);
        Run("Owner control-pipe framing is bounded and validated", ControlWireFraming);
        Run("Reparse-point guard passes normal paths", ReparseGuardAllowsNormalPaths);
        Run("Permanent-host signaling rate limit is per source and bounded", SignalingRatePerSource);
    }
    static void SignalingRatePerSource()
    {
        using (PersistentHost host = new PersistentHost(null, delegate { return new Synthetic(); }, delegate { }))
        {
            string flooder = "lume-" + new string('a', 32), paired = "lume-" + new string('b', 32);
            int allowed = 0; for (int i = 0; i < PersistentHost.MessagesPerSecond * 3; i++) if (host.AllowMessage(flooder)) allowed++;
            Check(allowed <= PersistentHost.MessagesPerSecond, "A single source exceeded its signaling rate.");
            Check(host.AllowMessage(paired), "A flooding source starved another sender.");
            for (int i = 0; i < PersistentHost.MessageRateSources * 2; i++) host.AllowMessage("lume-" + i.ToString("x32"));
            Check(host.MessageRateCount <= PersistentHost.MessageRateSources, "The signaling rate table is unbounded.");
        }
    }
    static string HardeningDir() { return Path.Combine(Path.GetTempPath(), "Lume-hardening-" + Guid.NewGuid().ToString("N")); }
    static void CleanupDir(string directory) { try { if (Directory.Exists(directory)) Directory.Delete(directory, true); } catch { } }

    static void RequestPolicy()
    {
        string directory = HardeningDir();
        try
        {
            TrustedStore store = new TrustedStore(directory, false);
            store.ChangeHost(delegate(HostPreferences host) { host.Enabled = true; });
            string owner = store.ReadHost().OwnerSid, hostId = store.ReadHost().HostId, brokerToken = store.ReadHost().BrokerToken;

            // Applying every legitimate owner request must never move ownership,
            // the host identity or the broker token.
            store.ApplyRequest(new HostRequest { Op = "keepawake", Flag = false });
            store.ApplyRequest(new HostRequest { Op = "folders", Folders = new List<string>() });
            store.ApplyRequest(new HostRequest { Op = "enable", Flag = false });
            HostPreferences after = store.ReadHost();
            Check(after.OwnerSid == owner && after.HostId == hostId && after.BrokerToken == brokerToken, "A settings request changed protected identity.");
            Check(!after.Enabled && !after.KeepAwake, "A settings request was not applied.");

            // Unknown and malformed operations are refused before any write.
            Reject(delegate { new HostRequest { Op = "ownersid" }.Validate(); });
            Reject(delegate { new HostRequest { Op = "revoke", ControllerId = "not-hex" }.Validate(); });
            Reject(delegate { new HostRequest { Op = "pair", PairId = "x", PairKey = "y" }.Validate(); });
            Reject(delegate { new HostRequest { Op = null }.Validate(); });
        }
        finally { CleanupDir(directory); }
    }

    static void SettingsFileLockSerializes()
    {
        string directory = HardeningDir();
        try
        {
            TrustedStore store = new TrustedStore(directory, false);
            store.ChangeHost(delegate(HostPreferences host) { host.Enabled = true; });
            // Hold the protected lock file briefly from another thread; a concurrent
            // change must wait for it, proving mutual exclusion is via this file.
            ManualResetEvent held = new ManualResetEvent(false);
            Task holder = Task.Run(delegate
            {
                using (FileStream guard = new FileStream(store.LockFile, FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None))
                { held.Set(); Thread.Sleep(700); }
            });
            Check(held.WaitOne(5000), "Could not acquire the lock file for the test.");
            DateTime start = DateTime.UtcNow;
            store.ChangeHost(delegate(HostPreferences host) { host.KeepAwake = false; });
            Check((DateTime.UtcNow - start).TotalMilliseconds >= 300, "The change did not wait for the held lock.");
            holder.Wait(5000);
            Check(!store.ReadHost().KeepAwake, "The serialized change was lost.");
        }
        finally { CleanupDir(directory); }
    }

    static void DisableSurvivesHeldLock()
    {
        string directory = HardeningDir();
        try
        {
            TrustedStore store = new TrustedStore(directory, false);
            store.ChangeHost(delegate(HostPreferences host) { host.Enabled = true; });
            // With the lock held for the whole attempt, Disable must still force the
            // host off (F3: immediate disable/revocation cannot be blocked).
            using (FileStream guard = new FileStream(store.LockFile, FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None))
            {
                store.Disable();
            }
            Check(!store.ReadHost().Enabled, "Disable did not take effect while the lock was held.");
        }
        finally { CleanupDir(directory); }
    }

    static void ControlWireFraming()
    {
        // Round-trip the length-prefixed frame used by the owner control pipe.
        byte[] payload = Encoding.UTF8.GetBytes("the quick brown fox");
        using (MemoryStream stream = new MemoryStream())
        {
            TrustedStore.WriteFrame(stream, payload);
            stream.Position = 0;
            byte[] read = TrustedStore.ReadFrame(stream, 65536);
            Check(Encoding.UTF8.GetString(read) == "the quick brown fox", "Control frame round-trip failed.");
        }
        // An oversized declared length is refused rather than allocated.
        using (MemoryStream stream = new MemoryStream())
        {
            TrustedStore.WriteFrame(stream, payload);
            stream.Position = 0;
            Reject(delegate { TrustedStore.ReadFrame(stream, 4); });
        }
        // A request decoded from the pipe is validated; garbage is refused.
        HostRequest request = new HostRequest { Op = "revoke", ControllerId = new string('a', 32) };
        HostRequest decoded = TrustedStore.ReadRequestJson(Encoding.UTF8.GetBytes(JsonData.Encode(request)));
        Check(decoded.Op == "revoke" && decoded.ControllerId == request.ControllerId, "Request JSON round-trip failed.");
        Reject(delegate { TrustedStore.ReadRequestJson(Encoding.UTF8.GetBytes("{\"Op\":\"nope\"}")); });
    }

    static void ReparseGuardAllowsNormalPaths()
    {
        string directory = HardeningDir();
        try
        {
            Directory.CreateDirectory(directory);
            string file = Path.Combine(directory, "host.dat");
            File.WriteAllText(file, "x");
            TrustedStore.CheckNoReparse(file); // a normal file and its parents must pass
        }
        finally { CleanupDir(directory); }
    }
}
