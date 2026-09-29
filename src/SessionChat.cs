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
        readonly ChatHistory history = new ChatHistory(); readonly TextBox message = Theme.Box(true);
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
            Theme.BeginLayout(this);
            this.send = send; Text = "Lume - Chat - " + peer; Icon = Brand.Icon;
            Size = new Size(460, 480); MinimumSize = new Size(350, 320); BackColor = Theme.Background; ForeColor = Theme.Text;
            Padding = new Padding(16); history.Dock = DockStyle.Fill; history.AccessibleName = "Chat messages";
            message.Dock = DockStyle.Bottom; message.Height = 75; message.MaxLength = 2048;
            Button submit = Theme.Button("Send", true); submit.Dock = DockStyle.Bottom; message.AccessibleName = "Message";
            submit.Click += async delegate { await Send(submit); };
            message.KeyDown += async delegate(object sender, KeyEventArgs e) { if (e.KeyCode == Keys.Enter && !e.Shift) { e.SuppressKeyPress = true; if (submit.Enabled) await Send(submit); } };
            status.Dock = DockStyle.Bottom; status.Height = 30;
            Controls.Add(history); Controls.Add(message); Controls.Add(submit); Controls.Add(status);
            Theme.EndLayout(this);
        }
        async Task Send(Button submit)
        {
            string text = message.Text.Trim(); if (text.Length == 0) return; submit.Enabled = false;
            try { await send(text); AddEntry("You", text, true); message.Clear(); status.Text = "Delivered"; }
            catch (Exception error) { status.Text = error.Message; }
            finally { if (!IsDisposed) submit.Enabled = true; }
        }
        // Remote names are claimed by the peer, so ownership is never inferred from the name.
        public void Add(string sender, string value) { AddEntry(sender, value, false); }
        void AddEntry(string sender, string value, bool mine)
        {
            if (value.Length > 8192) throw new InvalidOperationException("Chat message is too large.");
            lines.Enqueue(sender + ": " + value); while (lines.Count > 100) lines.Dequeue();
            history.Add(sender, value, mine);
            if (IsHandleCreated && !ContainsFocus) { var flash = new FlashInfo { Window = Handle, Flags = 3 | 12, Count = 0, Timeout = 0 }; flash.Size = (uint)Marshal.SizeOf(flash); FlashWindowEx(ref flash); }
        }
    }

    // Chat bubbles: mine on the right in the accent tint, theirs on the left. Bounded like the message queue.
    sealed class ChatHistory : Panel
    {
        sealed class Entry { public string Sender, Text, Time; public bool Mine; }
        readonly List<Entry> entries = new List<Entry>();
        public ChatHistory() { SetStyle(ControlStyles.AllPaintingInWmPaint | ControlStyles.OptimizedDoubleBuffer | ControlStyles.UserPaint | ControlStyles.ResizeRedraw, true); AutoScroll = true; BackColor = Theme.Background; Font = new Font(Theme.FontName, 10); }
        public void Add(string sender, string text, bool mine)
        {
            entries.Add(new Entry { Sender = sender, Text = text, Mine = mine, Time = DateTime.Now.ToString("HH:mm") }); while (entries.Count > 100) entries.RemoveAt(0);
            AccessibleDescription = sender + ": " + text; Relayout(); AutoScrollPosition = new Point(0, AutoScrollMinSize.Height); Invalidate();
        }
        protected override void OnResize(EventArgs e) { base.OnResize(e); Relayout(); }
        int BubbleWidth { get { return Math.Max(Theme.Px(120), (int)((ClientSize.Width - Theme.Px(24)) * 0.78)); } }
        Size Measure(Entry entry) { return TextRenderer.MeasureText(entry.Text, Font, new Size(BubbleWidth - Theme.Px(24), 0), TextFormatFlags.WordBreak | TextFormatFlags.NoPrefix); }
        void Relayout() { int height = Theme.Px(8); foreach (Entry entry in entries) height += Measure(entry).Height + Theme.Px(50); AutoScrollMinSize = new Size(0, height); }
        protected override void OnPaint(PaintEventArgs e)
        {
            Graphics g = e.Graphics; g.Clear(BackColor); g.SmoothingMode = System.Drawing.Drawing2D.SmoothingMode.AntiAlias; g.TranslateTransform(0, AutoScrollPosition.Y);
            int y = Theme.Px(8);
            using (Font small = new Font(Theme.FontName, 8.5f))
                foreach (Entry entry in entries)
                {
                    Size text = Measure(entry); int width = Math.Min(BubbleWidth, text.Width + Theme.Px(24)), x = entry.Mine ? ClientSize.Width - width - Theme.Px(8) : Theme.Px(8);
                    TextRenderer.DrawText(g, (entry.Mine ? "You" : entry.Sender) + "  " + entry.Time, small, new Rectangle(x + Theme.Px(4), y, width, Theme.Px(18)), Theme.Muted, (entry.Mine ? TextFormatFlags.Right : TextFormatFlags.Left) | TextFormatFlags.NoPrefix | TextFormatFlags.EndEllipsis);
                    Rectangle bubble = new Rectangle(x, y + Theme.Px(20), width, text.Height + Theme.Px(16));
                    using (System.Drawing.Drawing2D.GraphicsPath path = Theme.Rounded(bubble, Theme.Px(10))) using (SolidBrush fill = new SolidBrush(entry.Mine ? Theme.AccentSoft : Theme.Card)) g.FillPath(fill, path);
                    TextRenderer.DrawText(g, entry.Text, Font, new Rectangle(bubble.X + Theme.Px(12), bubble.Y + Theme.Px(8), bubble.Width - Theme.Px(24), text.Height), Theme.Text, TextFormatFlags.WordBreak | TextFormatFlags.NoPrefix);
                    y = bubble.Bottom + Theme.Px(14);
                }
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
