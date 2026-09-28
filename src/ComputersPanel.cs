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
        readonly Button connect = Theme.Button("Connect", true), enable = Theme.Button("Enable permanent access", true), pair = Theme.Button("Pair another computer", false);
        readonly Button update = Theme.Button("Update installed host", false);
        readonly Label state = Theme.Label("", 10, Theme.Muted), progress = Theme.Label("Select a saved PC and connect with one click.", 10, Theme.Muted);
        readonly Label deviceName = Theme.Label(Environment.MachineName, 16, Theme.Text), hostSummary = Theme.Label("Access off", 10, Theme.Muted);
        readonly CheckBox awake = new CheckBox { Text = "Keep this PC awake while plugged in", AutoSize = true, ForeColor = Theme.Text, Margin = new Padding(0, 8, 0, 12) };
        readonly System.Windows.Forms.Timer refresh = new System.Windows.Forms.Timer { Interval = 3000 };
        SavedPreferences saved;
        CancellationTokenSource connecting;
        bool loading;
        static ListBox List() { return new ListBox { Width = 398, Height = 130, BackColor = Theme.Field, ForeColor = Theme.Text, BorderStyle = BorderStyle.None, Font = new Font("Segoe UI", 11), IntegralHeight = false, Margin = new Padding(0, 0, 0, 12) }; }
        public ComputersPanel()
        {
            Dock = DockStyle.Fill; BackColor = Theme.Background;
            TableLayoutPanel columns = new TableLayoutPanel { Dock = DockStyle.Fill, ColumnCount = 1, RowCount = 1 };
            columns.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
            FlowLayoutPanel left = Theme.Column(), right = Theme.Column(); Panel a = new Panel { Dock = DockStyle.Fill, AutoScroll = true }, b = new Panel { Dock = DockStyle.Fill, AutoScroll = true };
            left.Padding = new Padding(22, 16, 22, 16); deviceName.Margin = hostSummary.Margin = new Padding(0, 0, 0, 6);
            a.Controls.Add(left); b.Controls.Add(right); columns.Controls.Add(a, 0, 0); Controls.Add(columns);
            left.Controls.Add(Theme.Label("Your computers", 22, Theme.Text));
            computers.Height = 128; computers.BackColor = Theme.Card; computers.DrawMode = DrawMode.OwnerDrawFixed; computers.ItemHeight = 64; computers.AccessibleName = "Saved computers";
            computers.DrawItem += DrawComputer;
            left.Controls.Add(computers);
            Button add = Theme.Button("Add a computer", false), remove = Theme.Button("Forget selected PC", false), wake = Theme.Button("Wake setup...", false), cancel = Theme.Button("Cancel connection", false);
            FlowLayoutPanel connectionActions = new FlowLayoutPanel { AutoSize = true, WrapContents = true, Margin = new Padding(0) };
            connect.Width = 170; add.Width = 180; Button manage = Theme.Button("More", false); manage.Width = 86;
            connectionActions.Controls.Add(connect); connectionActions.Controls.Add(add); connectionActions.Controls.Add(manage); left.Controls.Add(connectionActions); left.Controls.Add(progress);
            ContextMenuStrip computerMenu = new ContextMenuStrip();
            computerMenu.Items.Add("Wake setup", null, delegate { wake.PerformClick(); }); computerMenu.Items.Add("Forget selected PC", null, delegate { remove.PerformClick(); }); computerMenu.Items.Add("Cancel connection", null, delegate { cancel.PerformClick(); });
            manage.Click += delegate { computerMenu.Show(manage, new Point(0, manage.Height)); };
            left.Controls.Add(Theme.Label("THIS PC", 9, Theme.Accent)); left.Controls.Add(deviceName); left.Controls.Add(hostSummary);
            FlowLayoutPanel hostActions = new FlowLayoutPanel { AutoSize = true, WrapContents = true, Margin = new Padding(0) };
            enable.Width = 170; pair.Width = 180; Button hostSettings = Theme.Button("Settings", false); hostSettings.Width = 86;
            hostActions.Controls.Add(enable); hostActions.Controls.Add(pair); hostActions.Controls.Add(hostSettings); left.Controls.Add(hostActions);
            right.Controls.Add(Theme.Label("This PC", 22, Theme.Text)); right.Controls.Add(state); right.Controls.Add(awake);
            update.Width = 310; right.Controls.Add(update);
            Button networkFolders = Theme.Button("Network folders", false); networkFolders.Width = 310; right.Controls.Add(networkFolders);
            networkFolders.Click += delegate { try { if (!PermanentAccess.Installed) throw new InvalidOperationException("Enable permanent access before configuring network folders."); using (var dialog = new NetworkFoldersForm(TrustedStore.Machine.ReadHost().NetworkFolders.ToArray())) if (dialog.ShowDialog(this) == DialogResult.OK) TrustedStore.Machine.ChangeHost(delegate(HostPreferences host) { host.NetworkFolders = dialog.Roots.ToList(); }); } catch (Exception error) { ShowError(error); } };
            right.Controls.Add(Theme.Label("Update restarts the installed host and closes its old dashboard. Saved pairings and the access ON/OFF setting are kept. Windows asks for administrator permission.", 10, Theme.Muted));
            right.Controls.Add(Theme.Label("COMPUTERS ALLOWED TO CONNECT", 9, Theme.Muted)); right.Controls.Add(trusted);
            Button revoke = Theme.Button("Revoke selected access", false), helper = Theme.Button("Create wake-only pairing", false), uninstall = Theme.Button("Remove Windows service", false);
            revoke.Width = helper.Width = uninstall.Width = 310; right.Controls.Add(revoke); right.Controls.Add(helper); right.Controls.Add(uninstall);
            right.Controls.Add(Theme.Label("Paired computers can control this PC, send clipboard text and transfer files. Guest invitations still ask for approval. Disable access at any time, or use Ctrl + Alt + Shift + F12 while the dashboard is open.", 10, Theme.Muted));
            hostSettings.Click += delegate
            {
                using (Form settings = Dialog("Computer settings", 620, 650))
                { settings.Controls.Add(b); Fit(right, b); settings.Shown += delegate { Fit(right, b); }; settings.ShowDialog(this); settings.Controls.Remove(b); }
            };
            try { saved = TrustedStore.User.ReadSaved(); } catch (Exception error) { saved = new SavedPreferences(); progress.Text = error.Message; add.Enabled = remove.Enabled = false; }
            ReloadSaved(); RefreshHost();
            computers.SelectedIndexChanged += delegate { UpdateConnectButton(); };
            UpdateConnectButton();
            connect.Click += async delegate { await ConnectSelected(); }; computers.DoubleClick += async delegate { await ConnectSelected(); };
            cancel.Click += delegate { if (connecting != null) connecting.Cancel(); };
            add.Click += async delegate
            {
                string code = Prompt("Add a computer", "On the other PC: enable permanent access, then choose Pair another computer. Paste its one-time code here.", "", true); if (code == null) return;
                add.Enabled = false;
                try { using (CancellationTokenSource timeout = new CancellationTokenSource(40000)) { progress.Text = "Pairing securely..."; SavedComputer computer = await PairedClient.Pair(PairingCode.Parse(code), Environment.MachineName, timeout.Token); saved.Computers.RemoveAll(c => c.HostId == computer.HostId && c.WakeOnly == computer.WakeOnly && (!c.WakeOnly || c.WakeMac == computer.WakeMac)); saved.Computers.Add(computer); Save(); ReloadSaved(); computers.SelectedItem = computer; progress.Text = "Saved. Use Connect whenever you need this PC."; } }
                catch (Exception error) { ShowError(error); } finally { add.Enabled = true; }
            };
            remove.Click += delegate { SavedComputer selected = computers.SelectedItem as SavedComputer; if (selected == null) return; saved.Computers.Remove(selected); foreach (SavedComputer c in saved.Computers) if (c.WakeHelperId == selected.Id) c.WakeHelperId = null; Save(); ReloadSaved(); };
            enable.Click += async delegate
            {
                enable.Enabled = false;
                try
                {
                    if (!PermanentAccess.Installed) { state.Text = "Approve the Windows administrator prompt to install automatic access."; await PermanentAccess.Install(false); }
                    else TrustedStore.Machine.ChangeHost(delegate(HostPreferences host) { host.Enabled = !host.Enabled; if (!host.Enabled) { host.PairKey = host.PairId = null; host.PairExpires = 0; } });
                    RefreshHost();
                }
                catch (Exception error) { ShowError(error); } finally { enable.Enabled = true; }
            };
            update.Click += async delegate
            {
                update.Enabled = false;
                try { state.Text = "Approve Windows setup to update the host. An active session will reconnect after the restart."; await PermanentAccess.Install(false, true); RefreshHost(); state.Text = "Installed host updated. Saved computers can reconnect."; }
                catch (Exception error) { ShowError(error); }
                finally { update.Enabled = true; }
            };
            pair.Click += delegate { try { ShowPairing(TrustedStore.Machine.CreatePairing(false, null)); } catch (Exception error) { ShowError(error); } };
            awake.CheckedChanged += delegate { if (loading) return; try { TrustedStore.Machine.ChangeHost(delegate(HostPreferences host) { host.KeepAwake = awake.Checked; }); } catch (Exception error) { ShowError(error); } };
            revoke.Click += delegate { TrustedController controller = trusted.SelectedItem as TrustedController; if (controller == null) return; try { TrustedStore.Machine.ChangeHost(delegate(HostPreferences host) { host.Controllers.RemoveAll(c => c.Id == controller.Id); }); RefreshHost(); } catch (Exception error) { ShowError(error); } };
            helper.Click += delegate
            {
                string mac = Prompt("Wake helper", "Use this on an always-on PC in the sleeping PC's network. Enter the sleeping PC's Ethernet MAC. This pairing can only send wake packets to that address.", "", false); if (mac == null) return;
                try { ShowPairing(TrustedStore.Machine.CreatePairing(true, mac)); } catch (Exception error) { ShowError(error); }
            };
            wake.Click += delegate { ConfigureWake(); };
            uninstall.Click += async delegate { try { PermanentAccess.Disable(); uninstall.Enabled = false; await PermanentAccess.Install(true); RefreshHost(); } catch (Exception error) { ShowError(error); } finally { uninstall.Enabled = true; } };
            refresh.Tick += delegate { RefreshHost(); }; refresh.Start();
            VisibleChanged += delegate { if (Visible) { RefreshHost(); refresh.Start(); } else refresh.Stop(); };
            Disposed += delegate { refresh.Dispose(); if (connecting != null) connecting.Cancel(); b.Dispose(); computerMenu.Dispose(); };
            Resize += delegate { Fit(left, a); Fit(right, b); }; Load += delegate { Fit(left, a); Fit(right, b); };
        }
        static void Fit(FlowLayoutPanel panel, Panel parent)
        { int width = Math.Max(250, parent.ClientSize.Width - 70); panel.Width = parent.ClientSize.Width - 18; foreach (Control control in panel.Controls) { if (control is Label) control.MaximumSize = new Size(width, 0); else if (control is ListBox || control is FlowLayoutPanel) control.Width = width; else control.Width = Math.Min(398, width); } }
        void Save() { TrustedStore.User.SaveComputers(saved); }
        void ReloadSaved() { computers.Items.Clear(); computers.Items.AddRange(saved.Computers.ToArray()); if (computers.Items.Count > 0) computers.SelectedIndex = 0; progress.Text = computers.Items.Count == 0 ? "Add your first PC. Pair once, connect whenever you need it." : "Double-click a PC to connect. Each session opens in its own window."; }
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
            using (Brush background = new SolidBrush(selected ? Theme.Field : Theme.Card)) args.Graphics.FillRectangle(background, args.Bounds);
            int y = args.Bounds.Y;
            using (Pen line = new Pen(Theme.Accent, 2)) { args.Graphics.DrawRectangle(line, 17, y + 15, 31, 22); args.Graphics.DrawLine(line, 32, y + 38, 32, y + 44); args.Graphics.DrawLine(line, 24, y + 45, 40, y + 45); }
            using (Font name = new Font("Segoe UI Semibold", 12)) TextRenderer.DrawText(args.Graphics, computer.Name, name, new Rectangle(66, y + 8, args.Bounds.Width - 80, 26), Theme.Text, TextFormatFlags.EndEllipsis | TextFormatFlags.NoPrefix);
            ViewerForm viewer = OpenViewer(computer);
            string detail = computer.WakeOnly ? "Wake helper" : viewer == null ? "Saved computer" : viewer.IsSessionConnected ? "Connected - click to return" : "Session window open";
            TextRenderer.DrawText(args.Graphics, detail, Font, new Rectangle(66, y + 34, args.Bounds.Width - 80, 23), Theme.Muted, TextFormatFlags.EndEllipsis | TextFormatFlags.NoPrefix);
            args.DrawFocusRectangle();
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
                hostSummary.Text = host != null && host.Enabled ? "Access on - paired computers can connect" : "Access off";
                hostSummary.ForeColor = host != null && host.Enabled ? Theme.Accent : Theme.Muted; computers.Invalidate();
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
                panel.Controls.Add(helpers); Button apply = Theme.Button("Save wake setup", true); panel.Controls.Add(apply); dialog.Controls.Add(panel);
                apply.Click += delegate { try { SavedComputer helper = helpers.SelectedItem as SavedComputer; computer.WakeMac = String.IsNullOrWhiteSpace(mac.Text) ? null : WakeOnLan.Normalize(mac.Text); if (helper != null && helper.WakeMac != computer.WakeMac) throw new InvalidOperationException("The helper was paired for a different MAC. Create its wake-only code for this PC's Ethernet MAC."); computer.WakeHelperId = helper == null ? null : helper.Id; Save(); dialog.Close(); } catch (Exception error) { MessageBox.Show(dialog, error.Message, "Wake setup"); } };
                dialog.ShowDialog(this);
            }
        }
        void ShowPairing(PairingCode code)
        {
            using (Form dialog = Dialog("One-time pairing", 580, 385))
            {
                FlowLayoutPanel panel = Theme.Column(); panel.Dock = DockStyle.Fill; panel.BackColor = Theme.Background;
                panel.Controls.Add(Theme.Label("Pair once. Connect anytime.", 22, Theme.Text)); panel.Controls.Add(Theme.Label("Copy this code to Add a computer on your other PC. It expires in 15 minutes and works once. Anyone holding it can receive the access shown below.", 10, Theme.Muted));
                panel.Controls.Add(Theme.Label(code.wake ? "WAKE PACKETS ONLY / " + code.mac : "PERMANENT SCREEN, KEYBOARD AND MOUSE ACCESS", 10, Theme.Accent));
                TextBox value = Theme.Box(true); value.ReadOnly = true; value.Text = code.ToString(); panel.Controls.Add(value);
                Button copy = Theme.Button("Copy pairing code", true); copy.Click += delegate { try { Clipboard.SetText(code.ToString()); copy.Text = "Copied"; } catch (Exception error) { MessageBox.Show(dialog, error.Message); } }; panel.Controls.Add(copy); dialog.Controls.Add(panel); dialog.ShowDialog(this);
            }
        }
        static Form Dialog(string title, int width, int height) { return new Form { Text = "Lume - " + title, ClientSize = new Size(width, height), BackColor = Theme.Background, ForeColor = Theme.Text, StartPosition = FormStartPosition.CenterParent, AutoScaleMode = AutoScaleMode.Dpi, MinimumSize = new Size(width, height) }; }
        string Prompt(string title, string text, string initial, bool multiline)
        {
            using (Form dialog = Dialog(title, 580, 340))
            {
                FlowLayoutPanel panel = Theme.Column(); panel.Dock = DockStyle.Fill; panel.BackColor = Theme.Background; panel.Controls.Add(Theme.Label(text, 11, Theme.Text));
                TextBox value = Theme.Box(multiline); value.MaxLength = 4096; value.Text = initial; panel.Controls.Add(value); Button apply = Theme.Button("Continue", true); apply.DialogResult = DialogResult.OK; panel.Controls.Add(apply); dialog.Controls.Add(panel);
                return dialog.ShowDialog(this) == DialogResult.OK ? value.Text : null;
            }
        }
    }
}
