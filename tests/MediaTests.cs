using System;
using System.Drawing;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using LumeRemote;

static partial class Tests
{
    static byte[] PortablePcm(){byte[] pcm=new byte[19200];for(int i=0;i<pcm.Length;i++)pcm[i]=(byte)(i%251);return pcm;}
    static void PortableAudioWrite(string path){byte[] encoded=AudioBlock.Encode(PortablePcm(),19200);using(var stream=new FileStream(path,FileMode.CreateNew,FileAccess.Write))using(var writer=new BinaryWriter(stream)){writer.Write((byte)20);writer.Write(13);writer.Write(encoded.Length);writer.Write(encoded);}Console.WriteLine("PASS Windows PCM fixture encoded.");}
    static void PortableAudioRead(string path){using(var reader=new BinaryReader(File.OpenRead(path))){Check(reader.ReadByte()==21&&reader.ReadInt32()==13,"Portable PCM envelope changed.");int count=reader.ReadInt32();Check(count>=5&&count<=19456,"Portable PCM size invalid.");byte[] encoded=reader.ReadBytes(count);Check(reader.BaseStream.Position==reader.BaseStream.Length,"Portable PCM has trailing data.");Check(PortablePcm().SequenceEqual(AudioBlock.Decode(encoded)),"Portable PCM bytes differ.");}Console.WriteLine("PASS Windows independently decoded all portable PCM bytes.");}
    static void MediaChecks()
    {
        Run("Lossless audio preserves stereo samples and rejects decompression bombs", AudioSafety);
        Run("Recording produces an H.264 MP4 with a finalized movie index", RecordingFile);
        Run("Recording keeps existing files and rejects unsupported dimensions", RecordingSafety);
        Run("Dashboard exit finalizes an active recording before closing viewers", RecordingExit);
        Run("Viewer close waits for an already pending recording finalization", RecordingPendingClose);
        Run("Recording timeout keeps the viewer open and completed errors allow close", RecordingCloseTimeout);
        Run("Dashboard exit retains recording jobs after their viewer is disposed", RecordingDisposedViewerExit);
        Run("Disconnect and immediate viewer close preserve the finalized synthetic MP4", RecordingDisconnectClose);
        Run("Audio timeline preserves stereo alignment and bounded silence gaps", RecordingAudioSafety);
        Run("MP4 recording contains H.264 video and AAC system audio", RecordingWithAudio);
        Run("Voice consent denial and cancellation leave video running and microphones off", VoiceConsentSafety);
        Run("Native MP4 preserves all 180 submitted images in its sample table", RecordingHighRate);
    }
    static void AudioSafety()
    {
        byte[] pcm = new byte[AudioBlock.Maximum]; for (int i = 0; i < pcm.Length / 2; i++) { short value = (short)(12000 * Math.Sin(i * 0.1)); pcm[i * 2] = (byte)value; pcm[i * 2 + 1] = (byte)(value >> 8); }
        byte[] block = AudioBlock.Encode(pcm, pcm.Length); Check(pcm.SequenceEqual(AudioBlock.Decode(block)), "Audio samples changed.");
        Reject(delegate { AudioBlock.Encode(pcm, 3); }); Reject(delegate { AudioBlock.Decode(new byte[AudioBlock.Maximum + 257]); });
        byte[] bad = (byte[])block.Clone(); Buffer.BlockCopy(BitConverter.GetBytes(4), 0, bad, 0, 4); Reject(delegate { AudioBlock.Decode(bad); });
        Buffer.BlockCopy(BitConverter.GetBytes(Int32.MaxValue), 0, bad, 0, 4); Reject(delegate { AudioBlock.Decode(bad); });
        Console.WriteLine("AUDIO: exact 48 kHz stereo PCM16 round trip; hardware capture/playback not invoked.");
    }
    static void RecordingFile()
    {
        string root = Path.Combine(Path.GetTempPath(), "lume-recording-test-" + Guid.NewGuid().ToString("N")); Directory.CreateDirectory(root);
        try
        {
            string file = Path.Combine(root, "fixture.mp4");
            using (Bitmap frame = new Bitmap(640, 360)) using (var recording = new SessionRecording(file, frame.Width, frame.Height))
            {
                recording.Ready.GetAwaiter().GetResult();
                using (Graphics g = Graphics.FromImage(frame)) { g.Clear(Color.DarkBlue); g.FillRectangle(Brushes.Orange, 0, 0, 640, 150); }
                recording.Publish(frame); Thread.Sleep(700);
                using (Graphics g = Graphics.FromImage(frame)) g.FillRectangle(Brushes.Teal, 120, 160, 100, 100);
                recording.Publish(frame); Thread.Sleep(400); recording.Stop().GetAwaiter().GetResult();
            }
            byte[] bytes = File.ReadAllBytes(file); string atoms = System.Text.Encoding.ASCII.GetString(bytes);
            Check(bytes.Length > 1000 && atoms.Contains("ftyp") && atoms.Contains("moov") && atoms.Contains("mdat") && atoms.Contains("avc1"), "MP4 lacks a movie index or H.264 track.");
            Check(Directory.GetFiles(root).Length == 1, "Recording left partial data.");
            string evidence = Path.GetFullPath(Path.Combine("verification", "recording-fixture.mp4")); Directory.CreateDirectory(Path.GetDirectoryName(evidence)); File.Copy(file, evidence, true);
            Console.WriteLine("RECORDING_FIXTURE: " + evidence);
        }
        finally { Directory.Delete(root, true); }
    }
    static void RecordingSafety()
    {
        string file = Path.GetTempFileName();
        try
        {
            File.WriteAllText(file, "keep"); Reject(delegate { using (var recorder = new SessionRecording(file, 640, 360)) { } });
            Check(File.ReadAllText(file) == "keep", "Existing destination changed.");
            Reject(delegate { using (var recorder = new SessionRecording(file + ".mp4", 9000, 9000)) { } });
        }
        finally { File.Delete(file); }
    }
    static void RecordingExit()
    {
        string root = Path.Combine(Path.GetTempPath(), "lume-recording-exit-" + Guid.NewGuid().ToString("N")); Directory.CreateDirectory(root);
        try
        {
            using (var fixture = new ReconnectFixture()) using (var dashboard = new MainForm())
            {
                string file = Path.Combine(root, "saved.mp4"); Exception failure = null; ViewerForm viewer = null;
                dashboard.Shown += async delegate
                {
                    try
                    {
                        PairedLink link = fixture.Open(); viewer = new ViewerForm(link.Invitation, link.Peer); viewer.Show();
                        var wait = System.Diagnostics.Stopwatch.StartNew();
                        while (Field(viewer, "displayImage") == null && wait.ElapsedMilliseconds < 10000) await System.Threading.Tasks.Task.Delay(20);
                        Check(Field(viewer, "displayImage") != null, "Recording fixture has no presented video.");
                        Bitmap frame = (Bitmap)Field(viewer, "displayImage");
                        var recording = new SessionRecording(file, frame.Width, frame.Height); await recording.Ready;
                        frame = (Bitmap)Field(viewer, "displayImage"); recording.Publish(frame);
                        typeof(ViewerForm).GetField("recording", System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic).SetValue(viewer, recording);
                        await System.Threading.Tasks.Task.Delay(500); dashboard.ExitDashboard();
                    }
                    catch (Exception error) { failure = error; dashboard.ExitDashboard(); }
                };
                System.Windows.Forms.Application.Run(dashboard);
                if (failure != null) throw failure;
                Check(viewer != null && viewer.IsDisposed, "Application exit left a viewer alive.");
                Check(File.Exists(file) && System.Text.Encoding.ASCII.GetString(File.ReadAllBytes(file)).Contains("moov"), "Exit lost the recording movie index.");
            }
        }
        finally { Directory.Delete(root, true); }
    }
    static void RecordingPendingClose()
    {
        var pending = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        using (var viewer = new ViewerForm(Sample()))
        {
            try
            {
                IntPtr handle = viewer.Handle; // Create a closeable window without starting a session.
                viewer.TrackRecordingFinalization(pending.Task); viewer.Close(); System.Windows.Forms.Application.DoEvents();
                Check(!viewer.IsDisposed && (bool)Field(viewer, "savingRecording"), "A viewer closed before the pending writer finished.");
                pending.SetResult(true);
                PumpUntil(delegate { return viewer.IsDisposed; }, 3000, "The viewer did not close after finalization completed.");
            }
            finally { pending.TrySetCanceled(); }
        }
    }
    static void RecordingCloseTimeout()
    {
        foreach (bool cancelled in new[] { false, true })
        {
            var pending = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
            using (var viewer = new ViewerForm(Sample()))
            {
                try
                {
                    IntPtr handle = viewer.Handle; viewer.TrackRecordingFinalization(pending.Task);
                    Task<bool> finishing = viewer.FinishRecording(60); viewer.Close();
                    PumpUntil(delegate { return finishing.IsCompleted; }, 3000, "A stalled finalization exceeded its bounded wait.");
                    Check(!finishing.GetAwaiter().GetResult() && !viewer.IsDisposed, "Timeout abandoned the writer or closed the viewer.");
                    Check(((System.Windows.Forms.Label)Field(viewer, "information")).Text.Contains("still being saved"), "Recording timeout was not visible.");
                    Check(!RecordingFinalizationJobs.WaitForPending(20).GetAwaiter().GetResult(), "Timeout discarded the pending job.");
                    if (cancelled) pending.SetCanceled(); else pending.SetException(new IOException("Injected finalization failure"));
                    viewer.Close(); PumpUntil(delegate { return viewer.IsDisposed; }, 3000, "A completed recording error blocked close.");
                    Check(RecordingFinalizationJobs.WaitForPending(1000).GetAwaiter().GetResult(), "A completed error leaked a pending recording job.");
                }
                finally { pending.TrySetCanceled(); }
            }
        }
    }
    static void RecordingDisposedViewerExit()
    {
        foreach (bool expire in new[] { false, true })
        {
            var pending = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
            using (var dashboard = new MainForm())
            {
                try
                {
                    using (var viewer = new ViewerForm(Sample())) { viewer.TrackRecordingFinalization(pending.Task); }
                    dashboard.Show(); System.Windows.Forms.Application.DoEvents(); dashboard.ExitDashboard(expire ? 60 : 3000);
                    Check(!dashboard.IsDisposed && RecordingFinalizationJobs.IsExiting, "Application exit ignored a disposed viewer's pending writer.");
                    if (expire)
                    {
                        PumpUntil(delegate { return !(bool)Field(dashboard, "exitPreparing"); }, 3000, "Application exit exceeded its bounded wait.");
                        Check(!dashboard.IsDisposed && dashboard.Visible, "Application exit abandoned the pending writer on timeout.");
                        Check(((System.Windows.Forms.Label)Field(dashboard, "status")).Text.Contains("still being saved"), "Application exit timeout was not visible.");
                        Check(!RecordingFinalizationJobs.IsExiting, "Timed out shutdown prevented a later retry.");
                        pending.SetException(new IOException("Injected detached writer failure")); dashboard.ExitDashboard();
                    }
                    else pending.SetResult(true);
                    PumpUntil(delegate { return dashboard.IsDisposed; }, 3000, "Application exit did not finish after the detached writer completed.");
                    Check(RecordingFinalizationJobs.WaitForPending(1000).GetAwaiter().GetResult() && !RecordingFinalizationJobs.IsExiting, "Shutdown leaked recording lifetime state.");
                }
                finally { pending.TrySetCanceled(); if (!dashboard.IsDisposed) dashboard.ExitDashboard(1000); System.Windows.Forms.Application.DoEvents(); }
            }
        }
    }
    static void RecordingDisconnectClose()
    {
        string root = Path.Combine(Path.GetTempPath(), "lume-recording-disconnect-" + Guid.NewGuid().ToString("N")); Directory.CreateDirectory(root);
        try
        {
            using (var fixture = new ReconnectFixture())
            {
                PairedLink link = fixture.Open(); using (var viewer = new ViewerForm(link.Invitation, link.Peer))
                {
                    viewer.Show(); PumpUntil(delegate { return Field(viewer, "displayImage") != null; }, 10000, "Disconnect recording fixture has no video.");
                    Bitmap frame = (Bitmap)Field(viewer, "displayImage"); string file = Path.Combine(root, "saved.mp4");
                    using (var recording = new SessionRecording(file, frame.Width, frame.Height))
                    {
                        recording.Ready.GetAwaiter().GetResult(); RecordingFinalizationJobs.Track(recording.Completion);
                        typeof(ViewerForm).GetField("recording", System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic).SetValue(viewer, recording);
                        recording.Publish(frame); var enoughFrames = Task.Delay(400); PumpUntil(delegate { return enoughFrames.IsCompleted; }, 2000, "Recording timer did not complete.");
                        fixture.Hosts[0].Dispose();
                        PumpUntil(delegate { return Field(viewer, "recording") == null && Field(viewer, "recordingFinalization") != null; }, 5000, "Disconnect did not start recording finalization.");
                        viewer.Close(); PumpUntil(delegate { return viewer.IsDisposed; }, 5000, "Immediate viewer close left recording finalization unfinished.");
                        byte[] movie = File.ReadAllBytes(file);
                        Check(System.Text.Encoding.ASCII.GetString(movie).Contains("avc1") && Mp4SampleCount(movie, 0, movie.Length) > 0, "Disconnect lost the finalized H.264 MP4 samples/index.");
                        Check(Directory.GetFiles(root).Length == 1, "Disconnect left an incomplete recording file.");
                        string evidence = Path.GetFullPath(Path.Combine("verification", "disconnect-recording-fixture.mp4")); Directory.CreateDirectory(Path.GetDirectoryName(evidence)); File.Copy(file, evidence, true);
                        Console.WriteLine("DISCONNECT_RECORDING_FIXTURE: " + evidence + "; synthetic video, no real capture, microphone or input.");
                    }
                }
            }
        }
        finally { Directory.Delete(root, true); }
    }
    static void RecordingAudioSafety()
    {
        var timeline = new RecordingAudioTimeline(); byte[] pcm = new byte[9600]; for (int i = 0; i < pcm.Length; i++) pcm[i] = (byte)(i % 253);
        timeline.Add(pcm, 1000000); Check(timeline.Read(0, 2400).All(b => b == 0), "A missing audio interval was not silent.");
        Check(timeline.Read(2400, 2400).SequenceEqual(pcm), "Timeline misplaced or changed stereo PCM.");
        Check(timeline.Read(4800, 2400).All(b => b == 0), "Old audio repeated after its end.");
        Reject(delegate { timeline.Add(new byte[5], 0); }); Reject(delegate { timeline.Read(0, 4801); });
    }
    static void RecordingWithAudio()
    {
        string root = Path.Combine(Path.GetTempPath(), "lume-recording-audio-" + Guid.NewGuid().ToString("N")); Directory.CreateDirectory(root);
        try
        {
            string file = Path.Combine(root, "audio.mp4");
            using (var frame = new Bitmap(320, 180)) using (var recorder = new SessionRecording(file, 320, 180, 60, true))
            {
                recorder.Ready.GetAwaiter().GetResult(); using (Graphics g = Graphics.FromImage(frame)) g.Clear(Color.Teal); recorder.Publish(frame);
                int sample = 0;
                for (int block = 0; block < 24; block++)
                {
                    Thread.Sleep(50); byte[] pcm = new byte[9600];
                    for (int n = 0; n < 2400; n++, sample++) { short left = (short)(12000 * Math.Sin(2 * Math.PI * 440 * sample / 48000.0)), right = (short)(8000 * Math.Sin(2 * Math.PI * 880 * sample / 48000.0)); pcm[n * 4] = (byte)left; pcm[n * 4 + 1] = (byte)(left >> 8); pcm[n * 4 + 2] = (byte)right; pcm[n * 4 + 3] = (byte)(right >> 8); }
                    recorder.PublishAudio(pcm);
                }
                Thread.Sleep(100); recorder.Stop().GetAwaiter().GetResult();
            }
            string atoms = System.Text.Encoding.ASCII.GetString(File.ReadAllBytes(file)); Check(atoms.Contains("avc1") && atoms.Contains("mp4a") && atoms.Contains("moov"), "Recording has no playable AAC/video tracks.");
            string evidence = Path.GetFullPath(Path.Combine("verification", "recording-audio-fixture.mp4")); Directory.CreateDirectory(Path.GetDirectoryName(evidence)); File.Copy(file, evidence, true); Console.WriteLine("AUDIO_RECORDING_FIXTURE: " + evidence);
        }
        finally { Directory.Delete(root, true); }
    }
    static void VoiceConsentSafety()
    {
        var pending = new System.Threading.Tasks.TaskCompletionSource<IDisposable>(System.Threading.Tasks.TaskCreationOptions.RunContinuationsAsynchronously); int requests = 0;
        using (var host = new HostService(delegate { return new Synthetic(); }, Profile.All[1], true, delegate { return true; }, delegate { }, delegate { }, microphonePermission: delegate(Action stop, CancellationToken cancel) { Interlocked.Increment(ref requests); var current = pending; cancel.Register(delegate { current.TrySetResult(null); }); return current.Task; }))
        using (var viewer = new ViewerConnection())
        {
            host.Start(System.Net.IPAddress.Loopback, 0, "127.0.0.1"); viewer.Connect(host.Invite, "Voice permission fixture"); int frames = 0;
            var receiver = System.Threading.Tasks.Task.Run(delegate { try { using (var decoder = new FrameDecoder()) viewer.Receive(delegate(Packet p) { decoder.Apply(p); viewer.Ack(decoder.Sequence); Interlocked.Increment(ref frames); }, delegate { }); } catch { } });
            var call = viewer.Tools.Request(SessionTool.Voice, delegate(BinaryWriter w) { w.Write(true); w.Write(1); }); Spin(delegate { return requests == 1 && frames >= 3; }, 6000, "Pending microphone approval blocked frames.");
            Check(!call.IsCompleted, "Voice request did not wait for local approval."); pending.TrySetResult(null); Reject(delegate { Await(call); }); Check(host.HasSession && !viewer.VoiceEnabled, "A denied microphone request affected the desktop.");
            var withdrawnPrompt = pending = new System.Threading.Tasks.TaskCompletionSource<IDisposable>(System.Threading.Tasks.TaskCreationOptions.RunContinuationsAsynchronously);
            var withdrawn = viewer.Tools.Request(SessionTool.Voice, delegate(BinaryWriter w) { w.Write(true); w.Write(2); }); Spin(delegate { return requests == 2; }, 4000, "Second permission request did not arrive.");
            Await(viewer.Tools.Request(SessionTool.Voice, delegate(BinaryWriter w) { w.Write(false); w.Write(3); }));
            Spin(delegate { return withdrawnPrompt.Task.IsCompleted; }, 3000, "A withdrawn microphone request left its prompt open."); Reject(delegate { Await(withdrawn); });
            var supersededPrompt = pending = new System.Threading.Tasks.TaskCompletionSource<IDisposable>(System.Threading.Tasks.TaskCreationOptions.RunContinuationsAsynchronously);
            var superseded = viewer.Tools.Request(SessionTool.Voice, delegate(BinaryWriter w) { w.Write(true); w.Write(4); }); Spin(delegate { return requests == 3; }, 4000, "Third permission request did not arrive.");
            pending = new System.Threading.Tasks.TaskCompletionSource<IDisposable>(System.Threading.Tasks.TaskCreationOptions.RunContinuationsAsynchronously);
            var interrupted = viewer.Tools.Request(SessionTool.Voice, delegate(BinaryWriter w) { w.Write(true); w.Write(5); }); Spin(delegate { return requests == 4; }, 4000, "Newer permission request did not arrive.");
            Spin(delegate { return supersededPrompt.Task.IsCompleted; }, 3000, "A superseded microphone request left its prompt open."); Reject(delegate { Await(superseded); });
            viewer.Dispose(); receiver.Wait(3000);
            Spin(delegate { return pending.Task.IsCompleted; }, 5000, "Disconnect did not dismiss pending microphone consent.");
        }
        using (var fixture = new FilesFixture(false)) { Check((fixture.Viewer.Capabilities & SessionCapabilities.Voice) == 0, "View-only session acquired a microphone capability."); Reject(delegate { Await(fixture.Viewer.Tools.Request(SessionTool.Voice, delegate(BinaryWriter w) { w.Write(true); w.Write(1); })); }); Check(fixture.Host.HasSession, "Denied voice tool closed view-only video."); }
        Console.WriteLine("VOICE: native microphones were not opened; consent, denial, cancellation and protocol checks only.");
    }
    static void RecordingHighRate()
    {
        string root = Path.GetFullPath(Path.Combine("verification", "high-rate-recording")); Directory.CreateDirectory(root);
        string path = Path.Combine(root, "rate-180-" + Guid.NewGuid().ToString("N") + ".mp4"); IntPtr handle = IntPtr.Zero;
        try
        {
            MediaNative.Check(MediaNative.LumeRecordOpen(path, 64, 64, 180, out handle));
            using (var image = new Bitmap(64, 64, System.Drawing.Imaging.PixelFormat.Format32bppRgb))
            {
                for (int frame = 0; frame < 180; frame++)
                {
                    using (Graphics g = Graphics.FromImage(image)) g.Clear(Color.FromArgb(frame % 6 * 40, frame / 6 % 6 * 40, frame / 36 * 40));
                    var pixels = image.LockBits(new Rectangle(0, 0, 64, 64), System.Drawing.Imaging.ImageLockMode.ReadOnly, System.Drawing.Imaging.PixelFormat.Format32bppRgb);
                    try { MediaNative.Check(MediaNative.LumeRecordFrame(handle, pixels.Scan0, pixels.Stride, frame * 10000000L / 180)); }
                    finally { image.UnlockBits(pixels); }
                }
            }
            IntPtr closing = handle; handle = IntPtr.Zero; MediaNative.Check(MediaNative.LumeRecordClose(closing));
            byte[] movie = File.ReadAllBytes(path);
            Check(movie.Length > 1000 && Mp4SampleCount(movie, 0, movie.Length) == 180, "High-rate MP4 silently discarded submitted images.");
            Console.WriteLine("HIGH_RATE_RECORDING: " + path + "; synthetic offline timestamps, not measured capture/network FPS.");
        }
        finally { if (handle != IntPtr.Zero) MediaNative.LumeRecordClose(handle); }
    }
    static int Mp4SampleCount(byte[] movie, int offset, int end)
    {
        int samples = 0;
        while (offset < end)
        {
            Check(end - offset >= 8, "Truncated MP4 box.");
            long length = Mp4UInt(movie, offset); int header = 8;
            if (length == 1) { Check(end - offset >= 16, "Truncated large MP4 box."); length = ((long)Mp4UInt(movie, offset + 8) << 32) | Mp4UInt(movie, offset + 12); header = 16; }
            if (length == 0) length = end - offset;
            Check(length >= header && length <= end - offset, "Invalid MP4 box length.");
            int next = offset + (int)length; string kind = System.Text.Encoding.ASCII.GetString(movie, offset + 4, 4);
            if (kind == "stsz") { Check(length >= header + 12, "Truncated MP4 sample count."); samples += checked((int)Mp4UInt(movie, offset + header + 8)); }
            else if (kind == "moov" || kind == "trak" || kind == "mdia" || kind == "minf" || kind == "stbl") samples += Mp4SampleCount(movie, offset + header, next);
            offset = next;
        }
        return samples;
    }
    static uint Mp4UInt(byte[] bytes, int offset) { return (uint)bytes[offset] << 24 | (uint)bytes[offset + 1] << 16 | (uint)bytes[offset + 2] << 8 | bytes[offset + 3]; }
}
