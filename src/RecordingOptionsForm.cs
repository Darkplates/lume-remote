using System;
using System.Drawing;
using System.Windows.Forms;

namespace LumeRemote
{
    internal sealed class RecordingOptionsForm : Form
    {
        public int FramesPerSecond { get; private set; }
        public bool IncludeAudio { get; private set; }
        public RecordingOptionsForm(int sourceRate, bool audioAvailable, bool audioEnabled)
        {
            Text = "Lume - Recording"; Icon = Brand.Icon; Size = new Size(460, 315); FormBorderStyle = FormBorderStyle.FixedDialog; MaximizeBox = MinimizeBox = false; StartPosition = FormStartPosition.CenterParent; BackColor = Theme.Background; ForeColor = Theme.Text; AutoScaleMode = AutoScaleMode.Dpi;
            var layout = Theme.Column(); layout.Dock = DockStyle.Fill; layout.Padding = new Padding(20);
            layout.Controls.Add(Theme.Label("Recording frame rate", 11, Theme.Text));
            var rate = new NumericUpDown { Minimum = 1, Maximum = 1000, Value = 30, Width = 200, Font = new Font("Segoe UI", 11) }; layout.Controls.Add(rate);
            var source = new CheckBox { Text = "Use source refresh target (" + sourceRate + " FPS)", AutoSize = true, ForeColor = Theme.Text }; layout.Controls.Add(source); source.CheckedChanged += delegate { rate.Enabled = !source.Checked; };
            var sound = new CheckBox { Text = "Include remote system audio", AutoSize = true, ForeColor = Theme.Text, Enabled = audioAvailable, Checked = audioAvailable && audioEnabled }; layout.Controls.Add(sound);
            var note = Theme.Label("The target does not guarantee captured FPS. Recording keeps the initial canvas size. Higher rates use more CPU and disk.", 10, Theme.Muted); note.MaximumSize = new Size(390, 0); layout.Controls.Add(note);
            var start = Theme.Button("Choose file", true); start.Width = 160; layout.Controls.Add(start); start.Click += delegate { FramesPerSecond = source.Checked ? Math.Max(1, Math.Min(1000, sourceRate)) : (int)rate.Value; IncludeAudio = sound.Checked; DialogResult = DialogResult.OK; Close(); }; Controls.Add(layout); AcceptButton = start;
        }
    }
}
