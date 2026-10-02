using System;
using System.Drawing;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using System.Windows.Forms;

namespace LumeRemote
{
    public sealed class FileTransferForm : Form
    {
        readonly FileTransfer files;
        readonly TextBox path = Theme.Box(false);
        readonly ListView entries = new ListView { Dock = DockStyle.Fill, View = View.Details, FullRowSelect = true, HideSelection = false, MultiSelect = true, BorderStyle = BorderStyle.None, Font = new Font("Segoe UI", 10), BackColor = Theme.Background, ForeColor = Theme.Text };
        readonly Label status = Theme.Label("Choose a remote folder.", 10, Theme.Muted);
        readonly ProgressBar progress = new ProgressBar { Dock = DockStyle.Bottom, Height = 6, Maximum = 1000 };
        readonly Button send = Theme.Button("Send files", true), receive = Theme.Button("Receive selected", false), cancel = Theme.DangerButton("Cancel transfer");
        readonly Button sendFolder = Theme.Button("Send folder", false);
        readonly Button print = Theme.Button("Print PDF locally", ButtonKind.Ghost);
        readonly Button previous = Theme.Button("Previous", ButtonKind.Ghost), next = Theme.Button("Next", ButtonKind.Ghost);
        readonly System.Windows.Forms.Timer timer = new System.Windows.Forms.Timer { Interval = 250 };
        CancellationTokenSource transfer;
        string directory = "";
        readonly System.Collections.Generic.HashSet<string> roots = new System.Collections.Generic.HashSet<string>(StringComparer.OrdinalIgnoreCase);
        int page, navigation;
        bool closed, loading;
        public FileTransferForm(FileTransfer files, string remoteName)
        {
            Theme.BeginLayout(this);
            this.files = files; Text = "Lume - Files on " + remoteName; Icon = Brand.Icon; Size = new Size(900, 620); MinimumSize = new Size(700, 440);
            BackColor = Theme.Background; ForeColor = Theme.Text; StartPosition = FormStartPosition.CenterParent;
            Panel top = new Panel { Dock = DockStyle.Top, Height = 62, Padding = new Padding(14, 12, 14, 10), BackColor = Theme.Card };
            Button drives = Theme.Button(files.NetworkFolders ? "Roots" : "Drives", false), up = Theme.Button("Up", false), go = Theme.Button("Go", false);
            drives.Dock = DockStyle.Left; drives.Width = 82; up.Dock = DockStyle.Left; up.Width = 64; go.Dock = DockStyle.Right; go.Width = 60; path.Dock = DockStyle.Fill;
            top.Controls.Add(path); top.Controls.Add(go); top.Controls.Add(up); top.Controls.Add(drives);
            FlowLayoutPanel actions = new FlowLayoutPanel { Dock = DockStyle.Bottom, AutoSize = true, AutoSizeMode = AutoSizeMode.GrowAndShrink, Padding = new Padding(14, 5, 14, 0), BackColor = Theme.Card };
            send.Width = 130; receive.Width = 160; cancel.Width = 145; previous.Width = next.Width = 90;
            actions.Controls.Add(send); sendFolder.Width = 135; actions.Controls.Add(sendFolder); actions.Controls.Add(receive); print.Width = 160; actions.Controls.Add(print); actions.Controls.Add(cancel); actions.Controls.Add(previous); actions.Controls.Add(next);
            Panel summary = new Panel { Dock = DockStyle.Bottom, Height = 60, Padding = new Padding(16, 8, 16, 8), BackColor = Theme.Card };
            status.Dock = DockStyle.Fill; status.AutoSize = false; status.MaximumSize = Size.Empty; summary.Controls.Add(status);
            // Column widths are not auto-scaled. The last column fills the header so no unthemed strip remains.
            entries.Columns.Add("Name", Theme.Px(490)); entries.Columns.Add("Size", Theme.Px(130)); entries.Columns.Add("Type", Theme.Px(100)); entries.AllowDrop = true;
            EventHandler fillHeader = delegate { if (entries.IsHandleCreated) entries.Columns[entries.Columns.Count - 1].Width = -2; }; entries.Resize += fillHeader; Shown += fillHeader;
            Controls.Add(entries); Controls.Add(progress); Controls.Add(summary); Controls.Add(actions); Controls.Add(top);
            Theme.EndLayout(this);
            Shown += async delegate { await Navigate(""); };
            drives.Click += async delegate { await Navigate(""); };
            up.Click += async delegate { await Navigate(directory.Length <= 3 || roots.Contains(directory.TrimEnd('\\')) ? "" : Path.GetDirectoryName(directory.TrimEnd('\\')) ?? ""); };
            go.Click += async delegate { await Navigate(path.Text.Trim()); };
            path.KeyDown += async delegate(object sender, KeyEventArgs e) { if (e.KeyCode == Keys.Enter) { e.SuppressKeyPress = true; await Navigate(path.Text.Trim()); } };
            previous.Click += async delegate { await Navigate(directory, Math.Max(0, page - 1)); };
            next.Click += async delegate { await Navigate(directory, page + 1); };
            entries.DoubleClick += async delegate
            {
                if (entries.SelectedItems.Count != 1) return; RemoteFileEntry entry = (RemoteFileEntry)entries.SelectedItems[0].Tag;
                if (entry.Directory) await Navigate(directory.Length == 0 ? entry.Name : Path.Combine(directory, entry.Name));
            };
            entries.SelectedIndexChanged += delegate { UpdateActions(); };
            send.Click += async delegate
            {
                using (OpenFileDialog dialog = new OpenFileDialog { Title = "Send files to the remote folder", Multiselect = true, CheckFileExists = true })
                    if (dialog.ShowDialog(this) == DialogResult.OK) await Send(dialog.FileNames);
            };
            sendFolder.Click += async delegate
            {
                using (FolderBrowserDialog dialog = new FolderBrowserDialog { Description = "Choose a folder to send, including its contents." })
                    if (dialog.ShowDialog(this) == DialogResult.OK) await Send(new[] { dialog.SelectedPath });
            };
            receive.Click += async delegate
            {
                string[] selected = entries.SelectedItems.Cast<ListViewItem>().Where(item => files.Folders || !((RemoteFileEntry)item.Tag).Directory).Select(item => Path.Combine(directory, ((RemoteFileEntry)item.Tag).Name)).ToArray();
                if (selected.Length == 0) return;
                using (FolderBrowserDialog dialog = new FolderBrowserDialog { Description = "Choose where to save the received files on this PC.", ShowNewFolderButton = true })
                    if (dialog.ShowDialog(this) == DialogResult.OK) await Transfer(selected, delegate(string file, CancellationToken token) { bool folder = entries.Items.Cast<ListViewItem>().Any(item => ((RemoteFileEntry)item.Tag).Directory && Path.Combine(directory, ((RemoteFileEntry)item.Tag).Name) == file); return folder ? files.DownloadFolder(file, dialog.SelectedPath, token) : files.Download(file, dialog.SelectedPath, token); });
            };
            entries.DragEnter += delegate(object sender, DragEventArgs e) { e.Effect = transfer == null && directory.Length > 0 && e.Data.GetDataPresent(DataFormats.FileDrop) ? DragDropEffects.Copy : DragDropEffects.None; };
            entries.DragDrop += async delegate(object sender, DragEventArgs e) { string[] paths = e.Data.GetData(DataFormats.FileDrop) as string[]; if (paths != null) await Send(paths); };
            cancel.Click += delegate { if (transfer != null) transfer.Cancel(); };
            print.Click += async delegate { await PrintSelected(); };
            timer.Tick += delegate
            {
                FileProgress state = files.Progress;
                if (transfer != null && state != null)
                {
                    progress.Value = state.Total == 0 ? 0 : (int)Math.Min(1000, state.Bytes / (double)state.Total * 1000);
                    status.Text = state.Direction + " " + state.Name + "\n" + SizeText(state.Bytes) + " / " + SizeText(state.Total);
                }
            }; timer.Start();
            FormClosing += delegate { closed = true; if (transfer != null) transfer.Cancel(); files.CancelAll(); timer.Stop(); };
            FormClosed += delegate { timer.Dispose(); };
            UpdateActions();
        }
        async Task Navigate(string target, int targetPage = 0)
        {
            if (closed) return; int request = ++navigation; loading = true; UpdateActions(); status.Text = "Loading remote folder...";
            try
            {
                RemoteFileList list = await files.List(target, targetPage);
                if (closed || request != navigation) return;
                directory = list.Path; page = list.Page; path.Text = directory; entries.BeginUpdate(); entries.Items.Clear();
                if (directory.Length == 0) { roots.Clear(); foreach (RemoteFileEntry item in list.Entries) roots.Add(item.Name.TrimEnd('\\')); }
                foreach (RemoteFileEntry entry in list.Entries.OrderByDescending(item => item.Directory).ThenBy(item => item.Name, StringComparer.CurrentCultureIgnoreCase))
                {
                    ListViewItem item = new ListViewItem(HostService.Visible(entry.Name)) { Tag = entry }; item.SubItems.Add(entry.Directory ? "" : SizeText(entry.Length)); item.SubItems.Add(entry.Directory ? "Folder" : "File"); entries.Items.Add(item);
                }
                entries.EndUpdate(); previous.Enabled = page > 0; next.Enabled = list.More;
                status.Text = directory.Length == 0 ? "Open a drive or configured network folder." : files.Resume ? "Interrupted files resume when retried with the same source and destination. Cancel discards the active partial file." : "Send files or folders here, or select remote items to receive. Existing items are kept.";
            }
            catch (Exception error) { if (!closed && request == navigation) status.Text = error is OperationCanceledException ? "The connection ended." : error.Message; }
            finally { if (!closed && request == navigation) { loading = false; UpdateActions(); } }
        }
        async Task Send(string[] paths)
        {
            if (closed || transfer != null || directory.Length == 0) return;
            if (!files.Folders && paths.Any(Directory.Exists)) { status.Text = "Update both PCs to transfer folders."; return; }
            string destination = directory;
            await Transfer(paths, delegate(string file, CancellationToken token) { return Directory.Exists(file) ? files.UploadFolder(file, destination, token) : files.Upload(file, destination, token); });
        }
        async Task Transfer(string[] paths, Func<string, CancellationToken, Task<string>> action)
        {
            if (transfer != null || closed) return;
            using (CancellationTokenSource batch = new CancellationTokenSource())
            {
                transfer = batch; UpdateActions(); int complete = 0; string result = "";
                try
                {
                    foreach (string file in paths) { batch.Token.ThrowIfCancellationRequested(); result = await action(file, batch.Token); complete++; }
                    if (!closed) { await Navigate(directory, page); status.Text = complete + (complete == 1 ? " item transferred.\n" : " items transferred.\n") + result; progress.Value = 1000; }
                }
                catch (Exception error) { if (!closed) status.Text = (error is OperationCanceledException ? "Transfer cancelled. Completed files and folders are kept." : error.Message) + (complete == 0 ? "" : " " + complete + " files completed."); }
                finally { transfer = null; if (!closed) UpdateActions(); }
            }
        }
        void UpdateActions()
        {
            send.Enabled = !loading && transfer == null && directory.Length > 0;
            sendFolder.Enabled = send.Enabled && files.Folders;
            receive.Enabled = !loading && transfer == null && entries.SelectedItems.Cast<ListViewItem>().Any(item => directory.Length > 0 && (files.Folders || !((RemoteFileEntry)item.Tag).Directory));
            cancel.Enabled = transfer != null;
            print.Enabled = !loading && transfer == null && directory.Length > 0 && entries.SelectedItems.Count == 1 && !((RemoteFileEntry)entries.SelectedItems[0].Tag).Directory && String.Equals(Path.GetExtension(entries.SelectedItems[0].Text), ".pdf", StringComparison.OrdinalIgnoreCase);
        }
        async Task PrintSelected()
        {
            if (!print.Enabled || closed) return;
            string remote = Path.Combine(directory, entries.SelectedItems[0].Text);
            string temporary = Path.Combine(Path.GetTempPath(), "Lume-print-" + Guid.NewGuid().ToString("N"));
            using (var request = new CancellationTokenSource())
            {
                transfer = request; UpdateActions();
                try
                {
                    Directory.CreateDirectory(temporary); status.Text = "Receiving PDF for local print preview...";
                    string document = await files.Download(remote, temporary, request.Token); request.Token.ThrowIfCancellationRequested();
                    bool submitted = await RemotePrinting.Show(this, document, request.Token);
                    if (!closed) status.Text = submitted ? "Document submitted to the selected Windows printer." : "Printing cancelled. No document was submitted.";
                }
                catch (Exception error) { if (!closed) status.Text = error is OperationCanceledException ? "Printing cancelled." : error.Message; }
                finally
                {
                    // This directory is uniquely created above, never a remote-supplied path.
                    try { if (Directory.Exists(temporary)) Directory.Delete(temporary, true); } catch (IOException) { } catch (UnauthorizedAccessException) { }
                    transfer = null; if (!closed) UpdateActions();
                }
            }
        }
        internal static string SizeText(long bytes)
        { return bytes < 1024 ? bytes + " B" : bytes < 1024 * 1024 ? (bytes / 1024.0).ToString("0.0") + " KiB" : bytes < 1024L * 1024 * 1024 ? (bytes / (1024.0 * 1024)).ToString("0.0") + " MiB" : (bytes / (1024.0 * 1024 * 1024)).ToString("0.00") + " GiB"; }
    }
}
