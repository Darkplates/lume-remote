package com.lume.remote;

import java.nio.ByteBuffer;
import java.nio.charset.StandardCharsets;

final class Native {
    static { System.loadLibrary("lume_bridge"); System.loadLibrary("lume_jni"); }
    static native long open(byte[] invitation, byte[] library);
    static native boolean cancel(long handle);
    static native boolean close(long handle);
    static native byte[] state(long handle);
    static native byte[] pairing(long handle);
    static native byte[] error();
    static native boolean action(long handle, byte[] json);
    static native long frame(long handle, int previous, ByteBuffer buffer, int[] info);
    static native int audio(long handle,int kind,ByteBuffer buffer,int[] generation);
    static native boolean microphone(long handle,int generation,byte[] pcm,int length);
    static byte[] utf8(String text) { return text.getBytes(StandardCharsets.UTF_8); }
    static String text(byte[] bytes) { return bytes == null ? "" : new String(bytes, StandardCharsets.UTF_8); }
    private Native() {}
}
