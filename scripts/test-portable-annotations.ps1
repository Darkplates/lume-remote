param([Parameter(Mandatory=$true)][string]$Scratch)
$ErrorActionPreference = 'Stop'
$project = Split-Path -Parent $PSScriptRoot
$scratchRoot = [IO.Path]::GetFullPath($Scratch)
if (Test-Path -LiteralPath $scratchRoot) { throw 'Use a new isolated annotation directory.' }
[IO.Directory]::CreateDirectory($scratchRoot) | Out-Null
$invite = Join-Path $scratchRoot 'private.invite'
$fixture = Start-Process -FilePath (Join-Path $project 'tests\LumeTests.exe') -ArgumentList @('--portable-annotations-fixture', ('"' + $invite + '"')) -WindowStyle Hidden -PassThru -RedirectStandardOutput (Join-Path $scratchRoot 'host.out') -RedirectStandardError (Join-Path $scratchRoot 'host.err')
$null = $fixture.Handle
try {
    $timer = [Diagnostics.Stopwatch]::StartNew()
    while (-not (Test-Path -LiteralPath $invite)) {
        if ($fixture.HasExited -or $timer.Elapsed.TotalSeconds -gt 30) { throw 'Synthetic annotation fixture did not start.' }
        Start-Sleep -Milliseconds 50
    }
    & (Join-Path $project 'ports\target\debug\examples\annotation_windows_interop.exe') $invite
    if ($LASTEXITCODE -ne 0) { throw 'The real Windows annotation-gate probe failed.' }
} finally {
    [IO.File]::WriteAllText($invite + '.stop', 'stop')
    if (-not $fixture.WaitForExit(15000)) { $fixture.Kill(); throw 'Owned annotation fixture did not stop.' }
    $fixture.Refresh(); $code = $fixture.ExitCode; $fixture.Dispose()
    Get-Content -LiteralPath (Join-Path $scratchRoot 'host.out')
    if ($null -eq $code -or $code -ne 0) { Get-Content -LiteralPath (Join-Path $scratchRoot 'host.err'); throw 'Owned annotation fixture failed.' }
}
Write-Output 'PASS Actual Windows gate and positive receipts; empty clears only, synthetic monitors, injected input sink, joined teardown.'
