package com.lume.remote;

import android.content.Context;
import android.graphics.*;
import android.view.*;
import org.json.JSONObject;
import org.json.JSONArray;
import java.util.ArrayList;

final class RemoteSurface extends View {
    SessionService.Session session;
    boolean drawing;
    private final ArrayList<int[]> stroke=new ArrayList<>();
    private int strokeEpoch;
    void drawing(boolean value){release();drawing=value;stroke.clear();invalidate();}
    void clearMarks(){stroke.clear();sendStroke(new JSONArray(),drawnEpoch);invalidate();}
    private void sendStroke(JSONArray points,int epoch){if(session==null)return;try{session.action(new JSONObject().put("type","annotation").put("epoch",epoch).put("points",points));}catch(Exception ignored){}}
    private final RectF desktop=new RectF();
    private final Paint paint=new Paint(Paint.FILTER_BITMAP_FLAG);
    private final Paint annotationPaint=new Paint(Paint.ANTI_ALIAS_FLAG);
    private final Path annotationPath=new Path();
    private long drawnHandle;
    private int drawnSequence,drawnEpoch;
    void presentLatest(){SessionService.Session s=session;if(s==null){if(drawnHandle!=0)invalidate();return;}
        synchronized(s.frameLock){if(drawnHandle!=s.handle || drawnSequence!=s.sequence || (drawnEpoch!=0&&(!s.state.optBoolean("connected")||s.state.optInt("epoch")!=drawnEpoch||drawnEpoch!=s.bitmapEpoch)) || (drawnEpoch!=s.bitmapEpoch&&s.state.optBoolean("connected")&&s.state.optInt("epoch")==s.bitmapEpoch))invalidate();}
    }
    RemoteSurface(Context context){super(context);annotationPaint.setColor(Color.rgb(80,245,181));annotationPaint.setStyle(Paint.Style.STROKE);annotationPaint.setStrokeWidth(4);setFocusable(true);setFocusableInTouchMode(true);setBackgroundColor(Color.BLACK);setContentDescription("Remote desktop. Touch to move and drag the pointer.");}
    @Override protected void onDraw(Canvas canvas) {
        super.onDraw(canvas);SessionService.Session s=session;drawnEpoch=0;if(s==null){drawnHandle=0;return;}
        synchronized(s.frameLock){drawnHandle=s.handle;drawnSequence=s.sequence;
            if(s.bitmap==null || s.bitmapEpoch==0 || !s.state.optBoolean("connected") || s.state.optInt("epoch")!=s.bitmapEpoch){desktop.setEmpty();return;}
            float scale=Math.min((float)getWidth()/s.bitmap.getWidth(),(float)getHeight()/s.bitmap.getHeight());
            float w=s.bitmap.getWidth()*scale,h=s.bitmap.getHeight()*scale;
            desktop.set((getWidth()-w)/2,(getHeight()-h)/2,(getWidth()+w)/2,(getHeight()+h)/2);
            canvas.drawBitmap(s.bitmap,null,desktop,paint);
            drawnEpoch=s.bitmapEpoch;
            if(strokeEpoch!=drawnEpoch)stroke.clear();
            if(drawing&&stroke.size()>1){annotationPath.rewind();for(int i=0;i<stroke.size();i++){int[] p=stroke.get(i);float x=desktop.left+p[0]/65535f*desktop.width(),y=desktop.top+p[1]/65535f*desktop.height();if(i==0)annotationPath.moveTo(x,y);else annotationPath.lineTo(x,y);}canvas.drawPath(annotationPath,annotationPaint);}

        }
    }
    void input(int action,int a,int b){SessionService.Session s=session;if(drawing || s==null || drawnHandle!=s.handle || drawnEpoch==0 || drawnEpoch!=s.state.optInt("epoch") || !s.state.optBoolean("input_ready"))return;
        try{s.action(new JSONObject().put("type","input").put("action",action).put("a",a).put("b",b).put("epoch",drawnEpoch));}catch(Exception ignored){}
    }
    void release(){if(session!=null)try{session.action(new JSONObject().put("type","release"));}catch(Exception ignored){}}
    @Override public boolean onTouchEvent(MotionEvent event) {
        if(session==null)return false;int action=event.getActionMasked();
        if(drawing){
            if(action==MotionEvent.ACTION_CANCEL||action==MotionEvent.ACTION_POINTER_DOWN||drawnEpoch==0||drawnEpoch!=session.state.optInt("epoch")){stroke.clear();invalidate();return true;}
            if(action==MotionEvent.ACTION_DOWN){release();stroke.clear();strokeEpoch=drawnEpoch;}
            if(event.getPointerCount()==1&&desktop.contains(event.getX(),event.getY())){
                int[] p={(int)Math.max(0,Math.min(65535,(event.getX()-desktop.left)/desktop.width()*65535)),(int)Math.max(0,Math.min(65535,(event.getY()-desktop.top)/desktop.height()*65535))};
                if(stroke.size()==128)stroke.remove(126);stroke.add(p);
            }
            if(action==MotionEvent.ACTION_UP){if(stroke.size()>1&&strokeEpoch==drawnEpoch){JSONArray points=new JSONArray();for(int[] p:stroke)points.put(new JSONArray().put(p[0]).put(p[1]));sendStroke(points,strokeEpoch);}stroke.clear();performClick();}
            invalidate();return true;
        }
        if(action==MotionEvent.ACTION_UP || action==MotionEvent.ACTION_CANCEL || action==MotionEvent.ACTION_POINTER_DOWN){release();if(action==MotionEvent.ACTION_UP)performClick();return true;}
        if(!desktop.contains(event.getX(),event.getY())){release();return true;}
        if(event.getPointerCount()!=1)return true;
        int x=(int)Math.max(0,Math.min(65535,(event.getX()-desktop.left)/desktop.width()*65535));
        int y=(int)Math.max(0,Math.min(65535,(event.getY()-desktop.top)/desktop.height()*65535));
        input(0,x,y);if(action==MotionEvent.ACTION_DOWN){requestFocus();input(1,0,0);}return true;
    }
    @Override public boolean performClick(){super.performClick();return true;}
    @Override protected void onFocusChanged(boolean focused,int direction,Rect previous){if(!focused){release();stroke.clear();}super.onFocusChanged(focused,direction,previous);}
    private int vk(int code){if(code>=KeyEvent.KEYCODE_A && code<=KeyEvent.KEYCODE_Z)return 65+code-KeyEvent.KEYCODE_A;if(code>=KeyEvent.KEYCODE_0 && code<=KeyEvent.KEYCODE_9)return 48+code-KeyEvent.KEYCODE_0;
        return switch(code){case KeyEvent.KEYCODE_ENTER->13;case KeyEvent.KEYCODE_DEL->8;case KeyEvent.KEYCODE_TAB->9;case KeyEvent.KEYCODE_ESCAPE->27;case KeyEvent.KEYCODE_SPACE->32;case KeyEvent.KEYCODE_SHIFT_LEFT,KeyEvent.KEYCODE_SHIFT_RIGHT->16;case KeyEvent.KEYCODE_CTRL_LEFT,KeyEvent.KEYCODE_CTRL_RIGHT->17;case KeyEvent.KEYCODE_ALT_LEFT,KeyEvent.KEYCODE_ALT_RIGHT->18;case KeyEvent.KEYCODE_DPAD_LEFT->37;case KeyEvent.KEYCODE_DPAD_UP->38;case KeyEvent.KEYCODE_DPAD_RIGHT->39;case KeyEvent.KEYCODE_DPAD_DOWN->40;case KeyEvent.KEYCODE_FORWARD_DEL->46;default->0;};}
    @Override public boolean onKeyDown(int code,KeyEvent event){int key=vk(code);if(key==0)return super.onKeyDown(code,event);input(4,key,0);return true;}
    @Override public boolean onKeyUp(int code,KeyEvent event){int key=vk(code);if(key==0)return super.onKeyUp(code,event);input(5,key,0);return true;}
}
