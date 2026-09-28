package com.lume.remote;

import android.content.ContentResolver;
import android.database.Cursor;
import android.net.Uri;
import android.provider.DocumentsContract;
import java.io.*;
import java.nio.charset.StandardCharsets;
import java.nio.file.*;
import java.nio.file.attribute.BasicFileAttributes;
import java.util.*;
import java.util.function.BooleanSupplier;

/** Streams only the document tree explicitly granted by the system folder picker. */
final class FolderDocuments {
    private static final String DIRECTORY = DocumentsContract.Document.MIME_TYPE_DIR;
    private static final String[] COLUMNS = {DocumentsContract.Document.COLUMN_DOCUMENT_ID,
        DocumentsContract.Document.COLUMN_DISPLAY_NAME, DocumentsContract.Document.COLUMN_MIME_TYPE};
    static void name(String value) throws IOException {
        if (value == null || value.isEmpty() || value.getBytes(StandardCharsets.UTF_8).length > 255
            || value.equals(".") || value.equals("..") || value.endsWith(".") || value.endsWith(" ")
            || value.startsWith(".lume-")) throw new IOException("Unsupported file name.");
        for (int n=0;n<value.length();n++) if (Character.isISOControl(value.charAt(n))
            || "<>:\"/\\|?*".indexOf(value.charAt(n)) >= 0) throw new IOException("Unsupported file name.");
        String base=value.split("\\.",2)[0].replaceFirst(" +$", "").toUpperCase(Locale.ROOT);
        if (Arrays.asList("CON","PRN","AUX","NUL","CLOCK$","CONIN$","CONOUT$").contains(base)
            || base.matches("(?:COM|LPT)[1-9¹²³]")) throw new IOException("Reserved file name.");
    }
    static void check(BooleanSupplier cancelled) throws IOException {
        if (cancelled.getAsBoolean()) throw new InterruptedIOException("Folder operation cancelled.");
    }
    static Uri root(Uri tree) { return DocumentsContract.buildDocumentUriUsingTree(tree, DocumentsContract.getTreeDocumentId(tree)); }
    static String stage(ContentResolver resolver, Uri tree, File destination, BooleanSupplier cancelled) throws IOException {
        Uri document=root(tree); String label;
        try (Cursor c=resolver.query(document,COLUMNS,null,null,null)) {
            if(c==null||!c.moveToFirst()||!DIRECTORY.equals(c.getString(2))) throw new IOException("Choose a document folder.");
            label=c.getString(1); name(label);
        }
        stageChildren(resolver,tree,DocumentsContract.getTreeDocumentId(tree),destination,0,new HashSet<>(),cancelled);
        return label;
    }
    private static void stageChildren(ContentResolver resolver,Uri tree,String id,File local,int depth,Set<String> ancestors,BooleanSupplier cancelled) throws IOException {
        check(cancelled);
        if(depth>=64||!ancestors.add(id)) throw new IOException("Invalid or excessively nested folder.");
        try (Cursor c=resolver.query(DocumentsContract.buildChildDocumentsUriUsingTree(tree,id),COLUMNS,null,null,null)) {
            if(c==null) throw new IOException("Cannot enumerate the selected folder.");
            while(c.moveToNext()) {
                check(cancelled); String childId=c.getString(0),label=c.getString(1); name(label);
                File output=new File(local,label);
                if(!output.getCanonicalFile().getParentFile().equals(local.getCanonicalFile())) throw new IOException("Invalid child path.");
                if(DIRECTORY.equals(c.getString(2))) {
                    if(!output.mkdir()) throw new IOException("Duplicate or unavailable folder.");
                    stageChildren(resolver,tree,childId,output,depth+1,ancestors,cancelled);
                } else {
                    Uri document=DocumentsContract.buildDocumentUriUsingTree(tree,childId);
                    try (InputStream input=resolver.openInputStream(document);
                         OutputStream out=Files.newOutputStream(output.toPath(),StandardOpenOption.CREATE_NEW,StandardOpenOption.WRITE)) {
                        if(input==null) throw new IOException("Cannot read selected file.");
                        copy(input,out,cancelled);
                    }
                }
            }
        } finally { ancestors.remove(id); }
    }
    static void export(ContentResolver resolver,File source,Uri tree,BooleanSupplier cancelled) throws IOException {
        check(cancelled);
        // The provider creates a new top-level directory. Never merge with existing documents.
        String label=source.getName(); name(label);
        Uri destination=DocumentsContract.createDocument(resolver,root(tree),DIRECTORY,label);
        if(destination==null) throw new IOException("Cannot create the destination folder.");
        exportChildren(resolver,source,destination,0,cancelled);
    }
    private static void exportChildren(ContentResolver resolver,File source,Uri destination,int depth,BooleanSupplier cancelled) throws IOException {
        check(cancelled); if(depth>=64) throw new IOException("Folder nesting is too deep.");
        try (DirectoryStream<Path> children=Files.newDirectoryStream(source.toPath())) {
            for(Path child:children) {
                check(cancelled); name(child.getFileName().toString());
                if(Files.isSymbolicLink(child)) throw new IOException("Linked files are unsupported.");
                boolean directory=Files.isDirectory(child,LinkOption.NOFOLLOW_LINKS);
                if(!directory&&!Files.isRegularFile(child,LinkOption.NOFOLLOW_LINKS)) throw new IOException("Unsupported file type.");
                Uri document=DocumentsContract.createDocument(resolver,destination,directory?DIRECTORY:"application/octet-stream",child.getFileName().toString());
                if(document==null) throw new IOException("Cannot create a destination item.");
                if(directory) exportChildren(resolver,child.toFile(),document,depth+1,cancelled);
                else try(InputStream input=Files.newInputStream(child);OutputStream output=resolver.openOutputStream(document,"w")) {
                    if(output==null) throw new IOException("Cannot write the destination file."); copy(input,output,cancelled);
                }
            }
        }
    }
    static void copy(InputStream input,OutputStream output,BooleanSupplier cancelled) throws IOException {
        check(cancelled);
        byte[] buffer=new byte[65536]; int count;
        while((count=input.read(buffer))!=-1) { check(cancelled); output.write(buffer,0,count); }
        output.flush();
    }
    static void removeOwned(File path,File boundary) {
        if(path==null) return;
        try {
            Path parent=boundary.getCanonicalFile().toPath(),target=path.getAbsoluteFile().toPath().normalize();
            if(target.equals(parent)||!target.startsWith(parent)||!path.getCanonicalFile().toPath().equals(target)) return;
            Files.walkFileTree(target,new SimpleFileVisitor<Path>() {
                @Override public FileVisitResult visitFile(Path file,BasicFileAttributes attrs) throws IOException { Files.delete(file); return FileVisitResult.CONTINUE; }
                @Override public FileVisitResult postVisitDirectory(Path directory,IOException failure) throws IOException { if(failure!=null)throw failure;Files.delete(directory);return FileVisitResult.CONTINUE; }
            });
        } catch(IOException ignored) { /* Preserve anything whose ownership cannot be checked. */ }
    }
}
