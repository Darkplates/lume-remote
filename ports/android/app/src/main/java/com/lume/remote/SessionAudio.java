package com.lume.remote;

import android.media.*;
import android.media.audiofx.AcousticEchoCanceler;
import org.json.JSONObject;
import java.nio.ByteBuffer;

/** Devices run off the UI/network workers. Permission and per-call consent are separate gates. */
final class SessionAudio implements AutoCloseable {
    private final SessionService.Session session;
    private final Thread worker;
    private volatile boolean stopped,localConsent;
    private volatile int previousGeneration;
    private AudioTrack system,voice;
    private AudioRecord microphone;
    private AcousticEchoCanceler echo;
    private int audioGeneration,voiceGeneration;
    private final ByteBuffer pcm=ByteBuffer.allocateDirect(19200);
    private final int[] generation=new int[1];
    private final byte[] mono=new byte[1920],stereo=new byte[3840];
    SessionAudio(SessionService.Session s){session=s;worker=new Thread(this::run,"lume-audio-"+s.handle);worker.start();}
    void consent(boolean enabled){JSONObject m=session.state.optJSONObject("media");previousGeneration=m==null?0:m.optInt("voice_generation");localConsent=enabled;}
    private AudioTrack player(){int min=AudioTrack.getMinBufferSize(48000,AudioFormat.CHANNEL_OUT_STEREO,AudioFormat.ENCODING_PCM_16BIT);if(min<=0)throw new IllegalStateException();AudioTrack p=new AudioTrack.Builder().setAudioAttributes(new AudioAttributes.Builder().setUsage(AudioAttributes.USAGE_VOICE_COMMUNICATION).setContentType(AudioAttributes.CONTENT_TYPE_SPEECH).build()).setAudioFormat(new AudioFormat.Builder().setSampleRate(48000).setChannelMask(AudioFormat.CHANNEL_OUT_STEREO).setEncoding(AudioFormat.ENCODING_PCM_16BIT).build()).setBufferSizeInBytes(Math.max(min,7680)).setTransferMode(AudioTrack.MODE_STREAM).build();if(p.getState()!=AudioTrack.STATE_INITIALIZED){p.release();throw new IllegalStateException();}p.play();return p;}
    @android.annotation.SuppressLint("MissingPermission")
    private void startMicrophone(){int min=AudioRecord.getMinBufferSize(48000,AudioFormat.CHANNEL_IN_MONO,AudioFormat.ENCODING_PCM_16BIT);if(min<=0)throw new IllegalStateException();AudioRecord m=new AudioRecord.Builder().setAudioSource(MediaRecorder.AudioSource.VOICE_COMMUNICATION).setAudioFormat(new AudioFormat.Builder().setSampleRate(48000).setChannelMask(AudioFormat.CHANNEL_IN_MONO).setEncoding(AudioFormat.ENCODING_PCM_16BIT).build()).setBufferSizeInBytes(Math.max(min,3840)).build();if(m.getState()!=AudioRecord.STATE_INITIALIZED){m.release();throw new IllegalStateException();}microphone=m;if(AcousticEchoCanceler.isAvailable()){echo=AcousticEchoCanceler.create(m.getAudioSessionId());if(echo!=null)echo.setEnabled(true);}m.startRecording();}
    private void play(AudioTrack device,int kind,int expected){for(int i=0;i<4;i++){pcm.clear();int size=Native.audio(session.handle,kind,pcm,generation);if(size<=0)break;if(generation[0]!=expected)continue;pcm.position(0);pcm.limit(size);device.write(pcm,size,AudioTrack.WRITE_NON_BLOCKING);}}
    private void run(){try{while(!stopped&&!session.closed){try{
        JSONObject m=session.state.optJSONObject("media");boolean connected=session.state.optBoolean("connected");boolean a=m!=null&&connected&&m.optBoolean("audio"),v=m!=null&&connected&&localConsent&&m.optBoolean("voice")&&m.optInt("voice_generation")!=previousGeneration;
        int ag=m==null?0:m.optInt("audio_generation"),vg=m==null?0:m.optInt("voice_generation");
        if(!a||ag!=audioGeneration){release(system);system=null;}if(!v||vg!=voiceGeneration)stopVoice();audioGeneration=ag;voiceGeneration=vg;
        if(a&&system==null)system=player();if(v&&voice==null){voice=player();startMicrophone();}
        if(system!=null)play(system,20,ag);if(voice!=null)play(voice,21,vg);
        if(microphone!=null&&localConsent){int count=microphone.read(mono,0,mono.length,AudioRecord.READ_NON_BLOCKING);if(count<0)throw new IllegalStateException();count-=count%2;for(int i=0;i<count;i+=2){int d=i*2;stereo[d]=stereo[d+2]=mono[i];stereo[d+1]=stereo[d+3]=mono[i+1];}if(count>0&&!Native.microphone(session.handle,vg,stereo,count*2))stopVoice();}
    }catch(Exception e){localConsent=false;release(system);system=null;stopVoice();session.error="Audio unavailable. Check microphone permission and audio output, then retry.";disable("audio");disable("voice");}try{Thread.sleep(10);}catch(InterruptedException e){break;}}}finally{release(system);stopVoice();}}
    private void disable(String kind){try{session.action(new JSONObject().put("type",kind).put("enabled",false));}catch(Exception ignored){}}
    private static void release(AudioTrack p){if(p!=null){try{p.pause();p.flush();}catch(Exception ignored){}p.release();}}
    private void stopVoice(){if(microphone!=null){try{microphone.stop();}catch(Exception ignored){}microphone.release();microphone=null;}if(echo!=null){echo.release();echo=null;}release(voice);voice=null;}
    public void close(){localConsent=false;stopped=true;worker.interrupt();}
    void join(){close();try{worker.join();}catch(InterruptedException e){Thread.currentThread().interrupt();}}
}
