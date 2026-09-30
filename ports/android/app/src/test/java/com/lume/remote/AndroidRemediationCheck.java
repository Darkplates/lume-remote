package com.lume.remote;

import java.io.ByteArrayOutputStream;
import java.nio.ByteBuffer;
import java.util.Arrays;

/** Plain JVM fixture for the actual bounded playback and document-context helpers. */
public final class AndroidRemediationCheck {
    private static void check(boolean ok,String message){if(!ok)throw new AssertionError(message);}
    private static void expectFailure(Runnable action,String message){try{action.run();throw new AssertionError(message);}catch(IllegalStateException expected){}}
    private static final class Packets implements PcmPlayback.Source {
        int reads,remaining=1,generation=7;
        byte[] bytes=new byte[24];
        Packets(){for(int i=0;i<bytes.length;i++)bytes[i]=(byte)(i+1);}
        public int read(ByteBuffer pcm,int[] version){reads++;if(remaining--<=0)return 0;pcm.put(bytes);version[0]=generation;return bytes.length;}
    }
    public static void main(String[] args) {
        String identity="00000000-0000-0000-0000-000000000001";
        for(int request=20;request<=24;request++) {
            PendingDocumentOperation operation=new PendingDocumentOperation(request,request==22?0:41,request==22?null:identity,"folder/or/private/path",null);
            PendingDocumentOperation returned=operation.result("content://owned.fixture/document");
            check(returned.request==request&&returned.session==operation.session&&returned.context.equals(operation.context),"Result lost document context");
            if(request!=22){check(returned.matches(41,identity,false),"Live session rejected");check(!returned.matches(41,identity,true),"Closed session accepted");check(!returned.matches(41,"00000000-0000-0000-0000-000000000002",false),"Reused handle accepted");}
        }
        try{new PendingDocumentOperation(19,41,identity,"",null);throw new AssertionError("Unknown request accepted");}catch(IllegalArgumentException expected){}
        try{new PendingDocumentOperation(21,41,identity,"",null);throw new AssertionError("Missing export path accepted");}catch(IllegalArgumentException expected){}
        try{new PendingDocumentOperation(20,41,identity,"x".repeat(16385),null);throw new AssertionError("Unbounded context accepted");}catch(IllegalArgumentException expected){}
        System.out.println("PASS Document context preserves all five operation types and rejects closed/reused sessions and invalid state.");

        PcmPlayback playback=new PcmPlayback();Packets packets=new Packets();ByteArrayOutputStream output=new ByteArrayOutputStream();
        PcmPlayback.Sink partial=(pcm,size)->{int written=Math.min(8,size);for(int i=0;i<written;i++)output.write(pcm.get());return written;};
        playback.pump(7,packets,partial);check(playback.pendingBytes()==16&&packets.reads==1,"Partial write lost remainder or read another packet");
        playback.pump(7,packets,partial);check(playback.pendingBytes()==8&&packets.reads==1,"Remainder was overwritten");
        playback.pump(7,packets,partial);check(Arrays.equals(output.toByteArray(),packets.bytes)&&playback.pendingBytes()==0,"PCM prefix/remainder bytes changed");
        System.out.println("PASS Partial writes preserve one packet without overwriting, duplication or an extra source read.");

        playback=new PcmPlayback();packets=new Packets();final PcmPlayback stalled=playback;final Packets stalledPackets=packets;
        for(int i=0;i<1000;i++)stalled.pump(7,stalledPackets,(pcm,size)->0);
        check(packets.reads==1&&playback.pendingBytes()==24&&playback.pendingBytes()<=PcmPlayback.CAPACITY,"Stalled sink accumulated packets");
        System.out.println("PASS A stalled output retains one bounded packet and does not drain additional native packets.");

        for(int error:new int[]{-6,-3,-2,-1}) {
            PcmPlayback failed=new PcmPlayback();Packets source=new Packets();source.remaining=100;
            expectFailure(()->failed.pump(7,source,(pcm,size)->error),"Negative AudioTrack result ignored");
            check(source.reads==1&&failed.pendingBytes()==0,"Dead device drained later packets or retained old PCM");
        }
        System.out.println("PASS ERROR_DEAD_OBJECT and other negative output results stop draining immediately and clear pending PCM.");

        PcmPlayback.FailureGate gate=new PcmPlayback.FailureGate();gate.fail(7,11);
        for(int i=0;i<1000;i++)check(!gate.allows(true,false,7,11),"Failed output retried on unchanged enabled state");
        check(gate.allows(false,false,7,11)&&gate.allows(true,false,7,11),"Explicit media-off did not permit a fresh device");
        gate.fail(7,11);check(gate.allows(true,false,8,11),"New audio generation did not permit replacement");
        gate.fail(8,11);check(gate.allows(false,true,8,12),"New voice generation did not permit replacement");
        System.out.println("PASS Device failure blocks repeated setup/draining at the same generation and permits only an explicit/new-generation retry.");

        PcmPlayback changed=new PcmPlayback();Packets old=new Packets();changed.pump(7,old,(pcm,size)->0);
        Packets replacement=new Packets();replacement.generation=8;replacement.bytes=new byte[]{40,41,42,43};ByteArrayOutputStream fresh=new ByteArrayOutputStream();
        changed.pump(8,replacement,(pcm,size)->{while(pcm.hasRemaining())fresh.write(pcm.get());return size;});
        check(Arrays.equals(fresh.toByteArray(),replacement.bytes),"Old generation remainder reached a replacement device");
        PcmPlayback bounded=new PcmPlayback();Packets busy=new Packets();busy.remaining=100;
        bounded.pump(7,busy,(pcm,size)->size);check(busy.reads==PcmPlayback.WRITES_PER_TICK,"Playback exceeded per-tick work budget");
        PcmPlayback malformed=new PcmPlayback();Packets unaligned=new Packets();unaligned.bytes=new byte[3];
        expectFailure(()->malformed.pump(7,unaligned,(pcm,size)->size),"Unaligned PCM accepted");
        System.out.println("PASS Generation replacement drops old remainder, packet work is bounded, and malformed PCM fails closed.");
        System.out.println("BOUNDARY JVM helpers only; Activity/Bundle, SAF, AudioTrack hardware and native session execution are separate checks.");
    }
}
