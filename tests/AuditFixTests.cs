using System;
using System.Diagnostics;
using System.Net;
using System.Runtime.InteropServices;
using System.Threading;
using System.Threading.Tasks;
using System.Windows.Forms;
using LumeRemote;

// Regressions for the 2026-10 audit: guest limits, guest check code and the emergency shortcut.
static partial class Tests
{
    [DllImport("user32.dll", SetLastError = true)] static extern bool RegisterHotKey(IntPtr window, int id, uint modifiers, uint key);
    [DllImport("user32.dll")] static extern bool UnregisterHotKey(IntPtr window, int id);

    static void RelayOperatorToken()
    {
        string room = Guid.NewGuid().ToString("N");
        Check(Transport.RelayHeader("H", room, null) == "LUME1 H " + room + "\n" && Transport.RelayHeader("V", room, "") == "LUME1 V " + room + "\n", "The relay header without a token changed.");
        Check(Transport.RelayHeader("H", room, "operator-token_0123") == "LUME1 H " + room + " operator-token_0123\n", "The operator token was not sent.");
        Reject(delegate { Transport.RelayHeader("H", room, "short"); });
        Reject(delegate { Transport.RelayHeader("H", room, "bad token with spaces"); });
        Reject(delegate { Transport.RelayHeader("H", room, "line\nbreak-0123456789"); });
    }
    static void GuestControlMarker()
    {
        Check(!GuestControl.Active, "A guest-control marker was already active.");
        IDisposable first = GuestControl.Begin(), second = GuestControl.Begin();
        Check(GuestControl.Active, "An active guest session was not visible to owner-only actions.");
        first.Dispose(); first.Dispose();
        Check(GuestControl.Active, "Ending one of two guest sessions cleared the marker.");
        second.Dispose();
        Stopwatch clock = Stopwatch.StartNew();
        while (GuestControl.Active && clock.ElapsedMilliseconds < 3000) Thread.Sleep(20);
        Check(!GuestControl.Active, "The guest-control marker outlived its sessions.");
    }
    static void SettingsPipeDeadline()
    {
        string name = "lume-test-deadline-" + Guid.NewGuid().ToString("N");
        using (var server = new System.IO.Pipes.NamedPipeServerStream(name, System.IO.Pipes.PipeDirection.InOut, 1))
        using (var client = new System.IO.Pipes.NamedPipeClientStream(".", name, System.IO.Pipes.PipeDirection.InOut))
        {
            Task connecting = Task.Run(delegate { server.WaitForConnection(); }); client.Connect(5000); connecting.Wait(5000);
            // A finished request's deadline must not fire later against the next client.
            using (new PipeDeadline(server, 150)) { }
            Thread.Sleep(600);
            Check(server.IsConnected, "A finished request's deadline disconnected the pipe later.");
            client.WriteByte(7); client.Flush();
            Check(server.ReadByte() == 7, "The pipe stopped carrying data after a finished deadline.");
            // A stalled request is cut off.
            IDisposable stalled = new PipeDeadline(server, 100);
            Stopwatch clock = Stopwatch.StartNew();
            while (server.IsConnected && clock.ElapsedMilliseconds < 3000) Thread.Sleep(20);
            Check(!server.IsConnected, "A stalled settings client was not disconnected.");
            stalled.Dispose();
        }
    }
    static void GuestCheckCode()
    {
        Invitation session = Sample();
        PeerSignal offer = PeerSignal.Offer(session, SyntheticSdp("hostufrag"));
        PeerSignal reply = offer.Reply(SyntheticSdp("viewufrag")), other = offer.Reply(SyntheticSdp("otherufrag"));
        string code = PeerSignal.CheckCode(session, reply);
        Check(code.Length == 7 && code[3] == ' ' && code == PeerSignal.CheckCode(session, PeerSignal.Parse(reply.ToString())), "The guest check code is not stable across transport.");
        Check(code != PeerSignal.CheckCode(session, other), "Different replies produced the same check code.");
        Reject(delegate { PeerSignal.CheckCode(session, offer); });
    }
    static void GuestDisplayLocked()
    {
        using (var host = new HostService(delegate { return new SyntheticMonitors(); }, Profile.All[3], false, delegate { return true; }, delegate { }, delegate { }))
        using (var viewer = new ViewerConnection())
        {
            host.AllowMonitorSwitching = false;
            host.Start(IPAddress.Loopback, 0, "127.0.0.1"); viewer.Connect(host.Invite, "Display fixture");
            Task receiver = Task.Run(delegate { try { using (var decoder = new FrameDecoder()) viewer.Receive(delegate(Packet p) { decoder.Apply(p); viewer.Ack(decoder.Sequence); }, delegate { }); } catch (Exception) { } });
            Reject(delegate { Await(viewer.GetMonitors()); });
            Reject(delegate { viewer.SelectMonitor("two").GetAwaiter().GetResult(); });
            viewer.Dispose(); receiver.Wait(3000);
        }
    }
    static void EmergencyShortcutAfterHide()
    {
        using (MainForm main = new MainForm())
        {
            main.Show(); Application.DoEvents();
            if (!(bool)Field(main, "hotkeyRegistered")) { Console.WriteLine("SKIP Emergency shortcut: another application owns Ctrl+Alt+Shift+F12 on this desktop."); CloseDashboard(main); return; }
            IntPtr before = main.Handle;
            typeof(MainForm).GetMethod("HideDashboard", System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic).Invoke(main, null); Application.DoEvents();
            Check(main.Handle != before || (bool)Field(main, "hotkeyRegistered"), "The dashboard handle state is unexpected.");
            Check((bool)Field(main, "hotkeyRegistered"), "Hiding the dashboard dropped the emergency shortcut.");
            using (Form other = new Form())
            {
                IntPtr probe = other.Handle;
                bool taken = RegisterHotKey(probe, 0x4C56, 0x4007, 0x7B);
                if (taken) UnregisterHotKey(probe, 0x4C56);
                Check(!taken, "The emergency shortcut was free after hiding the dashboard, so it no longer reached Lume.");
            }
            CloseDashboard(main);
        }
    }
    static void CloseDashboard(MainForm main)
    {
        typeof(MainForm).GetField("exitRequested", System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic).SetValue(main, true);
        main.Close(); Application.DoEvents();
    }
}
