using System;
using System.Drawing;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using System.Windows.Forms;

namespace LumeRemote
{
    sealed class ComputersPanel : UserControl
    {
        readonly ListBox computers = List(), trusted = List();
        readonly Button connect = Theme.Button("Connect", true), enable = Theme.Button("Enable access", true), pair = Theme.Button("Pair another PC", false);
        readonly Button update = Theme.Button("Update installed host", false);
        readonly Label state = Theme.Label("", 10, Theme.Muted), progress = Theme.Label("Select a saved PC and connect with one click.", 10, Theme.Muted);
        readonly Label deviceName = Theme.Label(Environment.MachineName, 16, Theme.Text), hostDetail = Theme.Label("", 10, Theme.Muted);
        readonly StatusPill hostSummary = new StatusPill { Text = "Access off" };
        readonly TextAction trustedSummary = new TextAction();
        readonly FlowLayoutPanel empty = new FlowLayoutPanel { Dock = DockStyle.Fill, FlowDirection = FlowDirection.TopDown, WrapContents = false, BackColor = Theme.Card, Padding = new Padding(4, 12, 4, 4) };
        Button add;
        readonly CheckBox awake = new CheckBox { Text = "Keep this PC awake while plugged in", AutoSize = true, ForeColor = Theme.Text, Margin = new Padding(0, 8, 0, 12) };
        readonly System.Windows.Forms.Timer refresh = new System.Windows.Forms.Timer { Interval = 3000 };
        SavedPreferences saved;
        CancellationTokenSource connecting;
        bool loading;
        static ListBox List() { return new ListBox { Width = 398, Height = 130, BackColor = Theme.Field, ForeColor = Theme.Text, BorderStyle = BorderStyle.None, Font = new Font("Segoe UI", 11), IntegralHeight = false, Margin = new Padding(0, 0, 0, 12) }; }
        public ComputersPanel()
        {
            Dock = DockStyle.Fill; BackColor = Theme.Background;
            // Two cards: saved computers (outgoing) and this PC's permanent access (incoming).
            TableLayoutPanel columns = new TableLayoutPanel { Dock = DockStyle.Fill, ColumnCount = 2, RowCount = 1, Margin = new Padding(0), Padding = new Padding(0), BackColor = Theme.Background };
            columns.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 62)); columns.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 38));
            CardPanel listCard = new CardPanel { Dock = DockStyle.Fill, Margin = new Padding(0, 0, 8, 0) }, hostCard = new CardPanel { Dock = DockStyle.Fill, Margin = new Padding(8, 0, 0, 0), AutoScroll = true };
            TableLayoutPanel listLayout = new TableLayoutPanel { Dock = DockStyle.Fill, ColumnCount = 1, RowCount = 4, BackColor = Theme.Card, Margin = new Padding(0) };
            listLayout.RowStyles.Add(new RowStyle(SizeType.AutoSize)); listLayout.RowStyles.Add(new RowStyle(SizeType.Percent, 100)); listLayout.RowStyles.Add(new RowStyle(SizeType.AutoSize)); listLayout.RowStyles.Add(new RowStyle(SizeType.AutoSize));
            FlowLayoutPanel left = Theme.Column(), right = Theme.Column(); Panel a = new Panel { Dock = DockStyle.Fill, AutoScroll = true, BackColor = Theme.Card }, b = new Panel { Dock = DockStyle.Fill, AutoScroll = true };
            left.Padding = new Padding(0); left.Dock = DockStyle.Fill; a.Controls.Add(left); b.Controls.Add(right); hostCard.Controls.Add(a); listCard.Controls.Add(listLayout);
            columns.Controls.Add(listCard, 0, 0); columns.Controls.Add(hostCard, 1, 0); Controls.Add(columns);
            Label listTitle = Theme.Label("Your computers", 14, Theme.Text); listTitle.Margin = new Padding(0, 0, 0, 12); listLayout.Controls.Add(listTitle, 0, 0);
            computers.Dock = DockStyle.Fill; computers.BackColor = Theme.Card; computers.DrawMode = DrawMode.OwnerDrawFixed; computers.ItemHeight = Theme.Px(Theme.RowHeight); computers.AccessibleName = "Saved computers";
            computers.DrawItem += DrawComputer;
            Panel listArea = new Panel { Dock = DockStyle.Fill, Margin = new Padding(0, 0, 0, 8), BackColor = Theme.Card }; listArea.Controls.Add(computers); listArea.Controls.Add(empty); empty.BringToFront();
            listLayout.Controls.Add(listArea, 0, 1);
            Button add = Theme.Button("Add a computer", false), remove = Theme.Button("Forget selected PC", false), wake = Theme.Button("Wake setup...", false), cancel = Theme.Button("Cancel connection", false);
            this.add = add;
            FlowLayoutPanel connectionActions = new FlowLayoutPanel { AutoSize = true, WrapContents = true, Margin = new Padding(0), BackColor = Theme.Card };
            connect.Width = 130; add.Width = 160; Button manage = Theme.Button("More", false); manage.Width = 90;
            connectionActions.Controls.Add(connect); connectionActions.Controls.Add(add); connectionActions.Controls.Add(manage); listLayout.Controls.Add(connectionActions, 0, 2); listLayout.Controls.Add(progress, 0, 3);
            ContextMenuStrip computerMenu = new ContextMenuStrip();
            computerMenu.Items.Add("Cancel connection", null, delegate { cancel.PerformClick(); }); computerMenu.Items.Add("Wake setup...", null, delegate { wake.PerformClick(); }); computerMenu.Items.Add(new ToolStripSeparator()); computerMenu.Items.Add("Forget selected PC", null, delegate { remove.PerformClick(); });
            manage.Click += delegate { computerMenu.Show(manage, new Point(0, manage.Height)); };
            computers.ContextMenuStrip = computerMenu;
            computers.KeyDown += async delegate(object sender, KeyEventArgs e) { if (e.KeyCode == Keys.Enter) { e.Handled = e.SuppressKeyPress = true; await ConnectSelected(); } };
            // Empty state: a short guide with Add a computer as the next step.
            empty.Controls.Add(Theme.Label("No computers yet", 12, Theme.Text)); empty.Controls.Add(Theme.Label("Pair once, then connect with one click.", 10, Theme.Muted));
            foreach (string step in new[] { "1   On the PC you want to control, open Lume and choose Enable access.", "2   There, choose Pair another PC. It shows an eight-digit code.", "3   Here, choose Add a computer and type it." }) empty.Controls.Add(Theme.Label(step, 10, Theme.Text));
            Label hostHeading = Theme.Label("This PC", 14, Theme.Text); hostHeading.Margin = new Padding(0, 0, 0, 8);
            deviceName.Font = new Font(Theme.FontNameStrong, 12); deviceName.Margin = new Padding(0, 0, 0, 2);
            left.Controls.Add(hostHeading); left.Controls.Add(deviceName); left.Controls.Add(hostSummary); left.Controls.Add(hostDetail); left.Controls.Add(trustedSummary);
            FlowLayoutPanel hostActions = new FlowLayoutPanel { AutoSize = true, WrapContents = true, Margin = new Padding(0, 8, 0, 0), BackColor = Theme.Card };
            pair.Width = 150; Button hostSettings = Theme.Button("Settings", false); hostSettings.Width = 100;
            hostActions.Controls.Add(pair); hostActions.Controls.Add(hostSettings); left.Controls.Add(hostActions);
            enable.Width = 258; enable.Margin = new Padding(0, 4, 0, 8); left.Controls.Add(enable);
            trustedSummary.Click += delegate { hostSettings.PerformClick(); };
            right.Controls.Add(Theme.Label("Computer settings", 16, Theme.Text));
            right.Controls.Add(Section("Access")); right.Controls.Add(state); right.Controls.Add(awake);
            right.Controls.Add(Section("Computers allowed to connect")); right.Controls.Add(trusted);
            Button revoke = Theme.DangerButton("Revoke selected access"), helper = Theme.Button("Create wake-only pairing", false), uninstall = Theme.DangerButton("Remove Windows service");
            right.Controls.Add(revoke);
            right.Controls.Add(Theme.Label("Paired computers can see and control this PC, use the clipboard in both directions, transfer files and configured network folders, hear system sound, and lock, restart or shut it down. Guest invitations still ask for approval.", 9, Theme.Muted));
            right.Controls.Add(Section("Wake")); right.Controls.Add(helper);
            right.Controls.Add(Section("Network folders"));
            Button networkFolders = Theme.Button("Network folders", false); right.Controls.Add(networkFolders);
            networkFolders.Click += delegate { if (GuestBlocks("Network folder settings")) return; try { if (!PermanentAccess.Installed) throw new InvalidOperationException("Enable permanent access before configuring network folders."); using (var dialog = new NetworkFoldersForm(TrustedStore.Machine.ReadHost().NetworkFolders.ToArray())) if (dialog.ShowDialog(this) == DialogResult.OK) TrustedStore.Machine.Change(new HostRequest { Op = "folders", Folders = dialog.Roots.ToList() }); } catch (Exception error) { ShowError(error); } };
            right.Controls.Add(Section("Maintenance")); right.Controls.Add(update);
            right.Controls.Add(Theme.Label("Update restarts the installed host and closes its old dashboard. Saved pairings and the access setting are kept. Windows asks for administrator permission.", 9, Theme.Muted));
            right.Controls.Add(Section("Danger zone")); right.Controls.Add(uninstall);
            right.Controls.Add(Theme.Label("Disable access at any time from the dashboard or the tray icon, or press Ctrl + Alt + Shift + F12 while the dashboard is open.", 9, Theme.Muted));
            hostSettings.Click += delegate
            {
                using (Form settings = Dialog("Computer settings", 620, 650))
                { Theme.EndLayout(settings); settings.Controls.Add(b); Fit(right, b); settings.Shown += delegate { Fit(right, b); }; settings.ShowDialog(this); settings.Controls.Remove(b); }
            };
            try { saved = TrustedStore.User.ReadSaved(); } catch (Exception error) { saved = new SavedPreferences(); progress.Text = error.Message; add.Enabled = remove.Enabled = false; }
            ReloadSaved(); RefreshHost();
            computers.SelectedIndexChanged += delegate { UpdateConnectButton(); };
            UpdateConnectButton();
            connect.Click += async delegate { await ConnectSelected(); }; computers.DoubleClick += async delegate { await ConnectSelected(); };
            cancel.Click += delegate { if (connecting != null) connecting.Cancel(); };
            add.Click += async delegate
            {
                if (GuestBlocks("Adding a computer")) return;
                string code = Prompt("Add a computer", "On the other PC: choose Enable access, then Pair another PC. Type the eight-digit code it shows, or paste its full code.", "", true, delegate(string text) { if (ShortPairing.Normalize(text) != null) return null; try { PairingCode.Parse(text); return null; } catch (Exception) { return "Enter the eight digits shown on the other PC, or paste all of its full code."; } }, "Add computer"); if (code == null) return;
                add.Enabled = false;
                try
                {
                    string digits = ShortPairing.Normalize(code);
                    if (digits != null)
                        using (CancellationTokenSource joining = new CancellationTokenSource(300000))
                            code = await ShortPairing.Join(digits, Environment.MachineName, ConfirmOnUi, delegate(string text) { OnUi(delegate { progress.Text = text; }); }, joining.Token);
                    using (CancellationTokenSource timeout = new CancellationTokenSource(40000)) { progress.Text = "Pairing securely..."; SavedComputer computer = await PairedClient.Pair(PairingCode.Parse(code), Environment.MachineName, timeout.Token); saved.Computers.RemoveAll(c => c.HostId == computer.HostId && c.WakeOnly == computer.WakeOnly && (!c.WakeOnly || c.WakeMac == computer.WakeMac)); saved.Computers.Add(computer); Save(); ReloadSaved(); computers.SelectedItem = computer; progress.Text = "Saved. Use Connect whenever you need this PC."; }
                }
                catch (OperationCanceledException error) { progress.Text = error.Message; }
                catch (Exception error) { ShowError(error); } finally { add.Enabled = true; }
            };
            remove.Click += delegate { SavedComputer selected = computers.SelectedItem as SavedComputer; if (selected == null) return; if (MessageBox.Show(FindForm(), "Forget " + selected + " on this PC? You will need a new pairing code to connect again.", "Lume - Forget computer", MessageBoxButtons.YesNo, MessageBoxIcon.Warning, MessageBoxDefaultButton.Button2) != DialogResult.Yes) return; saved.Computers.Remove(selected); foreach (SavedComputer c in saved.Computers) if (c.WakeHelperId == selected.Id) c.WakeHelperId = null; Save(); ReloadSaved(); };
            enable.Click += async delegate
            {
                // Turning access off is always allowed; turning it on is an owner-only action.
                bool turningOn = !PermanentAccess.Installed || !TrustedStore.Machine.ReadHost().Enabled;
                if (turningOn && GuestBlocks("Enabling access")) return;
                enable.Enabled = false;
                try
                {
                    if (!PermanentAccess.Installed) { if (!ConfirmEnable()) return; progress.Text = "Approve the Windows administrator prompt to install automatic access."; await PermanentAccess.Install(false); }
                    else if (TrustedStore.Machine.ReadHost().Enabled) PermanentAccess.Disable();
                    else TrustedStore.Machine.Change(new HostRequest { Op = "enable", Flag = true });
                    RefreshHost();
                }
                catch (Exception error) { ShowError(error); } finally { enable.Enabled = true; }
            };
            update.Click += async delegate
            {
                if (GuestBlocks("Updating the installed host")) return;
                update.Enabled = false;
                try { state.Text = "Approve Windows setup to update the host. An active session will reconnect after the restart."; await PermanentAccess.Install(false, true); RefreshHost(); state.Text = "Installed host updated. Saved computers can reconnect."; }
                catch (Exception error) { ShowError(error); }
                finally { update.Enabled = true; }
            };
            pair.Click += delegate { if (GuestBlocks("Pairing")) return; try { ShowPairing(TrustedStore.Machine.CreatePairing(false, null)); } catch (Exception error) { ShowError(error); } };
            awake.CheckedChanged += delegate { if (loading) return; try { TrustedStore.Machine.Change(new HostRequest { Op = "keepawake", Flag = awake.Checked }); } catch (Exception error) { ShowError(error); } };
            revoke.Click += delegate { if (GuestBlocks("Changing paired computers")) return; TrustedController controller = trusted.SelectedItem as TrustedController; if (controller == null) return; try { TrustedStore.Machine.Change(new HostRequest { Op = "revoke", ControllerId = controller.Id }); RefreshHost(); } catch (Exception error) { ShowError(error); } };
            helper.Click += delegate
            {
                if (GuestBlocks("Pairing")) return;
                string mac = Prompt("Wake helper", "Use this on an always-on PC in the sleeping PC's network. Enter the sleeping PC's Ethernet MAC. This pairing can only send wake packets to that address.", "", false); if (mac == null) return;
                try { ShowPairing(TrustedStore.Machine.CreatePairing(true, mac)); } catch (Exception error) { ShowError(error); }
            };
            wake.Click += delegate { ConfigureWake(); };
            uninstall.Click += async delegate { if (MessageBox.Show(FindForm(), "Remove the Lume Windows service? Permanent access to this PC stops now, and Windows will ask for administrator approval.", "Lume - Remove Windows service", MessageBoxButtons.YesNo, MessageBoxIcon.Warning, MessageBoxDefaultButton.Button2) != DialogResult.Yes) return; try { PermanentAccess.Disable(); uninstall.Enabled = false; await PermanentAccess.Install(true); RefreshHost(); } catch (Exception error) { ShowError(error); } finally { uninstall.Enabled = true; } };
            refresh.Tick += delegate { RefreshHost(); }; refresh.Start();
            VisibleChanged += delegate { if (Visible) { RefreshHost(); refresh.Start(); } else refresh.Stop(); };
            Disposed += delegate { refresh.Dispose(); if (connecting != null) connecting.Cancel(); b.Dispose(); computerMenu.Dispose(); };
            Resize += delegate { Fit(left, a); Fit(right, b); }; Load += delegate { Fit(left, a); Fit(right, b); };
            // The settings column moves between dialogs, so it is scaled here once instead of by each dialog.
            b.Scale(new SizeF(Theme.Factor, Theme.Factor));
        }
        static Label Section(string title) { Label label = Theme.Label(title, 11, Theme.Text); label.Font = new Font(Theme.FontNameStrong, 11); label.Margin = new Padding(0, 16, 0, 6); return label; }
        static void Fit(FlowLayoutPanel panel, Panel parent)
        { int width = Math.Max(Theme.Px(250), parent.ClientSize.Width - Theme.Px(70)); panel.Width = parent.ClientSize.Width - Theme.Px(18); foreach (Control control in panel.Controls) { if (control is StatusPill) continue; if (control is Label) control.MaximumSize = new Size(width, 0); else if (control is ListBox || control is FlowLayoutPanel) control.Width = width; else control.Width = Math.Min(Theme.Px(398), width); } }
        void Save() { TrustedStore.User.SaveComputers(saved); }
        void ReloadSaved() { computers.Items.Clear(); computers.Items.AddRange(saved.Computers.ToArray()); if (computers.Items.Count > 0) computers.SelectedIndex = 0; UpdateEmptyState(); progress.Text = computers.Items.Count == 0 ? "" : "Double-click a PC to connect. Each session opens in its own window."; }
        void UpdateEmptyState()
        {
            bool none = computers.Items.Count == 0; empty.Visible = none; computers.Visible = !none; connect.Visible = !none;
            if (add != null) ((ReadableButton)add).Kind = none ? ButtonKind.Primary : ButtonKind.Secondary;
        }
        static ViewerForm OpenViewer(SavedComputer computer)
        { return Application.OpenForms.OfType<ViewerForm>().FirstOrDefault(window => window.ComputerId == computer.HostId && !window.IsDisposed); }
        void UpdateConnectButton()
        {
            SavedComputer selected = computers.SelectedItem as SavedComputer;
            connect.Enabled = connecting == null && selected != null && !selected.WakeOnly;
            connect.Text = selected != null && OpenViewer(selected) != null ? "Open session" : "Connect";
        }
        void DrawComputer(object sender, DrawItemEventArgs args)
        {
            if (args.Index < 0 || args.Index >= computers.Items.Count) return;
            SavedComputer computer = (SavedComputer)computers.Items[args.Index]; bool selected = (args.State & DrawItemState.Selected) != 0;
            Graphics g = args.Graphics; g.SmoothingMode = System.Drawing.Drawing2D.SmoothingMode.AntiAlias;
            using (Brush background = new SolidBrush(Theme.Card)) g.FillRectangle(background, args.Bounds);
            Rectangle row = new Rectangle(args.Bounds.X, args.Bounds.Y + Theme.Px(2), args.Bounds.Width - Theme.Px(2), args.Bounds.Height - Theme.Px(4));
            if (selected)
                using (System.Drawing.Drawing2D.GraphicsPath path = Theme.Rounded(row, Theme.Px(6))) using (Brush fill = new SolidBrush(Theme.AccentSoft)) using (Pen edge = new Pen(Theme.Accent)) { g.FillPath(fill, path); g.DrawPath(edge, path); }
            int y = row.Y + (row.Height - Theme.Px(52)) / 2;
            using (Pen line = new Pen(Theme.Accent, Theme.Px(2))) { g.DrawRectangle(line, Theme.Px(16), y + Theme.Px(14), Theme.Px(28), Theme.Px(19)); g.DrawLine(line, Theme.Px(30), y + Theme.Px(34), Theme.Px(30), y + Theme.Px(39)); g.DrawLine(line, Theme.Px(23), y + Theme.Px(40), Theme.Px(37), y + Theme.Px(40)); }
            using (Font name = new Font(Theme.FontNameStrong, 10.5f)) TextRenderer.DrawText(g, computer.Name, name, new Rectangle(Theme.Px(60), y + Theme.Px(6), row.Width - Theme.Px(72), Theme.Px(22)), Theme.Text, TextFormatFlags.EndEllipsis | TextFormatFlags.NoPrefix);
            ViewerForm viewer = OpenViewer(computer);
            string detail = computer.WakeOnly ? "Wake helper" : viewer == null ? "Saved computer" : viewer.IsSessionConnected ? "Connected - open its window" : "Session window open";
            TextRenderer.DrawText(g, detail, Font, new Rectangle(Theme.Px(60), y + Theme.Px(28), row.Width - Theme.Px(72), Theme.Px(20)), viewer != null && viewer.IsSessionConnected ? Theme.Success : Theme.Muted, TextFormatFlags.EndEllipsis | TextFormatFlags.NoPrefix);
            if (selected && (args.State & DrawItemState.Focus) != 0 && (args.State & DrawItemState.NoFocusRect) == 0) using (System.Drawing.Drawing2D.GraphicsPath path = Theme.Rounded(row, Theme.Px(6))) using (Pen ring = new Pen(Theme.Accent, Theme.Px(2))) g.DrawPath(ring, path);
        }
        void ShowError(Exception error) { progress.Text = error is OperationCanceledException ? "Cancelled." : error.Message; }
        public void RefreshHost()
        {
            loading = true;
            try
            {
                bool installed = PermanentAccess.Installed; HostPreferences host = installed ? TrustedStore.Machine.ReadHost() : null;
                update.Visible = installed && !String.Equals(Path.GetFullPath(AppDomain.CurrentDomain.BaseDirectory).TrimEnd(Path.DirectorySeparatorChar), Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles), "LumeRemote"), StringComparison.OrdinalIgnoreCase);
                enable.Text = !installed ? "Enable access" : host.Enabled ? "Disable access" : "Enable access"; pair.Text = "Pair another PC";
                pair.Enabled = awake.Enabled = installed && host.Enabled; awake.Checked = host != null && host.KeepAwake;
                string selection = trusted.SelectedItem is TrustedController ? ((TrustedController)trusted.SelectedItem).Id : null;
                trusted.Items.Clear(); if (host != null) foreach (TrustedController controller in host.Controllers) { trusted.Items.Add(controller); if (controller.Id == selection) trusted.SelectedItem = controller; }
                state.Text = !installed ? "Enable once to allow your paired PCs to connect automatically, including after Windows restarts. Windows will ask for administrator permission." : !host.Enabled ? "Permanent access is OFF. Saved PCs cannot connect." : "Permanent access is ON. Waiting for the host service...";
                string statusFile = Path.Combine(TrustedStore.MachineDirectory, "status.txt");
                if (host != null && host.Enabled && File.Exists(statusFile) && DateTime.UtcNow - File.GetLastWriteTimeUtc(statusFile) < TimeSpan.FromSeconds(30)) state.Text = File.ReadAllText(statusFile).Split(new[] { '\r', '\n' }, StringSplitOptions.RemoveEmptyEntries).Last();
                else if (host != null && host.Enabled) state.Text = "Permanent access is ON. Service status is not yet available.";
                bool on = host != null && host.Enabled; hostSummary.Text = on ? "Access on" : "Access off"; hostSummary.On = on;
                hostDetail.Text = !installed ? "Enable access to reach this PC from your other computers." : on ? "Paired computers can connect." : "Saved computers cannot connect.";
                int allowed = host == null ? 0 : host.Controllers.Count(c => !c.WakeOnly);
                trustedSummary.Text = allowed == 0 ? "No computers can connect yet" : allowed + (allowed == 1 ? " computer can connect - Manage" : " computers can connect - Manage");
                trustedSummary.Active = allowed > 0;
                ((ReadableButton)enable).Kind = on ? ButtonKind.Secondary : ButtonKind.Primary; computers.Invalidate();
                UpdateConnectButton();
            }
            catch (Exception error) { state.Text = error.Message; pair.Enabled = false; }
            finally { loading = false; }
        }
        async Task ConnectSelected()
        {
            SavedComputer computer = computers.SelectedItem as SavedComputer; if (computer == null || connecting != null) return;
            ViewerForm existing = OpenViewer(computer); if (existing != null) { existing.Show(); existing.WindowState = FormWindowState.Normal; existing.Activate(); return; }
            if (computer.WakeOnly) { progress.Text = "Select a desktop PC. Assign this wake helper using Wake setup."; return; }
            connecting = new CancellationTokenSource(); connect.Enabled = false;
            Action<string> report = delegate(string message) { if (!IsDisposed && IsHandleCreated) BeginInvoke((Action)delegate { if (!IsDisposed) progress.Text = message; }); };
            try
            {
                PairedLink link = null; Exception offline = null;
                try { link = await PairedClient.Connect(computer, report, connecting.Token); }
                catch (Exception error)
                {
                    if (!(error is IOException || error is TimeoutException)) throw;
                    offline = error;
                }
                if (offline != null)
                {
                    SavedComputer helper = saved.Computers.FirstOrDefault(c => c.Id == computer.WakeHelperId && c.WakeOnly); if (helper == null) throw offline;
                    report("Sending a wake packet through " + helper.Name + "..."); await PairedClient.Wake(helper, computer.WakeMac, connecting.Token);
                    DateTime deadline = DateTime.UtcNow.AddMinutes(2); link = null;
                    while (link == null && DateTime.UtcNow < deadline)
                    {
                        report("Waiting for " + computer.Name + " to wake and reconnect..."); await Task.Delay(5000, connecting.Token);
                        try { link = await PairedClient.Connect(computer, report, connecting.Token); }
                        catch (IOException) { } catch (TimeoutException) { }
                    }
                    if (link == null) throw new TimeoutException("Wake packet sent, but the PC did not reconnect. Check Ethernet, firmware wake support and power on the target PC.");
                }
                if (IsDisposed || connecting.IsCancellationRequested) { link.Dispose(); return; }
                ViewerForm viewer = new ViewerForm(link.Invitation, link.Peer, computer.Quality,
                    delegate(Action<string> update, CancellationToken cancellation)
                    {
                        SavedComputer current = TrustedStore.User.ReadSaved().Computers.FirstOrDefault(c => c.Id == computer.Id && c.HostId == computer.HostId && !c.WakeOnly && Security.Equal(c.Key, computer.Key));
                        if (current == null) throw new System.Security.Authentication.AuthenticationException("This saved computer was removed or replaced. Pair it again before connecting.");
                        return PairedClient.Connect(current, update, cancellation);
                    });
                viewer.ComputerId = computer.HostId; viewer.DisplayName = computer.Name; viewer.FileResumeKey = computer.Key;
                viewer.QualityChanged += delegate(StreamQuality quality) { computer.Quality = quality; Save(); }; viewer.Show(); progress.Text = "Connected to " + computer.Name + ".";
            }
            catch (Exception error) { if (!IsDisposed) ShowError(error); }
            finally { connecting.Dispose(); connecting = null; if (!IsDisposed) UpdateConnectButton(); }
        }
        void ConfigureWake()
        {
            SavedComputer computer = computers.SelectedItem as SavedComputer; if (computer == null || computer.WakeOnly) return;
            using (Form dialog = Dialog("Wake setup", 620, 440))
            {
                FlowLayoutPanel panel = Theme.Column(); panel.Dock = DockStyle.Fill; panel.BackColor = Theme.Background;
                panel.Controls.Add(Theme.Label("Wake and connect", 22, Theme.Text)); panel.Controls.Add(Theme.Label("The sleeping PC needs Ethernet, wake-enabled firmware and standby power. Across the Internet, pair an always-on Lume PC in that network using Create wake-only pairing, then select it here.", 11, Theme.Muted));
                TextBox mac = Theme.Box(false); mac.Text = computer.WakeMac ?? ""; panel.Controls.Add(Theme.Label("Target Ethernet MAC", 9, Theme.Muted)); panel.Controls.Add(mac);
                ComboBox helpers = Theme.Combo(); helpers.Items.Add("No wake helper"); helpers.Items.AddRange(saved.Computers.Where(c => c.WakeOnly).ToArray()); helpers.SelectedIndex = 0;
                foreach (object item in helpers.Items) { SavedComputer helper = item as SavedComputer; if (helper != null && helper.Id == computer.WakeHelperId) helpers.SelectedItem = item; }
                panel.Controls.Add(helpers); Button apply = Theme.Button("Save wake setup", true); panel.Controls.Add(apply); dialog.Controls.Add(panel); Theme.EndLayout(dialog);
                apply.Click += delegate { try { SavedComputer helper = helpers.SelectedItem as SavedComputer; computer.WakeMac = String.IsNullOrWhiteSpace(mac.Text) ? null : WakeOnLan.Normalize(mac.Text); if (helper != null && helper.WakeMac != computer.WakeMac) throw new InvalidOperationException("The helper was paired for a different MAC. Create its wake-only code for this PC's Ethernet MAC."); computer.WakeHelperId = helper == null ? null : helper.Id; Save(); dialog.Close(); } catch (Exception error) { MessageBox.Show(dialog, error.Message, "Wake setup"); } };
                dialog.ShowDialog(this);
            }
        }
        static readonly string[] PairedCapabilities = { "See this screen and use its keyboard and mouse", "Transfer files and configured network folders", "Use the clipboard in both directions", "Hear this PC's system sound", "Lock, restart or shut down this PC" };
        static Font CodeFont() { return new Font(Theme.InstalledFontName("Cascadia Mono", "Consolas"), 10); }
        void ShowPairing(PairingCode code)
        {
            using (Form dialog = Dialog("Pair another PC", 580, 640))
            {
                FlowLayoutPanel panel = Theme.Column(); panel.Dock = DockStyle.Fill; panel.BackColor = Theme.Background; panel.Padding = new Padding(28, 22, 28, 18); panel.AutoScroll = true;
                panel.Controls.Add(Theme.Label("Pair another PC", 16, Theme.Text));
                panel.Controls.Add(Theme.Label("On your other PC, choose Add a computer and type this code. It works once and expires in 15 minutes.", 10, Theme.Muted));
                Label shortCode = new Label { AutoSize = true, Text = "Preparing...", ForeColor = Theme.Text, Font = new Font(Theme.InstalledFontName("Cascadia Mono", "Consolas"), 26, FontStyle.Bold), Margin = new Padding(0, 0, 0, 6), AccessibleName = "Short pairing code" };
                panel.Controls.Add(shortCode);
                Label shortStatus = Theme.Label("Connecting to the pairing service...", 10, Theme.Muted); shortStatus.MaximumSize = new Size(520, 0); panel.Controls.Add(shortStatus);
                // Shown once the other PC has typed the code: both people compare one number.
                FlowLayoutPanel compare = new FlowLayoutPanel { AutoSize = true, WrapContents = false, Margin = new Padding(0, 0, 0, 6), Visible = false };
                Button matches = Theme.Button("They match", true), different = Theme.Button("They don't match", false); matches.Width = 140; different.Width = 170;
                compare.Controls.Add(matches); compare.Controls.Add(different); panel.Controls.Add(compare);
                Label manual = Theme.Label("Or copy the full code and paste it on the other PC:", 9, Theme.Muted); manual.Margin = new Padding(0, 8, 0, 4); panel.Controls.Add(manual);
                TextBox value = Theme.Box(true); value.ReadOnly = true; value.Text = code.ToString(); value.Font = CodeFont(); value.Height = 56; value.Width = 520; value.AccessibleName = "One-time pairing code"; value.TabStop = false; panel.Controls.Add(value);
                Button copy = Theme.Button("Copy full code", false); copy.Width = 150; copy.Click += delegate { try { Clipboard.SetText(code.ToString()); copy.Text = "Copied"; } catch (Exception error) { MessageBox.Show(dialog, error.Message); } }; panel.Controls.Add(copy);
                Label heading = Theme.Label(code.wake ? "The paired PC will only be able to:" : "The paired PC will be able to:", 10, Theme.Text); heading.Margin = new Padding(0, 10, 0, 4); panel.Controls.Add(heading);
                if (code.wake) panel.Controls.Add(new CapabilityRow("Send wake packets to " + code.mac, true));
                else foreach (string capability in PairedCapabilities) panel.Controls.Add(new CapabilityRow(capability, true));
                FlowLayoutPanel buttons = new FlowLayoutPanel { AutoSize = true, WrapContents = false, Margin = new Padding(0, 12, 0, 0) };
                Button revokeCode = Theme.Button("Cancel pairing code", false), done = Theme.Button("Done", false); revokeCode.Width = 180; done.Width = 110; done.DialogResult = DialogResult.OK;
                revokeCode.Click += delegate { try { TrustedStore.Machine.Change(new HostRequest { Op = "clearpair" }); dialog.Close(); progress.Text = "Pairing code cancelled. It can no longer be used."; } catch (Exception error) { MessageBox.Show(dialog, error.Message, "Lume"); } };
                buttons.Controls.Add(revokeCode); buttons.Controls.Add(done); panel.Controls.Add(buttons);
                ShortPairingOffer offer = null; bool closed = false;
                Action<Action> onUi = delegate(Action action) { try { if (!closed) dialog.BeginInvoke(action); } catch (InvalidOperationException) { } };
                matches.Click += delegate { if (GuestControl.Active) { dialog.Close(); return; } compare.Visible = false; if (offer != null) offer.Confirm(); shortStatus.Text = "Sent securely. Finish on the other PC; it appears there as a saved computer."; shortStatus.ForeColor = Theme.Text; };
                different.Click += delegate { compare.Visible = false; if (offer != null) { offer.Reject(); offer = null; } shortStatus.Text = "Stopped. Nothing was shared. Close this window and choose Pair another PC to try again."; shortStatus.ForeColor = Theme.Danger; };
                dialog.Shown += async delegate
                {
                    value.SelectionLength = 0;
                    try
                    {
                        ShortPairingOffer started = await ShortPairingOffer.Start(code.ToString(), Environment.MachineName);
                        if (closed) { started.Dispose(); return; }
                        offer = started; shortCode.Text = ShortPairing.Format(started.Code); shortStatus.Text = "Waiting for the other PC...";
                        started.Ready += delegate(string number, string name) { onUi(delegate { shortStatus.Text = "Check that " + name + " (name not verified) shows the same number:  " + number + "\nIf it does, choose They match. If not, stop."; shortStatus.ForeColor = Theme.Text; compare.Visible = true; dialog.ActiveControl = different; }); };
                        started.Failed += delegate(string message)
                        {
                            onUi(delegate
                            {
                                compare.Visible = false; shortStatus.Text = message; shortStatus.ForeColor = Theme.Danger;
                                // A mismatch reported after sending means the code may have reached the wrong PC.
                                if (started.CodeSent) { try { TrustedStore.Machine.Change(new HostRequest { Op = "clearpair" }); progress.Text = "Pairing code cancelled after a reported mismatch."; } catch (Exception error) { shortStatus.Text = message + " Cancel the pairing code: " + error.Message; } }
                            });
                        };
                    }
                    catch (Exception)
                    {
                        if (closed) return;
                        shortCode.Text = "Unavailable"; shortStatus.Text = "The short code needs an Internet connection to the pairing service. Use the full code below instead.";
                    }
                };
                // If a guest takes control while this window is open, withdraw the code at once.
                System.Windows.Forms.Timer guard = new System.Windows.Forms.Timer { Interval = 500 };
                guard.Tick += delegate { if (!GuestControl.Active) return; guard.Stop(); try { TrustedStore.Machine.Change(new HostRequest { Op = "clearpair" }); } catch (Exception) { } progress.Text = "Pairing was cancelled because a guest took control of this PC."; dialog.Close(); };
                dialog.Shown += delegate { guard.Start(); };
                dialog.FormClosed += delegate { closed = true; guard.Dispose(); if (offer != null) offer.Dispose(); };
                dialog.Controls.Add(panel); dialog.AcceptButton = done; dialog.CancelButton = done; dialog.ActiveControl = done; Theme.EndLayout(dialog); dialog.ShowDialog(this);
            }
        }
        // Owner-only actions must not be driven by a guest who currently controls this desktop.
        bool GuestBlocks(string action)
        {
            if (!GuestControl.Active) return false;
            MessageBox.Show(FindForm(), action + " is unavailable while a guest controls this PC. End the guest session first.", "Lume", MessageBoxButtons.OK, MessageBoxIcon.Information);
            return true;
        }
        void OnUi(Action action) { try { if (IsHandleCreated && !IsDisposed) BeginInvoke(action); } catch (InvalidOperationException) { } }
        Task<bool> ConfirmOnUi(string number, string name)
        {
            TaskCompletionSource<bool> answer = new TaskCompletionSource<bool>();
            try { BeginInvoke((Action)delegate { try { answer.TrySetResult(ConfirmNumber(number, name)); } catch (Exception error) { answer.TrySetException(error); } }); }
            catch (InvalidOperationException) { answer.TrySetResult(false); }
            return answer.Task;
        }
        // Both people compare the number; the safe choice is the default.
        bool ConfirmNumber(string number, string name)
        {
            using (Form dialog = Dialog("Compare the numbers", 520, 300))
            {
                FlowLayoutPanel panel = Theme.Column(); panel.Dock = DockStyle.Fill; panel.BackColor = Theme.Background; panel.Padding = new Padding(28, 22, 28, 18);
                panel.Controls.Add(Theme.Label("Does the other PC show this number?", 16, Theme.Text));
                panel.Controls.Add(new Label { AutoSize = true, Text = number, ForeColor = Theme.Text, Font = new Font(Theme.InstalledFontName("Cascadia Mono", "Consolas"), 26, FontStyle.Bold), Margin = new Padding(0, 0, 0, 8) });
                Label claimed = Theme.Label("Other PC: " + name + " (name not verified). Continue only if both PCs show the same number.", 10, Theme.Muted); claimed.MaximumSize = new Size(460, 0); panel.Controls.Add(claimed);
                FlowLayoutPanel buttons = new FlowLayoutPanel { AutoSize = true, WrapContents = false, Margin = new Padding(0, 10, 0, 0) };
                Button different = Theme.Button("They don't match", false), matches = Theme.Button("They match", true); different.Width = 170; matches.Width = 140;
                different.DialogResult = DialogResult.Cancel; matches.DialogResult = DialogResult.OK;
                buttons.Controls.Add(different); buttons.Controls.Add(matches); panel.Controls.Add(buttons); dialog.Controls.Add(panel);
                dialog.CancelButton = different; dialog.ActiveControl = different; Theme.EndLayout(dialog);
                return dialog.ShowDialog(this) == DialogResult.OK;
            }
        }
        // Explains permanent access before the Windows administrator prompt appears.
        bool ConfirmEnable()
        {
            using (Form dialog = Dialog("Enable access", 560, 470))
            {
                FlowLayoutPanel panel = Theme.Column(); panel.Dock = DockStyle.Fill; panel.BackColor = Theme.Background; panel.Padding = new Padding(28, 22, 28, 18);
                panel.Controls.Add(Theme.Label("Enable access on this PC", 16, Theme.Text));
                panel.Controls.Add(Theme.Label("Lume will install a Windows service that starts with Windows, so your paired PCs can connect even after a restart. Paired computers will be able to:", 10, Theme.Muted));
                foreach (string capability in PairedCapabilities) panel.Controls.Add(new CapabilityRow(capability, true));
                Label off = Theme.Label("You can disable access at any time from Lume, the tray icon or Ctrl + Alt + Shift + F12.", 10, Theme.Text); off.Margin = new Padding(0, 10, 0, 4); panel.Controls.Add(off);
                panel.Controls.Add(Theme.Label("Windows will ask for administrator permission.", 9, Theme.Muted));
                FlowLayoutPanel buttons = new FlowLayoutPanel { AutoSize = true, WrapContents = false, Margin = new Padding(0, 10, 0, 0) };
                Button cancel = Theme.Button("Cancel", false), accept = Theme.Button("Enable access", true); cancel.Width = 120; accept.Width = 150; cancel.DialogResult = DialogResult.Cancel; accept.DialogResult = DialogResult.OK;
                buttons.Controls.Add(cancel); buttons.Controls.Add(accept); panel.Controls.Add(buttons); dialog.Controls.Add(panel);
                dialog.CancelButton = cancel; dialog.ActiveControl = cancel; Theme.EndLayout(dialog);
                return dialog.ShowDialog(this) == DialogResult.OK;
            }
        }
        // Callers add their controls, then call Theme.EndLayout before showing the dialog.
        static Form Dialog(string title, int width, int height)
        {
            Form dialog = new Form { Text = "Lume - " + title, Icon = Brand.Icon, BackColor = Theme.Background, ForeColor = Theme.Text, StartPosition = FormStartPosition.CenterParent };
            Theme.BeginLayout(dialog); dialog.ClientSize = new Size(width, height); dialog.MinimumSize = new Size(width, height); return dialog;
        }
        string Prompt(string title, string text, string initial, bool multiline, Func<string, string> validate = null, string action = "Continue")
        {
            using (Form dialog = Dialog(title, 580, 340))
            {
                FlowLayoutPanel panel = Theme.Column(); panel.Dock = DockStyle.Fill; panel.BackColor = Theme.Background; panel.Padding = new Padding(28, 22, 28, 18);
                panel.Controls.Add(Theme.Label(title, 16, Theme.Text)); panel.Controls.Add(Theme.Label(text, 10, Theme.Muted));
                TextBox value = Theme.Box(multiline); value.MaxLength = 4096; value.Text = initial; value.Width = 520; value.AccessibleName = title; if (validate != null) value.Font = CodeFont(); panel.Controls.Add(value);
                Label error = Theme.Label("", 9, Theme.Danger); error.Visible = false; panel.Controls.Add(error);
                FlowLayoutPanel buttons = new FlowLayoutPanel { AutoSize = true, WrapContents = false, Margin = new Padding(0, 4, 0, 0) };
                Button cancel = Theme.Button("Cancel", false), apply = Theme.Button(action, true); cancel.Width = 120; apply.Width = 160; cancel.DialogResult = DialogResult.Cancel;
                apply.Click += delegate
                {
                    string problem = validate == null ? null : validate(value.Text.Trim());
                    if (problem == null) { dialog.DialogResult = DialogResult.OK; return; }
                    error.Text = problem; error.Visible = true; value.Focus();
                };
                value.TextChanged += delegate { error.Visible = false; };
                buttons.Controls.Add(cancel); buttons.Controls.Add(apply); panel.Controls.Add(buttons); dialog.Controls.Add(panel);
                dialog.AcceptButton = apply; dialog.CancelButton = cancel; Theme.EndLayout(dialog);
                return dialog.ShowDialog(this) == DialogResult.OK ? value.Text : null;
            }
        }
    }
}
