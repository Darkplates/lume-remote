param([string]$OutputPath = '', [switch]$SourceOnly)
$ErrorActionPreference = 'Stop'
$projectRoot = [IO.Path]::GetFullPath((Split-Path -Parent $PSScriptRoot))
if (-not $OutputPath) {
    $name = if ($SourceOnly) { 'LumeRemote-0.12.1-dev-source.zip' } else { 'LumeRemote-0.12.1-dev-win64.zip' }
    $OutputPath = Join-Path (Split-Path -Parent $projectRoot) $name
}
if (Test-Path -LiteralPath $OutputPath) { throw 'Archive exists. Choose a new output path to preserve it.' }
# Source and documentation files are packaged only when Git tracks them and they match
# HEAD, so stray or edited working-tree files cannot reach an archive. The build outputs
# below are untracked by design and are the only exception.
$git = Get-Command git -ErrorAction Stop
$tracked = New-Object 'System.Collections.Generic.HashSet[string]' ([StringComparer]::Ordinal)
$listing = & $git.Source -C $projectRoot -c core.quotePath=false ls-files -z
if ($LASTEXITCODE -ne 0) { throw 'git ls-files failed. Package from a Git checkout.' }
foreach ($name in (($listing -join "`n") -split "`0")) { if ($name) { [void]$tracked.Add($name) } }
$changed = New-Object 'System.Collections.Generic.HashSet[string]' ([StringComparer]::Ordinal)
$status = & $git.Source -C $projectRoot -c core.quotePath=false status --porcelain=v1 -z --untracked-files=no --no-renames
if ($LASTEXITCODE -ne 0) { throw 'git status failed. Package from a Git checkout.' }
foreach ($record in (($status -join "`n") -split "`0")) { if ($record.Length -gt 3) { [void]$changed.Add($record.Substring(3)) } }
$buildOutputs = @('LumeRemote.exe','LumeCapture.dll','LumeVideo.dll','datachannel.dll')
$rootFiles = @('.gitignore','.gitattributes','BENCHMARK.bat','BUILD-P2P.bat','BUILD.bat','CHANGELOG.md','CHECK-WAKE.bat','CONTRIBUTING.md','LICENSE.txt','LumeRemote.exe.config','README.md','SECURITY.md','START-HERE.txt','START.bat','THIRD-PARTY-NOTICES.txt','UPDATE-HOST.bat','VERIFY.bat','app.manifest')
# AGENTS.md holds internal maintainer handoff notes, so only the source archive carries it.
if ($SourceOnly) { $rootFiles += 'AGENTS.md' } else { $rootFiles += $buildOutputs }
$rootFiles += @('ports/README.md','benchmarks/trials-template.csv','docs/demo.html','docs/LAUNCH-COPY.txt')
if ($SourceOnly) { $rootFiles += 'BUILD-APPLE.command' }
$files = @()
foreach ($relative in $rootFiles) {
    $path = Join-Path $projectRoot $relative
    if (-not (Test-Path -LiteralPath $path -PathType Leaf)) { throw "Missing release file: $relative" }
    $files += Get-Item -LiteralPath $path
}
$allowed = '^(src/[^/]+\.cs|tests/[^/]+\.cs|native/(capture|video|media)\.cpp|native/peer/CMakeLists\.txt|native/peer/patches/manual-signaling-(ice-v1\.patch|ice-checks-v1\.patch|dtls-v1\.patch|v1\.json)|scripts/[^/]+\.(ps1|py|sh)|relay/[^/]+\.(py|md|bat)|docs/[^/]+\.(md|json)|docs/images/[^/]+\.png|docs/images/ui/(dark|light)/[^/]+\.png|docs/evidence/v(0[456789]|10|11|12)-[^/]+\.txt|third-party/[^/]+\.txt|third-party/portable/inventory\.json|third-party/portable/[^/]+/[^/]+\.txt|assets/(brand-source\.png|brand\.png|lume\.ico)|\.github/workflows/[^/]+\.yml|\.github/ISSUE_TEMPLATE/[^/]+\.yml|\.github/PULL_REQUEST_TEMPLATE\.md)$'
foreach ($folder in @('src','tests','native','scripts','relay','docs','third-party','assets','.github')) {
    foreach ($file in Get-ChildItem -LiteralPath (Join-Path $projectRoot $folder) -File -Recurse -Force) {
        $relative = $file.FullName.Substring($projectRoot.Length + 1).Replace('\','/')
        if ($relative -match $allowed) { $files += $file }
    }
}
if ($SourceOnly) {
    $portFiles = @('ports/Cargo.toml','ports/Cargo.lock','ports/core/Cargo.toml','ports/desktop/Cargo.toml','ports/bridge/Cargo.toml','ports/bridge/include/lume.h','ports/android/settings.gradle','ports/android/build.gradle','ports/android/gradle.properties','ports/android/gradlew','ports/android/gradlew.bat','ports/android/gradle/wrapper/gradle-wrapper.jar','ports/android/gradle/wrapper/gradle-wrapper.properties','ports/android/app/build.gradle','ports/android/app/src/main/AndroidManifest.xml','ports/android/native/jni.c','ports/ios/project.yml')
    foreach ($relative in $portFiles) {
        $path = Join-Path $projectRoot $relative
        if (-not (Test-Path -LiteralPath $path -PathType Leaf)) { throw ('Missing portable source file: ' + $relative) }
        $files += Get-Item -LiteralPath $path
    }
    foreach ($folder in @('ports/core/src','ports/core/examples','ports/desktop/src','ports/desktop/tests','ports/bridge/src','ports/android/app/src/main/java','ports/android/app/src/main/res','ports/android/app/src/androidTest','ports/android/app/src/test','ports/ios/Lume','ports/macos')) {
        foreach ($file in Get-ChildItem -LiteralPath (Join-Path $projectRoot $folder) -File -Recurse) {
            if ($file.Extension -in @('.rs','.java','.xml','.png','.swift','.h')) { $files += $file }
        }
    }
}
$files = @($files | Sort-Object FullName)
$problems = @()
foreach ($file in $files) {
    $relative = $file.FullName.Substring($projectRoot.Length + 1).Replace('\','/')
    if ($buildOutputs -contains $relative) { continue }
    if (-not $tracked.Contains($relative)) { $problems += "untracked: $relative" }
    elseif ($changed.Contains($relative)) { $problems += "modified: $relative" }
}
# Also catch a staged or deleted file that the allowlist would package.
foreach ($relative in $changed) {
    if ($relative -match $allowed -and $problems -notcontains "modified: $relative") { $problems += "changed or deleted: $relative" }
}
if ($problems.Count -gt 0) { throw ("Only committed source is packaged. Commit, restore or remove these files first:`n" + ($problems -join "`n")) }
$manifest = @($files | ForEach-Object {
    if ($_.Attributes -band [IO.FileAttributes]::ReparsePoint) { throw 'Release files must not be links.' }
    $relative = $_.FullName.Substring($projectRoot.Length + 1).Replace('\','/')
    '{0}  {1}' -f (Get-FileHash -Algorithm SHA256 -LiteralPath $_.FullName).Hash.ToLowerInvariant(), $relative
})
$encoding = New-Object Text.UTF8Encoding($false)
if (-not $SourceOnly) { [IO.File]::WriteAllLines((Join-Path $projectRoot 'SHA256SUMS.txt'), $manifest, $encoding) }
Add-Type -AssemblyName System.IO.Compression
Add-Type -AssemblyName System.IO.Compression.FileSystem
$stream = [IO.File]::Open($OutputPath, [IO.FileMode]::CreateNew)
$zip = New-Object IO.Compression.ZipArchive($stream, [IO.Compression.ZipArchiveMode]::Create)
try {
    foreach ($file in $files) {
        $relative = $file.FullName.Substring($projectRoot.Length + 1).Replace('\','/')
        [IO.Compression.ZipFileExtensions]::CreateEntryFromFile($zip, $file.FullName, "LumeRemote/$relative", [IO.Compression.CompressionLevel]::Optimal) | Out-Null
    }
    $entry = $zip.CreateEntry('LumeRemote/SHA256SUMS.txt')
    $writer = New-Object IO.StreamWriter($entry.Open(), $encoding)
    try { $writer.Write(($manifest -join "`r`n") + "`r`n") } finally { $writer.Dispose() }
} finally { $zip.Dispose(); $stream.Dispose() }
$hash = (Get-FileHash -Algorithm SHA256 -LiteralPath $OutputPath).Hash.ToLowerInvariant()
[IO.File]::WriteAllText($OutputPath + '.sha256', "$hash  $([IO.Path]::GetFileName($OutputPath))`r`n", $encoding)
& (Join-Path $PSScriptRoot 'verify-package.ps1') -Archive $OutputPath
Get-Item -LiteralPath $OutputPath | Select-Object FullName, Length
