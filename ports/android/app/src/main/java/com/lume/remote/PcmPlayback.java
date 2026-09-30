package com.lume.remote;

import java.nio.ByteBuffer;

/** Non-blocking playback retains at most one PCM packet per output device. */
final class PcmPlayback {
    interface Source { int read(ByteBuffer buffer,int[] generation); }
    interface Sink { int write(ByteBuffer buffer,int size); }
    /** A failed device is retried only after media-off or a negotiated generation change. */
    static final class FailureGate {
        private boolean failed;
        private int audioGeneration,voiceGeneration;
        void fail(int audio,int voice){failed=true;audioGeneration=audio;voiceGeneration=voice;}
        boolean allows(boolean audio,boolean voice,int audioVersion,int voiceVersion) {
            if(failed&&((!audio&&!voice)||audioVersion!=audioGeneration||voiceVersion!=voiceGeneration))failed=false;
            return !failed;
        }
    }
    static final int CAPACITY=19200,FRAME_BYTES=4,WRITES_PER_TICK=4;
    private final ByteBuffer pcm=ByteBuffer.allocateDirect(CAPACITY);
    private final int[] generation=new int[1];
    private int bufferedGeneration;

    PcmPlayback(){clear();}
    void clear(){pcm.clear();pcm.limit(0);}
    int pendingBytes(){return pcm.remaining();}
    void pump(int expected,Source source,Sink sink) {
        if(bufferedGeneration!=expected)clear();
        for(int i=0;i<WRITES_PER_TICK;i++) {
            if(!pcm.hasRemaining()) {
                pcm.clear();int size=source.read(pcm,generation);
                if(size<=0){clear();return;}
                if(size>pcm.capacity()||size%FRAME_BYTES!=0){clear();throw new IllegalStateException("Invalid PCM packet size");}
                pcm.position(0);pcm.limit(size);bufferedGeneration=generation[0];
                if(bufferedGeneration!=expected){clear();continue;}
            }
            int position=pcm.position(),requested=pcm.remaining();
            int written=sink.write(pcm,requested);
            // AudioTrack reports device failures as negative integers, including ERROR_DEAD_OBJECT.
            if(written<0||written>requested||written%FRAME_BYTES!=0){clear();throw new IllegalStateException("Audio output write failed: "+written);}
            pcm.position(position+written);
            if(written<requested)return;
        }
    }
}
