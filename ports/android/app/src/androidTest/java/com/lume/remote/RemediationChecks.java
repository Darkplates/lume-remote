package com.lume.remote;

import android.app.*;
import android.content.Intent;
import android.net.Uri;
import android.os.*;
import java.io.File;
import java.nio.charset.StandardCharsets;
import java.nio.file.Files;
import java.util.Arrays;
import java.util.UUID;
import java.util.concurrent.atomic.AtomicReference;
import java.util.function.BooleanSupplier;

/** Isolated lifecycle checks: private fixture files only; no host, invitation or live session. */
final class RemediationChecks {
    private static void check(boolean ok,String message){if(!ok)throw new AssertionError(message);}
    private static void waitFor(BooleanSupplier condition,String message)throws Exception {
        long until=SystemClock.elapsedRealtime()+20000;
        while(!condition.getAsBoolean()){if(SystemClock.elapsedRealtime()>until)throw new AssertionError(message);Thread.sleep(25);}
    }
    private static Bundle parcel(Bundle source) {
        Parcel data=Parcel.obtain();
        try{data.writeBundle(source);data.setDataPosition(0);return data.readBundle(MainActivity.class.getClassLoader());}finally{data.recycle();}
    }
    private static MainActivity recreate(Instrumentation test,MainActivity old)throws Exception {
        AtomicReference<MainActivity> created=new AtomicReference<>();
        Application application=old.getApplication();
        Application.ActivityLifecycleCallbacks callbacks=new Application.ActivityLifecycleCallbacks() {
            public void onActivityCreated(Activity activity,Bundle state){if(activity instanceof MainActivity&&activity!=old)created.set((MainActivity)activity);}
            public void onActivityStarted(Activity activity){}
            public void onActivityResumed(Activity activity){}
            public void onActivityPaused(Activity activity){}
            public void onActivityStopped(Activity activity){}
            public void onActivitySaveInstanceState(Activity activity,Bundle state){}
            public void onActivityDestroyed(Activity activity){}
        };
        application.registerActivityLifecycleCallbacks(callbacks);
        try{test.runOnMainSync(old::recreate);waitFor(()->created.get()!=null,"Activity was not recreated");return created.get();}
        finally{application.unregisterActivityLifecycleCallbacks(callbacks);}
    }
    static void run(Instrumentation test,Bundle result)throws Exception {
        MainActivity activity=(MainActivity)test.startActivitySync(new Intent(test.getTargetContext(),MainActivity.class).addFlags(Intent.FLAG_ACTIVITY_NEW_TASK));
        File recording=null,destination=null;
        try {
            final MainActivity initial=activity;
            waitFor(()->initial.service!=null,"Lifecycle fixture could not bind service");
            check(activity.service.sessions.isEmpty(),"Lifecycle fixture must not operate an existing session");
            String identity=UUID.randomUUID().toString();
            for(int request=20;request<=24;request++) {
                final int operation=request;final MainActivity current=activity;Bundle saved=new Bundle();
                test.runOnMainSync(()->{
                    current.pendingDocument=new PendingDocumentOperation(operation,operation==22?0:41,operation==22?null:identity,"owned-fixture-path","content://owned.fixture/document");
                    current.invitation.setText("test-only-secret-must-not-enter-saved-ui-state");
                    test.callActivityOnSaveInstanceState(current,saved);
                });
                Parcel encoded=Parcel.obtain();try {
                    encoded.writeBundle(saved);String bytes=new String(encoded.marshall(),StandardCharsets.UTF_16LE);
                    check(!bytes.contains("test-only-secret-must-not-enter-saved-ui-state"),"Invitation entered saved UI state");
                }finally{encoded.recycle();}
                Bundle restored=parcel(saved);
                test.runOnMainSync(()->{current.pendingDocument=null;current.restoreDocumentState(restored);});
                PendingDocumentOperation context=current.pendingDocument;
                check(context!=null&&context.request==request&&context.session==(request==22?0:41)&&java.util.Objects.equals(context.sessionIdentity,request==22?null:identity)&&context.context.equals("owned-fixture-path")&&context.resultUri.equals("content://owned.fixture/document"),"Parcel restore lost operation context");
            }
            result.putString("document_state","PASS all five picker contexts and deferred URI survive Bundle/Parcel; invitation is excluded");

            SessionService service=activity.service;
            String fixture=UUID.randomUUID().toString();
            recording=new File(service.recordings(),"remediation-"+fixture+".mkv");
            destination=new File(test.getTargetContext().getCacheDir(),"remediation-export-"+fixture+".mkv");
            byte[] expected=new byte[]{1,5,9,17,25,33,41,49};Files.write(recording.toPath(),expected);
            final String path=recording.getAbsolutePath();final MainActivity before=activity;
            test.runOnMainSync(()->{before.invitation.setText("");before.pendingDocument=new PendingDocumentOperation(22,0,null,path,null);});
            activity=recreate(test,activity);
            final MainActivity pickerReturn=activity;
            waitFor(()->pickerReturn.service!=null,"Recreated activity did not bind");
            check(activity.pendingDocument!=null&&activity.pendingDocument.context.equals(path),"Activity recreation lost recording path");
            final Uri destinationUri=Uri.fromFile(destination);
            test.runOnMainSync(()->{
                pickerReturn.service=null; // Deliver the callback before the asynchronous bind is available.
                pickerReturn.onActivityResult(22,Activity.RESULT_OK,new Intent().setData(destinationUri));
                check(pickerReturn.pendingDocument!=null&&pickerReturn.pendingDocument.resultUri!=null,"Unbound result was dropped");
            });
            check(!destination.exists(),"Unbound callback exported before service binding");
            activity=recreate(test,activity);
            final MainActivity rebound=activity;final File exported=destination;
            waitFor(()->rebound.service!=null&&rebound.pendingDocument==null&&exported.isFile()&&exported.length()==expected.length,"Deferred recording result did not dispatch after recreation/bind");
            check(Arrays.equals(Files.readAllBytes(destination.toPath()),expected),"Deferred export changed recording bytes");
            Files.write(recording.toPath(),new byte[]{99});
            test.runOnMainSync(rebound::dispatchDocumentResult);
            rebound.service.files.submit(()->{}).get();
            check(Arrays.equals(Files.readAllBytes(destination.toPath()),expected),"Rebind dispatched an operation twice");
            result.putString("document_lifecycle","PASS actual Activity recreation, unbound callback, second recreation, deferred export bytes and once-only dispatch");

            test.runOnMainSync(()->{
                rebound.pendingDocument=new PendingDocumentOperation(20,987654321,identity,"",null);
                rebound.onActivityResult(20,Activity.RESULT_OK,new Intent().setData(destinationUri));
                check(rebound.pendingDocument==null&&rebound.documentStatus.contains("closed session"),"Stale session was silently ignored");
                check(rebound.status.getText().toString().contains("closed session"),"Stale-session rejection is not visible");
                rebound.pendingDocument=new PendingDocumentOperation(20,987654321,identity,"",null);
                rebound.onActivityResult(20,Activity.RESULT_CANCELED,null);
                check(rebound.pendingDocument==null,"Picker cancellation retained old context");
                rebound.pendingDocument=new PendingDocumentOperation(21,987654321,identity,"owned-fixture-path",null);
                rebound.onActivityResult(20,Activity.RESULT_OK,new Intent().setData(destinationUri));
                check(rebound.pendingDocument.request==21,"Unrelated callback replaced pending context");
                rebound.pendingDocument=null;
                char[] oversized=new char[16385];Arrays.fill(oversized,'x');
                rebound.launchDocument(22,null,new String(oversized),new Intent());
                check(rebound.pendingDocument==null&&rebound.documentStatus.contains("cannot be restored safely"),"Unbounded path crashed or opened a picker");
                Bundle cleared=new Bundle();rebound.saveDocumentState(cleared);check(!cleared.containsKey("pending_document"),"Completed operation retained saved context");
            });
            result.putString("document_rejection","PASS visible stale-session rejection, cancellation cleanup and request matching");
        }finally {
            final MainActivity current=activity;
            test.runOnMainSync(()->{current.pendingDocument=null;current.finish();});
            if(recording!=null)Files.deleteIfExists(recording.toPath());
            if(destination!=null)Files.deleteIfExists(destination.toPath());
        }
    }
}
