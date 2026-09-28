package com.lume.remote;

import android.app.*;
import android.content.*;
import android.graphics.Color;
import android.os.*;
import android.view.View;
import android.widget.Button;
import org.json.JSONObject;
import java.io.File;
import java.io.FileOutputStream;
import java.nio.ByteBuffer;
import java.nio.charset.StandardCharsets;
import java.nio.file.Files;
import java.util.function.BooleanSupplier;

/** Separate test APK: it connects only to the supplied synthetic Windows fixtures. */
public final class ContractInstrumentation extends Instrumentation {
    private long viewer;
    private MainActivity activity;
    private static void check(boolean ok,String message){if(!ok)throw new AssertionError(message);}
    private static void waitFor(BooleanSupplier condition,String message)throws Exception{
        long until=SystemClock.elapsedRealtime()+20000;
        while(!condition.getAsBoolean()){if(SystemClock.elapsedRealtime()>until)throw new AssertionError(message);Thread.sleep(50);}
    }
    private String invitation(String name)throws Exception{
        File file=new File(getTargetContext().getFilesDir(),name);
        check(file.length()>0&&file.length()<=65536,"Missing bounded fixture invitation");
        String text=new String(Files.readAllBytes(file.toPath()),StandardCharsets.UTF_8);check(file.delete(),"Fixture invitation cleanup failed");return text;
    }
    private boolean action(String json){return Native.action(viewer,Native.utf8(json));}
    private JSONObject state(){try{return new JSONObject(Native.text(Native.state(viewer)));}catch(Exception e){throw new AssertionError("Invalid native state",e);}}
    @Override public void onCreate(Bundle bundle){super.onCreate(bundle);start();}
    @Override public void onStart(){
        Bundle result=new Bundle();int outcome=Activity.RESULT_CANCELED;
        try{
            for(String name:new String[]{"../escape","CON.txt","COM¹.txt","stream:tail","trailing.",".lume-partial"}) {
                boolean rejected=false;try{FolderDocuments.name(name);}catch(java.io.IOException expected){rejected=true;}
                check(rejected,"Unsafe folder entry name accepted");
            }
            FolderDocuments.name("notes café.txt");
            File boundary=new File(getTargetContext().getCacheDir(),"folder-check-"+java.util.UUID.randomUUID());
            check(boundary.mkdir(),"Cannot create owned folder fixture");
            File child=new File(boundary,"nested");check(child.mkdir(),"Cannot create nested fixture");
            Files.write(new File(child,"item.txt").toPath(),new byte[]{1,2,3});
            FolderDocuments.removeOwned(boundary,boundary);check(child.isDirectory(),"Boundary directory was deleted");
            FolderDocuments.removeOwned(child,boundary);check(!child.exists(),"Owned nested staging was not removed");
            check(boundary.delete(),"Cannot retire owned folder fixture");
            result.putString("folder_guards","PASS unsafe names and scoped staging cleanup");
            String library=getTargetContext().getApplicationInfo().nativeLibraryDir+"/libdatachannel.so";
            check(Native.open(Native.utf8("invalid"),Native.utf8(library))==0,"Malformed invitation accepted");
            viewer=Native.open(Native.utf8(invitation("contract-native.txt")),Native.utf8(library));
            check(viewer!=0,"JNI could not create viewer");
            waitFor(()->state().optBoolean("connected"),"Native TLS session did not connect");
            check(!state().optBoolean("control"),"Synthetic fixture unexpectedly allows control");
            check(action("{\"type\":\"quality\",\"height\":0,\"fps\":0,\"jpeg\":100,\"lossless\":true}"),"Source quality failed");
            int[] info=new int[4];ByteBuffer pixels=ByteBuffer.allocateDirect(640*360*4);
            waitFor(()->{long size=Native.frame(viewer,0,pixels,info);return size==pixels.capacity()&&(pixels.get(0)&255)==12&&(pixels.get(1)&255)==30&&(pixels.get(2)&255)==60;},"Source RGBA pixels changed across JNI/TLS");
            check(info[0]==640&&info[1]==360,"Frame metadata differs");
            ByteBuffer small=ByteBuffer.allocateDirect(8);small.put(0,(byte)87);
            check(Native.frame(viewer,0,small,info)==pixels.capacity()&&small.get(0)==87,"Undersized frame buffer was partially overwritten");
            check(!action("{\"type\":\"quality\",\"height\":-1,\"fps\":60,\"jpeg\":80,\"lossless\":false}"),"Invalid quality accepted");
            check(state().optBoolean("connected"),"Invalid local action closed session");
            check(!action("{\"type\":\"list_files\",\"path\":\"\",\"page\":0}"),"View-only session acquired file access");
            check(state().optBoolean("connected"),"Denied file action closed session");
            SavedStore savedStore=new SavedStore(getTargetContext());String fixtureId=java.util.UUID.randomUUID().toString();String privateValue="test-only-private-"+fixtureId;
            try {savedStore.save(new JSONObject().put("id",fixtureId).put("name","Owned key-store fixture").put("connection",privateValue));check(savedStore.read().toString().contains(fixtureId),"Protected saved computer did not round-trip");String stored=new String(Files.readAllBytes(new File(getTargetContext().getFilesDir(),"computers.encrypted").toPath()),StandardCharsets.ISO_8859_1);check(!stored.contains(privateValue),"Saved computer key was stored in clear text");} finally {savedStore.remove(fixtureId);}
            check(action("{\"type\":\"quality\",\"height\":360,\"fps\":10,\"jpeg\":80,\"lossless\":false}"),"Save-data quality failed");
            long before=state().optLong("frames");waitFor(()->state().optLong("frames")>before+1,"JPEG fallback stopped frames");
            check(Native.cancel(viewer)&&Native.close(viewer)&&Native.close(viewer),"Native teardown is not idempotent");viewer=0;
            result.putString("native","PASS JNI, pinned TLS, exact RGBA, buffer bounds, quality fallback, cleanup");
            String uiInvite=invitation("contract-ui.txt");
            activity=(MainActivity)startActivitySync(new Intent(getTargetContext(),MainActivity.class).addFlags(Intent.FLAG_ACTIVITY_NEW_TASK));
            waitFor(()->activity.service!=null,"Activity could not bind session service");
            runOnMainSync(()->{activity.invitation.setText(uiInvite);((Button)activity.home.getChildAt(2)).performClick();});
            waitFor(()->activity.current()!=null&&activity.current().state.optBoolean("connected"),"Dashboard connect failed");
            SessionService.Session session=activity.current();
            waitFor(()->{synchronized(session.frameLock){return session.bitmap!=null&&session.sequence>0;}},"Android bitmap did not render");
            synchronized(session.frameLock){int color=session.bitmap.getPixel(5,5);check(Math.abs(Color.red(color)-12)<6&&Math.abs(Color.green(color)-30)<6&&Math.abs(Color.blue(color)-60)<6,"Android RGBA/ARGB channel order differs");}
            waitForIdleSync();android.graphics.Bitmap screenshot=getUiAutomation().takeScreenshot();
            check(screenshot!=null,"UI screenshot unavailable");
            try(FileOutputStream file=new FileOutputStream(new File(getTargetContext().getFilesDir(),"contract-ui.png"))){check(screenshot.compress(android.graphics.Bitmap.CompressFormat.PNG,100,file),"UI screenshot could not be saved");}finally{screenshot.recycle();}
            SessionService service=activity.service;
            runOnMainSync(()->activity.finish());waitFor(()->service.visible==0,"Hidden dashboard still decodes frames");
            Thread.sleep(6000);check(session.state.optBoolean("connected"),"Closing dashboard ended session");
            runOnMainSync(()->service.close(session.handle));waitFor(()->session.closed,"Disconnect did not cancel");
            waitFor(()->{synchronized(session.frameLock){return session.bitmap==null&&session.bytes==null;}},"Frame memory retained after close");
            result.putString("ui","PASS dashboard, bitmap channels, foreground session after activity close, disconnect cleanup");
            result.putString("stream","PASS Android runtime contract on synthetic fixtures\n");outcome=Activity.RESULT_OK;
        }catch(Throwable error){result.putString("stream","FAIL "+error.getClass().getSimpleName()+": "+error.getMessage()+"\n");}
        finally{if(viewer!=0){Native.cancel(viewer);Native.close(viewer);}if(activity!=null)runOnMainSync(()->{if(activity.service!=null)activity.service.disconnectAll();activity.finish();});}
        finish(outcome,result);
    }
}
