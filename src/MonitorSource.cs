using System;
using System.Drawing;
using System.IO;
using System.Windows.Forms;

namespace LumeRemote
{
    public sealed class RemoteMonitor
    {
        public string Id, Name;
        public Rectangle Bounds;
        public int Refresh;
        public bool Selected;
        public void Write(BinaryWriter w) { Wire.Text(w, Id); Wire.Text(w, Name); w.Write(Bounds.X); w.Write(Bounds.Y); w.Write(Bounds.Width); w.Write(Bounds.Height); w.Write(Refresh); w.Write(Selected); }
        public static RemoteMonitor Read(Packet p)
        {
            RemoteMonitor m = new RemoteMonitor { Id = p.Text(256), Name = p.Text(256), Bounds = new Rectangle(p.Reader.ReadInt32(), p.Reader.ReadInt32(), p.Reader.ReadInt32(), p.Reader.ReadInt32()), Refresh = p.Reader.ReadInt32(), Selected = p.Reader.ReadBoolean() };
            if (m.Id.Length == 0 || m.Bounds.Width < 1 || m.Bounds.Height < 1 || m.Bounds.Width > 32768 || m.Bounds.Height > 32768 || m.Refresh < 1 || m.Refresh > 1000) throw new InvalidDataException("Invalid remote monitor.");
            return m;
        }
        public override string ToString() { return Name + " - " + Bounds.Width + " x " + Bounds.Height + " / " + Refresh + " Hz" + (Selected ? " (current)" : ""); }
    }
    public interface IMonitorSource { RemoteMonitor[] Monitors(); void Select(string id); }
    // Optional: follows resolution/position changes of the selected display and reports the capture backend.
    public interface IDisplayRefreshSource { bool RefreshBounds(); string Backend { get; } }

    // Selection and capture run on the capture worker, never on a UI/network thread.
    public sealed class MonitorSource : IScreenSource, IAdaptiveScreenSource, IFrameChangeSource, IMonitorSource, IDisplayRefreshSource
    {
        DesktopSource source;
        readonly Profile initial;
        string selected;
        readonly System.Diagnostics.Stopwatch clock = System.Diagnostics.Stopwatch.StartNew();
        long nextRefresh = 1000;
        public MonitorSource(Rectangle bounds, Profile profile)
        {
            initial = profile; foreach (Screen screen in Screen.AllScreens) if (screen.Bounds == bounds) selected = screen.DeviceName;
            if (selected == null) selected = Screen.PrimaryScreen.DeviceName;
            source = new DesktopSource(bounds, profile);
        }
        public Rectangle Bounds { get { return source.Bounds; } }
        public int RefreshRate { get { return source.RefreshRate; } }
        public bool FrameChanged { get { return source.FrameChanged; } }
        public void Configure(StreamQuality quality) { source.Configure(quality); }
        public Bitmap Capture() { return source.Capture(); }
        public string Backend { get { return source.Backend; } }
        // At most once per second, compare the selected display with its current bounds. A changed resolution or
        // position recreates the capture source for that display; the caller updates input bounds under its input gate.
        public bool RefreshBounds()
        {
            long now = clock.ElapsedMilliseconds; if (now < nextRefresh) return false; nextRefresh = now + 1000;
            foreach (Screen screen in Screen.AllScreens) if (screen.DeviceName == selected)
            {
                if (screen.Bounds == source.Bounds || screen.Bounds.Width < 1 || screen.Bounds.Height < 1) return false;
                DesktopSource replacement = new DesktopSource(screen.Bounds, initial); DesktopSource old = source;
                source = replacement; old.Dispose(); return true;
            }
            return false;
        }
        public RemoteMonitor[] Monitors()
        {
            Screen[] screens = Screen.AllScreens; if (screens.Length > 32) throw new InvalidOperationException("The desktop has too many displays.");
            RemoteMonitor[] result = new RemoteMonitor[screens.Length];
            for (int i = 0; i < screens.Length; i++) result[i] = new RemoteMonitor { Id = screens[i].DeviceName, Name = "Display " + (i + 1), Bounds = screens[i].Bounds, Refresh = DisplayInfo.RefreshRate(screens[i].Bounds), Selected = screens[i].DeviceName == selected };
            return result;
        }
        public void Select(string id)
        {
            foreach (Screen screen in Screen.AllScreens) if (screen.DeviceName == id)
            {
                DesktopSource replacement = new DesktopSource(screen.Bounds, initial); DesktopSource old = source;
                source = replacement; selected = id; old.Dispose(); return;
            }
            throw new InvalidOperationException("That display is no longer connected. Refresh the display list.");
        }
        public void Dispose() { source.Dispose(); }
    }
}
