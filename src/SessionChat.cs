using System;
using System.Collections.Generic;
using System.Drawing;
using System.Runtime.InteropServices;
using System.Threading;
using System.Threading.Tasks;
using System.Windows.Forms;

namespace LumeRemote
{
    public sealed class SessionChatForm : Form
    {
        readonly TextBox history = Theme.Box(true), message = Theme.Box(true);
        readonly Queue<string> lines = new Queue<string>();
        readonly Func<string, Task> send;
        readonly Label status = Theme.Label("Messages stay in this session.", 9, Theme.Muted);
        bool passive;
        // Incoming messages must not take keyboard focus from the remote desktop or a password field.
        protected override bool ShowWithoutActivation { get { return passive; } }
        [DllImport("user32.dll")] static extern bool FlashWindowEx(ref FlashInfo info);
        [StructLayout(LayoutKind.Sequential)] struct FlashInfo { public uint Size; public IntPtr Window; public uint Flags, Count, Timeout; }
        public void ShowPassive(IWin32Window owner)
        {
            passive = true;
            try { if (owner != null) Show(owner); else Show(); }
            finally { passive = false; }
        }
        public SessionChatForm(string peer, Func<string, Task> send)
        {
            this.send = send; Text = "Lume - Chat - " + peer; Icon = Brand.Icon;
            Size = new Size(460, 480); MinimumSize = new Size(350, 320); BackColor = Theme.Background; ForeColor = Theme.Text;
            Padding = new Padding(16); history.ReadOnly = true; history.Dock = DockStyle.Fill; history.ScrollBars = ScrollBars.Vertical;
            message.Dock = DockStyle.Bottom; message.Height = 75; message.MaxLength = 2048;
            Button submit = Theme.Button("Send message", true); submit.Dock = DockStyle.Bottom;
            submit.Click += async delegate { await Send(submit); };
            message.KeyDown += async delegate(object sender, KeyEventArgs e) { if (e.KeyCode == Keys.Enter && !e.Shift) { e.SuppressKeyPress = true; if (submit.Enabled) await Send(submit); } };
            status.Dock = DockStyle.Bottom; status.Height = 30;
            Controls.Add(history); Controls.Add(message); Controls.Add(submit); Controls.Add(status);
        }
        async Task Send(Button submit)
        {
            string text = message.Text.Trim(); if (text.Length == 0) return; submit.Enabled = false;
            try { await send(text); Add("You", text); message.Clear(); status.Text = "Delivered"; }
            catch (Exception error) { status.Text = error.Message; }
            finally { if (!IsDisposed) submit.Enabled = true; }
        }
        public void Add(string sender, string value)
        {
            if (value.Length > 8192) throw new InvalidOperationException("Chat message is too large.");
            lines.Enqueue(sender + ": " + value); while (lines.Count > 100) lines.Dequeue();
            history.Text = String.Join(Environment.NewLine + Environment.NewLine, lines.ToArray());
            history.SelectionStart = history.TextLength; history.ScrollToCaret();
            if (IsHandleCreated && !ContainsFocus) { var flash = new FlashInfo { Window = Handle, Flags = 3 | 12, Count = 0, Timeout = 0 }; flash.Size = (uint)Marshal.SizeOf(flash); FlashWindowEx(ref flash); }
        }
    }

    // The service's interactive worker has no main form. Give its chat a dedicated STA.
    public sealed class HostSessionChat : IDisposable
    {
        readonly object gate = new object();
        readonly Func<string, Task> send;
        readonly string peer;
        readonly TaskCompletionSource<Control> ready = new TaskCompletionSource<Control>(TaskCreationOptions.RunContinuationsAsynchronously);
        Thread thread;
        Control dispatcher;
        SessionChatForm form;
        volatile bool disposed;
        public HostSessionChat(string peer, Func<string, Task> send) { this.peer = peer; this.send = send; }
        public async Task Receive(string text)
        {
            lock (gate)
            {
                if (disposed) throw new OperationCanceledException();
                if (thread == null)
                {
                    thread = new Thread(delegate()
                    {
                        try
                        {
                            using (Control control = new Control())
                            {
                                control.CreateControl(); dispatcher = control; ready.TrySetResult(control);
                                Application.Run();
                            }
                        }
                        catch (Exception error) { ready.TrySetException(error); }
                    }) { IsBackground = true, Name = "Lume session chat" };
                    thread.SetApartmentState(ApartmentState.STA); thread.Start();
                }
            }
            Control target = await ready.Task.ConfigureAwait(false);
            TaskCompletionSource<bool> shown = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
            target.BeginInvoke((Action)delegate
            {
                try
                {
                    if (disposed) throw new OperationCanceledException();
                    if (form == null || form.IsDisposed) { form = new SessionChatForm(peer, send); form.ShowPassive(null); }
                    form.Add(peer, text); shown.TrySetResult(true);
                }
                catch (Exception error) { shown.TrySetException(error); }
            });
            await shown.Task.ConfigureAwait(false);
        }
        public void Dispose()
        {
            lock (gate) { disposed = true; }
            ready.Task.ContinueWith(delegate(Task<Control> task)
            {
                if (task.IsFaulted) { var observed = task.Exception; return; }
                try { task.Result.BeginInvoke((Action)delegate { if (form != null) form.Close(); Application.ExitThread(); }); } catch (InvalidOperationException) { }
            });
        }
    }
}
