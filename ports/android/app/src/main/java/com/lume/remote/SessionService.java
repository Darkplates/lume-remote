package com.lume.remote;

import android.app.*;
import android.content.Intent;
import android.graphics.Bitmap;
import android.os.*;
import org.json.JSONObject;
import java.nio.ByteBuffer;
import java.util.*;
import java.util.concurrent.*;

public final class SessionService extends Service {
    public final class LocalBinder extends Binder { SessionService service() { return SessionService.this; } }
    final LocalBinder binder = new LocalBinder();
    final Map<Long,Session> sessions = new ConcurrentHashMap<>();
    final ScheduledExecutorService worker = Executors.newSingleThreadScheduledExecutor();
    final java.util.concurrent.atomic.AtomicBoolean framePending=new java.util.concurrent.atomic.AtomicBoolean();
    final ExecutorService closing = Executors.newFixedThreadPool(2);
    final ExecutorService files = Executors.newSingleThreadExecutor();
    SavedStore savedStore;
    volatile org.json.JSONArray saved=new org.json.JSONArray();
    volatile int savedRevision;
    volatile String homeStatus="";
    volatile long visible;
    Notification sessionNotification;
    long voiceTarget;
    static final class Session {
        final long handle;
        final SessionAudio audio;
        final Object frameLock=new Object();
        volatile JSONObject state=new JSONObject();
        volatile String error="";
        volatile boolean closed;
        volatile boolean saving;
        final java.util.concurrent.atomic.AtomicBoolean preparing=new java.util.concurrent.atomic.AtomicBoolean();
        volatile boolean fileCancelled;
        volatile java.io.File stagedUpload;
        volatile long stagedOperation;
        final Set<java.io.File> temporary=ConcurrentHashMap.newKeySet();
        java.io.File transferDirectory;
        ByteBuffer bytes;
        Bitmap bitmap;
        final int[] info=new int[4];
        int sequence;
        int bitmapEpoch;
        long lastState;
        Session(long id){handle=id;audio=new SessionAudio(this);}
        boolean action(JSONObject message) {
            if(closed)return false;
            if("cancel_file".equals(message.optString("type")))fileCancelled=true;
            boolean ok=Native.action(handle,Native.utf8(message.toString()));
            if(!ok)error=Native.text(Native.error());else error="";return ok;
        }
        void poll(boolean display) {
            synchronized(frameLock) {
            if(closed)return;
            try {
                if(System.nanoTime()-lastState>200000000L){String json=Native.text(Native.state(handle));
                    if(!json.isEmpty())state=new JSONObject(json);lastState=System.nanoTime();}
                if(!display)return;
                long size=Native.frame(handle,sequence,bytes,info);
                if(size==0)return;
                if(size>Math.min(128L*1024*1024,Runtime.getRuntime().maxMemory()/3)) {
                    error="This resolution exceeds available memory. Choose a lower quality.";return;
                }
                if(bytes==null || bytes.capacity()<size) {
                    bytes=ByteBuffer.allocateDirect((int)size);
                    size=Native.frame(handle,sequence,bytes,info);
                    if(size==0 || size>bytes.capacity())return;
                }
                synchronized(frameLock) {
                    bitmapEpoch=0;
                    if(bitmap==null || bitmap.getWidth()!=info[0] || bitmap.getHeight()!=info[1]) {
                        Bitmap replacement=Bitmap.createBitmap(info[0],info[1],Bitmap.Config.ARGB_8888);
                        if(bitmap!=null)bitmap.recycle();
                        bitmap=replacement;
                    }
                    bytes.position(0);bitmap.copyPixelsFromBuffer(bytes);sequence=info[3];bitmapEpoch=info[2];
                }
            } catch(Exception e){error="Unable to update the session. Reconnect to retry.";}
            catch(OutOfMemoryError e){error="Not enough memory for this resolution. Choose a lower quality.";}
            }
        }
    }
    @Override public void onCreate() {
        super.onCreate();
        savedStore=new SavedStore(this);
        files.execute(this::reloadSaved);
        getSystemService(NotificationManager.class).createNotificationChannel(new NotificationChannel("sessions","Remote sessions",NotificationManager.IMPORTANCE_LOW));
        worker.scheduleWithFixedDelay(()->{for(Session s:sessions.values()){s.poll(false);if(s.state.optBoolean("pair_ready")&&!s.saving){s.saving=true;files.execute(()->savePairing(s));}JSONObject progress=s.state.optJSONObject("files");if(s.stagedUpload!=null&&progress!=null&&progress.optLong("operation")>s.stagedOperation&&!progress.optBoolean("active")){java.io.File completed=s.stagedUpload;s.stagedUpload=null;s.preparing.set(false);files.execute(()->{FolderDocuments.removeOwned(completed,getCacheDir());s.temporary.remove(completed);});}}},0,200,TimeUnit.MILLISECONDS);
    }
    // Vsync requests at most one queued/in-flight copy; never accumulate old frames.
    void requestFrame() {
        if(visible==0 || !framePending.compareAndSet(false,true))return;
        try { worker.execute(()->{try{Session s=sessions.get(visible);if(s!=null)s.poll(true);}finally{framePending.set(false);}}); }
        catch(RejectedExecutionException e){framePending.set(false);}
    }
    @Override public IBinder onBind(Intent intent){return binder;}
    @Override public int onStartCommand(Intent intent,int flags,int startId) {
        if(intent!=null && "disconnect".equals(intent.getAction())) {disconnectAll();stopSelf();}
        return START_NOT_STICKY;
    }
    long connect(String invitation) {
        String library=getApplicationInfo().nativeLibraryDir+"/libdatachannel.so";
        long handle=Native.open(Native.utf8(invitation),Native.utf8(library));
        if(handle==0)return 0;
        sessions.put(handle,new Session(handle));visible=handle;
        startService(new Intent(this,SessionService.class));
        Intent open=new Intent(this,MainActivity.class);
        PendingIntent content=PendingIntent.getActivity(this,0,open,PendingIntent.FLAG_UPDATE_CURRENT|PendingIntent.FLAG_IMMUTABLE);
        PendingIntent close=PendingIntent.getService(this,1,new Intent(this,SessionService.class).setAction("disconnect"),PendingIntent.FLAG_UPDATE_CURRENT|PendingIntent.FLAG_IMMUTABLE);
        Notification notification=new Notification.Builder(this,"sessions").setSmallIcon(android.R.drawable.ic_menu_view)
            .setContentTitle("Lume sessions are active").setContentText("Tap to view your remote sessions").setContentIntent(content).setOngoing(true)
            .addAction(new Notification.Action.Builder(null,"Disconnect all",close).build()).build();
        sessionNotification=notification;foreground(false);return handle;
    }
    private void foreground(boolean microphone){if(Build.VERSION.SDK_INT>=29){int type=Build.VERSION.SDK_INT>=34?android.content.pm.ServiceInfo.FOREGROUND_SERVICE_TYPE_SPECIAL_USE:0;if(microphone&&Build.VERSION.SDK_INT>=30)type|=android.content.pm.ServiceInfo.FOREGROUND_SERVICE_TYPE_MICROPHONE;startForeground(1,sessionNotification,type);}else startForeground(1,sessionNotification);}
    void voice(Session s,boolean enabled){try{
        if(enabled){if(checkSelfPermission(android.Manifest.permission.RECORD_AUDIO)!=android.content.pm.PackageManager.PERMISSION_GRANTED)throw new SecurityException();if(voiceTarget!=0&&voiceTarget!=s.handle){Session old=sessions.get(voiceTarget);if(old!=null)voice(old,false);}foreground(true);voiceTarget=s.handle;}
        s.audio.consent(enabled);s.action(new JSONObject().put("type","voice").put("enabled",enabled));
        if(!enabled&&voiceTarget==s.handle){voiceTarget=0;foreground(false);}
    }catch(Exception e){s.audio.consent(false);s.error="Microphone permission is required. Open Lume and retry.";}}
    void pauseMedia(){for(Session s:sessions.values()){voice(s,false);try{s.action(new JSONObject().put("type","audio").put("enabled",false));s.action(new JSONObject().put("type","stop_recording"));}catch(Exception ignored){}}}
    java.io.File recordings(){java.io.File folder=new java.io.File(getFilesDir(),"recordings");if(!folder.exists()&&!folder.mkdirs())throw new IllegalStateException();return folder;}
    void record(Session s,int fps){try{JSONObject r=s.state.optJSONObject("recording");if(r!=null&&r.optBoolean("active"))s.action(new JSONObject().put("type","stop_recording"));else s.action(new JSONObject().put("type","record").put("fps",fps).put("path",new java.io.File(recordings(),"Lume-"+UUID.randomUUID()+".mkv").getAbsolutePath()));}catch(Exception e){s.error="Unable to create the recording. Check free space.";}}
    void exportRecording(String path,android.net.Uri destination){files.execute(()->{try{java.io.File source=new java.io.File(path).getCanonicalFile();if(!source.getParentFile().equals(recordings().getCanonicalFile())||!source.isFile())throw new java.io.IOException();try(java.io.InputStream input=new java.io.FileInputStream(source);java.io.OutputStream output=getContentResolver().openOutputStream(destination,"w")){if(output==null)throw new java.io.IOException();byte[] buffer=new byte[65536];int count;while((count=input.read(buffer))!=-1)output.write(buffer,0,count);output.flush();}homeStatus="Recording exported. The private copy is kept in Lume.";}catch(Exception e){homeStatus="Unable to export the recording. The private copy was preserved.";}});}
    void close(long id) {
        Session s=sessions.remove(id);if(s==null)return;s.closed=true;s.audio.close();Native.cancel(id);
        closing.execute(()->{s.audio.join();Native.close(id);synchronized(s.frameLock){if(s.bitmap!=null){s.bitmap.recycle();s.bitmap=null;}s.bytes=null;}files.execute(()->{FolderDocuments.removeOwned(s.transferDirectory,getCacheDir());s.temporary.clear();});});
        if(visible==id)visible=0;
        if(sessions.isEmpty()){stopForeground(STOP_FOREGROUND_REMOVE);stopSelf();}
    }
    void disconnectAll(){for(long id:new ArrayList<>(sessions.keySet()))close(id);}
    void reloadSaved(){try{saved=savedStore.read();savedRevision++;}catch(Exception e){homeStatus="Unable to unlock saved computers on this device.";}}
    void savePairing(Session s){byte[] bytes=null;try{if(s.closed)return;bytes=Native.pairing(s.handle);JSONObject computer=new JSONObject(Native.text(bytes));savedStore.save(computer);s.action(new JSONObject().put("type","clear_pairing"));reloadSaved();homeStatus="Computer saved. Tap its name to connect.";close(s.handle);}catch(Exception e){s.error="Unable to protect this pairing. Keep this session open and retry saving.";}finally{if(bytes!=null)Arrays.fill(bytes,(byte)0);}}
    void retryPairing(Session s){if(s!=null && s.state.optBoolean("pair_ready")){s.saving=true;files.execute(()->savePairing(s));}}
    void forget(String id){files.execute(()->{try{savedStore.remove(id);reloadSaved();homeStatus="Removed from this device. Revoke access on the host to invalidate the key.";}catch(Exception e){homeStatus="Unable to remove the saved computer.";}});}
    synchronized java.io.File transferFolder(Session s) throws Exception {if(s.transferDirectory==null){s.transferDirectory=new java.io.File(getCacheDir(),"transfer-"+UUID.randomUUID());if(!s.transferDirectory.mkdir())throw new java.io.IOException("Cannot create transfer folder.");}return s.transferDirectory.getCanonicalFile();}
    void upload(Session s,android.net.Uri uri,String remoteFolder){if(!s.preparing.compareAndSet(false,true)){s.error="Wait for the selected file to finish.";return;}s.fileCancelled=false;files.execute(()->{
        java.io.File staged=null;try{
            if(s.closed){s.preparing.set(false);return;}String name="upload.bin";
            try(android.database.Cursor cursor=getContentResolver().query(uri,new String[]{android.provider.OpenableColumns.DISPLAY_NAME},null,null,null)){if(cursor!=null&&cursor.moveToFirst())name=cursor.getString(0);}
            staged=java.io.File.createTempFile("upload-",".data",transferFolder(s));s.temporary.add(staged);
            try(java.io.InputStream input=getContentResolver().openInputStream(uri);java.io.OutputStream output=new java.io.FileOutputStream(staged)){
                if(input==null)throw new java.io.IOException();byte[] buffer=new byte[65536];int count;while((count=input.read(buffer))!=-1){if(s.closed||s.fileCancelled)throw new java.io.InterruptedIOException();output.write(buffer,0,count);}
            }
            JSONObject progress=new JSONObject(Native.text(Native.state(s.handle))).optJSONObject("files");s.stagedOperation=progress==null?0:progress.optLong("operation");
            if(s.fileCancelled||!s.action(new JSONObject().put("type","upload_file").put("local",staged.getCanonicalPath()).put("folder",remoteFolder).put("name",name))){staged.delete();s.temporary.remove(staged);s.stagedUpload=null;s.preparing.set(false);}else s.stagedUpload=staged;
        }catch(Exception e){if(staged!=null){staged.delete();s.temporary.remove(staged);}s.stagedUpload=null;s.preparing.set(false);s.error=s.fileCancelled?"File selection cancelled.":"Unable to read the selected file.";}
    });}
    void uploadFolder(Session s,android.net.Uri tree,String remoteFolder) {
        if(!s.preparing.compareAndSet(false,true)){s.error="Wait for the current file operation.";return;}
        s.fileCancelled=false;s.error="Preparing the selected folder…";
        files.execute(()->{java.io.File staged=null;try {
            if(s.closed)throw new java.io.InterruptedIOException();
            staged=new java.io.File(transferFolder(s),"upload-"+UUID.randomUUID());
            if(!staged.mkdir())throw new java.io.IOException();s.temporary.add(staged);
            String name=FolderDocuments.stage(getContentResolver(),tree,staged,()->s.closed||s.fileCancelled);
            JSONObject progress=new JSONObject(Native.text(Native.state(s.handle))).optJSONObject("files");s.stagedOperation=progress==null?0:progress.optLong("operation");
            if(s.fileCancelled||!s.action(new JSONObject().put("type","upload_folder").put("local",staged.getCanonicalPath()).put("folder",remoteFolder).put("name",name)))throw new java.io.IOException();
            s.stagedUpload=staged;
        }catch(Exception e){FolderDocuments.removeOwned(staged,getCacheDir());if(staged!=null)s.temporary.remove(staged);s.stagedUpload=null;s.preparing.set(false);s.error=s.fileCancelled?"Folder selection cancelled.":"Unable to read or upload this folder. Check names, permissions and free space.";}});
    }
    void exportFolder(Session s,String path,android.net.Uri destination) {
        if(!s.preparing.compareAndSet(false,true)){s.error="Wait for the current file operation.";return;}
        s.fileCancelled=false;s.error="Saving the downloaded folder…";
        files.execute(()->{try {
            java.io.File source=new java.io.File(path).getCanonicalFile();
            if(!source.getParentFile().equals(transferFolder(s))||!source.isDirectory())throw new java.io.IOException();
            FolderDocuments.export(getContentResolver(),source,destination,()->s.closed||s.fileCancelled);
            s.error="Downloaded folder saved.";
        }catch(Exception e){s.error="Folder export stopped. Completed destination items were kept; the private copy remains until disconnect.";}
        finally{s.preparing.set(false);}});
    }
    void export(Session s,String path,android.net.Uri destination){
        if(!s.preparing.compareAndSet(false,true)){s.error="Wait for the current file operation.";return;}
        s.fileCancelled=false;s.error="Saving the downloaded file…";
        files.execute(()->{try{
        FolderDocuments.check(()->s.closed||s.fileCancelled);
        java.io.File source=new java.io.File(path).getCanonicalFile();if(!source.getParentFile().equals(transferFolder(s))||!source.isFile())throw new java.io.IOException();
        try(java.io.InputStream input=new java.io.FileInputStream(source);java.io.OutputStream output=getContentResolver().openOutputStream(destination,"w")){if(output==null)throw new java.io.IOException();FolderDocuments.copy(input,output,()->s.closed||s.fileCancelled);}s.error="Downloaded file saved.";
    }catch(java.io.InterruptedIOException e){s.error="File export stopped. The partial destination was kept; the private copy remains until disconnect.";}
    catch(Exception e){s.error="Unable to save the downloaded file. You can retry before disconnecting.";}
    finally{s.preparing.set(false);}});}
    @Override public void onDestroy(){disconnectAll();worker.shutdown();closing.shutdown();new Thread(()->{try{while(!closing.awaitTermination(30,TimeUnit.SECONDS)){}files.shutdown();}catch(InterruptedException e){Thread.currentThread().interrupt();}},"lume-file-cleanup").start();super.onDestroy();}
}
