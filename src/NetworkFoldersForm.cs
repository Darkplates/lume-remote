using System;
using System.Drawing;
using System.Linq;
using System.Windows.Forms;

namespace LumeRemote
{
    internal sealed class NetworkFoldersForm : Form
    {
        public string[] Roots { get; private set; }
        public NetworkFoldersForm(string[] roots)
        {
            Theme.BeginLayout(this);
            Text = "Lume - Network folders"; Icon = Brand.Icon; Size = new Size(650, 455); MinimumSize = Size; StartPosition = FormStartPosition.CenterParent; BackColor = Theme.Background; ForeColor = Theme.Text;
            var layout = new TableLayoutPanel { Dock = DockStyle.Fill, ColumnCount = 1, RowCount = 5, Padding = new Padding(18) };
            layout.RowStyles.Add(new RowStyle(SizeType.Absolute, 70)); layout.RowStyles.Add(new RowStyle(SizeType.Percent, 100)); layout.RowStyles.Add(new RowStyle(SizeType.Absolute, 40)); layout.RowStyles.Add(new RowStyle(SizeType.Absolute, 54)); layout.RowStyles.Add(new RowStyle(SizeType.Absolute, 35));
            var explanation = Theme.Label("Paired computers can access only the network folders added here, using your Windows permissions. Changes apply to new file operations; disable access to stop an active session.", 10, Theme.Muted); explanation.Dock = DockStyle.Fill; explanation.AutoSize = false; explanation.MaximumSize = Size.Empty;
            var list = new ListBox { Dock = DockStyle.Fill, BackColor = Theme.Field, ForeColor = Theme.Text, BorderStyle = BorderStyle.None, Font = new Font("Segoe UI", 10) }; list.Items.AddRange(roots);
            var path = Theme.Box(false); path.Dock = DockStyle.Fill; path.AccessibleName = "Network folder path";
            var actions = new FlowLayoutPanel { Dock = DockStyle.Fill }; var add = Theme.Button("Add folder", false); var remove = Theme.Button("Remove", false); var save = Theme.Button("Save", true); actions.Controls.Add(add); actions.Controls.Add(remove); actions.Controls.Add(save);
            var status = Theme.Label("Example: \\\\NAS\\Documents", 10, Theme.Muted); status.Dock = DockStyle.Fill; status.AutoSize = false; status.MaximumSize = Size.Empty;
            layout.Controls.Add(explanation); layout.Controls.Add(list); layout.Controls.Add(path); layout.Controls.Add(actions); layout.Controls.Add(status); Controls.Add(layout);
            add.Click += delegate { try { string root = RemoteFileAccess.CheckShareRoot(path.Text.Trim()); if (list.Items.Count >= 32) throw new InvalidOperationException("Remove an unused folder first."); if (!list.Items.Cast<string>().Contains(root, StringComparer.OrdinalIgnoreCase)) list.Items.Add(root); path.Clear(); status.Text = "Folder added. Save to enable access."; } catch (Exception error) { status.Text = error.Message; } };
            remove.Click += delegate { if (list.SelectedIndex >= 0) list.Items.RemoveAt(list.SelectedIndex); };
            save.Click += delegate { Roots = list.Items.Cast<string>().ToArray(); DialogResult = DialogResult.OK; Close(); };
            AcceptButton = add;
            Theme.EndLayout(this);
        }
    }
}
