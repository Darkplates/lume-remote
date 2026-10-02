using System;
using System.Drawing;
using System.Threading;
using System.Threading.Tasks;
using System.Windows.Forms;

namespace LumeRemote
{
    sealed class PeerHostForm : Form
    {
        // Setup budget after the reply is applied; not a session-duration limit.
        const int HostWait = 90000;
        readonly PeerSignal offer;
        readonly PeerTransport peer;
        readonly Action connected, canceled;
        readonly TextBox reply = Theme.Box(true);
        readonly Label status = Theme.Label("Send the invitation. The reply normally arrives here automatically; if it does not, paste it below.", 11, Theme.Text);
        readonly Button apply = Theme.Button("Use reply and connect", true);
        readonly System.Windows.Forms.Timer ticker = new System.Windows.Forms.Timer { Interval = 1000 };
        SignalBroker rendezvous;
        DateTime deadline;
        bool closed, accepted;
        public string CheckCode { get; private set; }
        public PeerHostForm(PeerSignal offer, PeerTransport peer, Action connected, Action canceled)
        {
            this.offer = offer; this.peer = peer; this.connected = connected; this.canceled = canceled;
            Theme.BeginLayout(this);
            Text = "Lume - P2P / sharing computer"; Tag = "LumePeer"; Icon = Brand.Icon; Size = new Size(670, 560); MinimumSize = new Size(610, 530);
            StartPosition = FormStartPosition.CenterParent; BackColor = Theme.Background; ForeColor = Theme.Text;
            FlowLayoutPanel panel = Theme.Column(); panel.Dock = DockStyle.Fill; panel.AutoScroll = true; panel.BackColor = Theme.Background;
            Label title = Theme.Label("Connect directly over the Internet", 20, Theme.Text); title.MaximumSize = new Size(590, 0); panel.Controls.Add(title);
            Label help = Theme.Label("1. Send your private invitation to the controlling PC.\n2. Its reply returns here automatically. If it does not, paste the reply code below.\n3. You still approve access before your screen is shared.", 11, Theme.Muted); help.MaximumSize = new Size(575, 0); panel.Controls.Add(help);
            Button copy = Theme.Button("Copy P2P invitation", false); copy.Width = 240; panel.Controls.Add(copy);
            copy.Click += delegate { try { Clipboard.SetText(offer.ToString()); } catch (Exception error) { status.Text = error.Message; } };
            panel.Controls.Add(Theme.Label("REPLY FROM THE CONTROLLING PC", 10, Theme.Accent));
            reply.Width = 570; reply.Height = 100; reply.MaxLength = 65536; reply.ScrollBars = ScrollBars.Vertical; panel.Controls.Add(reply);
            apply.Width = 270; panel.Controls.Add(apply); status.MaximumSize = new Size(570, 0); panel.Controls.Add(status); Controls.Add(panel);
            Theme.EndLayout(this);
            apply.Click += async delegate { await Connect(); };
            ticker.Tick += delegate { ShowProgress(); };
            Shown += async delegate { if (peer != null && GuestRendezvous.Enabled) await Listen(); };
            FormClosing += delegate
            {
                closed = true; ticker.Stop();
                if (rendezvous != null) { rendezvous.Dispose(); rendezvous = null; }
                if (!accepted) canceled();
            };
        }
        async Task Listen()
        {
            try
            {
                SignalBroker listening = await GuestRendezvous.Listen(offer, delegate(PeerSignal response)
                {
                    try { BeginInvoke((Action)delegate { ReplyArrived(response); }); } catch (InvalidOperationException) { }
                });
                if (closed) { listening.Dispose(); return; }
                rendezvous = listening;
            }
            catch (Exception)
            {
                if (!closed && !reply.ReadOnly) status.Text = "Automatic reply delivery is unavailable right now. Paste the reply from the controlling PC below.";
            }
        }
        async void ReplyArrived(PeerSignal response)
        {
            if (closed || reply.ReadOnly) return;
            reply.Text = response.ToString();
            await Connect();
        }
        void ShowProgress()
        {
            if (closed || peer == null) return;
            int left = Math.Max(0, (int)Math.Ceiling((deadline - DateTime.UtcNow).TotalSeconds));
            status.Text = peer.Phase + " (" + (left / 60) + ":" + (left % 60).ToString("00") + " left)";
        }
        async Task Connect()
        {
            try
            {
                PeerSignal response = PeerSignal.Parse(reply.Text); offer.VerifyReply(response); apply.Enabled = false; reply.ReadOnly = true;
                CheckCode = PeerSignal.CheckCode(offer.Session, response);
                deadline = DateTime.UtcNow.AddMilliseconds(HostWait); ShowProgress(); ticker.Start();
                await Task.Run(delegate { peer.AcceptAnswer(response.Sdp); peer.WaitReady(HostWait); });
                ticker.Stop();
                if (closed) return;
                // Only a successful hand-over counts as accepted; otherwise closing stops sharing.
                connected(); accepted = true; Close();
            }
            catch (Exception error)
            {
                ticker.Stop();
                if (closed) return;
                if (reply.ReadOnly)
                {
                    status.Text = error.Message + " Close this window to stop sharing, then start sharing again for fresh codes.";
                    if (rendezvous != null) { rendezvous.Dispose(); rendezvous = null; }
                }
                else { status.Text = error.Message; apply.Enabled = true; }
            }
        }
    }

    sealed class PeerViewerForm : Form
    {
        // Setup budget for returning the reply and opening the route; it matches the
        // native ICE deadline and is not a session-duration limit.
        const int ViewerWait = 600000;
        readonly PeerSignal offer;
        PeerTransport peer;
        readonly TextBox reply = Theme.Box(true);
        readonly Label status = Theme.Label("Discovering this PC's P2P addresses...", 11, Theme.Text);
        readonly Button copy = Theme.Button("Copy reply code", true);
        readonly System.Windows.Forms.Timer ticker = new System.Windows.Forms.Timer { Interval = 1000 };
        readonly CancellationTokenSource stopped = new CancellationTokenSource();
        string note = "", checkCode;
        DateTime deadline;
        bool closed, transferred;
        public PeerViewerForm(PeerSignal offer)
        {
            if (offer.IsReply) throw new FormatException("Paste the invitation from the sharing PC, not a reply code.");
            this.offer = offer;
            Theme.BeginLayout(this);
            Text = "Lume - P2P / controlling computer"; Tag = "LumePeer"; Icon = Brand.Icon; Size = new Size(670, 505); MinimumSize = new Size(610, 475);
            StartPosition = FormStartPosition.CenterScreen; BackColor = Theme.Background; ForeColor = Theme.Text;
            FlowLayoutPanel panel = Theme.Column(); panel.Dock = DockStyle.Fill; panel.AutoScroll = true; panel.BackColor = Theme.Background;
            Label title = Theme.Label("Connecting to the sharing PC", 19, Theme.Text); title.MaximumSize = new Size(590, 0); panel.Controls.Add(title);
            Label help = Theme.Label("Lume sends the reply below to the sharing PC automatically. If that does not work, copy it and send it back yourself. Keep this window open; the desktop opens after the host approves.", 11, Theme.Muted); help.MaximumSize = new Size(570, 0); panel.Controls.Add(help);
            reply.Width = 570; reply.Height = 112; reply.ReadOnly = true; reply.ScrollBars = ScrollBars.Vertical; panel.Controls.Add(reply);
            copy.Width = 240; copy.Enabled = false; panel.Controls.Add(copy);
            copy.Click += delegate { try { Clipboard.SetText(reply.Text); note = "Reply copied. Paste it on the sharing PC, then wait here."; ShowProgress(); } catch (Exception error) { status.Text = error.Message; } };
            status.MaximumSize = new Size(570, 0); panel.Controls.Add(status); Controls.Add(panel);
            Theme.EndLayout(this);
            ticker.Tick += delegate { ShowProgress(); };
            Shown += async delegate { await Prepare(); };
            FormClosing += delegate { closed = true; ticker.Stop(); stopped.Cancel(); if (!transferred) Release(); };
        }
        // Native deletion can wait for network threads, so it never runs on the UI thread.
        void Release()
        {
            PeerTransport old = Interlocked.Exchange(ref peer, null);
            if (old != null) Task.Run(delegate { old.Dispose(); });
        }
        void ShowProgress()
        {
            if (closed) return;
            PeerTransport current = peer; if (current == null) return;
            int left = Math.Max(0, (int)Math.Ceiling((deadline - DateTime.UtcNow).TotalSeconds));
            status.Text = note + "\n" + current.Phase + " (" + (left / 60) + ":" + (left % 60).ToString("00") + " left)" +
                (checkCode == null ? "" : "\nCheck code: " + checkCode + ". The sharing PC shows the same code when it asks for approval.");
        }
        async Task Deliver(PeerSignal signal)
        {
            note = "Sending the reply to the sharing PC automatically...";
            bool delivered = false;
            try { delivered = await GuestRendezvous.Deliver(offer, signal, stopped.Token); }
            catch (Exception) { }
            if (closed) return;
            note = delivered ? "The sharing PC received the reply automatically." :
                "Automatic delivery did not reach the sharing PC. Copy the reply, send it there, then wait here.";
            ShowProgress();
        }
        async Task Prepare()
        {
            try
            {
                PeerTransport created = await Task.Run(delegate { return new PeerTransport(); });
                if (closed) { Task disposal = Task.Run(delegate { created.Dispose(); }); return; } peer = created;
                string answer = await Task.Run(delegate { return created.CreateAnswer(offer.Sdp); });
                if (closed) return;
                PeerSignal signal = offer.Reply(answer);
                reply.Text = signal.ToString(); copy.Enabled = true; checkCode = PeerSignal.CheckCode(offer.Session, signal);
                deadline = DateTime.UtcNow.AddMilliseconds(ViewerWait);
                note = "Copy this reply and return it to the sharing PC, then wait here.";
                ShowProgress(); ticker.Start();
                if (GuestRendezvous.Enabled) { Task delivery = Deliver(signal); }
                await Task.Run(delegate { created.WaitReady(ViewerWait); });
                ticker.Stop();
                if (closed) return;
                ViewerForm viewer = new ViewerForm(offer.Session, created) { ApprovalCheck = checkCode }; transferred = true; viewer.Show(); Close();
            }
            catch (Exception error)
            {
                ticker.Stop();
                if (!closed) { copy.Enabled = false; status.Text = error.Message + " Close this window and request a fresh invitation to retry."; }
                Release();
            }
        }
    }
}
