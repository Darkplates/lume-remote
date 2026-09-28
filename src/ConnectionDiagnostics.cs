using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Drawing;
using System.Net;
using System.Net.NetworkInformation;
using System.Net.Security;
using System.Net.Sockets;
using System.Security.Authentication;
using System.Security.Cryptography.X509Certificates;
using System.Text;
using System.Threading.Tasks;
using System.Windows.Forms;
using Microsoft.Win32;

namespace LumeRemote
{
    public enum ConnectionStage { Contacting, PairingRelay, Securing, RequestingApproval, Approved }

    public static class ConnectionDiagnostics
    {
        public static string Endpoint(Invitation invite) { return invite.Host + ":" + invite.Port; }
        public static string Caption(ConnectionStage stage, Invitation invite)
        {
            switch (stage)
            {
                case ConnectionStage.PairingRelay: return "Relay reached. Looking for the sharing computer...";
                case ConnectionStage.Securing: return "Network connection established. Verifying the computer's identity...";
                case ConnectionStage.RequestingApproval: return "Secure connection established. Requesting host approval...";
                case ConnectionStage.Approved: return "The host approved. Waiting for the first desktop image...";
                default: return "Contacting " + Endpoint(invite) + "...";
            }
        }
        public static string Failure(ConnectionStage stage, Invitation invite, Exception error)
        {
            string endpoint = Endpoint(invite);
            if (stage == ConnectionStage.Contacting)
            {
                SocketException socket = error as SocketException;
                string reason = socket != null && socket.SocketErrorCode == SocketError.ConnectionRefused
                    ? "The connection to " + endpoint + " was refused."
                    : error is TimeoutException ? "No TCP response from " + endpoint + " within the connection timeout."
                    : "Could not open a network connection to " + endpoint + ". " + error.Message;
                return reason + "\nHost approval has not been requested.\n" + (invite.Relay
                    ? "Check that the relay is running and reachable on this address and port."
                    : "Keep Start sharing active on the other PC and copy a fresh invitation. Both PCs need a reachable LAN/VPN address. Check the host firewall and guest Wi-Fi isolation. Separate networks need a VPN or relay.");
            }
            if (stage == ConnectionStage.PairingRelay) return "The relay was reached, but the host could not be paired. " + error.Message;
            if (stage == ConnectionStage.Securing) return "The network was reached, but the secure connection failed. Copy a fresh invitation from the sharing PC. " + error.Message;
            if (stage == ConnectionStage.RequestingApproval)
                return "The secure connection was established. Host approval did not complete: " + error.Message;
            return error.Message;
        }
        public static bool SameSubnet(IPAddress first, IPAddress second, IPAddress mask)
        {
            byte[] a = first.GetAddressBytes(), b = second.GetAddressBytes(), m = mask.GetAddressBytes();
            if (a.Length != 4 || b.Length != 4 || m.Length != 4) return false;
            for (int i = 0; i < 4; i++) if ((a[i] & m[i]) != (b[i] & m[i])) return false;
            return true;
        }
        public static bool PrivateAddress(IPAddress address)
        {
            byte[] a = address.GetAddressBytes();
            return a.Length == 4 && (a[0] == 10 || (a[0] == 172 && a[1] >= 16 && a[1] <= 31) || (a[0] == 192 && a[1] == 168));
        }
        public static string LocalReport(Invitation invite, bool host)
        {
            StringBuilder report = new StringBuilder("LUME REMOTE 0.2.0 - CONNECTION DIAGNOSTICS\r\n");
            report.AppendLine("Role: " + (host ? "sharing computer" : "controlling computer"));
            report.AppendLine("Application: " + Application.ExecutablePath);
            report.AppendLine("Invitations, session keys and certificate pins are excluded from this report.");
            IPAddress target = null; bool sameSubnet = false, isThisPc = false;
            if (invite != null)
            {
                report.AppendLine("Route: " + (invite.Relay ? "relay" : "direct LAN/VPN"));
                report.AppendLine("Target: " + Endpoint(invite));
                IPAddress.TryParse(invite.Host, out target);
            }
            else report.AppendLine("Sharing is not active in this app. Start sharing to create a listening endpoint.");
            report.AppendLine(); report.AppendLine("LOCAL NETWORK ADDRESSES");
            try
            {
                foreach (NetworkInterface adapter in NetworkInterface.GetAllNetworkInterfaces())
                {
                    if (adapter.OperationalStatus != OperationalStatus.Up) continue;
                    IPInterfaceProperties properties = adapter.GetIPProperties();
                    foreach (UnicastIPAddressInformation address in properties.UnicastAddresses)
                    {
                        if (address.Address.AddressFamily != AddressFamily.InterNetwork) continue;
                        report.AppendLine("  " + adapter.Name + ": " + address.Address + " / mask " + address.IPv4Mask);
                        if (target != null)
                        {
                            isThisPc |= target.Equals(address.Address);
                            sameSubnet |= SameSubnet(address.Address, target, address.IPv4Mask);
                        }
                    }
                    foreach (GatewayIPAddressInformation gateway in properties.GatewayAddresses)
                        if (gateway.Address.AddressFamily == AddressFamily.InterNetwork) report.AppendLine("  Gateway: " + gateway.Address);
                }
            }
            catch (Exception error) { report.AppendLine("  Network inventory unavailable: " + error.Message); }
            if (invite != null && !invite.Relay && target != null)
            {
                if (!host && (isThisPc || IPAddress.IsLoopback(target))) report.AppendLine("WARNING: this invitation points to this PC. Use the invitation created on the computer you want to control.");
                if (PrivateAddress(target) && !sameSubnet) report.AppendLine("No matching local subnet was found. A routed VPN can still work; otherwise separate networks need a VPN or relay.");
                if (PrivateAddress(target)) report.AppendLine("A private LAN address cannot be reached directly across the public Internet.");
            }
            report.AppendLine(); report.AppendLine("RECORDED WINDOWS FIREWALL RULES FOR THIS APP");
            report.AppendLine("These entries do not establish the effective policy or third-party firewall behavior.");
            try
            {
                int count = 0;
                using (RegistryKey key = Registry.LocalMachine.OpenSubKey(@"SYSTEM\CurrentControlSet\Services\SharedAccess\Parameters\FirewallPolicy\FirewallRules"))
                {
                    if (key == null) report.AppendLine("  Firewall rule inventory unavailable.");
                    else foreach (string ruleName in key.GetValueNames())
                    {
                        string value = key.GetValue(ruleName) as string; if (value == null) continue;
                        Dictionary<string, string> fields = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
                        foreach (string part in value.Split('|')) { int separator = part.IndexOf('='); if (separator > 0) fields[part.Substring(0, separator)] = part.Substring(separator + 1); }
                        string app, active, direction, action, profiles, protocol;
                        if (!fields.TryGetValue("App", out app) || !String.Equals(Environment.ExpandEnvironmentVariables(app), Application.ExecutablePath, StringComparison.OrdinalIgnoreCase)) continue;
                        fields.TryGetValue("Active", out active); fields.TryGetValue("Dir", out direction); fields.TryGetValue("Action", out action); fields.TryGetValue("Profile", out profiles); fields.TryGetValue("Protocol", out protocol);
                        report.AppendLine("  " + action + " / " + direction + " / enabled=" + active + " / profiles=" + (profiles ?? "all") + " / protocol=" + (protocol ?? "any")); count++;
                    }
                }
                if (count == 0) report.AppendLine("  No app-specific entries found for this executable path. Other rules may apply.");
            }
            catch (Exception error) { report.AppendLine("  Firewall rule inventory unavailable: " + error.Message); }
            report.AppendLine();
            report.AppendLine(host ? "A successful test from this PC only checks its own listener. Run Test connection on the other PC to test the path between them."
                : "Test connection checks TCP and the exact TLS certificate identity. It does not request approval, capture a screen or send input.");
            return report.ToString();
        }
        public static string Probe(Invitation invite)
        {
            ConnectionStage stage = ConnectionStage.Contacting; Stopwatch clock = Stopwatch.StartNew();
            try
            {
                using (TcpClient client = Transport.Connect(invite.Host, invite.Port, 5000))
                {
                    if (invite.Relay) { stage = ConnectionStage.PairingRelay; Transport.JoinRelay(client, "V", invite.Room); }
                    stage = ConnectionStage.Securing;
                    using (SslStream tls = new SslStream(client.GetStream(), false, delegate(object sender, X509Certificate certificate, X509Chain chain, SslPolicyErrors errors)
                    { return certificate != null && Security.Equal(Security.Pin(certificate), invite.Fingerprint); }))
                    {
                        tls.ReadTimeout = 5000; tls.WriteTimeout = 5000;
                        tls.AuthenticateAsClient("Lume Remote Session", null, SslProtocols.None, false); Security.CheckTls(tls);
                        return "PASS: TCP and pinned TLS reached " + Endpoint(invite) + " in " + clock.ElapsedMilliseconds + " ms.\r\nNo approval was requested. Close this diagnostic window and use Connect to computer for a session.";
                    }
                }
            }
            catch (Exception error) { return "FAIL: " + Failure(stage, invite, error).Replace("\n", "\r\n"); }
        }
    }

    sealed class ConnectionDiagnosticsForm : Form
    {
        readonly TextBox report = Theme.Box(true);
        readonly Button test = Theme.Button("Test connection", true);
        readonly Invitation invite;
        readonly bool host;
        bool closed;
        public ConnectionDiagnosticsForm(Invitation invite, bool host)
        {
            this.invite = invite; this.host = host;
            Text = "Lume - Connection diagnostics"; Size = new Size(840, 640); MinimumSize = new Size(620, 420);
            BackColor = Theme.Background; StartPosition = FormStartPosition.CenterParent; Padding = new Padding(18);
            AutoScaleMode = AutoScaleMode.Dpi;
            report.ReadOnly = true; report.Dock = DockStyle.Fill; report.ScrollBars = ScrollBars.Both; report.WordWrap = true;
            report.Font = new Font("Consolas", 10); report.Text = "Reading local network information...";
            FlowLayoutPanel actions = new FlowLayoutPanel { Dock = DockStyle.Bottom, Height = 62 };
            Button copy = Theme.Button("Copy report", false); actions.Controls.Add(test); actions.Controls.Add(copy);
            test.Enabled = false;
            copy.Click += delegate { try { Clipboard.SetText(report.Text); } catch (Exception error) { MessageBox.Show(this, error.Message, "Clipboard busy"); } };
            test.Click += async delegate
            {
                test.Enabled = false; report.AppendText("\r\nTesting " + ConnectionDiagnostics.Endpoint(invite) + "...\r\n");
                string result = await Task.Run(delegate { return ConnectionDiagnostics.Probe(invite); });
                if (closed) return; report.AppendText(result + "\r\n"); test.Enabled = true;
            };
            Controls.Add(report); Controls.Add(actions);
            FormClosing += delegate { closed = true; };
            Shown += async delegate
            {
                string value = await Task.Run(delegate { return ConnectionDiagnostics.LocalReport(this.invite, this.host); });
                if (closed) return; report.Text = value; test.Enabled = this.invite != null;
            };
        }
    }
}
