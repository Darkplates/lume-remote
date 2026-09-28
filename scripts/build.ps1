param([switch]$Tests, [switch]$Native, [switch]$TestsOnly)
$ErrorActionPreference = 'Stop'
$projectRoot = Split-Path -Parent $PSScriptRoot
$existingApp = Join-Path $projectRoot 'LumeRemote.exe'
if ($TestsOnly -and $Native) { throw 'Build native binaries separately before running tests-only compilation.' }
if ($TestsOnly) { $Tests = $true }
if ((-not $TestsOnly) -and (Test-Path -LiteralPath $existingApp)) {
    try { $checkHandle = [System.IO.File]::Open($existingApp, [System.IO.FileMode]::Open, [System.IO.FileAccess]::ReadWrite, [System.IO.FileShare]::None); $checkHandle.Dispose() }
    catch { throw 'Close the running Lume Remote application before rebuilding or verifying this copy. Active sessions are not stopped automatically.' }
}
if ($Native) { & (Join-Path $PSScriptRoot 'build-native.ps1') }
$compiler = Join-Path $env:WINDIR 'Microsoft.NET\Framework64\v4.0.30319\csc.exe'
if (-not (Test-Path -LiteralPath $compiler)) { throw 'The Windows .NET Framework compiler is missing.' }
$framework = Get-ItemProperty 'HKLM:\SOFTWARE\Microsoft\NET Framework Setup\NDP\v4\Full'
if ($framework.Release -lt 528040) { throw '.NET Framework 4.8 or later is required.' }
$sources = @(Get-ChildItem -LiteralPath (Join-Path $projectRoot 'src') -Filter '*.cs' | ForEach-Object { $_.FullName })
$sdkMetadataRoot = Join-Path ${env:ProgramFiles(x86)} 'Windows Kits\10\UnionMetadata'
$sdkMetadata = @(Get-ChildItem -LiteralPath $sdkMetadataRoot -Directory -ErrorAction SilentlyContinue | Where-Object { $_.Name -match '^\d+\.' } | Sort-Object { [version]$_.Name } -Descending | ForEach-Object { Join-Path $_.FullName 'Windows.winmd' } | Where-Object { Test-Path -LiteralPath $_ }) | Select-Object -First 1
if (-not $sdkMetadata) { throw 'The Windows 10 SDK metadata is required to build PDF printing. Install the Windows SDK through the C++ Build Tools installer.' }
$frameworkDir = Split-Path -Parent $compiler
$runtimeFacade = Join-Path $env:WINDIR 'Microsoft.NET\assembly\GAC_MSIL\System.Runtime\v4.0_4.0.0.0__b03f5f7f11d50a3a\System.Runtime.dll'
$windowsRuntimeReferences = @("/r:$sdkMetadata", "/r:$runtimeFacade", "/r:$frameworkDir\System.Runtime.WindowsRuntime.dll")
$brandArguments = @("/resource:$projectRoot\assets\brand.png,LumeRemote.Brand.png", "/win32icon:$projectRoot\assets\lume.ico")
$arguments = @('/nologo', '/optimize+', '/unsafe', '/langversion:5', '/target:winexe', '/platform:x64', "/out:$projectRoot\LumeRemote.exe", "/win32manifest:$projectRoot\app.manifest", '/r:System.dll', '/r:System.Core.dll', '/r:System.Drawing.dll', '/r:System.Windows.Forms.dll', '/r:System.Security.dll', '/r:System.Web.Extensions.dll', '/r:System.ServiceProcess.dll') + $sources
if (-not $TestsOnly) {
    $arguments += $brandArguments + $windowsRuntimeReferences
    & $compiler @arguments
    if ($LASTEXITCODE -ne 0) { throw "Compiler failed with exit code $LASTEXITCODE" }
    Get-Item -LiteralPath (Join-Path $projectRoot 'LumeRemote.exe') | Select-Object Name, Length
}
if ($Tests) {
    $testDir = Join-Path $projectRoot 'tests'
    $testSources = @($sources | Where-Object { -not $_.EndsWith('Program.cs') }) + @(Get-ChildItem -LiteralPath $testDir -Filter '*.cs' | ForEach-Object { $_.FullName })
    $testArguments = @('/nologo', '/optimize+', '/unsafe', '/langversion:5', '/target:exe', '/platform:x64', "/out:$testDir\LumeTests.exe", '/r:System.dll', '/r:System.Core.dll', '/r:System.Drawing.dll', '/r:System.Windows.Forms.dll', '/r:System.Security.dll', '/r:System.Web.Extensions.dll', '/r:System.ServiceProcess.dll') + $testSources
    $testArguments += $brandArguments + $windowsRuntimeReferences
    & $compiler @testArguments
    if ($LASTEXITCODE -ne 0) { throw "Test compiler failed with exit code $LASTEXITCODE" }
    Copy-Item -LiteralPath (Join-Path $projectRoot 'LumeRemote.exe.config') -Destination (Join-Path $testDir 'LumeTests.exe.config')
    if (Test-Path -LiteralPath (Join-Path $projectRoot 'LumeCapture.dll')) { Copy-Item -LiteralPath (Join-Path $projectRoot 'LumeCapture.dll') -Destination (Join-Path $testDir 'LumeCapture.dll') }
    if (Test-Path -LiteralPath (Join-Path $projectRoot 'datachannel.dll')) { Copy-Item -LiteralPath (Join-Path $projectRoot 'datachannel.dll') -Destination (Join-Path $testDir 'datachannel.dll') }
    if (Test-Path -LiteralPath (Join-Path $projectRoot 'LumeVideo.dll')) { Copy-Item -LiteralPath (Join-Path $projectRoot 'LumeVideo.dll') -Destination (Join-Path $testDir 'LumeVideo.dll') }
}
