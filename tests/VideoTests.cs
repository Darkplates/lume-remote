using System;
using System.Diagnostics;
using System.Drawing;
using System.Drawing.Imaging;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using LumeRemote;

static partial class Tests
{
    static void VideoChecks()
    {
        Run("Versioned quality preserves legacy image settings and video bitrate", VideoQuality);
        Run("Video dimensions and malformed sequence headers are bounded", VideoBounds);
        Run("Windows H.264 software round trip preserves geometry and approximate colors", VideoSoftware);
        Run("Automatic H.264 and image mode transitions decode every acknowledged frame", VideoTransitions);
        Run("Latest-image queue replaces stale presentation without losing delta decoding", LatestPresentation);
        Run("Timing packets reject invalid numbers and overlong backend names", MetricsBounds);
        Run("Pinned certificate negotiates protocol 4 and still identifies legacy hosts", ProtocolCertificate);
        Run("Authenticated P2P video changes codec live and reports host timings", VideoSession);
        Run("Protocol 2 clients still receive the exact legacy quality layout", LegacyViewer);
        Run("New viewer negotiates and decodes a legacy protocol 2 host", LegacyHost);
        Run("Unsupported video dimensions fall back to full lossless frames", VideoFallback);
    }
    static void VideoQuality()
    {
        StreamQuality quality = new StreamQuality { Height=1080, Fps=60, Video=true, Lossless=false, BitrateKbps=4500 };
        using (Packet packet = new Packet(Wire.Message(Kind.Quality, delegate(BinaryWriter writer) { quality.Write(writer,3); }))) {
            StreamQuality decoded=StreamQuality.Read(packet.Reader,3); packet.End();
            Check(decoded.Video && !decoded.Lossless && decoded.BitrateKbps==4500 && decoded.Fps==60,"Video settings changed.");
        }
        quality.Video=false; quality.Lossless=true;
        using (Packet packet = new Packet(Wire.Message(Kind.Quality,quality.Write))) {
            StreamQuality decoded=StreamQuality.Read(packet.Reader); packet.End(); Check(decoded.Lossless && !decoded.Video,"Legacy quality changed.");
        }
        Reject(delegate { new StreamQuality {Video=true,Lossless=true}.Validate(); });
        Reject(delegate { new StreamQuality {BitrateKbps=Int32.MaxValue}.Validate(); });
    }
    static void VideoBounds()
    {
        Reject(delegate { using(var codec=new VideoEncoder(641,360,60,8000)){} });
        Reject(delegate { using(var codec=new VideoDecoder(8000,8000)){} });
        Reject(delegate { H264Bounds.Validate(new byte[]{0,0,1,0x67,0xff,0xff},640,360,true); });
        Reject(delegate { H264Bounds.Validate(new byte[]{0,0,1,0x65,0x80},640,360,true); });
        Random random=new Random(947); for(int i=0;i<300;i++) { byte[] bytes=new byte[random.Next(4,128)]; random.NextBytes(bytes); Reject(delegate { H264Bounds.Validate(bytes,640,360,true); }); }
    }
    static void DrawVideo(Bitmap image,int frame)
    {
        using(Graphics graphics=Graphics.FromImage(image)) {
            graphics.Clear(Color.FromArgb(20,40,70));
            int x=20+(frame*13)%Math.Max(1,image.Width-160);
            graphics.FillRectangle(Brushes.Orange,x,30,100,100);
            graphics.DrawString("Lume video validation",SystemFonts.DefaultFont,Brushes.White,12,image.Height-32);
        }
    }
    static void VideoSoftware()
    {
        using(var encoder=new VideoEncoder(640,360,60,4000,2))
        using(var decoder=new VideoDecoder(640,360))
        using(var source=new Bitmap(640,360,PixelFormat.Format32bppRgb))
        using(var target=new Bitmap(640,360,PixelFormat.Format32bppRgb)) {
            Check(!encoder.Hardware,"Software encoder was labelled hardware."); int count=0;
            for(int i=0;i<12;i++) {
                DrawVideo(source,i); byte[] bytes=encoder.Encode(source,i==0); Check(bytes!=null,"Low-latency encoder buffered a frame.");
                H264Bounds.Validate(bytes,640,360,i==0);
                if(i==0) Reject(delegate { H264Bounds.Validate(bytes,1280,720,true); });
                if(decoder.Decode(bytes,target)) count++;
            }
            Color actual=target.GetPixel(5,5); Check(count==12 && Math.Abs(actual.R-20)<8 && Math.Abs(actual.G-40)<8 && Math.Abs(actual.B-70)<8,"Decoded frame count or color is wrong.");
        }
    }
    static void VideoTransitions()
    {
        using(var encoder=new AdaptiveFrameEncoder()) using(var decoder=new FrameDecoder())
        using(var source=new Bitmap(640,360,PixelFormat.Format32bppRgb)) {
            for(int i=1;i<=18;i++) {
                DrawVideo(source,i);
                StreamQuality quality=new StreamQuality {Video=i<7 || i>12,Lossless=i>=7 && i<=12,Fps=60,BitrateKbps=4000};
                byte[] bytes=encoder.Encode(source,i,quality,true,60); Check(bytes!=null,"No complete mode-transition frame.");
                using(Packet packet=new Packet(bytes)) decoder.Apply(packet);
                Check(decoder.FrameReady && decoder.Sequence==i && decoder.Image.Width==640,"Video transition dropped a frame.");
            }
            Console.WriteLine("CODEC: " + encoder.Backend + "; hardware=" + encoder.Hardware);
        }
    }
    static void LatestPresentation()
    {
        using(var queue=new LatestFrameQueue()) using(var encoder=new AdaptiveFrameEncoder()) using(var decoder=new FrameDecoder())
        using(var source=new Bitmap(320,180,PixelFormat.Format32bppRgb)) {
            for(int i=1;i<=8;i++) {
                DrawVideo(source,i);
                using(Packet packet=new Packet(encoder.Encode(source,i,StreamQuality.Source,i==1,60))) decoder.Apply(packet);
                queue.Publish((Bitmap)decoder.Image.Clone());
            }
            using(Bitmap latest=queue.Take()) { Check(latest!=null && latest.GetPixel(220,35).ToArgb()==Color.Orange.ToArgb(),"Newest decoded image was not presented."); }
            Check(queue.Take()==null && decoder.Sequence==8,"Presentation queue retained stale images or dropped delta decoding.");
        }
    }
    static void MetricsBounds()
    {
        StreamMetrics metrics=new StreamMetrics {CaptureMilliseconds=1.5,EncodeMilliseconds=5,WaitMilliseconds=2,SendMilliseconds=1,ChecksPerSecond=60,Backend="Test codec",Hardware=true};
        using(Packet packet=new Packet(Wire.Message(Kind.StreamMetrics,metrics.Write))) { StreamMetrics decoded=StreamMetrics.Read(packet.Reader); packet.End(); Check(decoded.Hardware && decoded.Backend==metrics.Backend,"Timing packet changed."); }
        metrics.EncodeMilliseconds=Double.NaN;
        using(Packet packet=new Packet(Wire.Message(Kind.StreamMetrics,metrics.Write))) Reject(delegate { StreamMetrics.Read(packet.Reader); });
        metrics.EncodeMilliseconds=1; metrics.Backend=new string('a',513);
        using(Packet packet=new Packet(Wire.Message(Kind.StreamMetrics,metrics.Write))) Reject(delegate { StreamMetrics.Read(packet.Reader); });
    }
    static void ProtocolCertificate()
    {
        using(var current=Security.Certificate()) using(var previous=Security.Certificate(2)) {
            Check(current.GetNameInfo(System.Security.Cryptography.X509Certificates.X509NameType.SimpleName,false)=="Lume Remote Session v4","New protocol not advertised.");
            Check(previous.GetNameInfo(System.Security.Cryptography.X509Certificates.X509NameType.SimpleName,false)=="Lume Remote Session","Legacy advertisement changed.");
            Check(Security.Pin(current)!=Security.Pin(previous),"Certificate pin did not authenticate the version metadata.");
        }
    }
    static void VideoSession()
    {
        using(var host=new HostService(delegate { return new Synthetic(); },Profile.All[1],false,delegate {return true;},delegate {},delegate {}))
        using(var first=new PeerTransport(false)) using(var second=new PeerTransport(false)) using(var viewer=new ViewerConnection()) {
            host.StartPeer(); PairPeers(first,second); host.AcceptPeer(first); viewer.ConnectPeer(second,host.Invite,"Video integration fixture");
            Check(viewer.ProtocolVersion==4,"New protocol was not negotiated.");
            viewer.SetQuality(new StreamQuality {Video=true,Lossless=false,Fps=30,BitrateKbps=4000}); int videos=0, images=0; bool completed=false;
            Task receive=Task.Run(delegate {
                using(var decoder=new FrameDecoder()) try { viewer.Receive(delegate(Packet packet) {
                    decoder.Apply(packet); viewer.Ack(decoder.Sequence); var quality=viewer.CurrentQuality;
                    if(quality!=null && quality.Video && decoder.FrameReady) videos++;
                    if(videos>=8 && viewer.Metrics!=null && quality.Video) viewer.SetQuality(StreamQuality.Source);
                    if(videos>=8 && quality!=null && !quality.Video && decoder.FrameReady && ++images>=3) { completed=true;viewer.Dispose(); }
                },delegate {}); } catch { if(!completed) throw; }
            });
            if(!receive.Wait(20000)){viewer.Dispose();throw new Exception("Video stream or timing response timed out.");}
            Check(completed && videos>=8 && images>=3 && viewer.Metrics!=null,"Video/live-quality/metrics integration incomplete.");
        }
    }
    static void LegacyViewer()
    {
        using(var host=NewHost(false,true)) using(var raw=new RawClient(host.Invite)) using(var decoder=new FrameDecoder()) {
            raw.Wire.Send(Kind.Auth,delegate(BinaryWriter writer){writer.Write(2);Wire.Text(writer,host.Invite.Secret);Wire.Text(writer,"Legacy fixture");});
            using(Packet packet=raw.Wire.Read(2048)) { Check(packet.Kind==Kind.Accepted,"Legacy approval failed.");packet.Reader.ReadBoolean();packet.Text(256);packet.Reader.ReadInt32();packet.Reader.ReadInt32();Check(packet.Reader.ReadInt32()==2,"Legacy protocol changed.");packet.Reader.ReadInt32();packet.End(); }
            int frames=0;bool quality=false;
            while(frames<3) using(Packet packet=raw.Wire.Read(Wire.MaxPacket)) {
                if(packet.Kind==Kind.QualityInfo) {var settings=StreamQuality.Read(packet.Reader);packet.Reader.ReadInt32();packet.Reader.ReadInt32();packet.Reader.ReadInt32();packet.End();Check(!settings.Video,"Legacy received video.");quality=true;}
                else if(packet.Kind==Kind.Frame) {decoder.Apply(packet);raw.Wire.Send(Kind.Ack,delegate(BinaryWriter writer){writer.Write(decoder.Sequence);});frames++;}
                else Check(packet.Kind!=Kind.StreamMetrics,"Unnegotiated metrics reached a legacy viewer.");
            }
            Check(quality,"No legacy quality metadata.");
        }
    }
    static void LegacyHost()
    {
        using(var certificate=Security.Certificate(2)) using(var viewer=new ViewerConnection()) {
            var listener=new System.Net.Sockets.TcpListener(System.Net.IPAddress.Loopback,0);listener.Start();
            var invite=new Invitation {Host="127.0.0.1",Port=((System.Net.IPEndPoint)listener.LocalEndpoint).Port,Room="",Secret=Security.Token(32),Fingerprint=Security.Pin(certificate)};
            Task server=Task.Run(delegate {
                using(var client=listener.AcceptTcpClient()) using(var tls=new System.Net.Security.SslStream(client.GetStream(),false)) {
                    tls.ReadTimeout=tls.WriteTimeout=10000;tls.AuthenticateAsServer(certificate,false,System.Security.Authentication.SslProtocols.None,false);
                    var wire=new Wire(tls);
                    using(Packet auth=wire.Read(2048)){Check(auth.Kind==Kind.Auth && auth.Reader.ReadInt32()==2,"Viewer did not negotiate legacy protocol.");Check(Security.Equal(auth.Text(128),invite.Secret),"Legacy authentication changed.");auth.Text(128);auth.End();}
                    wire.Send(Kind.Accepted,delegate(BinaryWriter w){w.Write(false);Wire.Text(w,"Legacy host");w.Write(640);w.Write(360);w.Write(2);w.Write(60);});
                    using(Packet request=wire.Read(2048)){Check(request.Kind==Kind.Quality,"No quality request.");StreamQuality.Read(request.Reader);request.End();}
                    wire.Send(Kind.QualityInfo,delegate(BinaryWriter w){StreamQuality.Source.Write(w);w.Write(640);w.Write(360);w.Write(60);});
                    using(var source=new Bitmap(640,360,PixelFormat.Format32bppRgb)){DrawVideo(source,1);wire.Send(new FrameEncoder().Encode(source,1,85,true,true));}
                    using(Packet ack=wire.Read(2048)){Check(ack.Kind==Kind.Ack && ack.Reader.ReadInt32()==1,"No legacy acknowledgement.");ack.End();}
                }
            });
            try {
                viewer.Connect(invite,"New viewer fixture");Check(viewer.ProtocolVersion==2,"Incorrect old-host protocol.");
                viewer.SetQuality(new StreamQuality{Video=true,Lossless=false,Fps=60});bool complete=false;
                using(var decoder=new FrameDecoder()) try{viewer.Receive(delegate(Packet packet){decoder.Apply(packet);viewer.Ack(decoder.Sequence);complete=true;viewer.Dispose();},delegate{});}catch{if(!complete)throw;}
                Check(complete && server.Wait(12000),"Legacy host stream incomplete.");
            }finally{listener.Stop();}
        }
    }
    sealed class OddVideoSource : IScreenSource
    {
        readonly Bitmap image=new Bitmap(641,361,PixelFormat.Format32bppRgb);
        public Rectangle Bounds {get{return new Rectangle(0,0,641,361);}}
        public Bitmap Capture(){return image;}
        public void Dispose(){image.Dispose();}
    }
    static void VideoFallback()
    {
        using(var host=new HostService(delegate{return new OddVideoSource();},Profile.All[3],false,delegate{return true;},delegate{},delegate{}))
        using(var viewer=new ViewerConnection()) {
            host.Start(System.Net.IPAddress.Loopback,0,"127.0.0.1");viewer.Connect(host.Invite,"Video fallback fixture");
            viewer.SetQuality(new StreamQuality{Video=true,Lossless=false,Fps=60});bool notice=false,complete=false;
            Task receive=Task.Run(delegate{using(var decoder=new FrameDecoder())try{viewer.Receive(delegate(Packet packet){decoder.Apply(packet);viewer.Ack(decoder.Sequence);if(notice && viewer.CurrentQuality!=null && viewer.CurrentQuality.Lossless){Check(decoder.Image.Width==641 && decoder.Image.Height==361,"Fallback changed source dimensions.");complete=true;viewer.Dispose();}},delegate(string message){notice=message.Contains("Using lossless");});}catch{if(!complete)throw;}});
            if(!receive.Wait(15000)){viewer.Dispose();throw new Exception("No fallback frame.");}Check(notice && complete,"Codec fallback was not visible and usable.");
        }
    }
    static void VideoBenchmark()
    {
        Console.WriteLine("SYNTHETIC LOCAL CODEC BENCHMARK - no screen capture, network, or competitor comparison.");
        foreach(int mode in new int[]{0,2}) {
            using(var encoder=new VideoEncoder(1920,1080,60,8000,mode)) using(var decoder=new VideoDecoder(1920,1080))
            using(var source=new Bitmap(1920,1080,PixelFormat.Format32bppRgb)) using(var target=new Bitmap(1920,1080,PixelFormat.Format32bppRgb)) {
                double encode=0,decode=0; long bytes=0; int ready=0; Process process=Process.GetCurrentProcess(); TimeSpan cpu=process.TotalProcessorTime; Stopwatch wall=Stopwatch.StartNew();
                for(int i=0;i<50;i++) { DrawVideo(source,i); Stopwatch clock=Stopwatch.StartNew(); byte[] encoded=encoder.Encode(source,i==0); double encoding=clock.Elapsed.TotalMilliseconds; if(encoded==null)throw new Exception("Buffered video benchmark frame."); H264Bounds.Validate(encoded,1920,1080,i==0); clock.Restart(); bool decoded=decoder.Decode(encoded,target); double decoding=clock.Elapsed.TotalMilliseconds; if(i>=5){ encode+=encoding;decode+=decoding;bytes+=encoded.Length;if(decoded)ready++;} Thread.Sleep(2); }
                process.Refresh(); double cpuPercent=(process.TotalProcessorTime-cpu).TotalMilliseconds/wall.Elapsed.TotalMilliseconds/Environment.ProcessorCount*100;
                Console.WriteLine(String.Format(System.Globalization.CultureInfo.InvariantCulture,"{0}; hardware={1}; 1080p; 45 measured frames; encode={2:0.00} ms; decode={3:0.00} ms; codec-only={4:0.0} fps; decoded={5}; encoded={6} bytes; normalized CPU={7:0.0}%",encoder.Name,encoder.Hardware,encode/45,decode/45,45000/(encode+decode),ready,bytes,cpuPercent));
            }
        }
    }
}
