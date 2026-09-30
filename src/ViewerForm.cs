using System;
using System.Collections.Concurrent;
using System.Diagnostics;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Threading;
using System.Threading.Tasks;
using System.Windows.Forms;

namespace LumeRemote
{
    public sealed class ViewerForm : Form
    {
        readonly int uiThread = Thread.CurrentThread.ManagedThreadId;
        Invitation invite;
        PeerTransport peer;
        volatile ViewerConnection connection;
        readonly Func<Action<string>, CancellationToken, Task<PairedLink>> reconnect;
        readonly CancellationTokenSource closing = new CancellationTokenSource();
        readonly LatestFrameQueue presentation = new LatestFrameQueue();
        Bitmap displayImage;
        int presentationPosted;
        long decodeTicks;
        readonly RemoteCanvas canvas = new RemoteCanvas();
        readonly Panel viewport = new Panel { Dock = DockStyle.Fill, AutoScroll = true, BackColor = Theme.Canvas };
        StreamQuality desiredQuality = StreamQuality.Source;
        bool originalPixels;
        readonly Label information = Theme.Label("Connecting securely...", 10, Theme.Muted);
        readonly BlockingCollection<Action> outgoing = new BlockingCollection<Action>(256);
        readonly System.Windows.Forms.Timer timer = new System.Windows.Forms.Timer { Interval = 16 };
        readonly Stopwatch clock = Stopwatch.StartNew();
        volatile bool connected;
        bool ended, fullScreen;
        Button fullscreenButton;
        bool showDetails;
        volatile bool closed;
        int frameCount, framesAtReport;
        long lastReport, bytesAtReport;
        long noticeUntil, lastClipboardAction = -1000;
        FileTransferForm filesWindow;
        SessionChatForm chatWindow;
        SessionRecording recording;
        ClipboardSync clipboardSync;
        readonly System.Windows.Forms.Timer clipboardTimer = new System.Windows.Forms.Timer { Interval = 1200 };
        bool savingRecording, recordingBusy, closeAfterRecording;
        Task recordingFinalization;
        Task<bool> recordingSave;
        internal const int RecordingFinishTimeoutMilliseconds = 30000;
        readonly Label recordingBadge = Theme.Label("REC", 11, Color.OrangeRed);
        readonly Label microphoneBadge = Theme.Label("MIC ON", 11, Color.OrangeRed);
        ToolStripMenuItem recordMenu;
        bool changingMonitor, drawing;
        int strokeEpoch;
        readonly System.Collections.Generic.List<Point> stroke = new System.Collections.Generic.List<Point>();
        int displayedEpoch = 1;
        Point? pendingMouse;
        Rectangle previousBounds;
        FormWindowState previousState;
        public event Action<StreamQuality> QualityChanged;
        public string ComputerId { get; set; }
        internal string FileResumeKey { get; set; }
        public string DisplayName { get; set; }
        public bool IsSessionConnected { get { return connected; } }
        public ViewerForm(Invitation invite, PeerTransport peer = null, StreamQuality initialQuality = null,
            Func<Action<string>, CancellationToken, Task<PairedLink>> reconnect = null)
        {
            this.reconnect = reconnect;
            desiredQuality = initialQuality ?? StreamQuality.Source;
            Theme.BeginLayout(this);
            this.invite = invite; this.peer = peer; Text = "Lume - Remote desktop"; Size = new Size(1100, 750); MinimumSize = new Size(640, 420);
            Icon = Brand.Icon;
            canvas.StatusMessage = information.Text = ConnectionDiagnostics.Caption(peer == null ? ConnectionStage.Contacting : ConnectionStage.Securing, invite);
            StartPosition = FormStartPosition.CenterScreen; BackColor = Theme.Background; ForeColor = Theme.Text;
            FlowLayoutPanel toolbar = new FlowLayoutPanel { Dock = DockStyle.Top, AutoSize = true, AutoSizeMode = AutoSizeMode.GrowAndShrink, Padding = new Padding(10, 2, 10, 0), BackColor = Theme.Card, WrapContents = true };
            // One quiet toolbar: ghost buttons, a single danger action. No accent competes with the remote desktop.
            Button disconnect = Theme.DangerButton("Disconnect"), fullscreen = Theme.Button("Full screen", ButtonKind.Ghost), clip = Theme.Button("Send clipboard text", false), release = Theme.Button("Release keys", false);
            Button quality = Theme.Button("Quality", ButtonKind.Ghost), files = Theme.Button("Files", ButtonKind.Ghost), pixels = Theme.Button("1:1 pixels", false), more = Theme.Button("More", ButtonKind.Ghost); quality.Width = pixels.Width = 96; more.Width = files.Width = 80;
            disconnect.Width = 116; fullscreen.Width = 128; clip.Width = 176; release.Width = 124;
            PictureBox toolbarMark = Brand.Mark(24); toolbarMark.Margin = new Padding(4, 12, 12, 0); toolbarMark.AccessibleRole = AccessibleRole.Graphic; toolbarMark.AccessibleName = "Lume logo"; toolbar.Controls.Add(toolbarMark); toolbar.Controls.Add(quality); toolbar.Controls.Add(files); toolbar.Controls.Add(fullscreen); toolbar.Controls.Add(more); toolbar.Controls.Add(disconnect); recordingBadge.Visible = false; recordingBadge.Margin = new Padding(12, 13, 0, 0); toolbar.Controls.Add(recordingBadge); microphoneBadge.Visible = false; microphoneBadge.Margin = new Padding(12, 13, 0, 0); toolbar.Controls.Add(microphoneBadge);
            files.Click += delegate
            {
                ViewerConnection current = connection;
                if (!connected || current == null) { ShowNotice("Connect before opening files."); return; }
                if (current.Files == null) { ShowNotice(current.CanControl ? "Update Lume on both PCs to use file transfer." : "Files are unavailable in a view-only session."); return; }
                if (filesWindow != null && !filesWindow.IsDisposed) { filesWindow.Activate(); return; }
                filesWindow = new FileTransferForm(current.Files, DisplayName ?? current.RemoteName); filesWindow.Show(this);
            };
            quality.Click += delegate
            {
                if (!connected) { information.Text = "Connect before changing stream quality."; return; }
                using (StreamQualityForm settings = new StreamQualityForm(connection.CurrentQuality ?? desiredQuality, connection.SourceWidth, connection.SourceHeight, connection.SourceRefresh, connection.ProtocolVersion >= 3 && VideoDecoder.Available()))
                    if (settings.ShowDialog(this) == DialogResult.OK) { desiredQuality = settings.Selection; StreamQuality selected = desiredQuality; Enqueue(delegate(ViewerConnection session) { session.SetQuality(selected); }); if (QualityChanged != null) QualityChanged(desiredQuality); information.Text = "Applying " + desiredQuality.Description + "..."; }
            };
            pixels.Click += delegate { originalPixels = !originalPixels; pixels.Text = originalPixels ? "Fit to window" : "1:1 pixels"; ResizeCanvas(); };
            disconnect.Click += delegate { Close(); }; fullscreenButton = fullscreen; fullscreen.Click += delegate { ToggleFullscreen(); };
            release.Click += delegate { pendingMouse = null; Enqueue(delegate(ViewerConnection session) { session.Release(); }); canvas.Parent.Focus(); };
            clip.Click += delegate
            {
                try
                {
                    if (!connected || !connection.CanControl) { ShowNotice("Clipboard is unavailable in this session."); return; }
                    if (clock.ElapsedMilliseconds - lastClipboardAction < 1000) { ShowNotice("Clipboard request already sent. Wait a moment."); return; }
                    if (Clipboard.ContainsText())
                    {
                        string text = Clipboard.GetText();
                        if (System.Text.Encoding.UTF8.GetByteCount(text) > 262144) { ShowNotice("Text is larger than 256 KiB. Save it as a file and use Files."); return; }
                        lastClipboardAction = clock.ElapsedMilliseconds; Enqueue(delegate(ViewerConnection session) { session.SendClipboard(text); }); ShowNotice("Sending clipboard text...");
                    }
                    else ShowNotice("Your clipboard does not contain text.");
                }
                catch (Exception) { ShowNotice("Your clipboard is busy. Try again shortly."); }
            };
            Panel bottom = new Panel { Dock = DockStyle.Bottom, Height = 30, Padding = new Padding(12, 5, 12, 0), BackColor = Theme.Card };
            ContextMenuStrip menu = new ContextMenuStrip();
            menu.Items.Add("Fit / original pixels", null, delegate { pixels.PerformClick(); });
            menu.Items.Add("Send clipboard text", null, delegate { clip.PerformClick(); });
            menu.Items.Add("Get remote clipboard text", null, async delegate
            {
                ViewerConnection current = connection;
                try
                {
                    if (!connected || current == null) return;
                    string value = await current.GetClipboard();
                    if (connection != current || !connected || closed) return;
                    await ClipboardAccess.Write(value); ShowNotice("Remote text copied to your clipboard.");
                }
                catch (Exception error) { ShowNotice(error.Message); }
            });
            ToolStripMenuItem sync = new ToolStripMenuItem("Sync clipboard text") { CheckOnClick = false };
            sync.Click += delegate
            {
                if (clipboardSync != null) { clipboardSync.Dispose(); clipboardSync = null; sync.Checked = false; return; }
                try
                {
                    ViewerConnection current = connection; if (current == null || !connected) return; current.Require(SessionCapabilities.ClipboardSync);
                    if (MessageBox.Show(this, "Sync newly copied text with this paired PC until the session ends? Clipboard text may include private information.", "Lume - Clipboard sync", MessageBoxButtons.YesNo, MessageBoxIcon.Question, MessageBoxDefaultButton.Button2) != DialogResult.Yes) return;
                    clipboardSync = new ClipboardSync(ClipboardAccess.Read, async delegate(string value) { await ClipboardAccess.Write(value); }, current.GetClipboard, current.SetClipboardText, delegate { return !closed && connected && connection == current; });
                    sync.Checked = true; ShowNotice("Clipboard text sync on for this session.");
                }
                catch (Exception error) { ShowNotice(error.Message); }
            }; menu.Items.Add(sync);
            clipboardTimer.Tick += async delegate
            {
                ClipboardSync current = clipboardSync; if (current == null) { sync.Checked = false; return; }
                try { await current.Poll(); }
                catch (Exception error) { if (clipboardSync == current) { current.Dispose(); clipboardSync = null; sync.Checked = false; ShowNotice(error.Message); } }
            }; clipboardTimer.Start();
            bool audioBusy = false;
            ToolStripMenuItem audio = new ToolStripMenuItem("System audio") { CheckOnClick = false };
            audio.Click += async delegate
            {
                ViewerConnection current = connection; if (!connected || current == null) return; audio.Enabled = false; audioBusy = true;
                try { await current.SetAudio(!current.AudioEnabled); audio.Checked = current.AudioEnabled; ShowNotice(current.AudioEnabled ? "System audio on. Microphone is not shared." : "System audio off."); }
                catch (Exception error) { audio.Checked = false; ShowNotice("Audio: " + error.Message); }
                finally { audioBusy = false; if (!closed) audio.Enabled = true; }
            }; menu.Items.Add(audio);
            bool voiceBusy = false; ToolStripMenuItem voice = new ToolStripMenuItem("Start voice call");
            voice.Click += async delegate
            {
                ViewerConnection current = connection; if (!connected || current == null || voiceBusy) return; bool enabled = !current.VoiceEnabled;
                if (enabled && MessageBox.Show(this, "Share this PC's microphone for a two-way voice call? The other PC must also accept. Use headphones to avoid feedback.", "Lume - Voice call", MessageBoxButtons.YesNo, MessageBoxIcon.Question, MessageBoxDefaultButton.Button2) != DialogResult.Yes) return;
                voiceBusy = true; voice.Enabled = false;
                try { ShowNotice(enabled ? "Waiting for the other PC to accept the microphone request..." : "Ending voice call..."); await current.SetVoice(enabled); ShowNotice(current.VoiceEnabled ? "Voice call on. Use Stop voice call to turn off the microphone." : "Voice call off."); }
                catch (Exception error) { ShowNotice("Voice: " + error.Message); }
                finally { voiceBusy = false; if (!closed) { voice.Enabled = true; microphoneBadge.Visible = current.VoiceEnabled; } }
            }; menu.Items.Add(voice);
            ToolStripMenuItem record = recordMenu = new ToolStripMenuItem("Start recording (MP4)");
            record.Click += async delegate { await ToggleRecording(record); }; menu.Items.Add(record);
            ToolStripMenuItem power = new ToolStripMenuItem("Remote PC");
            foreach (PowerAction action in new[] { PowerAction.Lock, PowerAction.Restart, PowerAction.ShutDown })
            {
                PowerAction chosen = action; string label = chosen == PowerAction.ShutDown ? "Shut down" : chosen.ToString();
                power.DropDownItems.Add(label, null, async delegate
                {
                    ViewerConnection current = connection;
                    try
                    {
                        if (current == null || !connected) return; current.Require(SessionCapabilities.Power);
                        string prompt = label + " the remote PC?" + (chosen == PowerAction.Lock ? "" : " Save work first. The connection will end; Windows may wait for applications to close.");
                        if (MessageBox.Show(this, prompt, "Lume - " + label, MessageBoxButtons.YesNo, MessageBoxIcon.Warning, MessageBoxDefaultButton.Button2) != DialogResult.Yes) return;
                        if (!await FinishRecording()) return; await current.Power(chosen); ShowNotice(label + " requested. The session will reconnect when the host is available.");
                    }
                    catch (Exception error) { ShowNotice(error.Message); }
                });
            }
            menu.Items.Add(power);
            ToolStripMenuItem draw = new ToolStripMenuItem("Draw on remote screen");
            draw.Click += async delegate
            {
                try
                {
                    ViewerConnection current = connection; if (current == null || !connected) return; current.Require(SessionCapabilities.Annotations);
                    ReleaseInput(); drawing = !drawing; draw.Checked = drawing; canvas.Cursor = drawing ? Cursors.Cross : Cursors.Default; stroke.Clear(); canvas.Stroke = null;
                    if (!drawing) await current.Annotate(new Point[0], current.MonitorEpoch);
                    ShowNotice(drawing ? "Draw mode. Drag to mark the remote screen; switch Draw off to control it again. Marks expire after 30 seconds." : "Draw mode off.");
                }
                catch (Exception error) { ShowNotice(error.Message); }
            }; menu.Items.Add(draw);
            menu.Items.Add("Switch display", null, async delegate { await ChooseMonitor(); });
            menu.Items.Add("Chat", null, delegate { try { ShowChat(true); } catch (Exception error) { ShowNotice(error.Message); } });
            menu.Items.Add("Release keys", null, delegate { release.PerformClick(); });
            ToolStripMenuItem diagnostics = new ToolStripMenuItem("Connection details") { CheckOnClick = true };
            diagnostics.CheckedChanged += delegate { showDetails = diagnostics.Checked; bottom.Height = Theme.Px(showDetails ? 70 : 30); lastReport = 0; Tick(); }; menu.Items.Add(diagnostics);
            menu.Opening += delegate
            {
                ViewerConnection current = connection; SessionCapabilities capabilities = current == null ? SessionCapabilities.None : current.Capabilities;
                audio.Checked = current != null && current.AudioEnabled; audio.Enabled = connected && !audioBusy && (capabilities & SessionCapabilities.Audio) != 0;
                voice.Text = current != null && current.VoiceEnabled ? "Stop voice call" : "Start voice call"; voice.Enabled = connected && !voiceBusy && (capabilities & SessionCapabilities.Voice) != 0;
                sync.Checked = clipboardSync != null; sync.Enabled = connected && (capabilities & SessionCapabilities.ClipboardSync) != 0;
                draw.Checked = drawing; draw.Enabled = connected && (capabilities & SessionCapabilities.Annotations) != 0;
                power.Enabled = connected && (capabilities & SessionCapabilities.Power) != 0;
                record.Enabled = connected && MediaNative.Available && !recordingBusy && !savingRecording && (recordingFinalization == null || recordingFinalization.IsCompleted);
                record.Text = recording == null ? "Start recording (MP4)" : "Stop recording and save";
            };
            more.Click += delegate { menu.Show(more, new Point(0, more.Height)); };
            information.AutoSize = false; information.MaximumSize = Size.Empty; information.Dock = DockStyle.Fill; bottom.Controls.Add(information);
            canvas.Dock = DockStyle.Fill; canvas.Frame = delegate { return displayImage; };
            viewport.Controls.Add(canvas);
            canvas.KeyInput += delegate(byte action, int key, int extended) { if (CanSendInput()) QueueInput(action, key, extended); };
            canvas.MouseMove += delegate(object sender, MouseEventArgs e) { Point? mapped = Map(e.Location); if (drawing) { if (mapped.HasValue && canvas.Capture) AddStroke(mapped.Value); return; } if (mapped.HasValue && CanSendInput()) pendingMouse = mapped; };
            canvas.MouseDown += delegate(object sender, MouseEventArgs e)
            {
                Point? mapped = Map(e.Location);
                if (drawing) { if (mapped.HasValue && connected && e.Button == MouseButtons.Left) { stroke.Clear(); strokeEpoch = connection.MonitorEpoch; canvas.Capture = true; AddStroke(mapped.Value); } return; }
                if (!mapped.HasValue || !CanSendInput()) return;
                canvas.Focus(); canvas.Capture = true; SendPosition(mapped.Value); int button = ButtonNumber(e.Button);
                if (button >= 0) QueueInput(1, button, 0);
            };
            canvas.MouseUp += async delegate(object sender, MouseEventArgs e)
            {
                if (drawing)
                {
                    canvas.Capture = false; ViewerConnection current = connection; Point[] points = stroke.ToArray(); stroke.Clear(); canvas.Stroke = null; canvas.Invalidate();
                    try { if (current != null && connected && points.Length >= 2) await current.Annotate(points, strokeEpoch); } catch (Exception error) { ShowNotice(error.Message); }
                    return;
                }
                int button = ButtonNumber(e.Button); Point? mapped = Map(e.Location); if (mapped.HasValue) SendPosition(mapped.Value);
                if (button >= 0 && CanSendInput()) QueueInput(2, button, 0);
                canvas.Capture = false;
            };
            canvas.MouseWheel += delegate(object sender, MouseEventArgs e) { int delta = Math.Max(-1200, Math.Min(1200, e.Delta)); if (CanSendInput()) QueueInput(3, delta, 0); };
            canvas.LostFocus += delegate { ReleaseInput(); }; Deactivate += delegate { ReleaseInput(); };
            Controls.Add(viewport); Controls.Add(bottom); Controls.Add(toolbar);
            Theme.EndLayout(this);
            Shown += async delegate { await Run(); };
            FormClosing += async delegate(object sender, FormClosingEventArgs args)
            {
                if (savingRecording) { args.Cancel = true; closeAfterRecording = true; return; }
                if (recording != null || recordingFinalization != null)
                { args.Cancel = true; closeAfterRecording = true; await FinishRecording(); return; }
                closed = true; connected = false; clipboardTimer.Stop(); if (clipboardSync != null) { clipboardSync.Dispose(); clipboardSync = null; } closing.Cancel(); timer.Stop(); if (connection != null) connection.Dispose(); if (peer != null) peer.Dispose(); outgoing.CompleteAdding(); SessionLog.Write(SessionLog.UserDirectory, "viewer", "closed_locally"); };
            FormClosed += delegate { presentation.Dispose(); if (displayImage != null) { displayImage.Dispose(); displayImage = null; } timer.Dispose(); clipboardTimer.Dispose(); menu.Dispose(); pixels.Dispose(); clip.Dispose(); release.Dispose(); };
            timer.Tick += delegate { ViewerConnection current = connection; files.Enabled = connected && current != null && current.Files != null; microphoneBadge.Visible = connected && current != null && current.VoiceEnabled; Tick(); }; timer.Start();
            BackgroundWork.Run(delegate
            {
                foreach (Action action in outgoing.GetConsumingEnumerable()) { if (closed) break; action(); }
            });
            Microsoft.Win32.SystemEvents.PowerModeChanged += PowerChanged;
            FormClosed += delegate { Microsoft.Win32.SystemEvents.PowerModeChanged -= PowerChanged; };
        }
        async Task Run()
        {
            bool established = false;
            while (!closed)
            {
                ViewerConnection current = new ViewerConnection { FileResumeKey = FileResumeKey }; connection = current;
                current.SystemAudioReceived = delegate(byte[] pcm) { SessionRecording activeRecording = Volatile.Read(ref recording); if (activeRecording != null) activeRecording.PublishAudio(pcm); };
                current.ChatReceived = delegate(string text)
                {
                    TaskCompletionSource<bool> delivered = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
                    if (closed) { delivered.TrySetCanceled(); return delivered.Task; }
                    OnUi(delegate { try { if (connection != current || closed) throw new OperationCanceledException(); ShowChat(false); chatWindow.Add(DisplayName ?? current.RemoteName, text); delivered.TrySetResult(true); } catch (Exception error) { delivered.TrySetException(error); } });
                    return delivered.Task;
                };
                Exception failure = null;
                try
                {
                    await Task.Run(delegate
                    {
                        if (peer == null) current.Connect(invite, Environment.MachineName, UpdateProgress);
                        else current.ConnectPeer(peer, invite, Environment.MachineName, UpdateProgress);
                        current.SetQuality(desiredQuality);
                    });
                    if (closed) return;
                    established = connected = true; canvas.SessionEnded = false;
                    frameCount = framesAtReport = 0; decodeTicks = bytesAtReport = 0; lastReport = clock.ElapsedMilliseconds;
                    Text = "Lume - " + (DisplayName ?? current.RemoteName) + (current.CanControl ? "" : " - View only");
                    information.Text = reconnect == null ? "Connected. Ctrl + Alt + Shift + Escape releases input." : "Connected. Automatic reconnection is on. Disconnect stops it.";
                    SessionLog.Write(SessionLog.UserDirectory, "viewer", "connected");
                    await BackgroundWork.Run(delegate
                    {
                        using (FrameDecoder decoder = new FrameDecoder())
                        {
                            current.Receive(delegate(Packet packet)
                            {
                                if (closed) return;
                                long started = Stopwatch.GetTimestamp(); decoder.Apply(packet);
                                if (decoder.TakeVideoFailure() && !closed) VideoDecodeFailed(current);
                                if (decoder.FrameReady && !closed)
                                {
                                    SessionRecording capture = Volatile.Read(ref recording); if (capture != null) capture.Publish(decoder.Image);
                                    presentation.Publish((Bitmap)decoder.Image.Clone(), current.FrameEpoch);
                                    Interlocked.Increment(ref frameCount);
                                    Interlocked.Add(ref decodeTicks, Stopwatch.GetTimestamp() - started);
                                    if (Interlocked.Exchange(ref presentationPosted, 1) == 0)
                                        OnUi(delegate { PresentLatest(); });
                                }
                                if (!closed) current.Ack(decoder.Sequence);
                            }, delegate(string message) { OnUi(delegate { if (connection == current) ShowNotice(message); }); },
                            delegate(int x, int y, int shape) { OnUi(delegate { if (connection == current) { canvas.Pointer = new Point(x, y); canvas.PointerShape = shape; canvas.Invalidate(); } }); });
                        }
                    });
                    failure = current.Failure;
                }
                catch (Exception error) { failure = current.Failure ?? error; }
                finally { drawing = false; canvas.Cursor = Cursors.Default; stroke.Clear(); canvas.Stroke = null; if (clipboardSync != null) { clipboardSync.Dispose(); clipboardSync = null; } SessionRecording capture = Interlocked.Exchange(ref recording, null); if (capture != null) { TrackRecordingFinalization(capture.Stop()); Task cleanup = recordingFinalization.ContinueWith(delegate(Task done) { OnUi(delegate { recordingBadge.Visible = false; if (recordMenu != null) recordMenu.Text = "Start recording (MP4)"; }); if (done.IsFaulted) { var error = done.Exception; OnUi(delegate { ShowNotice("Recording could not be saved."); }); } }); } connected = false; pendingMouse = null; current.Dispose(); connection = null; if (filesWindow != null && !filesWindow.IsDisposed) filesWindow.Close(); if (chatWindow != null && !chatWindow.IsDisposed) chatWindow.Close(); if (peer != null) { peer.Dispose(); peer = null; } }
                if (closed) return;
                SessionLog.Write(SessionLog.UserDirectory, "viewer", SessionLog.Reason(failure), failure);
                Bitmap stale = presentation.Take(); if (stale != null) stale.Dispose();
                if (reconnect == null || !established || !PairedReconnect.Transient(failure))
                { EndSession(failure == null ? "The host ended the session." : ConnectionDiagnostics.Failure(current.Stage, invite, failure)); return; }
                canvas.SessionEnded = true; Text = "Lume - Reconnecting";
                try
                {
                    PairedLink link = await PairedReconnect.Connect(reconnect, ReconnectProgress, closing.Token);
                    if (closed) { link.Dispose(); return; }
                    invite = link.Invitation; peer = link.Peer;
                    SessionLog.Write(SessionLog.UserDirectory, "viewer", "new_route_ready");
                }
                catch (Exception error) { if (!closed) EndSession(error.Message); return; }
            }
        }
        void OnUi(Action action)
        { if (closed || IsDisposed) return; try { BeginInvoke((Action)delegate { if (!closed && !IsDisposed) action(); }); } catch (InvalidOperationException) { } }
        // Called on the receive worker. The frame is still acknowledged by the caller, and the host sends a complete image
        // after the quality change. The fallback also replaces a saved video quality so reconnects do not repeat the failure.
        void VideoDecodeFailed(ViewerConnection current)
        {
            StreamQuality fallback = (desiredQuality ?? current.CurrentQuality ?? StreamQuality.Source).Copy(); fallback.Video = false; fallback.Lossless = true;
            desiredQuality = fallback; current.SetQuality(fallback.Copy());
            SessionLog.Write(SessionLog.UserDirectory, "viewer", "video_decode_fallback");
            OnUi(delegate { if (QualityChanged != null) QualityChanged(fallback); ShowNotice("H.264 decoding failed on this PC. Switched to lossless images."); });
        }
        void ReconnectProgress(string message) { OnUi(delegate { information.Text = canvas.StatusMessage = message; canvas.Invalidate(); }); }
        void PowerChanged(object sender, Microsoft.Win32.PowerModeChangedEventArgs args)
        {
            SessionLog.Write(SessionLog.UserDirectory, "viewer", args.Mode == Microsoft.Win32.PowerModes.Resume ? "power_resume" : args.Mode == Microsoft.Win32.PowerModes.Suspend ? "power_suspend" : "power_status_changed");
            if (args.Mode == Microsoft.Win32.PowerModes.Resume && reconnect != null)
            { ViewerConnection current = connection; if (current != null) current.Abort(new System.IO.IOException("Windows resumed. Establishing a fresh connection.")); }
        }
        void PresentLatest()
        {
            Interlocked.Exchange(ref presentationPosted, 0);
            if (closed) { presentation.Dispose(); return; }
            int epoch; Bitmap next = presentation.Take(out epoch); if (next == null) return; displayedEpoch = epoch;
            Bitmap old = displayImage; displayImage = next; ResizeCanvas(); canvas.Invalidate(); if (old != null) old.Dispose();
        }
        internal void TrackRecordingFinalization(Task pending)
        {
            recordingFinalization = pending; RecordingFinalizationJobs.Track(pending);
        }
        // UI thread only: a recording is running or its file is still being written.
        internal bool RecordingPending { get { return savingRecording || recording != null || recordingFinalization != null && !recordingFinalization.IsCompleted; } }
        internal Task<bool> FinishRecording(int timeoutMilliseconds = RecordingFinishTimeoutMilliseconds)
        {
            if (recordingSave != null && !recordingSave.IsCompleted) return recordingSave;
            SessionRecording capture = Interlocked.Exchange(ref recording, null); if (capture != null) TrackRecordingFinalization(capture.Stop());
            Task pending = recordingFinalization; if (pending == null) return Task.FromResult(true);
            savingRecording = true; return recordingSave = FinishRecording(pending, timeoutMilliseconds);
        }
        async Task<bool> FinishRecording(Task pending, int timeoutMilliseconds)
        {
            bool finished = false; string message;
            try
            {
                if (!pending.IsCompleted && await Task.WhenAny(pending, Task.Delay(Math.Max(1, timeoutMilliseconds))).ConfigureAwait(false) != pending)
                    message = "Recording is still being saved. Lume will stay open; try closing again shortly.";
                else { finished = true; await pending.ConfigureAwait(false); message = "Recording saved."; }
            }
            catch (Exception error) { finished = true; message = "Recording: " + error.Message; }
            await RecordingUi.Run(this, uiThread, delegate
            {
                if (finished && recordingFinalization == pending) recordingFinalization = null;
                savingRecording = false; ShowNotice(message); recordingBadge.Visible = false; if (recordMenu != null) recordMenu.Text = "Start recording (MP4)";
                bool closeRequested = closeAfterRecording; closeAfterRecording = false;
                if (finished && closeRequested) OnUi(Close);
            }).ConfigureAwait(false);
            return finished;
        }
        async Task ToggleRecording(ToolStripMenuItem item)
        {
            if (recordingBusy || savingRecording || RecordingFinalizationJobs.IsExiting || recordingFinalization != null && !recordingFinalization.IsCompleted) return; recordingBusy = true; item.Enabled = false;
            try
            {
                SessionRecording capture = Interlocked.Exchange(ref recording, null);
                if (capture != null) { TrackRecordingFinalization(capture.Stop()); await FinishRecording(); return; }
                if (!connected || displayImage == null) throw new InvalidOperationException("Connect before recording.");
                ViewerConnection current = connection; int recordingRate; bool recordAudio;
                using (var options = new RecordingOptionsForm(current.SourceRefresh, MediaNative.Version >= 2 && (current.Capabilities & SessionCapabilities.Audio) != 0, current.AudioEnabled))
                { if (options.ShowDialog(this) != DialogResult.OK) return; recordingRate = options.FramesPerSecond; recordAudio = options.IncludeAudio; }
                using (SaveFileDialog dialog = new SaveFileDialog { Title = "Record this session", Filter = "MP4 video|*.mp4", FileName = "Lume session " + DateTime.Now.ToString("yyyy-MM-dd HH-mm-ss") + ".mp4", AddExtension = true, OverwritePrompt = true })
                {
                    if (dialog.ShowDialog(this) != DialogResult.OK) return;
                    if (recordAudio && !current.AudioEnabled) await current.SetAudio(true);
                    if (!connected || current != connection || displayImage == null || closed || RecordingFinalizationJobs.IsExiting) throw new OperationCanceledException("The connection changed or Lume is closing before recording started.");
                    capture = new SessionRecording(dialog.FileName, displayImage.Width, displayImage.Height, recordingRate, recordAudio); RecordingFinalizationJobs.Track(capture.Completion); recording = capture;
                    try { await capture.Ready; if (closed || !connected || recording != capture) throw new OperationCanceledException(); capture.Publish(displayImage); recordingBadge.Visible = true;
                        Task observation = capture.Completion.ContinueWith(delegate(Task done) { if (done.IsFaulted) { var problem = done.Exception; OnUi(delegate { if (Interlocked.CompareExchange(ref recording, null, capture) == capture) { recordingBadge.Visible = false; item.Text = "Start recording (MP4)"; ShowNotice("Recording stopped: " + problem.GetBaseException().Message); } }); } }); }
                    catch { Interlocked.CompareExchange(ref recording, null, capture); capture.Dispose(); throw; }
                    ShowNotice(recordAudio ? "Recording video and remote system audio. Stop recording in More to save." : "Recording video to MP4. Stop recording in More to save.");
                }
            }
            catch (Exception error) { ShowNotice("Recording: " + error.Message); }
            finally { recordingBusy = false; if (!closed) { item.Enabled = true; recordingBadge.Visible = recording != null; item.Text = recording == null ? "Start recording (MP4)" : "Stop recording and save"; } }
        }
        bool CanSendInput() { ViewerConnection current = connection; return connected && current != null && current.CanControl && !changingMonitor && !drawing && displayedEpoch == current.MonitorEpoch; }
        void AddStroke(Point point)
        {
            if (stroke.Count >= 128) for (int i = stroke.Count - 2; i > 0; i -= 2) stroke.RemoveAt(i);
            stroke.Add(point); canvas.Stroke = stroke.ToArray(); canvas.Invalidate();
        }
        void ShowChat(bool activate)
        {
            ViewerConnection current = connection; if (current == null) throw new InvalidOperationException("Connect before opening chat."); current.Require(SessionCapabilities.Chat);
            if (chatWindow == null || chatWindow.IsDisposed)
            {
                chatWindow = new SessionChatForm(DisplayName ?? current.RemoteName, current.SendChat);
                if (activate) chatWindow.Show(this); else chatWindow.ShowPassive(this);
            }
            else if (activate) chatWindow.Activate();
        }
        async Task ChooseMonitor()
        {
            ViewerConnection current = connection;
            try
            {
                if (!connected || current == null || changingMonitor) return;
                RemoteMonitor[] monitors = await current.GetMonitors(); if (current != connection || closed) return;
                using (Form dialog = new Form { Text = "Lume - Display", Icon = Brand.Icon, FormBorderStyle = FormBorderStyle.FixedDialog, MinimizeBox = false, MaximizeBox = false, StartPosition = FormStartPosition.CenterParent, BackColor = Theme.Background, ForeColor = Theme.Text })
                {
                    Theme.BeginLayout(dialog); dialog.Size = new Size(540, 175); dialog.Padding = new Padding(18);
                    ComboBox choices = Theme.Combo(); choices.Dock = DockStyle.Top; choices.Items.AddRange(monitors); choices.SelectedIndex = 0;
                    for (int i = 0; i < monitors.Length; i++) if (monitors[i].Selected) choices.SelectedIndex = i;
                    Button apply = Theme.Button("Use display", true); apply.Dock = DockStyle.Bottom; apply.DialogResult = DialogResult.OK; dialog.Controls.Add(choices); dialog.Controls.Add(apply); dialog.AcceptButton = apply; Theme.EndLayout(dialog);
                    if (dialog.ShowDialog(this) != DialogResult.OK) return;
                    changingMonitor = true; pendingMouse = null;
                    await current.SelectMonitor(((RemoteMonitor)choices.SelectedItem).Id);
                    ShowNotice("Display changed.");
                }
            }
            catch (Exception error) { ShowNotice(error.Message); }
            finally { changingMonitor = false; }
        }
        void UpdateProgress(ConnectionStage stage)
        {
            if (closed || ended || IsDisposed) return;
            if (InvokeRequired) { try { BeginInvoke((Action)delegate { UpdateProgress(stage); }); } catch (InvalidOperationException) { } return; }
            information.Text = canvas.StatusMessage = ConnectionDiagnostics.Caption(stage, invite); canvas.Invalidate();
        }
        void EndSession(string reason)
        {
            if (closed || ended || IsDisposed) return;
            if (InvokeRequired) { try { BeginInvoke((Action)delegate { EndSession(reason); }); } catch { } return; }
            ended = true; connected = false; pendingMouse = null; if (connection != null) connection.Dispose(); timer.Stop();
            canvas.SessionEnded = true; canvas.StatusMessage = "Connection ended\n\n" + reason;
            information.Text = "Disconnected: " + reason; Text = "Lume - Disconnected"; canvas.Invalidate();
        }
        void QueueInput(byte action, int a, int b) { ViewerConnection current = connection; if (!CanSendInput() || current == null) return; int epoch = current.MonitorEpoch; Enqueue(delegate(ViewerConnection session) { session.Input(action, a, b, epoch); }); }
        void Enqueue(Action<ViewerConnection> action)
        {
            if (closed || !connected) return;
            ViewerConnection current = connection;
            if (current == null) return;
            if (!outgoing.TryAdd(delegate
            {
                if (closed || !connected || current != connection) return;
                try { action(current); } catch (Exception error) { current.Abort(error); }
            })) current.Abort(new System.IO.IOException("The input queue is full. Reconnecting to release keys."));
        }
        void ReleaseInput() { pendingMouse = null; if (connected) Enqueue(delegate(ViewerConnection session) { session.Release(); }); }
        void ShowNotice(string message) { information.Text = message; noticeUntil = clock.ElapsedMilliseconds + 8000; }
        void SendPosition(Point point) { pendingMouse = null; QueueInput(0, point.X, point.Y); }
        static int ButtonNumber(MouseButtons button) { return button == MouseButtons.Left ? 0 : button == MouseButtons.Right ? 1 : button == MouseButtons.Middle ? 2 : -1; }
        Point? Map(Point p)
        {
            Rectangle r = canvas.ImageRectangle();
            if (r.Width < 1 || r.Height < 1) return null;
            if (!r.Contains(p) && !canvas.Capture) return null;
            return new Point((int)Math.Max(0, Math.Min(65535, (long)(p.X - r.X) * 65535 / Math.Max(1, r.Width - 1))), (int)Math.Max(0, Math.Min(65535, (long)(p.Y - r.Y) * 65535 / Math.Max(1, r.Height - 1))));
        }
        void Tick()
        {
            if (!connected) return;
            if (pendingMouse.HasValue) SendPosition(pendingMouse.Value);
            long now = clock.ElapsedMilliseconds;
            if (now - lastReport >= 2000)
            {
                long bytes = connection.Received; double seconds = (now - lastReport) / 1000.0;
                StreamQuality actual = connection.CurrentQuality ?? desiredQuality;
                int totalFrames = Volatile.Read(ref frameCount), frames = totalFrames - framesAtReport;
                double decodeMs = Interlocked.Exchange(ref decodeTicks, 0) * 1000.0 / Stopwatch.Frequency / Math.Max(1, frames);
                StreamMetrics metrics = connection.Metrics;
                string codecName = actual.Video ? "H.264" + (metrics == null ? "" : metrics.Hardware ? " / hardware" : " / software") : actual.Lossless ? "lossless" : "JPEG " + actual.JpegQuality;
                if (now >= noticeUntil) information.Text = String.Format(System.Globalization.CultureInfo.InvariantCulture, "{0} x {1}  /  {2}  /  {3}", connection.StreamWidth, connection.StreamHeight, frames == 0 ? "Idle" : (frames / seconds).ToString("0", System.Globalization.CultureInfo.InvariantCulture) + " updates/s", connection.CanControl ? "Connected" : "View only");
                if (showDetails && now >= noticeUntil)
                {
                    information.Text += String.Format(System.Globalization.CultureInfo.InvariantCulture, "  |  {0}  |  {1}  |  {2:0.00} Mbit/s  |  RTT {3} ms", codecName, actual.Fps == 0 ? "target " + connection.SourceRefresh + " Hz" : actual.Fps < 0 ? "uncapped" : "target " + actual.Fps + " FPS", (bytes - bytesAtReport) * 8.0 / seconds / 1000000, connection.RttMilliseconds);
                    information.Text += metrics == null ? "\nUnchanged pixels are skipped. Low updates on a still screen are normal." : String.Format(System.Globalization.CultureInfo.InvariantCulture, "\n{0} | capture {1:0.0} ms / encode {2:0.0} ms / decode {3:0.0} ms / ACK wait {4:0.0} ms", metrics.Idle ? "Idle - saving resources" : "Unchanged pixels skipped", metrics.CaptureMilliseconds, metrics.EncodeMilliseconds, decodeMs, metrics.WaitMilliseconds);
                }
                lastReport = now; bytesAtReport = bytes; framesAtReport = totalFrames;
            }
        }
        void ResizeCanvas()
        {
            canvas.OriginalPixels = originalPixels;
            if (originalPixels && displayImage != null)
            { canvas.Dock = DockStyle.None; if (canvas.Size != displayImage.Size) canvas.Size = displayImage.Size; viewport.AutoScrollMinSize = displayImage.Size; }
            else { viewport.AutoScrollMinSize = Size.Empty; canvas.Dock = DockStyle.Fill; }
            canvas.Invalidate();
        }
        void ToggleFullscreen()
        {
            if (!fullScreen) { previousBounds = Bounds; previousState = WindowState; WindowState = FormWindowState.Normal; FormBorderStyle = FormBorderStyle.None; Bounds = Screen.FromControl(this).Bounds; fullScreen = true; }
            else { FormBorderStyle = FormBorderStyle.Sizable; Bounds = previousBounds; WindowState = previousState; fullScreen = false; }
            if (fullscreenButton != null) fullscreenButton.Text = fullScreen ? "Exit full screen" : "Full screen";
        }
        protected override bool ProcessCmdKey(ref Message message, Keys keyData)
        {
            if (keyData == (Keys.Control | Keys.Alt | Keys.Shift | Keys.Escape)) { ReleaseInput(); if (fullScreen) ToggleFullscreen(); Focus(); return true; }
            return base.ProcessCmdKey(ref message, keyData);
        }
        protected override void Dispose(bool disposing)
        {
            if (disposing)
            {
                SessionRecording capture = Interlocked.Exchange(ref recording, null);
                if (capture != null) TrackRecordingFinalization(capture.Stop());
                if (!closed)
                {
                    closed = true; connected = false; closing.Cancel(); outgoing.CompleteAdding();
                    if (clipboardSync != null) { clipboardSync.Dispose(); clipboardSync = null; }
                    if (connection != null) connection.Dispose(); if (peer != null) peer.Dispose();
                    presentation.Dispose(); if (displayImage != null) { displayImage.Dispose(); displayImage = null; }
                }
                timer.Dispose(); clipboardTimer.Dispose(); Microsoft.Win32.SystemEvents.PowerModeChanged -= PowerChanged;
            }
            base.Dispose(disposing);
        }
    }

    // Native writers are background jobs. Their lifetime must survive a viewer being disposed,
    // and shutdown must never abandon a writer merely because a bounded wait expired.
    internal static class RecordingFinalizationJobs
    {
        static readonly object gate = new object();
        static readonly System.Collections.Generic.HashSet<Task> pending = new System.Collections.Generic.HashSet<Task>();
        static int exitWaiters;
        internal static bool IsExiting { get { lock (gate) return exitWaiters != 0; } }
        internal static void BeginExit() { lock (gate) exitWaiters++; }
        internal static bool HasPending { get { lock (gate) { pending.RemoveWhere(job => job.IsCompleted); return pending.Count != 0; } } }
        internal static void EndExit() { lock (gate) exitWaiters--; }
        internal static void Track(Task completion)
        {
            if (completion == null) throw new ArgumentNullException("completion");
            lock (gate) if (!pending.Add(completion)) return;
            completion.ContinueWith(delegate(Task done)
            {
                if (done.IsFaulted) { var observed = done.Exception; }
                lock (gate) pending.Remove(done);
            }, CancellationToken.None, TaskContinuationOptions.ExecuteSynchronously, TaskScheduler.Default);
        }
        internal static async Task<bool> WaitForPending(int timeoutMilliseconds)
        {
            Stopwatch elapsed = Stopwatch.StartNew();
            while (true)
            {
                Task[] snapshot; lock (gate) { pending.RemoveWhere(job => job.IsCompleted); snapshot = new Task[pending.Count]; pending.CopyTo(snapshot); }
                if (snapshot.Length == 0) return true;
                Task all = Task.WhenAll(snapshot);
                Task observation = all.ContinueWith(delegate(Task done) { var observed = done.Exception; }, CancellationToken.None, TaskContinuationOptions.OnlyOnFaulted | TaskContinuationOptions.ExecuteSynchronously, TaskScheduler.Default);
                int remaining = Math.Max(0, timeoutMilliseconds - (int)Math.Min(Int32.MaxValue, elapsed.ElapsedMilliseconds));
                if (!all.IsCompleted && (remaining == 0 || await Task.WhenAny(all, Task.Delay(remaining)).ConfigureAwait(false) != all)) return false;
                try { await all.ConfigureAwait(false); } catch (Exception) { } // Completed failures and cancellation no longer hold shutdown open.
            }
        }
    }

    // Do not depend on the ambient WinForms synchronization context: disposing the last
    // viewer can remove it while dashboard/recording continuations are still pending.
    internal static class RecordingUi
    {
        internal static Task<bool> Run(Form owner, int uiThread, Action action)
        {
            var result = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
            EventHandler disposed = null;
            disposed = delegate { owner.Disposed -= disposed; result.TrySetResult(false); };
            Action run = delegate
            {
                try { if (owner.IsDisposed) result.TrySetResult(false); else { action(); result.TrySetResult(true); } }
                catch (Exception error) { result.TrySetException(error); }
                finally { owner.Disposed -= disposed; }
            };
            owner.Disposed += disposed;
            if (owner.IsDisposed) disposed(owner, EventArgs.Empty);
            else if (Thread.CurrentThread.ManagedThreadId == uiThread) run();
            else
            {
                try { owner.BeginInvoke(run); }
                catch (InvalidOperationException) { owner.Disposed -= disposed; result.TrySetResult(false); }
            }
            return result.Task;
        }
    }

    sealed class RemoteCanvas : Control
    {
        public Func<Bitmap> Frame;
        public Point Pointer;
        public Point[] Stroke;
        public int PointerShape;
        public string StatusMessage = "Preparing the connection...";
        public bool SessionEnded;
        public bool OriginalPixels;
        public event Action<byte, int, int> KeyInput;
        public RemoteCanvas() { DoubleBuffered = true; BackColor = Theme.Canvas; TabStop = true; SetStyle(ControlStyles.Selectable, true); }
        public Rectangle ImageRectangle()
        {
            Bitmap image = Frame == null ? null : Frame(); if (image == null) return Rectangle.Empty;
            if (OriginalPixels) return new Rectangle(Point.Empty, image.Size);
            double scale = Math.Min((double)ClientSize.Width / image.Width, (double)ClientSize.Height / image.Height);
            int w = Math.Max(1, (int)(image.Width * scale)), h = Math.Max(1, (int)(image.Height * scale));
            return new Rectangle((ClientSize.Width - w) / 2, (ClientSize.Height - h) / 2, w, h);
        }
        protected override void OnPaint(PaintEventArgs e)
        {
            base.OnPaint(e); Bitmap image = Frame == null ? null : Frame();
            if (image == null)
            {
                DrawStatus(e.Graphics);
            }
            else
            {
                Rectangle r = ImageRectangle(); if (OriginalPixels) e.Graphics.DrawImageUnscaled(image, Point.Empty); else { e.Graphics.InterpolationMode = InterpolationMode.HighQualityBilinear; e.Graphics.DrawImage(image, r); }
                if (PointerShape != 0)
                {
                    Cursor pointer = PointerShape == 2 ? Cursors.IBeam : PointerShape == 3 ? Cursors.Hand : PointerShape == 4 ? Cursors.WaitCursor : Cursors.Arrow;
                    int x = r.X + (int)((long)Pointer.X * (r.Width - 1) / 65535), y = r.Y + (int)((long)Pointer.Y * (r.Height - 1) / 65535);
                    pointer.Draw(e.Graphics, new Rectangle(x - pointer.HotSpot.X, y - pointer.HotSpot.Y, pointer.Size.Width, pointer.Size.Height));
                }
                if (Stroke != null) AnnotationWire.Draw(e.Graphics, r, Stroke);
                if (SessionEnded)
                {
                    using (Brush shade = new SolidBrush(Color.FromArgb(225, Theme.Canvas))) e.Graphics.FillRectangle(shade, ClientRectangle);
                    DrawStatus(e.Graphics);
                }
            }
        }
        void DrawStatus(Graphics graphics)
        {
            Rectangle area = new Rectangle(Theme.Px(36), Theme.Px(24), Math.Max(1, ClientSize.Width - Theme.Px(72)), Math.Max(1, ClientSize.Height - Theme.Px(48)));
            using (Font font = new Font("Segoe UI", 14)) TextRenderer.DrawText(graphics, StatusMessage, font, area, Theme.Text,
                TextFormatFlags.HorizontalCenter | TextFormatFlags.VerticalCenter | TextFormatFlags.WordBreak | TextFormatFlags.NoPrefix);
        }
        protected override bool IsInputKey(Keys keyData) { return true; }
        protected override bool ProcessCmdKey(ref Message message, Keys keyData)
        {
            if (keyData == (Keys.Control | Keys.Alt | Keys.Shift | Keys.Escape)) return base.ProcessCmdKey(ref message, keyData);
            if ((message.Msg == 0x100 || message.Msg == 0x104) && KeyInput != null) { KeyInput(4, message.WParam.ToInt32() & 255, (int)(((long)message.LParam >> 24) & 1)); return true; }
            return base.ProcessCmdKey(ref message, keyData);
        }
        protected override void WndProc(ref Message message)
        {
            if (message.Msg == 0x100 || message.Msg == 0x101 || message.Msg == 0x104 || message.Msg == 0x105)
            {
                if (KeyInput != null) KeyInput((byte)(message.Msg == 0x101 || message.Msg == 0x105 ? 5 : 4), message.WParam.ToInt32() & 255, (int)(((long)message.LParam >> 24) & 1));
                return;
            }
            if (message.Msg == 0x102 || message.Msg == 0x106) return;
            base.WndProc(ref message);
        }
    }
}
