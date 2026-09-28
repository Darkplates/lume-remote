param([string]$Sdk = $env:ANDROID_HOME, [string]$JavaHome = $env:JAVA_HOME)
$ErrorActionPreference = 'Stop'
$project = Split-Path -Parent $PSScriptRoot
$android = Join-Path $Sdk 'platforms/android-36/android.jar'
$javac = Join-Path $JavaHome 'bin/javac.exe'
$java = Join-Path $JavaHome 'bin/java.exe'
foreach ($path in @($android, $javac, $java)) {
    if (-not (Test-Path -LiteralPath $path -PathType Leaf)) { throw "Missing prerequisite: $path" }
}
$scratch = Join-Path ([IO.Path]::GetTempPath()) ('lume-copy-check-' + [Guid]::NewGuid().ToString('N'))
New-Item -ItemType Directory -Path $scratch | Out-Null
$source = @'
package com.lume.remote;
import java.io.*;
import java.util.Arrays;
import java.util.concurrent.atomic.AtomicBoolean;

public final class CopyCheck {
    public static void main(String[] args) throws Exception {
        byte[] expected = new byte[196625];
        for (int i=0;i<expected.length;i++) expected[i]=(byte)(i*31);
        ByteArrayOutputStream complete = new ByteArrayOutputStream();
        FolderDocuments.copy(new ByteArrayInputStream(expected), complete, ()->false);
        if (!Arrays.equals(expected,complete.toByteArray())) throw new AssertionError("Copy changed bytes");
        System.out.println("PASS Streaming copy preserves all bytes across buffer boundaries.");

        InputStream forbidden = new InputStream() {
            public int read() { throw new AssertionError("Cancelled operation read its source"); }
        };
        try { FolderDocuments.copy(forbidden,new ByteArrayOutputStream(),()->true); throw new AssertionError("Cancellation ignored"); }
        catch (InterruptedIOException expectedCancellation) {}
        System.out.println("PASS Pre-cancelled copy performs no source read or destination write.");

        AtomicBoolean cancelled = new AtomicBoolean();
        ByteArrayOutputStream partial = new ByteArrayOutputStream() {
            @Override public synchronized void write(byte[] bytes,int offset,int length) {
                super.write(bytes,offset,length); cancelled.set(true);
            }
        };
        try { FolderDocuments.copy(new ByteArrayInputStream(expected),partial,cancelled::get); throw new AssertionError("Cancellation ignored"); }
        catch (InterruptedIOException expectedCancellation) {}
        if (partial.size()!=65536 || !Arrays.equals(Arrays.copyOf(expected,65536),partial.toByteArray())) throw new AssertionError("Copy continued after cancellation");
        System.out.println("PASS Mid-copy cancellation stops subsequent writes and preserves the completed prefix.");
        System.out.println("BOUNDARY JVM stream behaviour only; Android activity, SAF and device execution are not tested.");
    }
}
'@
[IO.File]::WriteAllText((Join-Path $scratch 'CopyCheck.java'), $source, (New-Object Text.UTF8Encoding($false)))
& $javac --release 17 -encoding UTF-8 -cp $android -d $scratch (Join-Path $project 'ports/android/app/src/main/java/com/lume/remote/FolderDocuments.java') (Join-Path $scratch 'CopyCheck.java')
if ($LASTEXITCODE -ne 0) { throw 'Copy regression fixture did not compile.' }
& $java -cp ($scratch + [IO.Path]::PathSeparator + $android) com.lume.remote.CopyCheck
if ($LASTEXITCODE -ne 0) { throw 'Copy regression fixture failed.' }
