using System;
using System.Diagnostics;
using System.Drawing;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.NetworkInformation;
using System.Net.Sockets;
using System.Runtime.InteropServices;
using System.Threading.Tasks;
using System.Windows.Forms;

namespace LumeRemote
{
    public sealed class MainForm : Form
    {
        readonly ComboBox mode = Theme.Combo(), address = Theme.Combo(), screen = Theme.Combo(), profile = Theme.Combo();
        readonly TextBox port = Theme.Box(false), relay = Theme.Box(false), invitation = Theme.Box(true), remote = Theme.Box(true);
        readonly CheckBox control = new CheckBox { Text = "Allow control and files after approval", Checked = true, AutoSize = true, ForeColor = Theme.Text, Margin = new Padding(0, 4, 0, 12) };
        readonly Button start = Theme.Button("Start sharing", true), stop = Theme.Button("Stop sharing", false), connect = Theme.Button("Connect to computer", true), copy = Theme.Button("Copy invitation", false);
        readonly Label status = Theme.Label("Ready. No connection is active.", 10, Theme.Muted), resources = Theme.Label("", 9, Theme.Muted);
        readonly Label endpoint = Theme.Label("Not sharing. Start sharing to create a private invitation.", 10, Theme.Muted);
        readonly Timer statistics = new Timer { Interval = 2000 };
        HostService host;
        PeerTransport peer;
        bool starting, closing, exitRequested, exitPreparing;
        int generation;
        int clipboardPending;
        readonly ComputersPanel home = new ComputersPanel();
        readonly NotifyIcon tray = new NotifyIcon { Icon = Brand.Icon, Text = "Lume Remote", Visible = true };
        const int HotkeyId = 0x4C55;
        public MainForm()
        {
            Theme.BeginLayout(this);
            Text = "Lume Remote"; BackColor = Theme.Background; ForeColor = Theme.Text; Font = new Font("Segoe UI", 10);
            Size = new Size(960, 760); MinimumSize = new Size(840, 640); StartPosition = FormStartPosition.CenterScreen;
            Icon = Brand.Icon;
            TableLayoutPanel root = new TableLayoutPanel { Dock = DockStyle.Fill, RowCount = 3, ColumnCount = 1, Padding = new Padding(24, 16, 24, 12) };
            root.RowStyles.Add(new RowStyle(SizeType.Absolute, 128)); root.RowStyles.Add(new RowStyle(SizeType.Percent, 100)); root.RowStyles.Add(new RowStyle(SizeType.Absolute, 32));
            FlowLayoutPanel header = new FlowLayoutPanel { Dock = DockStyle.Fill, FlowDirection = FlowDirection.TopDown, WrapContents = false };
            FlowLayoutPanel brand = new FlowLayoutPanel { AutoSize = true, WrapContents = false, Height = 48, Margin = new Padding(0) };
            brand.Controls.Add(Brand.Mark(44)); brand.Controls.Add(Theme.Label("Lume", 25, Theme.Text)); header.Controls.Add(brand);
            FlowLayoutPanel navigation = new FlowLayoutPanel { AutoSize = true, WrapContents = false, Margin = new Padding(0) };
            Button computersTab = Theme.Button("Computers", true), guestTab = Theme.Button("Guest access", false);
            computersTab.Width = guestTab.Width = 158; navigation.Controls.Add(computersTab); navigation.Controls.Add(guestTab); header.Controls.Add(navigation);
            root.Controls.Add(header, 0, 0);
            TableLayoutPanel columns = new TableLayoutPanel { Dock = DockStyle.Fill, ColumnCount = 2, RowCount = 1, AutoScroll = true };
            columns.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 50)); columns.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 50));
            Panel leftScroll = new Panel { Dock = DockStyle.Fill, AutoScroll = true, Margin = new Padding(0, 0, 12, 0), BackColor = Theme.Card };
            Panel rightScroll = new Panel { Dock = DockStyle.Fill, AutoScroll = true, Margin = new Padding(12, 0, 0, 0), BackColor = Theme.Card };
            FlowLayoutPanel left = Theme.Column(), right = Theme.Column(); leftScroll.Controls.Add(left); rightScroll.Controls.Add(right);
            columns.Controls.Add(leftScroll, 0, 0); columns.Controls.Add(rightScroll, 1, 0);
            Panel pages = new Panel { Dock = DockStyle.Fill, Margin = new Padding(0) }; pages.Controls.Add(columns); pages.Controls.Add(home); columns.Visible = false; home.BringToFront(); root.Controls.Add(pages, 0, 1);
            computersTab.Click += delegate { columns.Visible = false; home.Visible = true; home.BringToFront(); computersTab.BackColor = Theme.Accent; computersTab.ForeColor = Theme.AccentText; guestTab.BackColor = Theme.Field; guestTab.ForeColor = Theme.Text; };
            guestTab.Click += delegate { home.Visible = false; columns.Visible = true; columns.BringToFront(); FitColumn(left, leftScroll); FitColumn(right, rightScroll); guestTab.BackColor = Theme.Accent; guestTab.ForeColor = Theme.AccentText; computersTab.BackColor = Theme.Field; computersTab.ForeColor = Theme.Text; };
            left.Controls.Add(Theme.Label("Share this PC", 21, Theme.Text));
            left.Controls.Add(Theme.Label("Start, send the code, approve your guest.", 10, Theme.Muted));
            FlowLayoutPanel settings = Theme.Column(); settings.Name = "Settings"; settings.Padding = new Padding(0); settings.Visible = false;
            AddField(settings, "Connection route", mode); mode.Items.AddRange(new object[] { "P2P Internet - invitation and reply", "Direct - local network or VPN", "Internet - your own relay" }); mode.SelectedIndex = 0;
            AddField(settings, "Local network address", address);
            foreach (IPAddress ip in NetworkAddresses()) address.Items.Add(ip); if (address.Items.Count > 0) address.SelectedIndex = 0;
            AddField(settings, "Listening port", port); port.Text = "24816"; port.MaxLength = 5;
            AddField(settings, "Relay address (host:port)", relay); relay.Text = ""; relay.Enabled = false; relay.MaxLength = 260;
            mode.SelectedIndexChanged += delegate { address.Enabled = port.Enabled = mode.SelectedIndex == 1; relay.Enabled = mode.SelectedIndex == 2; };
            address.Enabled = port.Enabled = false;
            AddField(settings, "Screen to share", screen);
            for (int i = 0; i < Screen.AllScreens.Length; i++) { Rectangle r = Screen.AllScreens[i].Bounds; screen.Items.Add("Display " + (i + 1) + "  /  " + r.Width + " x " + r.Height + (Screen.AllScreens[i].Primary ? "  /  primary" : "")); }
            screen.SelectedIndex = 0;
            AddField(settings, "Initial stream quality", profile); profile.Items.AddRange(Profile.All); profile.SelectedIndex = 3;
            settings.Controls.Add(control);
            Label shareSummary = Theme.Label("P2P Internet / source resolution", 10, Theme.Muted); settings.Controls.Add(shareSummary);
            // The summary must reflect every choice that changes what a guest can do.
            EventHandler summarize = delegate
            {
                Profile selected = profile.SelectedItem as Profile;
                shareSummary.Text = (mode.SelectedIndex == 0 ? "P2P Internet" : mode.SelectedIndex == 1 ? "Direct LAN/VPN" : "Your Internet relay") + "  /  " + (selected == null ? "" : selected.Name.Split('-')[0].Trim().ToLowerInvariant() + " quality") + "\n" + (control.Checked ? "Viewing, keyboard, mouse and files require your approval." : "View only. Viewing requires your approval.");
            };
            mode.SelectedIndexChanged += summarize; profile.SelectedIndexChanged += summarize; control.CheckedChanged += summarize; summarize(null, EventArgs.Empty);
            FlowLayoutPanel actions = new FlowLayoutPanel { AutoSize = true, WrapContents = false, Margin = new Padding(0) }; actions.Controls.Add(start); actions.Controls.Add(stop); left.Controls.Add(actions);
            stop.Enabled = false; start.Click += async delegate { await StartSharing(); }; stop.Click += delegate { StopSharing(); };
            left.Controls.Add(Theme.Label("YOUR PRIVATE INVITATION", 9, Theme.Muted)); invitation.ReadOnly = true; invitation.ScrollBars = ScrollBars.Vertical; invitation.TabStop = false; left.Controls.Add(invitation);
            copy.Enabled = false;
            Button checkHost = Theme.Button("Check network", false);
            FlowLayoutPanel invitationActions = new FlowLayoutPanel { AutoSize = true, WrapContents = false, Margin = new Padding(0) };
            invitationActions.Controls.Add(copy); invitationActions.Controls.Add(checkHost); left.Controls.Add(invitationActions);
            settings.Controls.Add(endpoint);
            checkHost.Click += delegate { if (peer != null) SetStatus("P2P uses ICE/STUN and an invitation/reply exchange. No inbound TCP listener is required."); else new ConnectionDiagnosticsForm(host == null ? null : host.Invite, true).Show(this); };
            copy.Click += delegate { try { if (invitation.Text.Length > 0) { Clipboard.SetText(invitation.Text); SetStatus("Invitation copied. Send it privately to the person you trust."); } } catch (Exception e) { SetStatus(e.Message); } };
            left.Controls.Add(Theme.Label("Emergency stop: Ctrl + Alt + Shift + F12", 9, Theme.Accent));
            Button configure = Theme.Button("Connection and quality settings", false); configure.Width = 330;
            configure.Click += delegate { settings.Visible = !settings.Visible; configure.Text = settings.Visible ? "Hide settings" : "Connection and quality settings"; FitColumn(left, leftScroll); };
            left.Controls.Add(configure); left.Controls.Add(settings);
            right.Controls.Add(Theme.Label("Connect as a guest", 21, Theme.Text));
            right.Controls.Add(Theme.Label("Paste the code from the other PC.", 10, Theme.Muted));
            AddField(right, "Private invitation", remote); remote.Height = 132; remote.MaxLength = 65536; remote.ScrollBars = ScrollBars.Vertical;
            connect.Width = 240; right.Controls.Add(connect); connect.Click += delegate { ConnectRemote(); };
            Button checkRemote = Theme.Button("Check connection", false); checkRemote.Width = 240; right.Controls.Add(checkRemote);
            checkRemote.Click += delegate { try { if (remote.Text.Trim().StartsWith(PeerSignal.OfferPrefix, StringComparison.Ordinal)) { PeerSignal.Parse(remote.Text); SetStatus("Valid P2P invitation. Use Connect to computer, then send the reply back to the sharing PC."); } else new ConnectionDiagnosticsForm(Invitation.Parse(remote.Text), false).Show(this); } catch (Exception error) { SetStatus(error.Message); } };
            Button guide = Theme.Button("Open quick start", false); right.Controls.Add(guide);
            guide.Click += delegate { try { Process.Start(new ProcessStartInfo(Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "START-HERE.txt")) { UseShellExecute = true }); } catch (Exception e) { SetStatus(e.Message); } };
            FlowLayoutPanel footer = new FlowLayoutPanel { Dock = DockStyle.Fill, WrapContents = false, Padding = new Padding(0, 8, 0, 0) };
            status.Text = "Ready"; status.MaximumSize = new Size(700, 0); resources.MaximumSize = new Size(170, 0); footer.Controls.Add(status); footer.Controls.Add(resources); root.Controls.Add(footer, 0, 2); Controls.Add(root);
            Theme.EndLayout(this);
            Resize += delegate { FitColumn(left, leftScroll); FitColumn(right, rightScroll); };
            Shown += delegate { FitColumn(left, leftScroll); FitColumn(right, rightScroll); if (!RegisterHotKey(Handle, HotkeyId, 0x4007, 0x7B)) SetStatus("Emergency shortcut unavailable; use Stop sharing or Disable access."); };
            FormClosing += delegate(object sender, FormClosingEventArgs args)
            {
                if (args.CloseReason == CloseReason.UserClosing && !exitRequested) { args.Cancel = true; HideDashboard(); return; }
                closing = true; statistics.Stop(); StopSharing();
                foreach (Form window in Application.OpenForms.Cast<Form>().ToArray())
                    if (window is ViewerForm || window is PeerViewerForm) window.Close();
                UnregisterHotKey(Handle, HotkeyId);
                tray.Dispose(); home.Dispose();
            };
            statistics.Tick += delegate { UpdateResources(); }; statistics.Start();
            ContextMenuStrip menu = new ContextMenuStrip();
            menu.Opening += delegate
            {
                menu.Items.Clear(); menu.Items.Add("Open Lume", null, delegate { ShowDashboard(); });
                foreach (ViewerForm session in Application.OpenForms.OfType<ViewerForm>().ToArray())
                { ViewerForm window = session; menu.Items.Add(window.Text.Replace("&", "&&"), null, delegate { window.Show(); window.WindowState = FormWindowState.Normal; window.Activate(); }); }
                menu.Items.Add(new ToolStripSeparator());
                menu.Items.Add("Disable access to this PC", null, delegate { try { PermanentAccess.Disable(); home.RefreshHost(); } catch (Exception error) { SetStatus(error.Message); } });
                menu.Items.Add("Exit Lume and disconnect viewers", null, delegate { ExitDashboard(); });
            };
            tray.ContextMenuStrip = menu;
            tray.DoubleClick += delegate { ShowDashboard(); };
            Shown += delegate { if (Environment.GetCommandLineArgs().Contains("--tray")) HideDashboard(); };
        }
        void HideDashboard() { ShowInTaskbar = false; Hide(); }
        void ShowDashboard() { ShowInTaskbar = true; Show(); WindowState = FormWindowState.Normal; Activate(); }
        internal async void ExitDashboard() { if (exitPreparing || exitRequested) return; exitPreparing = true; foreach (ViewerForm viewer in Application.OpenForms.OfType<ViewerForm>().ToArray()) await viewer.FinishRecording(); exitRequested = true; Close(); }
        static void AddField(FlowLayoutPanel parent, string label, Control field) { parent.Controls.Add(Theme.Label(label, 9, Theme.Muted)); parent.Controls.Add(field); }
        static void FitColumn(FlowLayoutPanel column, Panel scroll)
        {
            int width = Math.Max(Theme.Px(270), scroll.ClientSize.Width - Theme.Px(70));
            column.Width = scroll.ClientSize.Width - Theme.Px(18);
            foreach (Control item in column.Controls)
            {
                if (item is Label) { item.MaximumSize = new Size(width, 0); }
                else if (item is TextBox || item is ComboBox) item.Width = width;
                else if (item is FlowLayoutPanel)
                {
                    if (item.Name == "Settings")
                    {
                        item.Width = width;
                        foreach (Control field in item.Controls) { if (field is Label) field.MaximumSize = new Size(width, 0); else if (field is TextBox || field is ComboBox) field.Width = width; }
                    }
                    else foreach (Control button in item.Controls) button.Width = Math.Max(Theme.Px(110), (width - Theme.Px(20)) / 2);
                }
            }
        }
        static IPAddress[] NetworkAddresses()
        {
            try
            {
                return NetworkInterface.GetAllNetworkInterfaces().Where(n => n.OperationalStatus == OperationalStatus.Up)
                    .SelectMany(n => n.GetIPProperties().UnicastAddresses).Select(a => a.Address)
                    .Where(a => a.AddressFamily == AddressFamily.InterNetwork && !IPAddress.IsLoopback(a))
                    .Distinct().OrderBy(a => a.ToString().StartsWith("192.168.") ? 0 : 1).Concat(new IPAddress[] { IPAddress.Loopback }).ToArray();
            }
            catch { return new IPAddress[] { IPAddress.Loopback }; }
        }
        async Task StartSharing()
        {
            if (starting || host != null) return;
            try
            {
                int listenPort;
                if (!Int32.TryParse(port.Text, out listenPort) || listenPort < 1 || listenPort > 65535) throw new FormatException("Choose a port between 1 and 65535.");
                bool usePeer = mode.SelectedIndex == 0, useRelay = mode.SelectedIndex == 2, allowControl = control.Checked;
                string relayHost = ""; int relayPort = 24817;
                if (useRelay)
                {
                    string value = relay.Text.Trim(); int separator = value.LastIndexOf(':');
                    if (separator < 1 || !Int32.TryParse(value.Substring(separator + 1), out relayPort) || relayPort < 1 || relayPort > 65535) throw new FormatException("Enter your relay as hostname:port, for example relay.example.net:24817.");
                    relayHost = value.Substring(0, separator);
                    if (Uri.CheckHostName(relayHost) == UriHostNameType.Unknown) throw new FormatException("The relay hostname is invalid.");
                }
                IPAddress bind = (IPAddress)address.SelectedItem; Rectangle bounds = Screen.AllScreens[screen.SelectedIndex].Bounds; Profile chosen = (Profile)profile.SelectedItem;
                starting = true; int current = ++generation; SetSharingControls(true); SetStatus("Creating an encrypted session...");
                HostService created = await Task.Run(delegate { return new HostService(delegate { return new MonitorSource(bounds, chosen); }, chosen, allowControl,
                    delegate(PeerRequest request) { return Approve(request, current); }, delegate(string text) { ReceiveClipboard(text, current); },
                    delegate(string value) { if (current == generation) SetStatus(value); }, delegate(string text) { return RequestClipboard(text, current); }, delegate { return new RemoteFileAccess(); }, delegate { return ReadClipboard(current); }, true, delegate { return AudioPermission(current); }, null, false, HostVoiceConsent.Request); });
                if (closing || current != generation) { created.Dispose(); return; }
                host = created;
                if (usePeer)
                {
                    created.PeerEnded += delegate
                    {
                        if (closing || IsDisposed) return;
                        try { BeginInvoke((Action)delegate { if (current == generation) { StopSharing(); SetStatus("P2P session ended. Start sharing creates a fresh invitation."); } }); } catch (InvalidOperationException) { }
                    };
                    host.StartPeer(); PeerTransport createdPeer = await Task.Run(delegate { return new PeerTransport(); });
                    if (closing || current != generation) { createdPeer.Dispose(); return; } peer = createdPeer;
                    PeerSignal offer = await Task.Run(delegate { return PeerSignal.Offer(created.Invite, createdPeer.CreateOffer()); });
                    if (closing || current != generation) { createdPeer.Dispose(); return; }
                    invitation.Text = offer.ToString(); copy.Enabled = true;
                    endpoint.Text = "P2P Internet / invitation and reply\nNo inbound TCP port or VPN is required.";
                    SetStatus("P2P invitation ready. Send it privately and paste the returned reply in the P2P window.");
                    new PeerHostForm(offer, createdPeer, delegate { if (current == generation && host == created) { SetStatus(createdPeer.RouteSummary() + " connected. Local approval is next."); created.AcceptPeer(createdPeer); } },
                        delegate { if (current == generation) StopSharing(); }).Show(this);
                    return;
                }
                if (useRelay) host.StartRelay(relayHost, relayPort); else host.Start(bind, listenPort, bind.ToString());
                invitation.Text = host.Invite.ToString(); copy.Enabled = true;
                endpoint.Text = (useRelay ? "Relay endpoint: " : "Listening on: ") + ConnectionDiagnostics.Endpoint(host.Invite) +
                    (useRelay ? "\nBoth PCs connect to your relay." : "\nDirect mode: same reachable LAN or VPN.");
            }
            catch (Exception error) { StopSharing(); SetStatus("Could not start: " + error.Message); }
            finally { starting = false; }
        }
        void SetSharingControls(bool sharing)
        {
            start.Enabled = !sharing; stop.Enabled = sharing; mode.Enabled = screen.Enabled = profile.Enabled = control.Enabled = !sharing;
            address.Enabled = port.Enabled = !sharing && mode.SelectedIndex == 1; relay.Enabled = !sharing && mode.SelectedIndex == 2;
        }
        void StopSharing()
        {
            generation++; if (peer != null) { peer.Dispose(); peer = null; } if (host != null) { host.Dispose(); host = null; }
            invitation.Clear(); copy.Enabled = false; SetSharingControls(false); SetStatus("Sharing stopped. The previous invitation is revoked.");
            endpoint.Text = "Not sharing. Start sharing to create a private invitation.";
            foreach (Form owned in OwnedForms) if (owned is ConsentForm || (owned.Tag as string) == "LumeClipboard" || (owned.Tag as string) == "LumePeer") owned.Close();
        }
        bool Approve(PeerRequest request, int current)
        {
            if (closing || IsDisposed || current != generation) return false;
            try { return (bool)Invoke(new Func<bool>(delegate { if (closing || current != generation) return false; using (ConsentForm dialog = new ConsentForm(request)) return dialog.ShowDialog(this) == DialogResult.Yes; })); }
            catch { return false; }
        }
        void ReceiveClipboard(string text, int current)
        { RequestClipboard(text, current); }
        Task<bool> RequestClipboard(string text, int current)
        {
            TaskCompletionSource<bool> result = new TaskCompletionSource<bool>();
            if (closing || IsDisposed || current != generation || System.Threading.Interlocked.CompareExchange(ref clipboardPending, 1, 0) != 0) { result.SetResult(false); return result.Task; }
            try { BeginInvoke((Action)delegate
            {
                try
                {
                    if (closing || current != generation) return;
                    using (Form dialog = new Form { Text = "Lume - Incoming clipboard text", Icon = Brand.Icon, Tag = "LumeClipboard", StartPosition = FormStartPosition.CenterParent, BackColor = Theme.Background, ForeColor = Theme.Text, MinimizeBox = false, MaximizeBox = false })
                    {
                        Theme.BeginLayout(dialog); dialog.Size = new Size(550, 390);
                        TextBox preview = Theme.Box(true); preview.Dock = DockStyle.Fill; preview.ReadOnly = true; preview.Text = text; preview.ScrollBars = ScrollBars.Both;
                        Button accept = Theme.Button("Copy to my clipboard", true); accept.Width = 210; accept.Dock = DockStyle.Bottom; accept.Click += delegate { try { if (text.Length == 0) Clipboard.Clear(); else Clipboard.SetText(text); result.TrySetResult(true); dialog.Close(); } catch (Exception e) { MessageBox.Show(dialog, e.Message, "Clipboard busy"); } };
                        Button ignore = Theme.Button("Don't copy", false); ignore.Dock = DockStyle.Bottom; ignore.DialogResult = DialogResult.Cancel; dialog.CancelButton = ignore;
                        dialog.Padding = new Padding(20); dialog.Controls.Add(preview); dialog.Controls.Add(accept); dialog.Controls.Add(ignore); Theme.EndLayout(dialog); using (LocalConsent.Begin()) dialog.ShowDialog(this);
                    }
                }
                finally { result.TrySetResult(false); System.Threading.Interlocked.Exchange(ref clipboardPending, 0); }
            }); } catch { result.TrySetResult(false); System.Threading.Interlocked.Exchange(ref clipboardPending, 0); }
            return result.Task;
        }
        Task<bool> AudioPermission(int current)
        {
            var result = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
            if (closing || IsDisposed || current != generation) { result.TrySetResult(false); return result.Task; }
            try { BeginInvoke((Action)delegate
            {
                bool allowed; using (LocalConsent.Begin()) allowed = !closing && current == generation && MessageBox.Show(this, "The connected guest wants to hear this PC's system sound. This includes sound from other applications. Allow until the session ends?", "Lume - System audio", MessageBoxButtons.YesNo, MessageBoxIcon.Question, MessageBoxDefaultButton.Button2) == DialogResult.Yes;
                result.TrySetResult(allowed && !closing && current == generation);
            }); } catch { result.TrySetResult(false); }
            return result.Task;
        }
        Task<string> ReadClipboard(int current)
        {
            TaskCompletionSource<string> result = new TaskCompletionSource<string>(TaskCreationOptions.RunContinuationsAsynchronously);
            if (closing || IsDisposed || current != generation) { result.SetCanceled(); return result.Task; }
            try { BeginInvoke((Action)async delegate
            {
                try
                {
                    if (closing || current != generation) throw new OperationCanceledException();
                    using (LocalConsent.Begin()) if (MessageBox.Show(this, "The connected guest wants to read your clipboard text. Allow this once?", "Lume - Clipboard request", MessageBoxButtons.YesNo, MessageBoxIcon.Question, MessageBoxDefaultButton.Button2) != DialogResult.Yes) throw new OperationCanceledException();
                    string text = await ClipboardAccess.Read();
                    if (closing || current != generation) throw new OperationCanceledException(); result.TrySetResult(text);
                }
                catch (Exception error) { result.TrySetException(error); }
            }); } catch { result.TrySetCanceled(); }
            return result.Task;
        }
        void ConnectRemote()
        {
            try { if (remote.Text.Trim().StartsWith(PeerSignal.OfferPrefix, StringComparison.Ordinal)) new PeerViewerForm(PeerSignal.Parse(remote.Text)).Show(); else { Invitation invite = Invitation.Parse(remote.Text); ViewerForm viewer = new ViewerForm(invite); viewer.Show(); } }
            catch (Exception error) { SetStatus(error.Message); }
        }
        void SetStatus(string value)
        {
            if (closing || IsDisposed) return;
            if (InvokeRequired) { try { BeginInvoke((Action)delegate { if (!closing) status.Text = value; }); } catch { } }
            else status.Text = value;
        }
        void UpdateResources()
        {
            int sessions = Application.OpenForms.OfType<ViewerForm>().Count(window => window.IsSessionConnected);
            resources.Text = sessions == 0 ? "v" + typeof(MainForm).Assembly.GetName().Version.ToString(2) + " preview" : sessions + (sessions == 1 ? " session" : " sessions");
            tray.Text = "Lume Remote - " + resources.Text;
        }
        protected override void WndProc(ref Message message) { if (message.Msg == 0x312 && message.WParam.ToInt32() == HotkeyId) { StopSharing(); try { PermanentAccess.Disable(); home.RefreshHost(); } catch (Exception error) { SetStatus(error.Message); } ShowDashboard(); } base.WndProc(ref message); }
        [DllImport("user32.dll", SetLastError = true)] static extern bool RegisterHotKey(IntPtr window, int id, uint modifiers, uint key);
        [DllImport("user32.dll")] static extern bool UnregisterHotKey(IntPtr window, int id);
    }

    sealed class ConsentForm : Form
    {
        readonly Timer timeout = new Timer { Interval = 1000 };
        int remaining = 60;
        public ConsentForm(PeerRequest request)
        {
            Theme.BeginLayout(this);
            Text = "Lume - Connection request"; Icon = Brand.Icon; ClientSize = new Size(540, 365); BackColor = Theme.Background; ForeColor = Theme.Text;
            FormBorderStyle = FormBorderStyle.FixedDialog; StartPosition = FormStartPosition.CenterParent; MinimizeBox = false; MaximizeBox = false; TopMost = true;
            FlowLayoutPanel panel = Theme.Column(); panel.BackColor = Theme.Background; panel.Dock = DockStyle.Fill;
            panel.Controls.Add(Theme.Label("Allow this connection?", 23, Theme.Text));
            panel.Controls.Add(Theme.Label("Claimed name (not verified): " + request.Name + "\nRoute: " + request.Address, 12, Theme.Text));
            panel.Controls.Add(Theme.Label(request.Control ? "This person can see your screen, use your keyboard and mouse" + (request.Files ? ", and browse, send and receive your files" : "") + ". Only accept someone you trust." : "This person will see the selected screen. Keyboard, mouse, clipboard and file access are disabled.", 11, Theme.Muted));
            Label countdown = Theme.Label("Automatically declined in 60 seconds.", 10, Theme.Muted); panel.Controls.Add(countdown);
            FlowLayoutPanel buttons = new FlowLayoutPanel { AutoSize = true }; Button deny = Theme.Button("Decline", false), allow = Theme.Button(request.Control ? "Allow control" : "Allow viewing", true);
            deny.DialogResult = DialogResult.No; allow.DialogResult = DialogResult.Yes; buttons.Controls.Add(deny); buttons.Controls.Add(allow); panel.Controls.Add(buttons); Controls.Add(panel);
            Theme.EndLayout(this);
            CancelButton = deny; AcceptButton = deny;
            timeout.Tick += delegate { remaining--; countdown.Text = "Automatically declined in " + remaining + " seconds."; if (remaining <= 0) { DialogResult = DialogResult.No; Close(); } }; timeout.Start();
            FormClosed += delegate { timeout.Dispose(); };
        }
    }
}
