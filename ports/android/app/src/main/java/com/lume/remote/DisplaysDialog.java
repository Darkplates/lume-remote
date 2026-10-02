package com.lume.remote;

import android.app.AlertDialog;
import android.os.*;
import android.widget.*;
import org.json.*;

/** Display metadata only; desktop images remain in the session surface. */
final class DisplaysDialog {
    private final MainActivity activity;
    private final SessionService.Session session;
    private final Handler handler=new Handler(Looper.getMainLooper());
    private AlertDialog dialog;
    private LinearLayout rows;
    private String previous="";
    DisplaysDialog(MainActivity activity,SessionService.Session session){this.activity=activity;this.session=session;}
    void close(){handler.removeCallbacksAndMessages(null);if(dialog!=null)dialog.dismiss();}
    private void action(String type,String id){try{JSONObject value=new JSONObject().put("type",type);if(id!=null)value.put("id",id);session.action(value);}catch(JSONException ignored){}}
    void show(){rows=new LinearLayout(activity);rows.setOrientation(LinearLayout.VERTICAL);rows.setPadding(24,12,24,12);ScrollView scroll=new ScrollView(activity);scroll.addView(rows);
        dialog=new AlertDialog.Builder(activity).setTitle("Displays").setView(scroll).setNegativeButton("Close",null).setNeutralButton("Refresh",null).create();MainActivity.secure(dialog);
        dialog.setOnDismissListener(d->handler.removeCallbacksAndMessages(null));dialog.show();
        dialog.getButton(AlertDialog.BUTTON_NEUTRAL).setOnClickListener(v->action("list_monitors",null));
        action("list_monitors",null);update();
    }
    private void update(){if(!dialog.isShowing())return;
        JSONObject state=session.state;JSONArray displays=state.optJSONArray("monitors");
        boolean pending=state.optBoolean("monitor_pending"),connected=state.optBoolean("connected");
        String message=pending?"Changing display…":state.optString("monitor_status");if(!session.error.isEmpty())message=session.error;
        String fingerprint=String.valueOf(displays)+pending+connected+message;
        if(!fingerprint.equals(previous)){previous=fingerprint;rows.removeAllViews();
            if(!message.isEmpty()){TextView text=new TextView(activity);text.setText(message);rows.addView(text);}
            if(displays!=null)for(int i=0;i<displays.length();i++){JSONObject display=displays.optJSONObject(i);if(display==null)continue;
                Button choice=new Button(activity);choice.setText((display.optBoolean("selected")?"✓ ":"")+display.optString("name")+" · "+display.optInt("width")+" × "+display.optInt("height")+" · "+display.optInt("refresh")+" Hz");
                choice.setEnabled(connected&&!pending);choice.setOnClickListener(v->action("select_monitor",display.optString("id")));rows.addView(choice);}
            dialog.getButton(AlertDialog.BUTTON_NEUTRAL).setEnabled(connected&&!pending);
        }
        handler.postDelayed(this::update,200);
    }
}
