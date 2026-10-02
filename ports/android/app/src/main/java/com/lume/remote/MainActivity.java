package com.lume.remote;

import android.Manifest;
import android.app.*;
import android.content.*;
import android.os.*;
import android.graphics.Color;
import android.view.*;
import android.widget.*;
import org.json.JSONObject;
import java.util.*;

public final class MainActivity extends Activity {
    SessionService service;
    RemoteSurface surface;
    TextView status;
    LinearLayout tabs,home,saved;
    EditText invitation;
    Button reply,actions;
    final Handler updates=new Handler(Looper.getMainLooper());
    final Runnable refreshTick=this::refresh;
    final Choreographer.FrameCallback frameTick=new Choreographer.FrameCallback(){
        public void doFrame(long time){if(!resumed)return;if(service!=null)service.requestFrame();surface.presentLatest();Choreographer.getInstance().postFrameCallback(this);}
    };
    boolean resumed;
    boolean clipboardRequested;
    long selected;
    int tabCount=-1;
    int savedRevision=-1;
    long voicePermissionTarget;
    DisplaysDialog displaysDialog;
    PendingDocumentOperation pendingDocument;
    String documentStatus="";
    // A document notice is shown briefly, then session status and errors take over again.
    long documentStatusUntil;
    private static final String DOCUMENT_STATE="pending_document";
    final ServiceConnection connection=new ServiceConnection(){
        public void onServiceConnected(ComponentName name,IBinder binder){service=((SessionService.LocalBinder)binder).service();selected=service.visible;dispatchDocumentResult();refresh();}
        public void onServiceDisconnected(ComponentName name){service=null;surface.session=null;}
    };
    Button button(String label,LinearLayout parent,Runnable clicked){Button b=new Button(this);b.setText(label);parent.addView(b);b.setOnClickListener(v->clicked.run());return b;}
    LinearLayout row(LinearLayout parent){LinearLayout r=new LinearLayout(this);r.setOrientation(LinearLayout.HORIZONTAL);HorizontalScrollView scroll=new HorizontalScrollView(this);scroll.addView(r);parent.addView(scroll,new LinearLayout.LayoutParams(-1,-2));return r;}
    /** Clipboard text that keyboards and clipboard previews must not display or retain. */
    static ClipData sensitiveClip(String label,String text){ClipData clip=ClipData.newPlainText(label,text);PersistableBundle extras=new PersistableBundle();extras.putBoolean("android.content.extra.IS_SENSITIVE",true);clip.getDescription().setExtras(extras);return clip;}
    @Override public void onCreate(Bundle state){super.onCreate(state);
        // Remote frames are private desktop content: keep them out of screenshots, recordings and recents.
        getWindow().addFlags(WindowManager.LayoutParams.FLAG_SECURE);
        restoreDocumentState(state);
        LinearLayout root=new LinearLayout(this);root.setOrientation(LinearLayout.VERTICAL);root.setBackgroundColor(Color.rgb(17,24,28));root.setPadding(14,8,14,8);setContentView(root);
        root.setOnApplyWindowInsetsListener((v,insets)->{if(Build.VERSION.SDK_INT>=30){android.graphics.Insets bars=insets.getInsets(WindowInsets.Type.systemBars());v.setPadding(14+bars.left,8+bars.top,14+bars.right,8+bars.bottom);}else{v.setPadding(14+insets.getSystemWindowInsetLeft(),8+insets.getSystemWindowInsetTop(),14+insets.getSystemWindowInsetRight(),8+insets.getSystemWindowInsetBottom());}return insets;});
        home=new LinearLayout(this);home.setOrientation(LinearLayout.VERTICAL);root.addView(home);
        TextView title=new TextView(this);title.setText("Lume");title.setTextSize(28);home.addView(title);
        invitation=new EditText(this);invitation.setHint("Paste an invitation or one-time pairing code");invitation.setMaxLines(3);invitation.setSaveEnabled(false);home.addView(invitation);
        button("Connect",home,()->{if(service==null)return;try{long id=service.connect(invitation.getText().toString().trim());if(id==0){status.setText(Native.text(Native.error()));return;}invitation.setText("");select(id);if(Build.VERSION.SDK_INT>=33 && checkSelfPermission(Manifest.permission.POST_NOTIFICATIONS)!=android.content.pm.PackageManager.PERMISSION_GRANTED)requestPermissions(new String[]{Manifest.permission.POST_NOTIFICATIONS},1);}catch(Exception e){status.setText("Unable to start the session.");}});
        button("Recordings",home,this::recordings);
        saved=new LinearLayout(this);saved.setOrientation(LinearLayout.VERTICAL);home.addView(saved);
        tabs=row(root);LinearLayout toolbar=row(root);
        button("Computers",toolbar,()->{home.setVisibility(home.getVisibility()==View.VISIBLE?View.GONE:View.VISIBLE);});
        button("Quality",toolbar,this::quality);
        actions=button("More",toolbar,this::actions);
        button("Disconnect",toolbar,()->{if(service!=null){surface.release();service.close(selected);select(0);}});
        status=new TextView(this);status.setTextSize(13);root.addView(status);
        reply=button("Copy reply to the sharing computer",root,()->{SessionService.Session s=current();if(s!=null){String text=s.state.optString("reply","");if(!text.isEmpty()&&!text.equals("null"))getSystemService(ClipboardManager.class).setPrimaryClip(sensitiveClip("Private Lume reply",text));}});
        reply.setVisibility(View.GONE);
        surface=new RemoteSurface(this);root.addView(surface,new LinearLayout.LayoutParams(-1,0,1));
        bindService(new Intent(this,SessionService.class),connection,BIND_AUTO_CREATE);
    }
    SessionService.Session current(){return service==null?null:service.sessions.get(selected);}
    void select(long id){surface.drawing(false);selected=id;clipboardRequested=false;if(service!=null)service.visible=id;surface.session=current();tabCount=-1;home.setVisibility(id==0?View.VISIBLE:View.GONE);}
    void action(String type,String key,Object value){SessionService.Session s=current();if(s==null)return;try{JSONObject json=new JSONObject().put("type",type);if(key!=null)json.put(key,value);s.action(json);}catch(Exception ignored){}}
    void actions(){SessionService.Session s=current();if(s==null)return;PopupMenu menu=new PopupMenu(this,actions);boolean control=s.state.optBoolean("control");long caps=s.state.optLong("capabilities");
        menu.getMenu().add(0,1,0,"Send clipboard text").setEnabled(control);
        menu.getMenu().add(0,2,1,"Get clipboard text").setEnabled(control&&(caps&1)!=0);
        menu.getMenu().add(0,3,2,"Chat").setEnabled((caps&4)!=0);
        menu.getMenu().add(0,4,3,"Release held keys").setEnabled(control);
        menu.getMenu().add(0,5,4,"Files").setEnabled(s.state.optBoolean("files_allowed")&&s.state.optBoolean("connected"));
        menu.getMenu().add(0,6,5,"Retry saving pairing").setEnabled(s.state.optBoolean("pair_ready"));
        JSONObject media=s.state.optJSONObject("media"),record=s.state.optJSONObject("recording");boolean audio=media!=null&&(media.optBoolean("audio")||media.optBoolean("audio_pending")),voice=media!=null&&(media.optBoolean("voice")||media.optBoolean("voice_pending"));
        menu.getMenu().add(0,7,6,audio?"Sound off":"Sound").setEnabled((caps&32)!=0);
        menu.getMenu().add(0,8,7,voice?"End voice call":"Voice call").setEnabled((caps&1024)!=0);
        menu.getMenu().add(0,9,8,record!=null&&record.optBoolean("active")?"Stop recording":"Record to MKV");
        menu.getMenu().add(0,10,9,"Displays").setEnabled(s.state.optBoolean("connected")&&(caps&2)!=0);
        menu.getMenu().add(0,11,10,surface.drawing?"Finish drawing":"Draw").setEnabled(s.state.optBoolean("input_ready")&&(caps&64)!=0);
        menu.getMenu().add(0,12,11,"Clear marks").setEnabled(s.state.optBoolean("input_ready")&&(caps&64)!=0);
        menu.setOnMenuItemClickListener(item->{switch(item.getItemId()){
            case 1->{ClipboardManager clipboard=getSystemService(ClipboardManager.class);ClipData clip=clipboard.getPrimaryClip();if(clip!=null&&clip.getItemCount()>0){CharSequence text=clip.getItemAt(0).getText();if(text!=null)action("clipboard","text",text.toString());}}
            case 2->{clipboardRequested=true;action("read_clipboard",null,null);}
            case 3->chat(s);
            case 4->surface.release();
            case 5->{surface.release();new FilesDialog(this,s).show();}
            case 6->service.retryPairing(s);
            case 7->action("audio","enabled",!audio);
            case 8->{if(voice)service.voice(s,false);else new AlertDialog.Builder(this).setTitle("Allow your microphone?").setMessage("The host must also accept this call. Audio stops when you leave Lume. Use headphones to reduce echo.").setNegativeButton("Cancel",null).setPositiveButton("Allow and request call",(d,w)->requestVoice(s)).show();}
            case 9->record(s);
            case 10->{surface.release();if(displaysDialog!=null)displaysDialog.close();displaysDialog=new DisplaysDialog(this,s);displaysDialog.show();}
            case 11->surface.drawing(!surface.drawing);
            case 12->surface.clearMarks();
            default->{return false;}
        }return true;});menu.show();
    }
    void chat(SessionService.Session s){LinearLayout box=new LinearLayout(this);box.setOrientation(LinearLayout.VERTICAL);box.setPadding(24,8,24,8);TextView history=new TextView(this);StringBuilder lines=new StringBuilder();org.json.JSONArray messages=s.state.optJSONArray("chats");if(messages!=null)for(int i=0;i<messages.length();i++)lines.append(messages.optString(i)).append('\n');history.setText(lines);ScrollView scroll=new ScrollView(this);scroll.addView(history);box.addView(scroll,new LinearLayout.LayoutParams(-1,220));EditText message=new EditText(this);message.setHint("Message");message.setSaveEnabled(false);message.setFilters(new android.text.InputFilter[]{new android.text.InputFilter.LengthFilter(2000)});box.addView(message);new AlertDialog.Builder(this).setTitle("Session chat").setView(box).setNegativeButton("Close",null).setPositiveButton("Send",(d,w)->{try{s.action(new JSONObject().put("type","chat").put("text",message.getText().toString()));}catch(Exception ignored){}}).show();}
    void quality(){SessionService.Session s=current();if(s==null)return;
        LinearLayout box=new LinearLayout(this);box.setOrientation(LinearLayout.VERTICAL);box.setPadding(24,8,24,8);
        EditText height=new EditText(this),fps=new EditText(this);height.setInputType(2);fps.setInputType(2);height.setHint("Height — 0 keeps source");fps.setHint("FPS — 0 follows source");height.setText("1080");fps.setText("60");box.addView(height);box.addView(fps);
        CheckBox lossless=new CheckBox(this);lossless.setText("Lossless pixels");lossless.setEnabled((s.state.optLong("capabilities")&2048)!=0);box.addView(lossless);
        new AlertDialog.Builder(this).setTitle("Quality").setView(box).setNeutralButton("Source",(d,w)->setQuality(0,0,true)).setNegativeButton("Save data",(d,w)->setQuality(360,10,false)).setPositiveButton("Apply",(d,w)->{try{setQuality(Integer.parseInt(height.getText().toString()),Integer.parseInt(fps.getText().toString()),lossless.isChecked());}catch(NumberFormatException e){status.setText("Enter a valid resolution and FPS.");}}).show();
    }
    void setQuality(int height,int fps,boolean lossless){SessionService.Session s=current();if(s==null)return;try{s.action(new JSONObject().put("type","quality").put("height",height).put("fps",fps).put("jpeg",lossless?100:80).put("lossless",lossless));}catch(Exception ignored){}}
    void refresh(){if(service!=null){
        if(savedRevision!=service.savedRevision){saved.removeAllViews();org.json.JSONArray computers=service.saved;for(int i=0;i<computers.length();i++){JSONObject computer=computers.optJSONObject(i);if(computer==null)continue;LinearLayout line=row(saved);button(computer.optString("name","Saved computer"),line,()->{try{long id=service.connect(computer.getString("connection"));if(id!=0)select(id);else status.setText(Native.text(Native.error()));}catch(Exception e){status.setText("Unable to connect to this saved computer.");}});button("Forget",line,()->new AlertDialog.Builder(this).setMessage("Remove this credential from this device? You can revoke its access in the host's trusted computers.").setNegativeButton("Keep",null).setPositiveButton("Forget",(d,w)->service.forget(computer.optString("id"))).show());}savedRevision=service.savedRevision;}
        if(selected!=0&&!service.sessions.containsKey(selected))select(0);
        if(tabCount!=service.sessions.size()){tabs.removeAllViews();for(SessionService.Session s:service.sessions.values()){long id=s.handle;button("Computer "+id,tabs,()->select(id));}tabCount=service.sessions.size();}
        SessionService.Session s=current();surface.session=s;
        if(s!=null){status.setText(!documentNotice().isEmpty()?documentStatus:s.error.isEmpty()?s.state.optString("status"):s.error);actions.setEnabled(s.state.optBoolean("connected")||s.state.optBoolean("pair_ready"));reply.setVisibility(s.state.isNull("reply")?View.GONE:View.VISIBLE);
            if(clipboardRequested&&!s.state.isNull("clipboard")){String text=s.state.optString("clipboard");getSystemService(ClipboardManager.class).setPrimaryClip(sensitiveClip("Remote text",text));action("clear_clipboard","text",text);clipboardRequested=false;}
        }else{status.setText(!documentNotice().isEmpty()?documentStatus:!service.homeStatus.isEmpty()?service.homeStatus:service.sessions.isEmpty()?"Connect to a computer you own or have permission to use.":"Select a computer.");actions.setEnabled(false);reply.setVisibility(View.GONE);}
    }updates.removeCallbacks(refreshTick);if(resumed)updates.postDelayed(refreshTick,200);}
    @Override public void onResume(){super.onResume();resumed=true;if(service!=null)service.visible=selected;updates.removeCallbacksAndMessages(null);refresh();Choreographer.getInstance().removeFrameCallback(frameTick);Choreographer.getInstance().postFrameCallback(frameTick);}
    @Override public void onPause(){resumed=false;Choreographer.getInstance().removeFrameCallback(frameTick);updates.removeCallbacksAndMessages(null);surface.release();if(service!=null){service.visible=0;service.pauseMedia();}super.onPause();}
    @Override public void onDestroy(){if(displaysDialog!=null){displaysDialog.close();displaysDialog=null;}unbindService(connection);super.onDestroy();}
    void pickUpload(SessionService.Session s,String folder){Intent intent=new Intent(Intent.ACTION_OPEN_DOCUMENT).setType("*/*").addCategory(Intent.CATEGORY_OPENABLE);launchDocument(20,s,folder,intent);}
    void record(SessionService.Session s){JSONObject state=s.state.optJSONObject("recording");if(state!=null&&state.optBoolean("active")){service.record(s,0);return;}EditText fps=new EditText(this);fps.setInputType(android.text.InputType.TYPE_CLASS_NUMBER);fps.setText("0");new AlertDialog.Builder(this).setTitle("Recording FPS").setMessage("0 follows the source refresh rate. Or choose 1–1000 FPS; actual recording depends on received frames and encoding speed.").setView(fps).setPositiveButton("Record",(dialog,which)->{try{int value=Integer.parseInt(fps.getText().toString());if(value<0||value>1000)throw new IllegalArgumentException();service.record(s,value);}catch(Exception e){s.error="Choose recording FPS from 0 to 1000.";}}).setNegativeButton("Cancel",null).show();}
    void pickUploadFolder(SessionService.Session s,String folder){launchDocument(23,s,folder,new Intent(Intent.ACTION_OPEN_DOCUMENT_TREE));}
    void exportDownloadedFolder(SessionService.Session s,String path){launchDocument(24,s,path,new Intent(Intent.ACTION_OPEN_DOCUMENT_TREE));}
    void exportDownload(SessionService.Session s,String path){Intent intent=new Intent(Intent.ACTION_CREATE_DOCUMENT).setType("application/octet-stream").addCategory(Intent.CATEGORY_OPENABLE).putExtra(Intent.EXTRA_TITLE,new java.io.File(path).getName());launchDocument(21,s,path,intent);}
    void requestVoice(SessionService.Session s){voicePermissionTarget=s.handle;if(checkSelfPermission(Manifest.permission.RECORD_AUDIO)!=android.content.pm.PackageManager.PERMISSION_GRANTED)requestPermissions(new String[]{Manifest.permission.RECORD_AUDIO},7);else service.voice(s,true);}
    @Override public void onRequestPermissionsResult(int request,String[] permissions,int[] granted){super.onRequestPermissionsResult(request,permissions,granted);if(request==7&&service!=null){SessionService.Session s=service.sessions.get(voicePermissionTarget);if(s!=null&&resumed&&granted.length>0&&granted[0]==android.content.pm.PackageManager.PERMISSION_GRANTED)service.voice(s,true);else if(s!=null)s.error="Microphone permission was not granted. You can retry.";}}
    void recordings(){if(service==null)return;java.io.File[] list=service.recordings().listFiles((dir,name)->name.endsWith(".mkv"));if(list==null||list.length==0){status.setText("No recordings saved yet.");return;}Arrays.sort(list,Comparator.comparingLong(java.io.File::lastModified).reversed());String[] names=new String[list.length];for(int i=0;i<list.length;i++)names[i]=list[i].getName();new AlertDialog.Builder(this).setTitle("Export a recording").setItems(names,(d,n)->{for(SessionService.Session s:service.sessions.values()){JSONObject r=s.state.optJSONObject("recording");if(r!=null&&r.optBoolean("active")&&list[n].getAbsolutePath().equals(r.optString("path"))){status.setText("Stop recording before exporting.");return;}}launchDocument(22,null,list[n].getAbsolutePath(),new Intent(Intent.ACTION_CREATE_DOCUMENT).setType("video/x-matroska").addCategory(Intent.CATEGORY_OPENABLE).putExtra(Intent.EXTRA_TITLE,names[n]));}).setNegativeButton("Close",null).show();}
    void launchDocument(int request,SessionService.Session session,String context,Intent intent) {
        if(session!=null&&session.closed){documentMessage("This document operation belongs to a closed session. Reconnect and select the document again.");return;}
        try{launchDocument(new PendingDocumentOperation(request,session==null?0:session.handle,session==null?null:session.documentIdentity,context,null),intent);}
        catch(IllegalArgumentException e){documentMessage("This document path cannot be restored safely. Choose another document.");}
    }
    void launchDocument(PendingDocumentOperation operation,Intent intent) {
        if(pendingDocument!=null){documentMessage("Finish or cancel the current document selection first.");return;}
        pendingDocument=operation;documentStatus="";
        try{startActivityForResult(intent,operation.request);}catch(Exception e){pendingDocument=null;documentMessage("Unable to open the document picker. Please retry.");}
    }
    @Override protected void onSaveInstanceState(Bundle state){saveDocumentState(state);super.onSaveInstanceState(state);}
    void saveDocumentState(Bundle state) {
        PendingDocumentOperation operation=pendingDocument;if(operation==null){state.remove(DOCUMENT_STATE);return;}
        Bundle document=new Bundle();document.putInt("request",operation.request);document.putLong("session",operation.session);
        document.putString("identity",operation.sessionIdentity);document.putString("context",operation.context);document.putString("result",operation.resultUri);
        state.putBundle(DOCUMENT_STATE,document);
    }
    void restoreDocumentState(Bundle state) {
        if(state==null)return;Bundle document=state.getBundle(DOCUMENT_STATE);if(document==null)return;
        try{pendingDocument=new PendingDocumentOperation(document.getInt("request"),document.getLong("session"),document.getString("identity"),document.getString("context"),document.getString("result"));}
        catch(RuntimeException e){pendingDocument=null;documentStatus="The document operation could not be restored. Please select it again.";documentStatusUntil=SystemClock.elapsedRealtime()+10000;}
    }
    void documentMessage(String message){documentStatus=message;documentStatusUntil=SystemClock.elapsedRealtime()+10000;if(status!=null)status.setText(message);}
    String documentNotice(){if(!documentStatus.isEmpty()&&SystemClock.elapsedRealtime()>documentStatusUntil)documentStatus="";return documentStatus;}
    void dispatchDocumentResult() {
        PendingDocumentOperation operation=pendingDocument;if(operation==null||operation.resultUri==null)return;
        if(service==null){documentMessage("Waiting for Lume to reconnect to the session service…");return;}
        // Consume before dispatch so a repeated bind/result cannot export or upload twice.
        pendingDocument=null;documentStatus="";
        android.net.Uri uri=android.net.Uri.parse(operation.resultUri);
        if(operation.request==PendingDocumentOperation.EXPORT_RECORDING){service.exportRecording(operation.context,uri);return;}
        SessionService.Session s=service.sessions.get(operation.session);
        if(s==null||!operation.matches(s.handle,s.documentIdentity,s.closed)){documentMessage("This document operation belongs to a closed session. Reconnect and select the document again.");return;}
        select(s.handle);
        switch(operation.request) {
            case PendingDocumentOperation.UPLOAD_FILE->service.upload(s,uri,operation.context);
            case PendingDocumentOperation.SAVE_FILE->service.export(s,operation.context,uri);
            case PendingDocumentOperation.UPLOAD_FOLDER->service.uploadFolder(s,uri,operation.context);
            case PendingDocumentOperation.SAVE_FOLDER->service.exportFolder(s,operation.context,uri);
            default->documentMessage("The document operation could not be restored. Please select it again.");
        }
    }
    @Override protected void onActivityResult(int request,int result,Intent data) {
        super.onActivityResult(request,result,data);if(!PendingDocumentOperation.supports(request))return;
        if(pendingDocument==null||pendingDocument.request!=request){documentMessage("The document operation is no longer available. Please select it again.");return;}
        if(result!=RESULT_OK){pendingDocument=null;documentStatus="";return;}
        if(data==null||data.getData()==null){pendingDocument=null;documentMessage("The document picker did not return a document. Please retry.");return;}
        try{pendingDocument=pendingDocument.result(data.getData().toString());dispatchDocumentResult();}
        catch(RuntimeException e){pendingDocument=null;documentMessage("Unable to use the selected document. Please retry.");}
    }
}
