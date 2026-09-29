using System;
using System.Drawing;
using System.Threading;
using System.Windows.Forms;

namespace LumeRemote
{
    // A small always-on-top pill on the host's screen while someone is connected: who it is,
    // whether they can control the PC, and a Disconnect button. It never takes keyboard focus.
    internal sealed class SessionIndicator : IDisposable
    {
        readonly object gate = new object();
        Form form; bool disposed;
        SessionIndicator(string name, bool control, Action disconnect)
        {
            Thread thread = new Thread(delegate() { Run(name, control, disconnect); }) { IsBackground = true, Name = "Lume session indicator" };
            thread.SetApartmentState(ApartmentState.STA); thread.Start();
        }
        public static IDisposable Show(string name, bool control, Action disconnect) { return new SessionIndicator(name, control, disconnect); }
        void Run(string name, bool control, Action disconnect)
        {
            try
            {
                using (IndicatorForm window = new IndicatorForm(name, control))
                {
                    window.Disconnect += delegate { try { disconnect(); } catch (Exception) { } window.Close(); };
                    lock (gate) { if (disposed) return; form = window; window.CreateControl(); var handle = window.Handle; }
                    Application.Run(window);
                }
            }
            catch (Exception) { }
            finally { lock (gate) form = null; }
        }
        public void Dispose()
        {
            Form current; lock (gate) { if (disposed) return; disposed = true; current = form; }
            if (current != null) try { current.BeginInvoke((Action)current.Close); } catch (InvalidOperationException) { }
        }

        sealed class IndicatorForm : Form
        {
            public event Action Disconnect;
            readonly string text;
            protected override bool ShowWithoutActivation { get { return true; } }
            protected override CreateParams CreateParams { get { CreateParams value = base.CreateParams; value.ExStyle |= 0x08000000 | 0x00000080; return value; } } // WS_EX_NOACTIVATE | WS_EX_TOOLWINDOW
            public IndicatorForm(string name, bool control)
            {
                Theme.BeginLayout(this);
                text = (String.IsNullOrEmpty(name) ? "A computer" : name) + (control ? " is connected" : " is viewing");
                FormBorderStyle = FormBorderStyle.None; ShowInTaskbar = false; TopMost = true; StartPosition = FormStartPosition.Manual; BackColor = Theme.Card; ForeColor = Theme.Text;
                Font = new Font(Theme.FontNameStrong, 9.5f); Size = new Size(380, 48); Padding = new Padding(8);
                AccessibleName = text; AccessibleRole = AccessibleRole.Alert; Text = "Lume - " + text;
                Button stop = Theme.DangerButton("Disconnect"); stop.Width = 116; stop.Height = 32; stop.Dock = DockStyle.Right; stop.Margin = new Padding(0);
                stop.Click += delegate { Action handler = Disconnect; if (handler != null) handler(); };
                Controls.Add(stop);
                Theme.EndLayout(this);
                Rectangle area = Screen.PrimaryScreen.WorkingArea; Location = new Point(area.Left + (area.Width - Width) / 2, area.Top + Theme.Px(8));
            }
            protected override void OnPaint(PaintEventArgs e)
            {
                e.Graphics.SmoothingMode = System.Drawing.Drawing2D.SmoothingMode.AntiAlias;
                using (Pen edge = new Pen(Theme.Border)) e.Graphics.DrawRectangle(edge, 0, 0, Width - 1, Height - 1);
                int dot = Theme.Px(10); using (SolidBrush live = new SolidBrush(Theme.Success)) e.Graphics.FillEllipse(live, Theme.Px(16), (Height - dot) / 2, dot, dot);
                TextRenderer.DrawText(e.Graphics, text, Font, new Rectangle(Theme.Px(36), 0, Width - Theme.Px(170), Height), Theme.Text, TextFormatFlags.VerticalCenter | TextFormatFlags.EndEllipsis | TextFormatFlags.NoPrefix);
            }
        }
    }
}
