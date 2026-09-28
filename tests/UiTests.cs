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
