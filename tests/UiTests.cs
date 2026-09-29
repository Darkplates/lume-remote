using System;
using System.Drawing;
using System.Drawing.Imaging;
using System.IO;
using System.Linq;
using System.Windows.Forms;
using LumeRemote;

static partial class Tests
{
    static void UiChecks()
    {
        Run("Two viewers survive closing the dashboard and disconnect independently", MultiSessionTray);
        Run("Simple quality presets retain custom source and frame rate controls", QualityPresetUi);
        Run("Layouts scale once with the system DPI and buttons fit their labels", DpiLayout);
        Run("Themed inputs, list headers and later controls follow the theme", ThemedControls);
        Run("Guest audio and clipboard prompts decline by default and time out", TimedConsentDefaults);
    }
    static void TimedConsentDefaults()
    {
        using (TimedConsentForm form = new TimedConsentForm("Lume - Clipboard request", "Share your clipboard?", "The connected guest wants to read your clipboard text. Allow this once?", "Allow once"))
        {
            Button decline = (Button)form.CancelButton;
            Check(decline != null && decline.DialogResult == DialogResult.No && form.AcceptButton == decline && form.ActiveControl == decline, "Enter, Esc or focus could approve the guest request.");
            Check(form.Icon != null && form.ShowIcon, "The consent prompt lost the Lume icon.");
        }
        using (TimedConsentForm form = new TimedConsentForm("Lume - System audio", "Share this PC's sound?", "Countdown fixture.", "Allow sound", 1))
        {
            System.Diagnostics.Stopwatch watch = System.Diagnostics.Stopwatch.StartNew();
            Check(form.ShowDialog() == DialogResult.No && watch.ElapsedMilliseconds < 5000, "An unanswered guest request was not declined automatically.");
        }
    }
    // Valid at any Windows display scale: run at 100/125/150/200% to compare. Every issue is reported at once.
    static void DpiLayout()
    {
        Console.WriteLine("System DPI " + Theme.Dpi + " (" + (Theme.Dpi * 100 / 96) + "% scale)" + (Theme.HighContrast ? ", high contrast" : ""));
        System.Collections.Generic.List<string> issues = new System.Collections.Generic.List<string>();
        using (MainForm dashboard = new MainForm())
        {
            dashboard.Show(); Application.DoEvents();
            ComputersPanel home = (ComputersPanel)Field(dashboard, "home");
            ((System.Windows.Forms.Timer)Field(home, "refresh")).Stop();
            InspectLayout(dashboard, "Dashboard", issues);
            Button update = (Button)Field(home, "update"); if (Math.Abs(update.Height - Theme.Px(Theme.ButtonHeight)) > 1) issues.Add("Computer settings column: button height " + update.Height + ", expected " + Theme.Px(Theme.ButtonHeight) + " (scaled zero or two times).");
            if (((ListBox)Field(home, "computers")).ItemHeight != Theme.Px(Theme.RowHeight)) issues.Add("Saved computers row height was not scaled.");
            FindButton(dashboard, "Guest access").PerformClick(); Application.DoEvents(); InspectLayout(dashboard, "Guest access", issues);
            dashboard.ExitDashboard(); Application.DoEvents();
        }
        using (ConsentForm form = new ConsentForm(new PeerRequest { Name = "Test computer", Address = "127.0.0.1", Control = true, Files = true })) { form.Show(); Application.DoEvents(); InspectLayout(form, "Approval", issues); form.Close(); }
        using (TimedConsentForm form = new TimedConsentForm("Lume - System audio", "Share this PC's sound?", "The connected guest wants to hear this PC's system sound. This includes sound from other applications. Allow until the session ends?", "Allow sound")) { form.Show(); Application.DoEvents(); InspectLayout(form, "Guest request", issues); form.Close(); }
        using (StreamQualityForm form = new StreamQualityForm(StreamQuality.Source, 2560, 1440, 180))
        { form.Show(); Application.DoEvents(); FindButton(form, "Custom settings").PerformClick(); Application.DoEvents(); InspectLayout(form, "Quality", issues); form.Close(); }
        using (RecordingOptionsForm form = new RecordingOptionsForm(60, true, true)) { form.Show(); Application.DoEvents(); InspectLayout(form, "Recording", issues); form.Close(); }
        using (NetworkFoldersForm form = new NetworkFoldersForm(new[] { "\\\\NAS\\Documents" })) { form.Show(); Application.DoEvents(); InspectLayout(form, "Network folders", issues); form.Close(); }
        using (SessionChatForm form = new SessionChatForm("Studio PC", delegate { return System.Threading.Tasks.Task.FromResult(0); })) { form.Show(); Application.DoEvents(); InspectLayout(form, "Chat", issues); form.Close(); }
        using (PeerHostForm form = new PeerHostForm(new PeerSignal { Session = Sample() }, null, delegate { }, delegate { })) { form.Show(); Application.DoEvents(); InspectLayout(form, "P2P sharing", issues); form.Close(); }
        Check(issues.Count == 0, String.Join(Environment.NewLine, issues));
    }
    static void InspectLayout(Control parent, string where, System.Collections.Generic.List<string> issues)
    {
        foreach (Control control in parent.Controls)
        {
            Button button = control as Button;
            if (button != null && button.Visible)
            {
                // Theme buttons are Theme.ButtonHeight px at 96 DPI; docked Fill/Left/Right buttons take their container's height.
                if (button is ReadableButton && (button.Dock == DockStyle.None || button.Dock == DockStyle.Top || button.Dock == DockStyle.Bottom) && Math.Abs(button.Height - Theme.Px(Theme.ButtonHeight)) > 1)
                    issues.Add(where + ": \"" + button.Text + "\" is " + button.Height + " px high, expected " + Theme.Px(Theme.ButtonHeight) + ".");
                Size text = TextRenderer.MeasureText(button.Text, button.Font);
                if (text.Width > button.Width || text.Height > button.Height) issues.Add(where + ": \"" + button.Text + "\" needs " + text.Width + " x " + text.Height + " px, button is " + button.Width + " x " + button.Height + ".");
            }
            InspectLayout(control, where, issues);
        }
    }
    static void ThemedControls()
    {
        using (StreamQualityForm form = new StreamQualityForm(StreamQuality.Source, 1920, 1080, 60))
        {
            form.Show(); Application.DoEvents();
            foreach (string name in new[] { "resolution", "fps", "codec" }) { ComboBox combo = (ComboBox)Field(form, name); Check(combo is ThemedComboBox && (Theme.HighContrast || (combo.BackColor == Theme.Field && combo.DrawMode == DrawMode.OwnerDrawFixed)), name + " is not a themed drop-down list."); }
            if (!Theme.HighContrast) foreach (string name in new[] { "jpeg", "bitrate" }) Check(((NumericUpDown)Field(form, name)).BackColor == Theme.Field, name + " kept the light number box.");
            form.Close();
        }
        using (Form form = new Form())
        {
            ListView list = new ListView { View = View.Details }; list.Columns.Add("Name", 100); form.Controls.Add(list); Theme.Apply(form);
            NumericUpDown later = new NumericUpDown(); Panel host = new Panel(); host.Controls.Add(later); form.Controls.Add(host);
            form.Show(); Application.DoEvents();
            // High contrast keeps native headers and system colours.
            Check(Theme.HighContrast ? !list.OwnerDraw : list.OwnerDraw && later.BackColor == Theme.Field, "Theme.Apply did not style the list header or a control added after it ran.");
            form.Close();
        }
    }
    static void MultiSessionTray()
    {
        using (ReconnectFixture fixture = new ReconnectFixture()) using (MainForm dashboard = new MainForm())
        {
            PairedLink a = fixture.Open(), b = fixture.Open();
            using (ViewerForm first = new ViewerForm(a.Invitation, a.Peer)) using (ViewerForm second = new ViewerForm(b.Invitation, b.Peer))
            {
                dashboard.Show(); first.Show(); second.Show();
                PumpUntil(delegate { return (int)Field(first, "frameCount") > 0 && (int)Field(second, "frameCount") > 0; }, 12000, "Both sessions did not receive frames.");
                dashboard.Close(); Application.DoEvents();
                Check(!dashboard.Visible && !dashboard.IsDisposed && !dashboard.ShowInTaskbar, "The X button did not hide the dashboard in the tray.");
                Check(first.Visible && second.Visible && first.IsSessionConnected && second.IsSessionConnected, "Hiding the dashboard closed or hid a session.");
                first.Close();
                PumpUntil(delegate { return !fixture.Hosts[0].HasSession; }, 3000, "The first session was not released.");
                Check(second.IsSessionConnected && fixture.Hosts[1].HasSession, "Closing one viewer ended another session.");
                dashboard.ExitDashboard(); Application.DoEvents();
                Check(second.IsDisposed && dashboard.IsDisposed, "Explicit application exit left viewer windows alive.");
            }
        }
    }
    static Button FindButton(Control parent, string text)
    {
        foreach (Control control in parent.Controls)
        {
            Button button = control as Button; if (button != null && button.Text == text) return button;
            Button found = FindButton(control, text); if (found != null) return found;
        }
        return null;
    }
    static void QualityPresetUi()
    {
        using (StreamQualityForm quality = new StreamQualityForm(StreamQuality.Source, 1920, 1080, 180))
        {
            quality.Show(); Application.DoEvents();
            FindButton(quality, "Save data").PerformClick(); FindButton(quality, "Apply").PerformClick();
            Check(quality.Selection.Height == 360 && quality.Selection.Fps == 10 && !quality.Selection.Video && !quality.Selection.Lossless, "Save data preset did not apply 360p/10 FPS.");
        }
        using (StreamQualityForm quality = new StreamQualityForm(new StreamQuality {Height = 360, Fps = 10, Lossless = false}, 2560, 1440, 180))
        {
            quality.Show(); Application.DoEvents(); FindButton(quality, "Source").PerformClick(); FindButton(quality, "Apply").PerformClick();
            Check(quality.Selection.Height == 0 && quality.Selection.Fps == 0 && quality.Selection.Lossless, "Source did not preserve dimensions, refresh and exact pixels.");
        }
        using (StreamQualityForm quality = new StreamQualityForm(StreamQuality.Source, 1920, 1080, 60, false))
        {
            quality.Show(); Application.DoEvents(); Check(!FindButton(quality, "Smooth video").Enabled, "Unsupported video preset was offered.");
            FindButton(quality, "Custom settings").PerformClick(); Application.DoEvents();
            Check(((ComboBox)Field(quality, "fps")).Visible && ((ComboBox)Field(quality, "resolution")).Visible, "Custom quality controls disappeared.");
            quality.Close();
        }
    }
    static void PreviewUi(string directory)
    {
        Directory.CreateDirectory(directory);
        using (MainForm dashboard = new MainForm())
        {
            dashboard.Show(); Application.DoEvents();
            ComputersPanel home = (ComputersPanel)Field(dashboard, "home");
            ((System.Windows.Forms.Timer)Field(home, "refresh")).Stop();
            ListBox saved = (ListBox)Field(home, "computers"); saved.Items.Clear();
            saved.Items.Add(new SavedComputer { Name = "Home office", HostId = "preview-home" });
            saved.Items.Add(new SavedComputer { Name = "Studio PC", HostId = "preview-studio" }); saved.SelectedIndex = 0;
            ((Label)Field(home, "deviceName")).Text = "This laptop";
            ((Label)Field(home, "hostSummary")).Text = "Access on - paired computers can connect";
            ((Label)Field(home, "progress")).Text = "Double-click a PC to connect. Each session opens in its own window.";
            ((Label)Field(dashboard, "status")).Text = "Ready";
            Application.DoEvents(); SaveUi(dashboard, Path.Combine(directory, "computers.png")); dashboard.ExitDashboard();
        }
        using (StreamQualityForm quality = new StreamQualityForm(StreamQuality.Source, 1920, 1080, 60))
        {
            quality.Show(); Application.DoEvents(); SaveUi(quality, Path.Combine(directory, "quality.png"));
            FindButton(quality, "Custom settings").PerformClick(); Application.DoEvents(); SaveUi(quality, Path.Combine(directory, "quality-custom.png")); quality.Close();
        }
        using (ReconnectFixture fixture = new ReconnectFixture())
        {
            PairedLink link = fixture.Open();
            using (ViewerForm viewer = new ViewerForm(link.Invitation, link.Peer))
            {
                viewer.DisplayName = "Studio PC"; viewer.Show();
                PumpUntil(delegate { return (int)Field(viewer, "frameCount") > 0; }, 10000, "Preview session had no frame.");
                SaveUi(viewer, Path.Combine(directory, "session.png")); viewer.Close();
            }
        }
        using (FilesFixture fixture = new FilesFixture()) using (FileTransferForm form = new FileTransferForm(fixture.Viewer.Files, "Studio PC"))
        {
            string folder = Path.Combine(fixture.Root, "remote");
            File.WriteAllText(Path.Combine(folder, "Project notes.txt"), "Sample project notes for the interface preview.");
            File.WriteAllBytes(Path.Combine(folder, "Sample data.bin"), new byte[102400]); Directory.CreateDirectory(Path.Combine(folder, "Images"));
            form.Show(); Application.DoEvents(); PumpUntil(delegate { return !(bool)Field(form, "loading") && ((ListView)Field(form, "entries")).Items.Count > 0; }, 5000, "Preview browser did not load.");
            System.Threading.SynchronizationContext.SetSynchronizationContext(new WindowsFormsSynchronizationContext());
            ((TextBox)Field(form, "path")).Text = folder; FindButton(form, "Go").PerformClick();
            PumpUntil(delegate { return !(bool)Field(form, "loading") && (string)Field(form, "directory") == folder; }, 5000, "Preview folder did not load.");
            ((TextBox)Field(form, "path")).Text = "C:\\Shared";
            SaveUi(form, Path.Combine(directory, "files.png")); form.Close();
        }
    }
    static void SaveUi(Form form, string path)
    { using (Bitmap image = new Bitmap(form.Width, form.Height)) { form.DrawToBitmap(image, new Rectangle(Point.Empty, form.Size)); image.Save(path, ImageFormat.Png); } }
}
