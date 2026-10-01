using System;
using System.Drawing;
using System.IO;
using System.Windows.Forms;
using Microsoft.Win32;

namespace LumeRemote
{
    // Guest invitations as clickable links. Messaging apps only make https links clickable,
    // so a shared link points to a static page that hands the invitation to Lume through the
    // per-user lume-open: protocol. The invitation stays in the URL fragment, which browsers
    // never send to the page's server. A link never connects by itself: Lume asks first, and
    // the sharing PC still approves the request.
    public static class InvitationLinks
    {
        public const string OpenPage = "https://darkplates.github.io/lume-remote/open.html";
        const string Scheme = "lume-open";
        public static string Link(string invitation) { return OpenPage + "#" + Uri.EscapeDataString(invitation.Trim()); }
        public static bool IsLaunch(string argument) { return argument != null && argument.StartsWith(Scheme + ":", StringComparison.OrdinalIgnoreCase); }
        static bool IsGuestInvitation(string value) { return value.StartsWith(PeerSignal.OfferPrefix, StringComparison.Ordinal) || value.StartsWith("lume://", StringComparison.Ordinal); }
        // Accepts a pasted invitation, a shared https link or a lume-open: launch argument.
        public static string Unwrap(string text)
        {
            if (text == null || text.Length > 200000) throw new FormatException("The invitation is missing or too large.");
            string value = text.Trim();
            if (value.StartsWith(OpenPage + "#", StringComparison.OrdinalIgnoreCase)) value = value.Substring(OpenPage.Length + 1);
            else if (IsLaunch(value)) value = value.Substring(Scheme.Length + 1);
            else return value;
            value = Uri.UnescapeDataString(value).Trim().TrimEnd('/');
            if (!IsGuestInvitation(value)) throw new FormatException("This link does not contain a Lume invitation. Ask for a new one.");
            return value;
        }
        // Per-user registration, refreshed on each dashboard start so it follows this copy.
        public static void Register()
        {
            try
            {
                string exe = Application.ExecutablePath;
                using (RegistryKey root = Registry.CurrentUser.CreateSubKey(@"Software\Classes\" + Scheme))
                {
                    root.SetValue("", "URL:Lume Remote invitation"); root.SetValue("URL Protocol", "");
                    using (RegistryKey icon = root.CreateSubKey("DefaultIcon")) icon.SetValue("", "\"" + exe + "\",0");
                    using (RegistryKey command = root.CreateSubKey(@"shell\open\command")) command.SetValue("", "\"" + exe + "\" \"%1\"");
                }
            }
            catch (Exception) { } // Links are a convenience; pasting the invitation still works.
        }
        // Started from a clicked link: confirm, then open only the viewer windows.
        public static void Open(string argument)
        {
            string invitation, destination; Form first;
            try
            {
                invitation = Unwrap(argument);
                if (invitation.StartsWith(PeerSignal.OfferPrefix, StringComparison.Ordinal)) { first = new PeerViewerForm(PeerSignal.Parse(invitation)); destination = "Route: P2P Internet. Your network address becomes visible to the sharing PC."; }
                else { Invitation invite = Invitation.Parse(invitation); first = new ViewerForm(invite); destination = (invite.Relay ? "Route: relay at " : "Connects to: ") + ConnectionDiagnostics.Endpoint(invite); }
            }
            catch (Exception)
            {
                MessageBox.Show("This Lume link is incomplete or damaged. Ask for a new invitation.", "Lume Remote", MessageBoxButtons.OK, MessageBoxIcon.Warning);
                return;
            }
            if (!Confirm(destination)) { first.Dispose(); return; }
            first.Show();
            Application.Run(new OpenWindows());
        }
        static bool Confirm(string destination)
        {
            using (Form dialog = new Form { Text = "Lume - Invitation link", Icon = Brand.Icon, BackColor = Theme.Background, ForeColor = Theme.Text, StartPosition = FormStartPosition.CenterScreen, TopMost = true })
            {
                Theme.BeginLayout(dialog); dialog.ClientSize = new Size(500, 300); dialog.MinimumSize = dialog.Size;
                FlowLayoutPanel panel = Theme.Column(); panel.Dock = DockStyle.Fill; panel.BackColor = Theme.Background; panel.Padding = new Padding(28, 22, 28, 18);
                panel.Controls.Add(Theme.Label("Connect to a shared PC?", 16, Theme.Text));
                Label text = Theme.Label("A Lume invitation link was opened, from a message or a website. Connect only if you asked this person to share their screen with you. They still approve the connection on their PC.", 10, Theme.Muted); text.MaximumSize = new Size(440, 0); panel.Controls.Add(text);
                Label where = Theme.Label(destination, 10, Theme.Text); where.MaximumSize = new Size(440, 0); panel.Controls.Add(where);
                FlowLayoutPanel buttons = new FlowLayoutPanel { AutoSize = true, WrapContents = false, Margin = new Padding(0, 10, 0, 0) };
                Button cancel = Theme.Button("Cancel", false), connect = Theme.Button("Connect", true); cancel.Width = 120; connect.Width = 130;
                cancel.DialogResult = DialogResult.Cancel; connect.DialogResult = DialogResult.OK;
                buttons.Controls.Add(cancel); buttons.Controls.Add(connect); panel.Controls.Add(buttons); dialog.Controls.Add(panel);
                dialog.CancelButton = cancel; dialog.ActiveControl = cancel; Theme.EndLayout(dialog);
                return dialog.ShowDialog() == DialogResult.OK;
            }
        }
        // Keeps a link-started process alive while any of its windows remain open.
        sealed class OpenWindows : ApplicationContext
        {
            readonly Timer check = new Timer { Interval = 500 };
            public OpenWindows() { check.Tick += delegate { if (Application.OpenForms.Count == 0) { check.Stop(); ExitThread(); } }; check.Start(); }
            protected override void Dispose(bool disposing) { if (disposing) check.Dispose(); base.Dispose(disposing); }
        }
    }
}
