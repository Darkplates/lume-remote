package com.lume.remote;

import android.app.Dialog;
import android.os.*;
import android.widget.*;
import org.json.*;

/** The system picker grants access only to files explicitly selected by the user. */
final class FilesDialog extends Dialog {
    final MainActivity activity;final SessionService.Session session;final Handler refresh=new Handler(Looper.getMainLooper());
    final LinearLayout entries;final TextView path,status;final Button upload,uploadFolder,save,print,previous,next;String signature="";
    FilesDialog(MainActivity activity,SessionService.Session session){super(activity);this.activity=activity;this.session=session;setTitle("Files");
        LinearLayout box=new LinearLayout(activity);box.setOrientation(LinearLayout.VERTICAL);box.setPadding(20,14,20,14);setContentView(box);
        LinearLayout tools=activity.row(box);activity.button("Drives / shares",tools,()->list("",0));activity.button("Up",tools,()->list(parent(state().optString("path")),0));activity.button("Refresh",tools,()->list(state().optString("path"),state().optInt("page")));
        path=new TextView(activity);box.addView(path);ScrollView scroll=new ScrollView(activity);entries=new LinearLayout(activity);entries.setOrientation(LinearLayout.VERTICAL);scroll.addView(entries);box.addView(scroll,new LinearLayout.LayoutParams(-1,360));
        LinearLayout pages=activity.row(box);previous=activity.button("Previous",pages,()->list(state().optString("path"),state().optInt("page")-1));next=activity.button("Next",pages,()->list(state().optString("path"),state().optInt("page")+1));
        upload=activity.button("Upload to this folder",box,()->activity.pickUpload(session,state().optString("path")));
        uploadFolder=activity.button("Upload folder…",box,()->activity.pickUploadFolder(session,state().optString("path")));
        save=activity.button("Save download…",box,()->{if(state().optBoolean("completed_directory"))activity.exportDownloadedFolder(session,state().optString("completed"));else activity.exportDownload(session,state().optString("completed"));});
        print=activity.button("Print PDF…",box,()->PdfPrinting.open(activity,session,state().optString("completed")));
        activity.button("Cancel transfer",box,()->action(new JSONObject(),"cancel_file"));status=new TextView(activity);box.addView(status);activity.button("Close",box,this::dismiss);
        setOnDismissListener(d->refresh.removeCallbacksAndMessages(null));list("",0);
    }
    JSONObject state(){JSONObject s=session.state.optJSONObject("files");return s==null?new JSONObject():s;}
    void action(JSONObject message,String type){try{message.put("type",type);session.action(message);}catch(Exception e){session.error="Unable to queue the file action.";}}
    void list(String folder,int page){try{action(new JSONObject().put("path",folder).put("page",page),"list_files");}catch(Exception ignored){}}
    void download(String remote,String name){try{action(new JSONObject().put("remote",remote).put("name",name).put("folder",activity.service.transferFolder(session).getCanonicalPath()),"download_file");}catch(Exception e){session.error="Unable to create a download folder.";}}
    void downloadFolder(String remote,String name){try{action(new JSONObject().put("remote",remote).put("name",name).put("folder",activity.service.transferFolder(session).getCanonicalPath()),"download_folder");}catch(Exception e){session.error="Unable to create a download folder.";}}
    @Override public void show(){super.show();getWindow().setLayout(-1,-2);tick();}
    void tick(){if(!isShowing())return;JSONObject s=state();String current=s.optString("path");boolean busy=s.optBoolean("active")||session.preparing.get();path.setText(current.isEmpty()?"Choose a remote drive or share":current);
        upload.setEnabled(!busy&&!current.isEmpty()&&!session.closed);uploadFolder.setEnabled(upload.isEnabled()&&(session.state.optLong("capabilities")&16)!=0);save.setEnabled(!busy&&!s.isNull("completed")&&!s.optString("completed").isEmpty()&&!session.closed);
        print.setEnabled(save.isEnabled()&&!s.optBoolean("completed_directory")&&s.optString("completed").toLowerCase(java.util.Locale.ROOT).endsWith(".pdf"));
        previous.setEnabled(!s.optBoolean("listing")&&s.optInt("page")>0);next.setEnabled(!s.optBoolean("listing")&&s.optBoolean("more"));
        JSONArray values=s.optJSONArray("entries");String latest=current+":"+busy+":"+(values==null?"":values.toString());
        if(!latest.equals(signature)){signature=latest;entries.removeAllViews();if(values!=null)for(int i=0;i<values.length();i++){JSONObject entry=values.optJSONObject(i);if(entry==null)continue;String name=entry.optString("name"),remote=child(current,name);boolean directory=entry.optBoolean("directory");LinearLayout row=activity.row(entries);Button item=activity.button(name+(directory?" /":" — Download"),row,()->{if(directory)list(remote,0);else download(remote,name);});item.setEnabled(directory||!busy);if(directory&&!current.isEmpty()&&(session.state.optLong("capabilities")&16)!=0)activity.button("Download folder",row,()->downloadFolder(remote,name)).setEnabled(!busy);}}
        status.setText(!session.error.isEmpty()?session.error:s.optString("status")+(s.optLong("resumed_bytes")>0?"\n"+s.optLong("resumed_bytes")+" bytes resumed":"")+(busy?"\n"+s.optLong("bytes")+" / "+s.optLong("total")+" bytes":"")+(s.optBoolean("folder_job")?"\n"+s.optLong("items_done")+" items · "+s.optLong("bytes_done")+" bytes verified":""));refresh.postDelayed(this::tick,250);
    }
    static String child(String path,String name){if(path.isEmpty())return name;return path.replaceAll("[\\\\/]+$","")+(path.contains("\\")?"\\":"/")+name;}
    static String parent(String path){String value=path.replaceAll("[\\\\/]+$","");int at=Math.max(value.lastIndexOf('\\'),value.lastIndexOf('/'));if(at==2&&value.charAt(1)==':')return value.substring(0,3);return at>0?value.substring(0,at):at==0?"/":"";}
}
