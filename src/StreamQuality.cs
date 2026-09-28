using System;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Drawing.Imaging;
using System.IO;
using System.Runtime.InteropServices;
using System.Windows.Forms;

namespace LumeRemote
{
    public sealed class StreamQuality
    {
        public const int MaxPixels = 33554432, MaxDimension = 16384;
        public int Height, Fps, JpegQuality = 85;
        public bool Lossless = true;
        public bool Video;
        internal bool PortableImages;
        public int BitrateKbps = 8000;
        public static StreamQuality Source { get { return new StreamQuality(); } }
        public StreamQuality Copy() { return new StreamQuality { Height = Height, Fps = Fps, JpegQuality = JpegQuality, Lossless = Lossless, Video = Video, BitrateKbps = BitrateKbps, PortableImages = PortableImages }; }
        public void Validate()
        {
            if (Height < 0 || Height > MaxDimension || (Height != 0 && Height < 120) || Fps < -1 || Fps > 1000 || JpegQuality < 10 || JpegQuality > 100 || BitrateKbps < 250 || BitrateKbps > 100000 || (Video && Lossless))
                throw new InvalidDataException("Invalid stream settings. Choose source or 120-16384 lines, source/unlimited or 1-1000 FPS, and JPEG quality 10-100.");
        }
        public Size Dimensions(Size source)
        {
            Validate();
            if (source.Width < 1 || source.Height < 1 || source.Width > MaxDimension || source.Height > MaxDimension || (long)source.Width * source.Height > MaxPixels)
                throw new InvalidDataException("The source display exceeds the supported 32-million-pixel safety bound.");
            double scale = Height == 0 ? 1.0 : Math.Min(1.0, (double)Height / source.Height);
            return new Size(Math.Max(1, (int)Math.Round(source.Width * scale)), Math.Max(1, (int)Math.Round(source.Height * scale)));
        }
        public int Limit(int sourceHz) { return Fps == 0 ? Math.Max(1, Math.Min(1000, sourceHz)) : Fps; }
        public void Write(BinaryWriter writer) { Validate(); writer.Write(Height); writer.Write(Fps); writer.Write(JpegQuality); writer.Write(Lossless); }
        public void Write(BinaryWriter writer, int version) { Write(writer); if (version >= 3) { writer.Write(Video); writer.Write(BitrateKbps); } }
        public static StreamQuality Read(BinaryReader reader)
        { StreamQuality value = new StreamQuality { Height = reader.ReadInt32(), Fps = reader.ReadInt32(), JpegQuality = reader.ReadInt32(), Lossless = reader.ReadBoolean() }; value.Validate(); return value; }
        public static StreamQuality Read(BinaryReader reader, int version)
        { StreamQuality value = Read(reader); if (version >= 3) { value.Video = reader.ReadBoolean(); value.BitrateKbps = reader.ReadInt32(); } value.Validate(); return value; }
        public static StreamQuality FromProfile(Profile profile, Rectangle bounds)
        { return new StreamQuality { Height = profile.MaxWidth == 0 ? 0 : Math.Max(120, (int)Math.Round(bounds.Height * Math.Min(1.0, (double)profile.MaxWidth / bounds.Width))), Fps = profile.Fps, JpegQuality = profile.Quality, Lossless = profile.Lossless }; }
        public string Description
        { get { return (Height == 0 ? "Source resolution" : Height + "p") + " / " + (Fps == 0 ? "source refresh" : Fps < 0 ? "uncapped" : Fps + " FPS cap") + " / " + (Video ? "H.264 " + BitrateKbps + " kbit/s" : Lossless ? "lossless" : "JPEG " + JpegQuality); } }
    }

    public interface IAdaptiveScreenSource { void Configure(StreamQuality quality); int RefreshRate { get; } }

    public sealed class FrameScaler : IDisposable
    {
        Bitmap resized; Graphics graphics;
        public Bitmap Scale(Bitmap source, StreamQuality quality)
        {
            Size size = quality.Dimensions(source.Size); if (size == source.Size) return source;
            if (resized == null || resized.Size != size)
            { Dispose(); resized = new Bitmap(size.Width, size.Height, PixelFormat.Format32bppRgb); graphics = Graphics.FromImage(resized); graphics.InterpolationMode = InterpolationMode.HighQualityBilinear; }
            graphics.DrawImage(source, new Rectangle(Point.Empty, size), new Rectangle(Point.Empty, source.Size), GraphicsUnit.Pixel); return resized;
        }
        public void Dispose() { if (graphics != null) { graphics.Dispose(); graphics = null; } if (resized != null) { resized.Dispose(); resized = null; } }
    }

    public static class DisplayInfo
    {
        public static int RefreshRate(Rectangle bounds)
        {
            foreach (Screen screen in Screen.AllScreens) if (screen.Bounds == bounds)
            {
                Mode mode = new Mode(); mode.Size = (short)Marshal.SizeOf(typeof(Mode));
                if (EnumDisplaySettings(screen.DeviceName, -1, ref mode) && mode.Frequency > 1 && mode.Frequency <= 1000) return (int)mode.Frequency;
            }
            return 60;
        }
        [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)] struct Mode
        {
            [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 32)] public string Device;
            public short Spec, Driver, Size, Extra; public uint Fields; public int X, Y; public uint Orientation, FixedOutput;
            public short Color, Duplex, YResolution, TT, Collate;
            [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 32)] public string Form;
            public short LogPixels; public uint Bits, Width, Height, Flags, Frequency, IcmMethod, IcmIntent, Media, Dither, Reserved1, Reserved2, PanningWidth, PanningHeight;
        }
        [DllImport("user32.dll", CharSet = CharSet.Unicode)] static extern bool EnumDisplaySettings(string device, int number, ref Mode mode);
    }

    sealed class StreamQualityForm : Form
    {
        readonly ComboBox resolution = Theme.Combo(), fps = Theme.Combo(), codec = Theme.Combo();
        readonly NumericUpDown jpeg = new NumericUpDown { Minimum = 10, Maximum = 100, Width = 180 };
        readonly NumericUpDown bitrate = new NumericUpDown { Minimum = 250, Maximum = 100000, Increment = 500, Width = 180 };
        readonly int[] heights = { 0, 360, 480, 720, 900, 1080, 1440, 2160, 4320 };
        readonly int[] rates = { 0, 5, 10, 15, 24, 30, 60, 90, 120, 144, 165, 180, 240, 360, -1 };
        public StreamQuality Selection { get; private set; }
        public StreamQualityForm(StreamQuality current, int sourceWidth, int sourceHeight, int sourceHz, bool videoAvailable = true)
        {
            Text = "Lume - Quality"; Size = new Size(560, 380); MinimumSize = new Size(520, 360); BackColor = Theme.Background; StartPosition = FormStartPosition.CenterParent; AutoScaleMode = AutoScaleMode.Dpi; Icon = Brand.Icon;
            FlowLayoutPanel content = Theme.Column(); content.AutoSize = false; content.Dock = DockStyle.Fill; content.AutoScroll = true; Controls.Add(content);
            content.Controls.Add(Theme.Label("Quality", 22, Theme.Text));
            FlowLayoutPanel presets = new FlowLayoutPanel { AutoSize = true, Width = 480, WrapContents = true, Margin = new Padding(0) };
            Button textPreset = Theme.Button("Source", false), videoPreset = Theme.Button("Smooth video", false), ecoPreset = Theme.Button("Save data", false); textPreset.Width = videoPreset.Width = ecoPreset.Width = 144; videoPreset.Enabled = videoAvailable;
            presets.Controls.Add(textPreset); presets.Controls.Add(videoPreset); presets.Controls.Add(ecoPreset); content.Controls.Add(presets);
            Label summary = Theme.Label("", 11, Theme.Muted); content.Controls.Add(summary);
            Button custom = Theme.Button("Custom settings", false); custom.Width = 210; content.Controls.Add(custom);
            FlowLayoutPanel settings = Theme.Column(); settings.Dock = DockStyle.None; settings.Padding = new Padding(0); settings.Width = 460; settings.Visible = false; content.Controls.Add(settings);
            custom.Click += delegate { settings.Visible = !settings.Visible; custom.Text = settings.Visible ? "Hide custom settings" : "Custom settings"; Height = settings.Visible ? 740 : 380; };
            settings.Controls.Add(Theme.Label("RESOLUTION", 9, Theme.Accent));
            foreach (int height in heights) resolution.Items.Add(height == 0 ? "Source - original pixels" : height + "p (no upscaling)");
            resolution.SelectedIndex = Math.Max(0, Array.IndexOf(heights, current.Height)); settings.Controls.Add(resolution);
            settings.Controls.Add(Theme.Label("FRAME RATE LIMIT", 9, Theme.Accent));
            foreach (int rate in rates) fps.Items.Add(rate == 0 ? "Source refresh - " + sourceHz + " Hz" : rate < 0 ? "Uncapped" : rate + " FPS");
            fps.SelectedIndex = Math.Max(0, Array.IndexOf(rates, current.Fps)); settings.Controls.Add(fps);
            settings.Controls.Add(Theme.Label("IMAGE COMPRESSION", 9, Theme.Accent)); codec.Items.AddRange(new object[] { "Lossless - exact pixels", "JPEG - smaller image updates" }); if (videoAvailable) codec.Items.Add("H.264 - hardware when available"); codec.SelectedIndex = current.Video && videoAvailable ? 2 : current.Lossless ? 0 : 1; settings.Controls.Add(codec);
            settings.Controls.Add(Theme.Label("JPEG QUALITY (10-100)", 9, Theme.Muted)); jpeg.Value = current.JpegQuality; jpeg.Enabled = codec.SelectedIndex == 1; settings.Controls.Add(jpeg);
            settings.Controls.Add(Theme.Label("VIDEO BITRATE (kbit/s)", 9, Theme.Muted)); bitrate.Value = current.BitrateKbps; bitrate.Enabled = codec.SelectedIndex == 2; settings.Controls.Add(bitrate);
            codec.SelectedIndexChanged += delegate { jpeg.Enabled = codec.SelectedIndex == 1; bitrate.Enabled = codec.SelectedIndex == 2; };
            textPreset.Click += delegate { resolution.SelectedIndex = 0; fps.SelectedIndex = 0; codec.SelectedIndex = 0; };
            videoPreset.Click += delegate { resolution.SelectedIndex = Array.IndexOf(heights, 1080); fps.SelectedIndex = Array.IndexOf(rates, 60); codec.SelectedIndex = 2; bitrate.Value = 8000; };
            ecoPreset.Click += delegate { resolution.SelectedIndex = Array.IndexOf(heights, 360); fps.SelectedIndex = Array.IndexOf(rates, 10); codec.SelectedIndex = 1; jpeg.Value = 70; };
            Action describe = delegate
            {
                StreamQuality selected = new StreamQuality { Height = heights[resolution.SelectedIndex], Fps = rates[fps.SelectedIndex], Lossless = codec.SelectedIndex == 0, Video = codec.SelectedIndex == 2, BitrateKbps = (int)bitrate.Value, JpegQuality = (int)jpeg.Value };
                Size dimensions = selected.Dimensions(new Size(sourceWidth, sourceHeight));
                summary.Text = dimensions.Width + " x " + dimensions.Height + "  /  " + (selected.Fps < 0 ? "uncapped" : (selected.Fps == 0 ? sourceHz : selected.Fps) + " FPS target") + "\n" + (selected.Video ? "H.264 video" : selected.Lossless ? "Exact pixels" : "JPEG " + selected.JpegQuality);
            };
            resolution.SelectedIndexChanged += delegate { describe(); }; fps.SelectedIndexChanged += delegate { describe(); }; codec.SelectedIndexChanged += delegate { describe(); }; jpeg.ValueChanged += delegate { describe(); }; bitrate.ValueChanged += delegate { describe(); }; describe();
            Panel actions = new Panel { Dock = DockStyle.Bottom, Height = 68, Padding = new Padding(26, 8, 26, 8), BackColor = Theme.Card };
            Button apply = Theme.Button("Apply", true); apply.Dock = DockStyle.Fill; actions.Controls.Add(apply); Controls.Add(actions);
            apply.Click += delegate { Selection = new StreamQuality { Height = heights[resolution.SelectedIndex], Fps = rates[fps.SelectedIndex], Lossless = codec.SelectedIndex == 0, Video = codec.SelectedIndex == 2, BitrateKbps = (int)bitrate.Value, JpegQuality = (int)jpeg.Value }; DialogResult = DialogResult.OK; Close(); };
            settings.Controls.Add(Theme.Label("H.264 reduces colour detail (4:2:0). Source keeps exact pixels. FPS is a limit; unchanged screens send fewer updates.", 10, Theme.Muted));
        }
    }
}
