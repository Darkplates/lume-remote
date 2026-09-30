package com.lume.remote;

/** One document operation, containing no invitation, credential or session secret. */
final class PendingDocumentOperation {
    static final int UPLOAD_FILE=20, SAVE_FILE=21, EXPORT_RECORDING=22, UPLOAD_FOLDER=23, SAVE_FOLDER=24;
    private static final int MAX_TEXT=16384;
    final int request;
    final long session;
    final String sessionIdentity,context,resultUri;

    PendingDocumentOperation(int request,long session,String identity,String context,String resultUri) {
        if(!supports(request)||context==null||context.length()>MAX_TEXT||
                ((request==SAVE_FILE||request==SAVE_FOLDER||request==EXPORT_RECORDING)&&context.isEmpty())||
                (resultUri!=null&&(resultUri.isEmpty()||resultUri.length()>MAX_TEXT)))throw new IllegalArgumentException("Invalid document operation");
        if(request==EXPORT_RECORDING) {
            if(session!=0||identity!=null)throw new IllegalArgumentException("Recording export is session-independent");
        }else if(session<=0||identity==null||identity.length()!=36)throw new IllegalArgumentException("Missing document session");
        this.request=request;this.session=session;sessionIdentity=identity;this.context=context;this.resultUri=resultUri;
    }
    static boolean supports(int request){return request>=UPLOAD_FILE&&request<=SAVE_FOLDER;}
    PendingDocumentOperation result(String uri){return new PendingDocumentOperation(request,session,sessionIdentity,context,uri);}
    boolean matches(long handle,String identity,boolean closed){return !closed&&session==handle&&sessionIdentity!=null&&sessionIdentity.equals(identity);}
}
