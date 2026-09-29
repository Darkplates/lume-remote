using System;
using System.Drawing;
using System.Threading.Tasks;
using System.Windows.Forms;

namespace LumeRemote
{
    sealed class PeerHostForm : Form
    {
        readonly PeerSignal offer;
        readonly PeerTransport peer;
        readonly Action connected, canceled;
        readonly TextBox reply = Theme.Box(true);
        readonly Label status = Theme.Label("Send the invitation, then paste the reply returned by the controlling PC.", 11, Theme.Text);
        readonly Button apply = Theme.Button("Use reply and connect", true);
        bool closed, accepted;
        public PeerHostForm(PeerSignal offer, PeerTransport peer, Action connected, Action canceled)
        {
            this.offer = offer; this.peer = peer; this.connected = connected; this.canceled = canceled;
            Theme.BeginLayout(this);
            Text = "Lume - P2P / sharing computer"; Tag = "LumePeer"; Icon = Brand.Icon; Size = new Size(670, 560); MinimumSize = new Size(610, 530);
            StartPosition = FormStartPosition.CenterParent; BackColor = Theme.Background; ForeColor = Theme.Text;
            FlowLayoutPanel panel = Theme.Column(); panel.Dock = DockStyle.Fill; panel.AutoScroll = true; panel.BackColor = Theme.Background;
            Label title = Theme.Label("Connect directly over the Internet", 20, Theme.Text); title.MaximumSize = new Size(590, 0); panel.Controls.Add(title);
            Label help = Theme.Label("1. Send your private invitation to the controlling PC.\n2. That PC returns a reply code. Paste it below.\n3. You still approve access before your screen is shared.", 11, Theme.Muted); help.MaximumSize = new Size(575, 0); panel.Controls.Add(help);
            Button copy = Theme.Button("Copy P2P invitation", false); copy.Width = 240; panel.Controls.Add(copy);
            copy.Click += delegate { try { Clipboard.SetText(offer.ToString()); } catch (Exception error) { status.Text = error.Message; } };
            panel.Controls.Add(Theme.Label("REPLY FROM THE CONTROLLING PC", 10, Theme.Accent));
            reply.Width = 570; reply.Height = 100; reply.MaxLength = 65536; reply.ScrollBars = ScrollBars.Vertical; panel.Controls.Add(reply);
            apply.Width = 270; panel.Controls.Add(apply); status.MaximumSize = new Size(570, 0); panel.Controls.Add(status); Controls.Add(panel);
            Theme.EndLayout(this);
            apply.Click += async delegate { await Connect(); };
            FormClosing += delegate { closed = true; if (!accepted) canceled(); };
        }
        async Task Connect()
        {
            try
            {
                PeerSignal response = PeerSignal.Parse(reply.Text); offer.VerifyReply(response); apply.Enabled = false; reply.ReadOnly = true;
                status.Text = "Negotiating a direct P2P route through both routers...";
                await Task.Run(delegate { peer.AcceptAnswer(response.Sdp); peer.WaitReady(90000); });
                if (closed) return;
                accepted = true; connected(); Close();
            }
            catch (Exception error)
            {
                if (closed) return;
                status.Text = error.Message + " Stop sharing and create fresh codes if this attempt expired.";
                if (!reply.ReadOnly) apply.Enabled = true;
            }
        }
    }

    sealed class PeerViewerForm : Form
    {
        readonly PeerSignal offer;
        PeerTransport peer;
        readonly TextBox reply = Theme.Box(true);
        readonly Label status = Theme.Label("Discovering this PC's P2P addresses...", 11, Theme.Text);
        readonly Button copy = Theme.Button("Copy reply code", true);
        bool closed, transferred;
        public PeerViewerForm(PeerSignal offer)
        {
            if (offer.IsReply) throw new FormatException("Paste the invitation from the sharing PC, not a reply code.");
            this.offer = offer;
            Theme.BeginLayout(this);
            Text = "Lume - P2P / controlling computer"; Tag = "LumePeer"; Icon = Brand.Icon; Size = new Size(670, 505); MinimumSize = new Size(610, 475);
            StartPosition = FormStartPosition.CenterScreen; BackColor = Theme.Background; ForeColor = Theme.Text;
            FlowLayoutPanel panel = Theme.Column(); panel.Dock = DockStyle.Fill; panel.AutoScroll = true; panel.BackColor = Theme.Background;
            Label title = Theme.Label("Return this reply to the sharing PC", 19, Theme.Text); title.MaximumSize = new Size(590, 0); panel.Controls.Add(title);
            Label help = Theme.Label("Send the reply below back to the sharing PC. Paste it there and click Use reply and connect. Keep this window open; the desktop opens automatically after the host approves.", 11, Theme.Muted); help.MaximumSize = new Size(570, 0); panel.Controls.Add(help);
            reply.Width = 570; reply.Height = 112; reply.ReadOnly = true; reply.ScrollBars = ScrollBars.Vertical; panel.Controls.Add(reply);
            copy.Width = 240; copy.Enabled = false; panel.Controls.Add(copy);
            copy.Click += delegate { try { Clipboard.SetText(reply.Text); status.Text = "Reply copied. Paste it on the sharing PC, then wait here."; } catch (Exception error) { status.Text = error.Message; } };
            status.MaximumSize = new Size(570, 0); panel.Controls.Add(status); Controls.Add(panel);
            Theme.EndLayout(this);
            Shown += async delegate { await Prepare(); };
            FormClosing += delegate { closed = true; if (!transferred && peer != null) peer.Dispose(); };
        }
        async Task Prepare()
        {
            try
            {
                PeerTransport created = await Task.Run(delegate { return new PeerTransport(); });
                if (closed) { created.Dispose(); return; } peer = created;
                string answer = await Task.Run(delegate { return peer.CreateAnswer(offer.Sdp); });
                if (closed) return;
                reply.Text = offer.Reply(answer).ToString(); copy.Enabled = true; status.Text = "Copy this reply and return it to the sharing PC. Waiting for its P2P negotiation...";
                await Task.Run(delegate { peer.WaitReady(180000); });
                if (closed) return;
                ViewerForm viewer = new ViewerForm(offer.Session, peer); transferred = true; viewer.Show(); Close();
            }
            catch (Exception error)
            {
                if (peer != null) peer.Dispose();
                if (!closed) { copy.Enabled = false; status.Text = error.Message + " Close this window and request a fresh invitation to retry."; }
            }
        }
    }
}
