using System;
using System.Drawing;
using System.IO;
using System.Net;
using System.Threading;
using System.Threading.Tasks;
using LumeRemote;

static partial class Tests
{
    static void CollaborationChecks()
    {
        Run("Clipboard sync copies changes, avoids echoes and pauses conflicts", ClipboardSyncRules);
        Run("Session actions use permissions, receipts and safe power callbacks", SessionActions);
        Run("Stale monitor input cannot move the pointer on a new display", StaleMonitorInput);
        Run("Annotation coordinates and stroke sizes are bounded", AnnotationBounds);
        Run("Session loops leave a constrained thread pool available for file actions", SessionWorkerIsolation);
    }
    static void ClipboardSyncRules()
    {
        string local = "local initial", remote = "remote initial"; int writes = 0;
        using (var sync = new ClipboardSync(delegate { return Task.FromResult(local); }, delegate(string text) { local = text; writes++; return Task.FromResult(true); }, delegate { return Task.FromResult(remote); }, delegate(string text) { remote = text; writes++; return Task.FromResult(true); }, delegate { return true; }))
        {
            sync.Poll().GetAwaiter().GetResult(); Check(writes == 0, "Enabling sync replaced an existing clipboard.");
            local = "copied locally"; sync.Poll().GetAwaiter().GetResult(); Check(remote == local && writes == 1, "Local text did not sync.");
            sync.Poll().GetAwaiter().GetResult(); Check(writes == 1, "Clipboard echo caused a redundant write.");
            remote = "copied remotely"; sync.Poll().GetAwaiter().GetResult(); Check(local == remote && writes == 2, "Remote text did not sync.");
            local = "local conflict"; remote = "remote conflict"; Reject(delegate { sync.Poll().GetAwaiter().GetResult(); });
            Check(local == "local conflict" && remote == "remote conflict" && writes == 2, "Conflict replaced text.");
        }
        bool active = true; var delayed = new TaskCompletionSource<string>();
        using (var sync = new ClipboardSync(delegate { return Task.FromResult("local"); }, delegate { writes++; return Task.FromResult(true); }, delegate { return delayed.Task; }, delegate { writes++; return Task.FromResult(true); }, delegate { return active; }))
        { Task poll = sync.Poll(); active = false; sync.Dispose(); delayed.SetResult("stale"); poll.GetAwaiter().GetResult(); Check(writes == 2, "Disposed session copied stale clipboard text."); }
    }
    static void SessionActions()
    {
        string clipboardValue = "fixture"; int powers = 0, permitted = 0;
        using (var host = new HostService(delegate { return new Synthetic(); }, Profile.All[1], true, delegate { return true; }, delegate { }, delegate { },
            delegate(string text) { clipboardValue = text; return Task.FromResult(true); }, null, delegate { return Task.FromResult(clipboardValue); }, true,
            delegate { permitted++; return Task.FromResult(false); }, delegate(PowerAction action) { powers += (int)action; return Task.FromResult(true); }, true))
        using (var viewer = new ViewerConnection())
        {
            host.Start(IPAddress.Loopback, 0, "127.0.0.1"); viewer.Connect(host.Invite, "Local collaboration QA fixture");
            Task receiver = Task.Run(delegate { try { using (var decoder = new FrameDecoder()) viewer.Receive(delegate(Packet p) { decoder.Apply(p); viewer.Ack(decoder.Sequence); }, delegate { }); } catch { } });
            viewer.SetClipboardText(new string('a', 50000)).GetAwaiter().GetResult(); Check(Await(viewer.GetClipboard()).Length == 50000, "Clipboard action receipt lost data.");
            viewer.SendChat("Synthetic local QA message. No external recipient.").GetAwaiter().GetResult();
            viewer.Power(PowerAction.Lock).GetAwaiter().GetResult(); viewer.Power(PowerAction.Restart).GetAwaiter().GetResult(); viewer.Power(PowerAction.ShutDown).GetAwaiter().GetResult();
            Check(powers == 6, "Power actions did not reach the safe callbacks.");
            Reject(delegate { Await(viewer.Tools.Request(SessionTool.Audio, delegate(System.IO.BinaryWriter w) { w.Write(true); w.Write(1); })); });
            Check(permitted == 1 && host.HasSession, "Audio permission failure stopped video or skipped consent.");
            viewer.Annotate(new Point[0], 1).GetAwaiter().GetResult(); viewer.Dispose(); receiver.Wait(3000);
            Console.WriteLine("POWER: injected callbacks only. Windows lock, restart and shutdown were not executed.");
        }
    }
    static void StaleMonitorInput()
    {
        int injected = 0;
        using (var host = new HostService(delegate { return new SyntheticMonitors(); }, Profile.All[3], true, delegate { return true; }, delegate { }, delegate { }))
        using (var viewer = new ViewerConnection())
        {
            host.InputFactory = delegate(Rectangle bounds) { return new InputController(bounds, delegate { Interlocked.Increment(ref injected); }); };
            host.Start(IPAddress.Loopback, 0, "127.0.0.1"); viewer.Connect(host.Invite, "Input epoch fixture");
            Task receiver = Task.Run(delegate { try { using (var decoder = new FrameDecoder()) viewer.Receive(delegate(Packet p) { decoder.Apply(p); viewer.Ack(decoder.Sequence); }, delegate { }); } catch { } });
            viewer.Input(0, 100, 100, 1); Spin(delegate { return injected == 1; }, 1000, "Authorized input did not arrive.");
            viewer.SelectMonitor("two").GetAwaiter().GetResult(); viewer.Input(0, 200, 200, 1); Thread.Sleep(50); Check(injected == 1, "Viewer relabelled stale queued input.");
            Wire wire = (Wire)Field(viewer, "wire"); wire.Send(Kind.Input, delegate(System.IO.BinaryWriter w) { w.Write(1); w.Write((byte)0); w.Write(500); w.Write(500); });
            Thread.Sleep(100); Check(injected == 1, "Host accepted stale coordinates.");
            viewer.Input(0, 300, 300, 2); Spin(delegate { return injected == 2; }, 1000, "New display rejected fresh input."); viewer.Dispose(); receiver.Wait(3000);
        }
    }
    static void AnnotationBounds()
    {
        using (var packet = new Packet(SessionTools.Payload(delegate(System.IO.BinaryWriter w) { w.Write(129); }))) Reject(delegate { AnnotationWire.Read(packet); });
        using (var packet = new Packet(SessionTools.Payload(delegate(System.IO.BinaryWriter w) { w.Write(2); w.Write(-1); w.Write(0); w.Write(65535); w.Write(65535); }))) Reject(delegate { AnnotationWire.Read(packet); });
        using (Bitmap bitmap = new Bitmap(100, 100)) using (Graphics graphics = Graphics.FromImage(bitmap))
        { graphics.Clear(Color.Black); AnnotationWire.Draw(graphics, new Rectangle(0, 0, 100, 100), new[] { new Point(0, 0), new Point(65535, 65535) }); Check(bitmap.GetPixel(50, 50).G > 100 && bitmap.GetPixel(50, 10).G == 0, "Annotation mapping changed."); }
    }
    static void SessionWorkerIsolation()
    {
        int minWorker, minIo, maxWorker, maxIo; ThreadPool.GetMinThreads(out minWorker, out minIo); ThreadPool.GetMaxThreads(out maxWorker, out maxIo);
        Check(ThreadPool.SetMinThreads(1, minIo) && ThreadPool.SetMaxThreads(4, maxIo), "Could not constrain the fixture's thread pool.");
        try
        {
            using (var fixture = new FilesFixture())
            {
                string file = Path.Combine(fixture.Root, "worker.txt"), folder = Path.Combine(fixture.Root, "remote"); File.WriteAllText(file, "pool isolation fixture");
                Check(File.Exists(Await(fixture.Viewer.Files.Upload(file, folder, CancellationToken.None), 5000)), "File action was starved by idle session workers.");
                Check(Await(fixture.Viewer.Files.List(folder), 5000).Entries.Count == 1, "Directory action was starved.");
            }
        }
        finally { ThreadPool.SetMaxThreads(maxWorker, maxIo); ThreadPool.SetMinThreads(minWorker, minIo); }
    }
}
