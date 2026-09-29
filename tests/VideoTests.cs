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
        Run("A local H.264 decoder failure keeps the image, acknowledges the frame and recovers with image frames", VideoDecodeFailure);
        Run("A buffered H.264 picture is flushed even when the next capture is unchanged", VideoPendingOutput);
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
    sealed class FailingVideoDecoder : IVideoFrameDecoder
    {
        public bool Fail, Disposed;
        public bool Decode(byte[] bytes,Bitmap image){ if(Disposed) throw new ObjectDisposedException("decoder"); if(Fail) throw new InvalidOperationException("Windows video codec failed (0xC00D36B4). Try Lossless or JPEG."); return true; }
        public void Dispose(){ Disposed=true; }
    }
    // Header-valid 640 x 360 baseline access units whose slice data is not decodable.
    static readonly byte[] Sps640={0,0,0,1,0x67,0x42,0xC0,0x1E,0xDA,0x02,0x80,0xBF,0xE5,0x40}, Sps1280={0,0,0,1,0x67,0x42,0xC0,0x1E,0xDA,0x01,0x40,0x16,0xE4};
    static readonly byte[] PpsIdr={0,0,0,1,0x68,0xCE,0x38,0x80,0,0,0,1,0x65,0x88,0x84,0x21,0xA0,0x13}, Delta={0,0,0,1,0x41,0x9A,0x24,0x6C,0x42};
    static byte[] Join(byte[] a,byte[] b){ byte[] r=new byte[a.Length+b.Length]; Buffer.BlockCopy(a,0,r,0,a.Length); Buffer.BlockCopy(b,0,r,a.Length,b.Length); return r; }
    static byte[] VideoFrame(int sequence,int w,int h,byte[] unit,bool reset)
    {
        return Wire.Message(Kind.Frame,delegate(BinaryWriter writer){ writer.Write(sequence);writer.Write(w);writer.Write(h);writer.Write(1);writer.Write(0);writer.Write(0);writer.Write(w);writer.Write(h);writer.Write((byte)2);writer.Write(unit.Length+2);writer.Write((byte)1);writer.Write((byte)(reset?1:0));writer.Write(unit); });
    }
    static void VideoDecodeFailure()
    {
        byte[] keyframe=Join(Sps640,PpsIdr); H264Bounds.Validate(keyframe,640,360,true); H264Bounds.Validate(Delta,640,360,false);
        var created=new System.Collections.Generic.List<FailingVideoDecoder>();
        using(var decoder=new FrameDecoder(delegate(int w,int h){ var next=new FailingVideoDecoder{Fail=created.Count==0}; created.Add(next); return next; }))
        using(var source=new Bitmap(640,360,PixelFormat.Format32bppRgb)) {
            DrawVideo(source,3); var images=new FrameEncoder();
            using(Packet packet=new Packet(images.Encode(source,1,85,true,true))) decoder.Apply(packet);
            int orange=decoder.Image.GetPixel(100,60).ToArgb(); Check(decoder.FrameReady && orange==Color.Orange.ToArgb(),"Image frame was not decoded.");
            using(Packet packet=new Packet(VideoFrame(2,640,360,keyframe,true))) decoder.Apply(packet);
            Check(!decoder.FrameReady && decoder.Sequence==2 && decoder.VideoFailed,"Failed video frame was not acknowledged without presentation.");
            Check(decoder.TakeVideoFailure() && !decoder.TakeVideoFailure(),"Decoder failure was not reported exactly once.");
            Check(created.Count==1 && created[0].Disposed && decoder.Image.GetPixel(100,60).ToArgb()==orange,"Failed decoder was kept or the last image was lost.");
            using(Packet packet=new Packet(VideoFrame(3,640,360,Delta,false))) decoder.Apply(packet);
            Check(!decoder.FrameReady && decoder.Sequence==3 && created.Count==1 && !decoder.TakeVideoFailure(),"In-flight video after a failure stalled or retried decoding.");
            Reject(delegate { using(Packet packet=new Packet(VideoFrame(4,640,360,Join(Sps1280,PpsIdr),true))) decoder.Apply(packet); });
            using(Packet packet=new Packet(images.Encode(source,5,85,true,true))) decoder.Apply(packet);
            Check(decoder.FrameReady && decoder.Sequence==5 && !decoder.VideoFailed,"Image frames did not resume after the fallback.");
            using(Packet packet=new Packet(VideoFrame(6,640,360,keyframe,true))) decoder.Apply(packet);
            Check(decoder.FrameReady && decoder.Sequence==6 && created.Count==2 && !created[1].Disposed,"An explicit new H.264 request did not create a fresh decoder.");
        }
        using(var decoder=new FrameDecoder(delegate(int w,int h){ throw new DllNotFoundException("LumeVideo.dll"); })) {
            using(Packet packet=new Packet(VideoFrame(1,640,360,keyframe,true))) decoder.Apply(packet);
            Check(!decoder.FrameReady && decoder.Sequence==1 && decoder.TakeVideoFailure(),"Missing decoder ended the session.");
        }
    }
    // Mimics a synchronous MFT that answers one delta input with NEED_MORE_INPUT.
    sealed class BufferingVideoEncoder : IVideoFrameEncoder
    {
        public int Calls;
        public bool Hardware { get { return false; } }
        public string Name { get { return "Buffering test encoder"; } }
        public byte[] Encode(Bitmap image,bool keyframe) { Calls++; return Calls==2 ? null : Join(Sps640,PpsIdr); }
        public void Dispose() { }
    }
    static void VideoPendingOutput()
    {
        var fake=new BufferingVideoEncoder();
        using(var encoder=new AdaptiveFrameEncoder(delegate(int w,int h,int fps,int kbps){ return fake; }))
        using(var decoder=new FrameDecoder(delegate(int w,int h){ return new FailingVideoDecoder(); }))
        using(var source=new Bitmap(640,360,PixelFormat.Format32bppRgb)) {
            StreamQuality quality=new StreamQuality {Video=true,Lossless=false,Fps=30,BitrateKbps=4000};
            DrawVideo(source,1); byte[] first=encoder.Encode(source,1,quality,false,60);
            Check(first!=null && !encoder.PendingOutput,"Initial keyframe missing.");
            using(Packet packet=new Packet(first)) decoder.Apply(packet);
            DrawVideo(source,2);
            Check(encoder.Encode(source,2,quality,false,60)==null && encoder.PendingOutput,"Buffered input was not tracked.");
            byte[] flushed=encoder.Encode(source,2,quality,false,60);
            Check(flushed!=null && fake.Calls==3 && !encoder.PendingOutput,"An unchanged capture did not flush the buffered picture.");
            using(Packet packet=new Packet(flushed)) decoder.Apply(packet);
            Check(decoder.FrameReady && decoder.Sequence==2,"Flushed picture was not delivered in order.");
            Check(encoder.Encode(source,3,quality,false,60)==null && fake.Calls==3,"Unchanged frames were encoded after the flush.");
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
