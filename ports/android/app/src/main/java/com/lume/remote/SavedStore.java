package com.lume.remote;

import android.content.Context;
import android.security.keystore.KeyGenParameterSpec;
import android.security.keystore.KeyProperties;
import android.util.AtomicFile;
import java.io.*;
import java.security.KeyStore;
import javax.crypto.*;
import javax.crypto.spec.GCMParameterSpec;
import org.json.*;

/** App-private AES-GCM data; the encryption key remains in Android Keystore. */
final class SavedStore {
    private final AtomicFile file;
    private static final String ALIAS="lume.saved-computers.v1";
    SavedStore(Context context){file=new AtomicFile(new File(context.getFilesDir(),"computers.encrypted"));}
    private SecretKey key() throws Exception {
        KeyStore store=KeyStore.getInstance("AndroidKeyStore");store.load(null);
        if(store.containsAlias(ALIAS))return (SecretKey)store.getKey(ALIAS,null);
        if(file.getBaseFile().exists())throw new IOException("The saved-computer key is unavailable.");
        KeyGenerator generator=KeyGenerator.getInstance(KeyProperties.KEY_ALGORITHM_AES,"AndroidKeyStore");
        generator.init(new KeyGenParameterSpec.Builder(ALIAS,KeyProperties.PURPOSE_ENCRYPT|KeyProperties.PURPOSE_DECRYPT)
            .setBlockModes(KeyProperties.BLOCK_MODE_GCM).setEncryptionPaddings(KeyProperties.ENCRYPTION_PADDING_NONE).setKeySize(256).build());
        return generator.generateKey();
    }
    synchronized JSONArray read() throws Exception {
        if(!file.getBaseFile().exists())return new JSONArray();
        if(file.getBaseFile().length()>131072)throw new IOException("Invalid saved-computer data.");
        byte[] bytes=file.readFully();if(bytes.length<29)throw new IOException("Invalid saved-computer data.");
        Cipher cipher=Cipher.getInstance("AES/GCM/NoPadding");cipher.init(Cipher.DECRYPT_MODE,key(),new GCMParameterSpec(128,bytes,0,12));
        cipher.updateAAD(Native.utf8(ALIAS));byte[] plain=cipher.doFinal(bytes,12,bytes.length-12);
        try{JSONArray values=new JSONArray(Native.text(plain));if(values.length()>64)throw new IOException("Too many saved computers.");return values;}finally{java.util.Arrays.fill(plain,(byte)0);}
    }
    synchronized void save(JSONObject computer) throws Exception {
        JSONArray old=read(),next=new JSONArray();String id=computer.getString("id");
        for(int i=0;i<old.length();i++)if(!old.getJSONObject(i).getString("id").equals(id))next.put(old.getJSONObject(i));
        if(next.length()>=64)throw new IOException("Remove an unused saved computer first.");next.put(computer);write(next);
    }
    synchronized void remove(String id) throws Exception {
        JSONArray old=read(),next=new JSONArray();for(int i=0;i<old.length();i++)if(!old.getJSONObject(i).getString("id").equals(id))next.put(old.getJSONObject(i));write(next);
    }
    private void write(JSONArray values) throws Exception {
        byte[] plain=Native.utf8(values.toString());if(plain.length>120000)throw new IOException("Saved-computer data exceeds its limit.");
        try{
            Cipher cipher=Cipher.getInstance("AES/GCM/NoPadding");cipher.init(Cipher.ENCRYPT_MODE,key());cipher.updateAAD(Native.utf8(ALIAS));byte[] encrypted=cipher.doFinal(plain);
            FileOutputStream output=null;try{output=file.startWrite();output.write(cipher.getIV());output.write(encrypted);file.finishWrite(output);}catch(Exception error){if(output!=null)file.failWrite(output);throw error;}
        }finally{java.util.Arrays.fill(plain,(byte)0);}
    }
}
