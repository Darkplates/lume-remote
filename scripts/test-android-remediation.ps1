param([string]$JavaHome=$env:JAVA_HOME)
$ErrorActionPreference='Stop'
$project=Split-Path -Parent $PSScriptRoot
if(-not $JavaHome) { throw 'Set JAVA_HOME to a JDK 17 or newer.' }
$javac=Join-Path $JavaHome 'bin/javac.exe'
$java=Join-Path $JavaHome 'bin/java.exe'
foreach($required in @($javac,$java)) { if(-not(Test-Path -LiteralPath $required -PathType Leaf)) { throw 'A complete JDK is required.' } }
$scratch=Join-Path ([IO.Path]::GetTempPath()) ('lume-android-jvm-'+[Guid]::NewGuid().ToString('N'))
[IO.Directory]::CreateDirectory($scratch) | Out-Null
$sources=@('ports/android/app/src/main/java/com/lume/remote/PendingDocumentOperation.java','ports/android/app/src/main/java/com/lume/remote/PcmPlayback.java','ports/android/app/src/test/java/com/lume/remote/AndroidRemediationCheck.java') | ForEach-Object { Join-Path $project $_ }
& $javac --release 17 -encoding UTF-8 -d $scratch @sources
if($LASTEXITCODE -ne 0) { throw 'Android remediation JVM fixture compilation failed.' }
& $java -cp $scratch com.lume.remote.AndroidRemediationCheck
if($LASTEXITCODE -ne 0) { throw 'Android remediation JVM regression failed.' }
Write-Output 'BOUNDARY: JVM policy/playback checks only; Activity/Bundle/SAF and audio-device execution require the isolated instrumentation or device tests.'
