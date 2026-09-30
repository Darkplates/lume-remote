using System;
using System.Collections.Generic;
using System.Collections.Concurrent;
using System.Drawing;
using System.IO;
using System.Net;
using System.Net.Security;
using System.Net.Sockets;
using System.Security.Authentication;
using System.Security.Cryptography.X509Certificates;
using System.Threading;
using System.Threading.Tasks;
using LumeRemote;

static partial class Tests
{
    // Observe real production Windows tool receipts without modifying the product handler.
    // Only empty annotation clear is forwarded, so HostAnnotations never creates an overlay.
    static void PortableAnnotationsFixture(string file)
    {
        string path = Path.GetFullPath(file); Check(!File.Exists(path), "Use a new annotation fixture path.");
        using (var host = new HostService(delegate { return new SyntheticMonitors(); }, Profile.All[3], true,
            delegate { return true; }, delegate { throw new InvalidOperationException("Clipboard is forbidden in this fixture."); }, delegate { }, enableChat: true))
        {
            host.InputFactory = delegate(Rectangle bounds) { return new InputController(bounds, delegate { throw new InvalidOperationException("Real input is forbidden in this fixture."); }); };
            host.Start(IPAddress.Loopback, 0, "127.0.0.1");
            using (var relay = new AnnotationReceiptRelay(host, path + ".receipts"))
            {
                File.WriteAllText(path, relay.Invitation.ToString());
                try
                {
                    var clock = System.Diagnostics.Stopwatch.StartNew();
                    while (!File.Exists(path + ".stop") && clock.Elapsed.TotalSeconds < 120) Thread.Sleep(25);
                    Check(File.Exists(path + ".stop"), "Annotation fixture stop exceeded its bound.");
                    Check(relay.Failure == null, "The annotation receipt relay failed: " + (relay.Failure == null ? "" : relay.Failure.GetType().Name));
                    Check(relay.Accepted == 3 && relay.Rejected == 0, "The real Windows gate did not accept exactly three clear receipts.");
                    Console.WriteLine("PASS Real Windows annotation gate accepted 3 empty clears across display epochs 1/2/3; no overlay, clipboard or input.");
                }
                finally { File.Delete(path); }
            }
        }
    }
    sealed class AnnotationReceiptRelay : IDisposable
    {
        readonly TcpListener listener = new TcpListener(IPAddress.Loopback, 0);
        readonly List<TcpClient> clients = new List<TcpClient>();
        readonly Task worker;
        readonly Invitation destination;
        readonly X509Certificate2 certificate;
        readonly string receipts;
        volatile bool stopped;
        public Invitation Invitation { get; private set; }
        public Exception Failure;
        public int Accepted, Rejected;
        public AnnotationReceiptRelay(HostService host, string receipts)
        {
            this.receipts = receipts; destination = host.Invite; certificate = (X509Certificate2)Field(host, "certificate");
            Check(!File.Exists(receipts), "Preserve previous annotation receipts."); File.WriteAllText(receipts, "");
            listener.Start(1); Invitation = LumeRemote.Invitation.Parse(destination.ToString()); Invitation.Port = ((IPEndPoint)listener.LocalEndpoint).Port;
            worker = BackgroundWork.Run(delegate
            {
                try
                {
                    using (TcpClient incoming = listener.AcceptTcpClient())
                    using (TcpClient outgoing = Transport.Connect(destination.Host, destination.Port, 3000))
                    {
                        lock (clients) { clients.Add(incoming); clients.Add(outgoing); }
                        using (var fromViewer = new SslStream(incoming.GetStream(), false))
                        using (var fromHost = new SslStream(outgoing.GetStream(), false, delegate(object sender, X509Certificate cert, X509Chain chain, SslPolicyErrors errors) { return cert != null && Security.Equal(Security.Pin(cert), destination.Fingerprint); }))
                        {
                            fromViewer.ReadTimeout = fromHost.ReadTimeout = 15000; fromViewer.WriteTimeout = fromHost.WriteTimeout = 15000;
                            fromViewer.AuthenticateAsServer(certificate, false, SslProtocols.None, false); Security.CheckTls(fromViewer);
                            fromHost.AuthenticateAsClient("lume-remote", null, SslProtocols.None, false); Security.CheckTls(fromHost);
                            var annotations = new ConcurrentDictionary<long, int>();
                            Task upload = BackgroundWork.Run(delegate { Forward(fromViewer, fromHost, true, annotations); });
                            Forward(fromHost, fromViewer, false, annotations);
                            incoming.Close(); outgoing.Close(); Check(upload.Wait(3000), "Annotation relay upload did not stop.");
                        }
                    }
                }
                catch (Exception error) { if (!stopped) Failure = error; }
            });
        }
        void Forward(SslStream source, SslStream target, bool fromViewer, ConcurrentDictionary<long, int> annotations)
        {
            try
            {
                var output = new Wire(target);
                while (!stopped)
                {
                    int count = BitConverter.ToInt32(Wire.ReadExact(source, 4), 0);
                    Check(count >= 1 && count <= (fromViewer ? 270000 : 4 * 1024 * 1024), "Annotation relay packet exceeded its bound.");
                    byte[] bytes = Wire.ReadExact(source, count);
                    using (var packet = new Packet(bytes))
                    {
                        if (fromViewer)
                        {
                            Check(packet.Kind == Kind.Auth || packet.Kind == Kind.Ack || packet.Kind == Kind.Release || packet.Kind == Kind.Ping || packet.Kind == Kind.Quality || packet.Kind == Kind.Goodbye || packet.Kind == Kind.Tools, "Unsafe action was blocked by the annotation fixture.");
                            if (packet.Kind == Kind.Tools)
                            {
                                long id = packet.Reader.ReadInt64(); SessionTool op = (SessionTool)packet.Reader.ReadByte();
                                Check(op == SessionTool.Monitors || op == SessionTool.SelectMonitor || op == SessionTool.PortableImages || op == SessionTool.Annotation, "Unsafe tool was blocked by the annotation fixture.");
                                if (op == SessionTool.Annotation)
                                {
                                    int epoch = packet.Reader.ReadInt32(); Point[] points = AnnotationWire.Read(packet);
                                    Check(points.Length == 0 && annotations.TryAdd(id, epoch), "Nonempty or repeated annotation was blocked by the fixture.");
                                }
                            }
                        }
                        else if (packet.Kind == Kind.ToolReply)
                        {
                            long id = packet.Reader.ReadInt64(); bool success = packet.Reader.ReadBoolean(); string error = packet.Text(1024);
                            int size = packet.Reader.ReadInt32(); byte[] body = packet.Reader.ReadBytes(size); packet.End(); int epoch;
                            if (annotations.TryRemove(id, out epoch))
                            {
                                Check(size == 1 && body[0] == (byte)Kind.Tools, "Annotation receipt body differs.");
                                if (success) Interlocked.Increment(ref Accepted); else Interlocked.Increment(ref Rejected);
                                File.AppendAllText(receipts, epoch + "\t" + (success ? "accepted" : "rejected") + "\n");
                            }
                        }
                    }
                    output.Send(bytes);
                }
            }
            catch (EndOfStreamException) { }
            catch (IOException) { }
            catch (ObjectDisposedException) { }
            catch (Exception error) { if (!stopped) Failure = error; }
        }
        public void Dispose()
        {
            stopped = true; listener.Stop(); lock (clients) foreach (TcpClient client in clients) client.Close();
            Check(worker.Wait(5000), "Annotation receipt relay did not stop.");
        }
    }
}
