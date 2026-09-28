param([string]$FixtureDirectory = (Join-Path ([IO.Path]::GetTempPath()) 'lume-portable-tests'), [switch]$SkipBuild)
$ErrorActionPreference = 'Stop'
$project = Split-Path -Parent $PSScriptRoot
if (-not $SkipBuild) {
    & "$env:WINDIR\System32\WindowsPowerShell\v1.0\powershell.exe" -NoProfile -ExecutionPolicy Bypass -File (Join-Path $PSScriptRoot 'build.ps1') -Tests
    if ($LASTEXITCODE -ne 0) { throw 'Windows fixture build failed.' }
    & cargo build --manifest-path (Join-Path $project 'ports\Cargo.toml') -p lume-core --examples --locked -j 2
    if ($LASTEXITCODE -ne 0) { throw 'Portable fixture build failed.' }
}
$scratch = [IO.Path]::GetFullPath($FixtureDirectory)
[IO.Directory]::CreateDirectory($scratch) | Out-Null
$windows = Join-Path $project 'tests\LumeTests.exe'
$rust = Join-Path $project 'ports\target\debug\examples\interop.exe'
$rustHost = Join-Path $project 'ports\target\debug\examples\host_fixture.exe'
$native = Join-Path $project 'datachannel.dll'
function Wait-Invitation($Path, $Process) {
    $timer = [Diagnostics.Stopwatch]::StartNew()
    while (-not [IO.File]::Exists($Path)) {
        if ($Process.HasExited -or $timer.Elapsed.TotalSeconds -gt 30) { throw 'The synthetic host did not start.' }
        Start-Sleep -Milliseconds 50
    }
}
function Stop-Fixture($Path, $Process) {
    [IO.File]::WriteAllText($Path + '.stop', 'stop')
    if (-not $Process.WaitForExit(10000)) { $Process.Kill(); throw 'Synthetic fixture failed to stop.' }
    $Process.Refresh()
    $code = $Process.ExitCode
    $Process.Dispose()
    if ($null -eq $code) { throw 'The fixture exit status was unavailable.' }
    if ($code -ne 0) { throw ('Synthetic host failed: ' + $code) }
}
foreach ($mode in @('--portable-fixture', '--portable-peer-fixture')) {
    $invite = Join-Path $scratch ([Guid]::NewGuid().ToString('N') + '.invite')
    $hostProcess = Start-Process -FilePath $windows -ArgumentList @($mode, ('"' + $invite + '"')) -WindowStyle Hidden -PassThru -RedirectStandardOutput ($invite + '.out') -RedirectStandardError ($invite + '.err')
    # Hold the native process handle before it exits so Windows PowerShell retains its exit code.
    $null = $hostProcess.Handle
    try {
        Wait-Invitation $invite $hostProcess
        & $rust $invite $native
        if ($LASTEXITCODE -ne 0) { throw ('Rust viewer failed: ' + $LASTEXITCODE) }
        Write-Output ('PASS Windows host / Rust viewer: ' + $mode)
    } finally { Stop-Fixture $invite $hostProcess }
}
$invite = Join-Path $scratch ([Guid]::NewGuid().ToString('N') + '.invite')
$hostProcess = Start-Process -FilePath $rustHost -ArgumentList ('"' + $invite + '"') -WindowStyle Hidden -PassThru -RedirectStandardOutput ($invite + '.out') -RedirectStandardError ($invite + '.err')
$null = $hostProcess.Handle
try {
    Wait-Invitation $invite $hostProcess
    & $windows --portable-viewer $invite
    if ($LASTEXITCODE -ne 0) { throw ('Windows viewer failed: ' + $LASTEXITCODE) }
} finally { Stop-Fixture $invite $hostProcess }
Write-Output 'PASS Cross-language TLS, P2P, negotiated source pixels and clean process teardown. Synthetic capture only.'
