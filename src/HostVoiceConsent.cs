using System;
using System.Drawing;
using System.Threading;
using System.Threading.Tasks;
using System.Windows.Forms;

namespace LumeRemote
{
    // A microphone grant exists only while its visible local stop control exists.
    internal sealed class HostVoiceConsent : IDisposable
    {
        readonly TaskCompletionSource<IDisposable> approved = new TaskCompletionSource<IDisposable>(TaskCreationOptions.RunContinuationsAsynchronously);
        readonly Action stopped;
        readonly object gate = new object();
        Form form; bool disposed, granted;
        CancellationTokenRegistration registration;
        HostVoiceConsent(Action stopped, CancellationToken cancellation)
        {
            this.stopped = stopped; registration = cancellation.Register(Dispose);
            Thread thread = new Thread(Run) { IsBackground = true, Name = "Lume microphone consent" }; thread.SetApartmentState(ApartmentState.STA); thread.Start();
        }
        public static Task<IDisposable> Request(Action stopped, CancellationToken cancellation) { return new HostVoiceConsent(stopped, cancellation).approved.Task; }
        void Run()
        {
            try
            {
                using (var window = new Form { Text = "Lume - Voice call", Icon = Brand.Icon, Size = new Size(455, 240), FormBorderStyle = FormBorderStyle.FixedDialog, MaximizeBox = false, MinimizeBox = false, TopMost = true, StartPosition = FormStartPosition.CenterScreen, BackColor = Theme.Background, ForeColor = Theme.Text, AutoScaleMode = AutoScaleMode.Dpi })
                {
                    var text = Theme.Label("The connected computer requests a two-way voice call. Allow this PC's microphone for this call? Use headphones to avoid feedback.", 11, Theme.Text); text.Dock = DockStyle.Fill; text.AutoSize = false; text.MaximumSize = Size.Empty;
                    var buttons = new FlowLayoutPanel { Dock = DockStyle.Bottom, Height = 55 }; var allow = Theme.Button("Allow call", true); var stop = Theme.Button("Decline", false); allow.Width = stop.Width = 170; buttons.Controls.Add(allow); buttons.Controls.Add(stop); window.Padding = new Padding(18); window.Controls.Add(text); window.Controls.Add(buttons);
                    allow.Click += delegate { lock (gate) { if (disposed || granted) return; granted = true; } allow.Visible = false; stop.Text = "Stop microphone"; text.Text = "Microphone ON for this Lume call. Stop sharing at any time."; approved.TrySetResult(this); };
                    stop.Click += delegate { window.Close(); };
                    window.FormClosed += delegate { bool notify; lock (gate) { notify = granted && !disposed; disposed = true; form = null; } approved.TrySetResult(null); if (notify) stopped(); };
                    lock (gate) { if (disposed) return; form = window; window.CreateControl(); var handle = window.Handle; }
                    Application.Run(window);
                }
            }
            catch (Exception error) { approved.TrySetException(error); }
            finally { approved.TrySetResult(null); registration.Dispose(); }
        }
        public void Dispose()
        {
            Form current; lock (gate) { if (disposed) return; disposed = true; current = form; }
            approved.TrySetResult(null); if (current != null) try { current.BeginInvoke((Action)current.Close); } catch (InvalidOperationException) { }
        }
    }
}
