param([string]$SourceDirectory = '', [string]$BuildDirectory = '', [string]$OutputPath = '')
$ErrorActionPreference = 'Stop'
$projectRoot = Split-Path -Parent $PSScriptRoot
if (-not $SourceDirectory) { $SourceDirectory = Join-Path $projectRoot 'build\peer-source' }
$SourceDirectory = [System.IO.Path]::GetFullPath($SourceDirectory)
$git = Get-Command git.exe -ErrorAction Stop
$python = Get-Command python.exe -ErrorAction Stop
$vswhere = Join-Path ${env:ProgramFiles(x86)} 'Microsoft Visual Studio\Installer\vswhere.exe'
if (-not (Test-Path -LiteralPath $vswhere)) { throw 'Visual Studio C++ Build Tools and a Windows SDK are required to rebuild WebRTC. The packaged DLL needs neither.' }
$installation = & $vswhere -latest -products '*' -requires Microsoft.VisualStudio.Component.VC.Tools.x86.x64 -property installationPath
if (-not $installation) { throw 'The Visual Studio C++ compiler is unavailable.' }
New-Item -ItemType Directory -Path $SourceDirectory -Force | Out-Null
$sources = @(
    @{ Name='libdatachannel'; Url='https://github.com/paullouisageneau/libdatachannel.git'; Tag='v0.24.6'; Commit='6b1e2e620f1e37f0eafeee702eaea0043cb305fd' },
    @{ Name='mbedtls'; Url='https://github.com/Mbed-TLS/mbedtls.git'; Tag='mbedtls-3.6.7'; Commit='068ff080b369adfac81509f9b57b2afabaf82dc5' }
)
foreach ($source in $sources) {
    $checkout = Join-Path $SourceDirectory $source.Name
    if (-not (Test-Path -LiteralPath $checkout)) {
        & $git.Source clone --depth 1 --branch $source.Tag --recurse-submodules --shallow-submodules $source.Url $checkout
        if ($LASTEXITCODE -ne 0) { throw "Source checkout failed: $($source.Name)" }
    }
    $commit = & $git.Source -C $checkout rev-parse HEAD
    if ($LASTEXITCODE -ne 0 -or $commit.Trim() -ne $source.Commit) { throw "Unexpected source revision: $checkout. Existing files are preserved." }
    $changes = & $git.Source -C $checkout status --porcelain --untracked-files=no
    if ($LASTEXITCODE -ne 0 -or $changes) { throw "Source changes detected: $checkout. Existing files are preserved." }
    $submodules = & $git.Source -C $checkout submodule status --recursive
    if ($LASTEXITCODE -ne 0 -or ($submodules | Where-Object { $_ -match '^[-+U]' })) { throw "Submodule revisions are not ready: $checkout" }
}
$toolsDirectory = Join-Path $SourceDirectory 'tools'
New-Item -ItemType Directory -Path $toolsDirectory -Force | Out-Null
$cmakeZip = Join-Path $toolsDirectory 'cmake-4.4.3-windows-x86_64.zip'
$cmakeHash = '4d52ebab7193a698651639ed80d8d04fd903358843572cf44c7fd234cb7c26ab'
if (-not (Test-Path -LiteralPath $cmakeZip)) { Invoke-WebRequest -UseBasicParsing -Uri 'https://github.com/Kitware/CMake/releases/download/v4.4.3/cmake-4.4.3-windows-x86_64.zip' -OutFile $cmakeZip }
$cmakeDirectory = Join-Path $toolsDirectory 'cmake-4.4.3-windows-x86_64'
# A previously extracted folder is never trusted: its files could have changed after the
# archive was checked. Hash the archive through a handle that denies writers, extract
# those same bytes into a fresh folder, then replace the fixed-path copy with it.
Add-Type -AssemblyName System.IO.Compression
Add-Type -AssemblyName System.IO.Compression.FileSystem
$cmakeStaging = Join-Path $toolsDirectory ('cmake-extract-' + [guid]::NewGuid().ToString('N'))
try {
$archiveStream = [System.IO.File]::Open($cmakeZip, [System.IO.FileMode]::Open, [System.IO.FileAccess]::Read, [System.IO.FileShare]::Read)
try {
    $sha = [System.Security.Cryptography.SHA256]::Create()
    try { $actualHash = [BitConverter]::ToString($sha.ComputeHash($archiveStream)).Replace('-', '').ToLowerInvariant() } finally { $sha.Dispose() }
    if ($actualHash -ne $cmakeHash) { throw 'CMake archive digest mismatch. Nothing from that archive was executed.' }
    $archiveStream.Position = 0
    $archive = New-Object System.IO.Compression.ZipArchive($archiveStream, [System.IO.Compression.ZipArchiveMode]::Read, $true)
    try { [System.IO.Compression.ZipFileExtensions]::ExtractToDirectory($archive, $cmakeStaging) } finally { $archive.Dispose() }
} finally { $archiveStream.Dispose() }
$extractedCMake = Join-Path $cmakeStaging 'cmake-4.4.3-windows-x86_64'
if (-not (Test-Path -LiteralPath (Join-Path $extractedCMake 'bin\cmake.exe') -PathType Leaf)) { throw "The verified CMake archive has an unexpected layout: $cmakeStaging" }
if (Test-Path -LiteralPath $cmakeDirectory) { Remove-Item -LiteralPath $cmakeDirectory -Recurse -Force }
Move-Item -LiteralPath $extractedCMake -Destination $cmakeDirectory
} finally {
    # A failed or interrupted extraction never leaves a staging folder behind.
    if (Test-Path -LiteralPath $cmakeStaging) { Remove-Item -LiteralPath $cmakeStaging -Recurse -Force -ErrorAction SilentlyContinue }
}
$cmakeExe = Join-Path $cmakeDirectory 'bin\cmake.exe'
if (-not $BuildDirectory) { $BuildDirectory = Join-Path $projectRoot 'build\peer-bin' }
$BuildDirectory = [System.IO.Path]::GetFullPath($BuildDirectory)
$patchedSource = Join-Path $BuildDirectory 'patched-source'
& $python.Source (Join-Path $PSScriptRoot 'prepare-peer-source.py') --source-root $SourceDirectory --output $patchedSource
if ($LASTEXITCODE -ne 0) { throw 'Verified WebRTC source overlay preparation failed.' }
& $cmakeExe -S (Join-Path $projectRoot 'native\peer') -B $BuildDirectory -G 'Visual Studio 17 2022' -A x64 "-DLUME_PEER_SOURCE_ROOT=$patchedSource" '-DCMAKE_POLICY_VERSION_MINIMUM=3.5'
if ($LASTEXITCODE -ne 0) { throw 'WebRTC dependency configuration failed.' }
& $cmakeExe --build $BuildDirectory --config Release --target datachannel --parallel 2
if ($LASTEXITCODE -ne 0) { throw 'WebRTC dependency build failed.' }
if (-not $OutputPath) { $OutputPath = Join-Path $projectRoot 'datachannel.dll' }
$OutputPath = [System.IO.Path]::GetFullPath($OutputPath)
New-Item -ItemType Directory -Path (Split-Path -Parent $OutputPath) -Force | Out-Null
Copy-Item -LiteralPath (Join-Path $BuildDirectory 'libdatachannel\Release\datachannel.dll') -Destination $OutputPath
Get-Item -LiteralPath $OutputPath | Select-Object FullName, Length
