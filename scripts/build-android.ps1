param(
    [Parameter(Mandatory=$true)][string]$Sdk,
    [Parameter(Mandatory=$true)][string]$CMake,
    [Parameter(Mandatory=$true)][string]$Ninja,
    [Parameter(Mandatory=$true)][string]$PeerSource,
    [string]$BuildDirectory = '',
    [string[]]$Abis = @('arm64-v8a', 'x86_64')
)
$ErrorActionPreference = 'Stop'
$project = Split-Path -Parent $PSScriptRoot
$Sdk = [IO.Path]::GetFullPath($Sdk)
$PeerSource = [IO.Path]::GetFullPath($PeerSource)
$python = Get-Command python.exe -ErrorAction Stop
if (-not $BuildDirectory) { $BuildDirectory = Join-Path $project 'build\android' }
$BuildDirectory = [IO.Path]::GetFullPath($BuildDirectory)
$ndk = Join-Path $Sdk 'ndk\28.2.13676358'
$bin = Join-Path $ndk 'toolchains\llvm\prebuilt\windows-x86_64\bin'
if (-not (Test-Path -LiteralPath (Join-Path $bin 'clang.exe'))) { throw 'Android NDK 28.2.13676358 is required.' }
foreach ($entry in @(@('libdatachannel','6b1e2e620f1e37f0eafeee702eaea0043cb305fd'),@('mbedtls','068ff080b369adfac81509f9b57b2afabaf82dc5'))) {
    $path = Join-Path $PeerSource $entry[0]
    $commit = & git -C $path rev-parse HEAD
    if ($LASTEXITCODE -ne 0 -or $commit.Trim() -ne $entry[1]) { throw ('Unexpected pinned dependency: ' + $entry[0]) }
    $changes = & git -C $path status --porcelain --untracked-files=no
    if ($LASTEXITCODE -ne 0 -or $changes) { throw 'Dependency sources must be unchanged.' }
    $submodules = & git -C $path submodule status --recursive
    if ($LASTEXITCODE -ne 0 -or ($submodules | Where-Object { $_ -match '^[-+U]' })) { throw 'Pinned submodules are incomplete.' }
}
$patchedSource = Join-Path $BuildDirectory 'patched-source'
& $python.Source (Join-Path $PSScriptRoot 'prepare-peer-source.py') --source-root $PeerSource --output $patchedSource
if ($LASTEXITCODE -ne 0) { throw 'Verified Android WebRTC source overlay preparation failed.' }
$previousFlags = $env:RUSTFLAGS
$previousEnvironment = @{}
function Set-TemporaryEnvironment($Name, $Value) {
    if (-not $previousEnvironment.ContainsKey($Name)) { $previousEnvironment[$Name] = [Environment]::GetEnvironmentVariable($Name,'Process') }
    [Environment]::SetEnvironmentVariable($Name,$Value,'Process')
}
try {
    $env:RUSTFLAGS = '-C link-arg=-Wl,-z,max-page-size=16384'
    foreach ($abi in $Abis) {
        if ($abi -eq 'arm64-v8a') { $target = 'aarch64-linux-android'; $triple = 'aarch64-linux-android' }
        elseif ($abi -eq 'x86_64') { $target = 'x86_64-linux-android'; $triple = 'x86_64-linux-android' }
        else { throw 'Supported Android ABIs: arm64-v8a, x86_64.' }
        $clang = Join-Path $bin ($triple + '26-clang.cmd')
        $normalized = $target.Replace('-','_')
        Set-TemporaryEnvironment ('CARGO_TARGET_' + $normalized.ToUpperInvariant() + '_LINKER') $clang
        Set-TemporaryEnvironment ('CC_' + $normalized) $clang
        Set-TemporaryEnvironment ('AR_' + $normalized) (Join-Path $bin 'llvm-ar.exe')
        & cargo build --manifest-path (Join-Path $project 'ports\Cargo.toml') -p lume-bridge --release --target $target --locked -j 2
        if ($LASTEXITCODE -ne 0) { throw ('Rust Android build failed: ' + $abi) }
        $nativeBuild = Join-Path $BuildDirectory $abi
        & $CMake -S (Join-Path $project 'native\peer') -B $nativeBuild -G Ninja "-DCMAKE_MAKE_PROGRAM=$Ninja" "-DCMAKE_TOOLCHAIN_FILE=$ndk\build\cmake\android.toolchain.cmake" "-DANDROID_ABI=$abi" '-DANDROID_PLATFORM=android-26' '-DANDROID_SUPPORT_FLEXIBLE_PAGE_SIZES=ON' '-DCMAKE_BUILD_TYPE=Release' '-DCMAKE_POLICY_VERSION_MINIMUM=3.5' "-DLUME_PEER_SOURCE_ROOT=$patchedSource"
        if ($LASTEXITCODE -ne 0) { throw ('Android WebRTC configure failed: ' + $abi) }
        & $CMake --build $nativeBuild --target datachannel --parallel 2
        if ($LASTEXITCODE -ne 0) { throw ('Android WebRTC build failed: ' + $abi) }
        $output = Join-Path $project ('ports\android\app\src\main\jniLibs\' + $abi)
        New-Item -ItemType Directory -Path $output -Force | Out-Null
        Copy-Item -LiteralPath (Join-Path $project "ports\target\$target\release\liblume_bridge.so") -Destination $output
        $peerLibrary = Get-ChildItem -LiteralPath (Join-Path $nativeBuild 'libdatachannel') -Filter 'libdatachannel.so*' -File | Sort-Object Length -Descending | Select-Object -First 1
        if (-not $peerLibrary) { throw 'The Android WebRTC library is missing.' }
        Copy-Item -LiteralPath $peerLibrary.FullName -Destination (Join-Path $output 'libdatachannel.so')
        Copy-Item -LiteralPath (Join-Path $ndk "toolchains\llvm\prebuilt\windows-x86_64\sysroot\usr\lib\$triple\libc++_shared.so") -Destination $output
        & $clang -shared -fPIC -O2 '-Wl,-z,max-page-size=16384' ("-I" + (Join-Path $project 'ports\bridge\include')) (Join-Path $project 'ports\android\native\jni.c') ("-L" + $output) -llume_bridge -o (Join-Path $output 'liblume_jni.so')
        if ($LASTEXITCODE -ne 0) { throw ('JNI adapter build failed: ' + $abi) }
        & (Join-Path $bin 'llvm-strip.exe') --strip-unneeded (Join-Path $output 'libdatachannel.so') (Join-Path $output 'liblume_jni.so')
        if ($LASTEXITCODE -ne 0) { throw 'Android library stripping failed.' }
        Write-Output ('Built Android libraries for ' + $abi)
    }
} finally {
    $env:RUSTFLAGS = $previousFlags
    foreach ($name in $previousEnvironment.Keys) {
        if ($null -eq $previousEnvironment[$name]) { Remove-Item -LiteralPath ('Env:' + $name) -ErrorAction SilentlyContinue }
        else { [Environment]::SetEnvironmentVariable($name,$previousEnvironment[$name],'Process') }
    }
}
