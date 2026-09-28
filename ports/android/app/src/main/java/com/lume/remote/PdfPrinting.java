package com.lume.remote;

import android.graphics.Bitmap;
import android.graphics.Color;
import android.graphics.Rect;
import android.graphics.pdf.PdfDocument;
import android.graphics.pdf.PdfRenderer;
import android.os.Bundle;
import android.os.CancellationSignal;
import android.os.ParcelFileDescriptor;
import android.print.*;
import java.io.*;
import java.util.ArrayList;
import java.util.Locale;
import java.util.concurrent.ExecutorService;
import java.util.concurrent.Executors;

/** A private snapshot survives session disconnect; each write honours selected pages and cancel. */
final class PdfPrinting extends PrintDocumentAdapter {
    private final File snapshot;
    private final int pageCount;
    private final ExecutorService worker=Executors.newSingleThreadExecutor();
    private volatile boolean finished;
    private PdfPrinting(File snapshot,int pages){this.snapshot=snapshot;pageCount=pages;}
    static void open(MainActivity activity,SessionService.Session session,String path){
        if(!session.preparing.compareAndSet(false,true)){session.error="Wait for the current file operation.";return;}
        activity.service.files.execute(()->{
            File snapshot=null;
            try{
                File source=new File(path).getCanonicalFile();
                if(!source.getParentFile().equals(activity.service.transferFolder(session))||!source.isFile()
                    ||!source.getName().toLowerCase(Locale.ROOT).endsWith(".pdf")||source.length()<8||source.length()>128L*1024*1024)throw new IOException();
                snapshot=File.createTempFile("lume-print-",".pdf",activity.getCacheDir());
                try(InputStream input=new FileInputStream(source);OutputStream output=new FileOutputStream(snapshot)){
                    byte[] magic=new byte[5];if(input.read(magic)!=5||!java.util.Arrays.equals(magic,new byte[]{37,80,68,70,45}))throw new IOException();
                    output.write(magic);FolderDocuments.copy(input,output,()->session.closed);
                }
                int pages;
                try(ParcelFileDescriptor fd=ParcelFileDescriptor.open(snapshot,ParcelFileDescriptor.MODE_READ_ONLY);PdfRenderer renderer=new PdfRenderer(fd)){pages=renderer.getPageCount();}
                if(pages<1||pages>10000)throw new IOException();
                PdfPrinting adapter=new PdfPrinting(snapshot,pages);snapshot=null;
                activity.runOnUiThread(()->{
                    if(activity.isFinishing()||activity.isDestroyed()){adapter.onFinish();return;}
                    try{PrintManager manager=activity.getSystemService(PrintManager.class);if(manager==null)throw new IllegalStateException();manager.print("Lume downloaded PDF",adapter,null);}
                    catch(Exception e){adapter.onFinish();session.error="The system print panel is unavailable.";}
                });
            }catch(Exception e){session.error="Unable to print this PDF. Check its format and free space.";}
            finally{if(snapshot!=null)snapshot.delete();session.preparing.set(false);}
        });
    }
    @Override public void onLayout(PrintAttributes oldAttributes,PrintAttributes newAttributes,CancellationSignal cancellation,LayoutResultCallback callback,Bundle extras){
        if(finished||cancellation.isCanceled()){callback.onLayoutCancelled();return;}
        callback.onLayoutFinished(new PrintDocumentInfo.Builder("Lume.pdf").setContentType(PrintDocumentInfo.CONTENT_TYPE_DOCUMENT).setPageCount(pageCount).build(),!newAttributes.equals(oldAttributes));
    }
    @Override public void onWrite(PageRange[] ranges,ParcelFileDescriptor destination,CancellationSignal cancellation,WriteResultCallback callback){
        if(finished){callback.onWriteCancelled();return;}
        worker.execute(()->{
            PdfDocument document=null;
            try(ParcelFileDescriptor fd=ParcelFileDescriptor.open(snapshot,ParcelFileDescriptor.MODE_READ_ONLY);
                PdfRenderer renderer=new PdfRenderer(fd);
                OutputStream output=new ParcelFileDescriptor.AutoCloseOutputStream(destination)){
                document=new PdfDocument();
                ArrayList<PageRange> written=new ArrayList<>();long pixelBudget=Math.min(16L*1024*1024,Runtime.getRuntime().maxMemory()/16);long pixels=0;
                for(int index=0;index<pageCount;index++){
                    if(finished||cancellation.isCanceled())throw new InterruptedIOException();
                    boolean wanted=false;for(PageRange range:ranges)if(index>=range.getStart()&&index<=range.getEnd())wanted=true;
                    if(!wanted)continue;
                    try(PdfRenderer.Page page=renderer.openPage(index)){
                        int width=page.getWidth(),height=page.getHeight();
                        if(width<1||height<1||width>14400||height>14400)throw new IOException();
                        float scale=Math.min(2f,2048f/Math.max(width,height));
                        pixels+=(long)Math.max(1,(int)(width*scale))*Math.max(1,(int)(height*scale));
                        if(pixels>pixelBudget||written.size()>=64)throw new IOException("Choose fewer pages for this device.");
                        Bitmap bitmap=Bitmap.createBitmap(Math.max(1,(int)(width*scale)),Math.max(1,(int)(height*scale)),Bitmap.Config.ARGB_8888);
                        try{
                            bitmap.eraseColor(Color.WHITE);page.render(bitmap,null,null,PdfRenderer.Page.RENDER_MODE_FOR_PRINT);
                            PdfDocument.Page printed=document.startPage(new PdfDocument.PageInfo.Builder(width,height,index).create());
                            try{printed.getCanvas().drawBitmap(bitmap,null,new Rect(0,0,width,height),null);}
                            finally{document.finishPage(printed);}
                        }finally{bitmap.recycle();}
                        written.add(new PageRange(index,index));
                    }
                }
                if(finished||cancellation.isCanceled())throw new InterruptedIOException();
                document.writeTo(output);
                if(finished||cancellation.isCanceled())callback.onWriteCancelled();else callback.onWriteFinished(written.toArray(new PageRange[0]));
            }catch(InterruptedIOException e){callback.onWriteCancelled();}
            catch(Exception e){callback.onWriteFailed("Unable to render these pages. Try a smaller page range.");}
            finally{if(document!=null)document.close();}
        });
    }
    @Override public void onFinish(){if(finished)return;finished=true;worker.execute(()->snapshot.delete());worker.shutdown();}
}
