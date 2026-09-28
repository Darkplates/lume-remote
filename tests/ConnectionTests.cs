using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Net;
using System.Security.Authentication;
using System.Threading;
using System.Threading.Tasks;
using System.Windows.Forms;
using LumeRemote;

static partial class Tests
{
    static void ConnectionChecks()
    {
        Run("Heartbeat runs without a UI pump and stops after disposal", HeartbeatLifetime);
        Run("Reconnect backs off, cancels and refuses authentication/protocol failures", ReconnectPolicy);
        Run("Connection logs retain errors without credentials or exception text", ConnectionLog);
        Run("Paired viewer recovers a lost P2P session in the same window", ViewerReconnect);
    }
    static void HeartbeatLifetime()
    {
        int pulses = 0, failures = 0;
        SessionHeartbeat heartbeat = new SessionHeartbeat(delegate { Interlocked.Increment(ref pulses); }, delegate { Interlocked.Increment(ref failures); }, 25);
        Thread.Sleep(180); Check(pulses >= 2, "Heartbeat needed the Windows message loop.");
        heartbeat.Dispose(); Thread.Sleep(50); int stopped = pulses; Thread.Sleep(100);
        Check(stopped == pulses && failures == 0, "Heartbeat survived disposal.");
        using (SessionHeartbeat broken = new SessionHeartbeat(delegate { throw new IOException("Synthetic failure"); }, delegate { Interlocked.Increment(ref failures); }, 10))
        { Thread.Sleep(100); Check(failures == 1, "Heartbeat failure was hidden or repeated."); }
    }
    static void ReconnectPolicy()
    {
        int attempts = 0; List<int> delays = new List<int>();
        using (CancellationTokenSource cancelled = new CancellationTokenSource())
        {
            Task<PairedLink> pending = PairedReconnect.Connect(delegate(Action<string> status, CancellationToken token)
            { attempts++; if (attempts == 8) return Task.FromResult(new PairedLink()); throw new IOException("Temporary outage"); }, delegate { }, cancelled.Token,
            delegate(int milliseconds, CancellationToken token) { delays.Add(milliseconds); return Task.FromResult(true); });
            Check(pending.Result != null && attempts == 8 && String.Join(",", delays) == "1000,2000,5000,10000,20000,30000,30000,30000", "Backoff or recovery failed.");
            cancelled.Cancel(); attempts = 0;
            Reject(delegate { PairedReconnect.Connect(delegate(Action<string> report, CancellationToken token) { attempts++; return Task.FromResult(new PairedLink()); }, delegate { }, cancelled.Token).GetAwaiter().GetResult(); });
            Check(attempts == 0, "Cancelled reconnect still negotiated.");
        }
        foreach (Exception rejected in new Exception[] { new AuthenticationException(), new InvalidDataException(), new OperationCanceledException() })
        {
            attempts = 0;
            Reject(delegate { PairedReconnect.Connect(delegate(Action<string> report, CancellationToken token) { attempts++; throw rejected; }, delegate { }, CancellationToken.None,
                delegate { return Task.FromResult(true); }).GetAwaiter().GetResult(); });
            Check(attempts == 1, "Security or cancellation error was retried.");
        }
    }
    static void ConnectionLog()
    {
        string folder = Path.Combine(Path.GetTempPath(), "lume-log-test-" + Guid.NewGuid().ToString("N"));
        try
        {
            string secret = Security.Token(32); SessionLog.Write(folder, "viewer", "transport_failed", new IOException("Private host " + secret));
            string text = File.ReadAllText(Path.Combine(folder, "connections.log"));
            Check(text.Contains("transport_failed IOException") && !text.Contains(secret) && !text.Contains("Private host"), "Exception text leaked into diagnostics.");
            File.WriteAllText(Path.Combine(folder, "connections.log"), new string('x', 256 * 1024));
            SessionLog.Write(folder, "host", "remote_closed");
            Check(new FileInfo(Path.Combine(folder, "connections.log")).Length < 1024 && File.Exists(Path.Combine(folder, "connections.log.previous")), "Log retention is unbounded.");
        }
        finally { if (Directory.Exists(folder)) Directory.Delete(folder, true); }
    }
    sealed class ReconnectFixture : IDisposable
    {
        public readonly List<HostService> Hosts = new List<HostService>();
        public readonly List<PeerTransport> Peers = new List<PeerTransport>();
        public PairedLink Open()
        {
            HostService host = new HostService(delegate { return new Synthetic(false); }, Profile.All[1], false, delegate { return true; }, delegate { }, delegate { });
            Hosts.Add(host); host.StartPeer(); PeerTransport first = new PeerTransport(false), second = new PeerTransport(false);
            Peers.Add(first); Peers.Add(second); PairPeers(first, second); host.AcceptPeer(first);
            return new PairedLink { Invitation = host.Invite, Peer = second };
        }
        public void Dispose() { foreach (HostService host in Hosts) host.Dispose(); foreach (PeerTransport peer in Peers) peer.Dispose(); }
    }
    static void ViewerReconnect()
    {
        using (ReconnectFixture fixture = new ReconnectFixture())
        {
            PairedLink first = fixture.Open(); int attempts = 0;
            using (ViewerForm viewer = new ViewerForm(first.Invitation, first.Peer, StreamQuality.Source,
                delegate(Action<string> report, CancellationToken token) { attempts++; token.ThrowIfCancellationRequested(); if (attempts == 1) throw new IOException("Synthetic brief outage"); return Task.FromResult(fixture.Open()); }))
            {
                viewer.WindowState = FormWindowState.Minimized; viewer.Show();
                PumpUntil(delegate { return (int)Field(viewer, "frameCount") > 0; }, 10000, "Initial viewer did not receive a frame.");
                ViewerConnection old = (ViewerConnection)Field(viewer, "connection"); fixture.Hosts[0].Dispose();
                PumpUntil(delegate { return attempts >= 2 && Field(viewer, "connection") != null && Field(viewer, "connection") != old && (bool)Field(viewer, "connected") && (int)Field(viewer, "frameCount") > 0; }, 15000, "The viewer did not automatically recover.");
                Check(fixture.Hosts[0].Invite.Fingerprint != fixture.Hosts[1].Invite.Fingerprint && !Security.Equal(fixture.Hosts[0].Invite.Secret, fixture.Hosts[1].Invite.Secret), "Reconnect reused session credentials.");
                Check(!((RemoteCanvas)Field(viewer, "canvas")).SessionEnded, "Recovered session retained the disconnected overlay.");
                fixture.Hosts[1].Dispose(); viewer.Close(); int before = attempts; Thread.Sleep(1500); Application.DoEvents();
                Check(attempts == before, "Closing the viewer restarted a connection.");
            }
        }
    }
    static void IdleSoak(int seconds)
    {
        if (seconds < 35 || seconds > 7200) throw new ArgumentOutOfRangeException("seconds");
        using (ReconnectFixture fixture = new ReconnectFixture())
        {
            PairedLink link = fixture.Open();
            using (ViewerForm viewer = new ViewerForm(link.Invitation, link.Peer))
            {
                viewer.WindowState = FormWindowState.Minimized; viewer.Show();
                PumpUntil(delegate { return (int)Field(viewer, "frameCount") > 0; }, 10000, "The idle fixture never connected.");
                ViewerConnection connection = (ViewerConnection)Field(viewer, "connection"); long before = connection.Sent;
                Stopwatch watch = Stopwatch.StartNew(); Console.WriteLine("SOAK started: static synthetic desktop, local native P2P + pinned TLS, minimized viewer, NO UI message pump.");
                while (watch.Elapsed.TotalSeconds < seconds)
                {
                    Thread.Sleep(Math.Min(30000, Math.Max(1, (int)(seconds * 1000 - watch.ElapsedMilliseconds))));
                    Check(fixture.Hosts[0].HasSession && connection.Failure == null, "Session dropped while idle with a blocked UI.");
                    Console.WriteLine("SOAK alive at " + watch.ElapsedMilliseconds / 1000 + " s; heartbeat bytes sent: " + (connection.Sent - before));
                }
                Check(connection.Sent - before >= (seconds / 4) * 13, "No independent heartbeat traffic.");
                Application.DoEvents();
                Check((bool)Field(viewer, "connected") && !((RemoteCanvas)Field(viewer, "canvas")).SessionEnded, "The connection ended while the UI was stalled.");
                Check((int)Field(viewer, "frameCount") <= 3, "Static desktop was needlessly sent repeatedly.");
                viewer.Close(); Console.WriteLine("SOAK completed: " + watch.Elapsed.TotalSeconds.ToString("0.0", System.Globalization.CultureInfo.InvariantCulture) + " seconds. This is a local test, not a two-network WAN test.");
            }
        }
    }
}
