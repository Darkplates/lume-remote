using System;
using System.Collections.Concurrent;
using System.Diagnostics;
using System.Drawing;
using System.Drawing.Imaging;
using System.IO;
using System.IO.Compression;
using System.Runtime.InteropServices;
using System.Threading;
using System.Threading.Tasks;

namespace LumeRemote
{
    internal static class MediaNative
    {
        public static int Version { get { try { return LumeMediaVersion(); } catch (DllNotFoundException) { return 0; } catch (EntryPointNotFoundException) { return 0; } catch (BadImageFormatException) { return 0; } } }
        public static bool Available { get { return Version >= 1; } }
        public static void Check(int hr) { if (hr < 0) Marshal.ThrowExceptionForHR(hr); }
        [DllImport("LumeVideo.dll", CallingConvention = CallingConvention.Cdecl)] static extern int LumeMediaVersion();
        [DllImport("LumeVideo.dll", CallingConvention = CallingConvention.Cdecl)] internal static extern int LumeAudioOpen(int playback, out IntPtr handle);
        [DllImport("LumeVideo.dll", CallingConvention = CallingConvention.Cdecl)] internal static extern int LumeAudioRead(IntPtr handle, byte[] target, uint capacity, out uint written);
        [DllImport("LumeVideo.dll", CallingConvention = CallingConvention.Cdecl)] internal static extern int LumeAudioWrite(IntPtr handle, byte[] data, uint length, out uint written);
        [DllImport("LumeVideo.dll", CallingConvention = CallingConvention.Cdecl)] internal static extern void LumeAudioClose(IntPtr handle);
        [DllImport("LumeVideo.dll", CallingConvention = CallingConvention.Cdecl, CharSet = CharSet.Unicode)] internal static extern int LumeRecordOpen(string path, uint width, uint height, uint fps, out IntPtr handle);
        [DllImport("LumeVideo.dll", CallingConvention = CallingConvention.Cdecl)] internal static extern int LumeRecordFrame(IntPtr handle, IntPtr pixels, int stride, long time);
        [DllImport("LumeVideo.dll", CallingConvention = CallingConvention.Cdecl)] internal static extern int LumeRecordClose(IntPtr handle);
        [DllImport("LumeVideo.dll", CallingConvention = CallingConvention.Cdecl, CharSet = CharSet.Unicode)] internal static extern int LumeRecordOpenAudio(string path, uint width, uint height, uint fps, out IntPtr handle);
        [DllImport("LumeVideo.dll", CallingConvention = CallingConvention.Cdecl)] internal static extern int LumeRecordAudio(IntPtr handle, byte[] samples, uint bytes, long time);
    }
    public static class AudioBlock
    {
        public const int Maximum = 19200; // At most 100 ms of stereo 48 kHz PCM16.
        public static byte[] Encode(byte[] pcm, int count)
        {
            if (count < 4 || count > Maximum || count > pcm.Length || count % 4 != 0) throw new InvalidDataException("Invalid audio block.");
            using (MemoryStream output = new MemoryStream())
            {
                output.Write(BitConverter.GetBytes(count), 0, 4);
                using (DeflateStream compressed = new DeflateStream(output, CompressionLevel.Fastest, true)) compressed.Write(pcm, 0, count);
                return output.ToArray();
            }
        }
        public static byte[] Decode(byte[] block)
        {
            if (block.Length < 5 || block.Length > Maximum + 256) throw new InvalidDataException("Invalid audio packet.");
            int length = BitConverter.ToInt32(block, 0); if (length < 4 || length > Maximum || length % 4 != 0) throw new InvalidDataException("Invalid audio sample count.");
            using (MemoryStream input = new MemoryStream(block, 4, block.Length - 4, false)) using (DeflateStream compressed = new DeflateStream(input, CompressionMode.Decompress))
            {
                byte[] result = Wire.ReadExact(compressed, length); if (compressed.ReadByte() != -1) throw new InvalidDataException("Expanded audio exceeds its declared size."); return result;
            }
        }
    }
    public sealed class SessionAudio : IDisposable
    {
        readonly bool playback;
        readonly bool microphone;
        readonly Action<byte[]> send;
        readonly Action<Exception> failed;
        readonly BlockingCollection<byte[]> queue = new BlockingCollection<byte[]>(4);
        readonly TaskCompletionSource<bool> started = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        readonly Thread thread;
        volatile bool disposed;
        public Task Ready { get { return started.Task; } }
        public bool IsRunning { get { return started.Task.Status == TaskStatus.RanToCompletion && !disposed; } }
        public SessionAudio(bool playback, Action<byte[]> send, Action<Exception> failed, bool microphone = false)
        {
            if (playback && microphone || microphone && MediaNative.Version < 2) throw new InvalidOperationException("Microphone capture requires the updated native media component.");
            this.playback = playback; this.send = send; this.failed = failed; this.microphone = microphone;
            if (!playback) BackgroundWork.Run(delegate
            {
                try { while (!disposed) { byte[] block; if (queue.TryTake(out block, 50) && !disposed) send(block); } }
                catch (Exception error) { if (!disposed) try { failed(error); } catch { } }
                finally { disposed = true; }
            });
            thread = new Thread(Run) { IsBackground = true, Name = playback ? "Lume audio playback" : "Lume audio capture" }; thread.Start();
        }
        public void Receive(byte[] block)
        {
            ReceivePcm(AudioBlock.Decode(block));
        }
        public void ReceivePcm(byte[] pcm)
        {
            if (!playback || disposed) return; Queue(pcm);
        }
        void Queue(byte[] pcm)
        {
            byte[] old;
            while (!disposed && !queue.TryAdd(pcm)) queue.TryTake(out old);
        }
        void Run()
        {
            IntPtr handle = IntPtr.Zero;
            try
            {
                MediaNative.Check(MediaNative.LumeAudioOpen(playback ? 1 : microphone ? 2 : 0, out handle)); started.TrySetResult(true);
                if (playback)
                {
                    while (!disposed)
                    {
                        byte[] pcm; if (!queue.TryTake(out pcm, 50)) continue;
                        int position = 0; Stopwatch deadline = Stopwatch.StartNew();
                        while (!disposed && position < pcm.Length && deadline.ElapsedMilliseconds < 150)
                        {
                            byte[] remaining = new byte[pcm.Length - position]; Buffer.BlockCopy(pcm, position, remaining, 0, remaining.Length);
                            uint written; MediaNative.Check(MediaNative.LumeAudioWrite(handle, remaining, (uint)remaining.Length, out written)); position += (int)written;
                            if (written == 0) Thread.Sleep(5);
                        }
                    }
                }
                else
                {
                    byte[] buffer = new byte[AudioBlock.Maximum];
                    while (!disposed)
                    {
                        uint count; MediaNative.Check(MediaNative.LumeAudioRead(handle, buffer, (uint)buffer.Length, out count));
                        if (count > 0 && !disposed) Queue(AudioBlock.Encode(buffer, (int)count));
                        Thread.Sleep(10);
                    }
                }
            }
            catch (Exception error) { started.TrySetException(error); if (!disposed) try { failed(error); } catch { } }
            finally { disposed = true; if (handle != IntPtr.Zero) MediaNative.LumeAudioClose(handle); }
        }
        public void Dispose() { disposed = true; }
    }

    public sealed class SessionRecording : IDisposable
    {
        readonly LatestFrameQueue frames = new LatestFrameQueue();
        readonly AutoResetEvent signal = new AutoResetEvent(false);
        readonly string destination, temporary;
        readonly int width, height, fps;
        readonly bool includeAudio;
        readonly RecordingAudioTimeline audio = new RecordingAudioTimeline();
        readonly Stopwatch clock = new Stopwatch();
        readonly Task worker;
        readonly object lifetime = new object();
        bool signalClosed;
        readonly TaskCompletionSource<bool> started = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        volatile bool stopping; long published;
        public Task Ready { get { return started.Task; } }
        public Task Completion { get { return worker; } }
        public SessionRecording(string destination, int width, int height, int fps = 30, bool includeAudio = false)
        {
            if (width < 2 || height < 2 || width > 4096 || height > 2160) throw new InvalidOperationException("Recording supports up to 4096 x 2160. Choose a lower stream resolution first.");
            if (fps < 1 || fps > 1000 || includeAudio && MediaNative.Version < 2) throw new InvalidOperationException("Choose 1-1000 recording FPS; audio requires the updated media component.");
            this.fps = fps; this.includeAudio = includeAudio;
            this.width = width & ~1; this.height = height & ~1; this.destination = Path.GetFullPath(destination);
            if (File.Exists(this.destination)) throw new IOException("Choose a new file name. Existing recordings are kept.");
            temporary = Path.Combine(Path.GetDirectoryName(this.destination), ".lume-record-" + Guid.NewGuid().ToString("N") + ".mp4");
            worker = BackgroundWork.Run((Action)Run);
        }
        public void Publish(Bitmap frame) { lock (lifetime) { long now = Stopwatch.GetTimestamp(); if (!stopping && now - published >= Stopwatch.Frequency / fps) { published = now; frames.Publish((Bitmap)frame.Clone()); signal.Set(); } } }
        public void PublishAudio(byte[] pcm) { if (includeAudio && !stopping && clock.IsRunning) audio.Add(pcm, clock.Elapsed.Ticks); }
        void Run()
        {
            IntPtr handle = IntPtr.Zero; Bitmap last = null; bool finalized = false;
            try
            {
                MediaNative.Check(includeAudio ? MediaNative.LumeRecordOpenAudio(temporary, (uint)width, (uint)height, (uint)fps, out handle) : MediaNative.LumeRecordOpen(temporary, (uint)width, (uint)height, (uint)fps, out handle)); clock.Start(); started.TrySetResult(true);
                long next = 0, timestamp = 0, audioPosition = 0;
                using (Bitmap canvas = new Bitmap(width, height, PixelFormat.Format32bppRgb)) using (Graphics graphics = Graphics.FromImage(canvas))
                {
                    while (!stopping)
                    {
                        Bitmap latest = frames.Take(); if (latest != null) { if (last != null) last.Dispose(); last = latest; }
                        long now = clock.Elapsed.Ticks;
                        if (includeAudio && last != null) while ((audioPosition + 2400) * 10000000L / 48000 <= now) { byte[] pcm = audio.Read(audioPosition, 2400); MediaNative.Check(MediaNative.LumeRecordAudio(handle, pcm, (uint)pcm.Length, audioPosition * 10000000L / 48000)); audioPosition += 2400; }
                        if (last == null || now < next) { signal.WaitOne((int)Math.Max(1, Math.Min(20, (next - now) / 10000))); continue; }
                        graphics.Clear(Color.Black); double scale = Math.Min(width / (double)last.Width, height / (double)last.Height);
                        int w = (int)(last.Width * scale), h = (int)(last.Height * scale); graphics.DrawImage(last, new Rectangle((width - w) / 2, (height - h) / 2, w, h));
                        BitmapData pixels = canvas.LockBits(new Rectangle(0, 0, width, height), ImageLockMode.ReadOnly, PixelFormat.Format32bppRgb);
                        try { timestamp = Math.Max(timestamp + 1, now); MediaNative.Check(MediaNative.LumeRecordFrame(handle, pixels.Scan0, pixels.Stride, timestamp)); }
                        finally { canvas.UnlockBits(pixels); }
                        next = now + 10000000L / fps;
                    }
                }
                if (last == null) throw new IOException("No frames were recorded.");
                if (includeAudio) { int remaining = (int)Math.Max(0, Math.Min(2400, (long)(clock.Elapsed.TotalSeconds * 48000) - audioPosition)); if (remaining > 0) { byte[] pcm = audio.Read(audioPosition, remaining); MediaNative.Check(MediaNative.LumeRecordAudio(handle, pcm, (uint)pcm.Length, audioPosition * 10000000L / 48000)); } }
                IntPtr closing = handle; handle = IntPtr.Zero; MediaNative.Check(MediaNative.LumeRecordClose(closing)); finalized = true;
                Publish(temporary, destination);
            }
            catch (Exception error) { started.TrySetException(error); throw; }
            finally
            {
                lock (lifetime) { stopping = true; frames.Dispose(); signalClosed = true; signal.Dispose(); } if (last != null) last.Dispose();
                if (handle != IntPtr.Zero) MediaNative.LumeRecordClose(handle);
                if (!finalized) try { File.Delete(temporary); } catch (IOException) { } catch (UnauthorizedAccessException) { }
            }
        }
        // A finished recording is never deleted: a name taken meanwhile gets a numbered suffix,
        // and any other failure leaves the complete temporary file in place.
        static void Publish(string temporary, string destination)
        {
            string folder = Path.GetDirectoryName(destination), name = Path.GetFileNameWithoutExtension(destination), extension = Path.GetExtension(destination);
            for (int attempt = 0; attempt < 100; attempt++)
            {
                string target = attempt == 0 ? destination : Path.Combine(folder, name + " (" + attempt + ")" + extension);
                if (File.Exists(target)) continue;
                try { File.Move(temporary, target); return; }
                catch (IOException) { if (!File.Exists(target)) throw new IOException("The recording was kept as " + temporary + " because it could not be renamed."); }
            }
            throw new IOException("The recording was kept as " + temporary + ". Choose a folder with a free file name.");
        }
        public Task Stop() { lock (lifetime) { stopping = true; if (!signalClosed) signal.Set(); } return worker; }
        public void Dispose() { Stop(); worker.ContinueWith(delegate(Task done) { var observed = done.Exception; }, TaskContinuationOptions.OnlyOnFaulted); }
    }
}
