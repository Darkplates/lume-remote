param([Parameter(Mandatory=$true)][string]$Scratch)
$ErrorActionPreference='Stop'
$project=Split-Path -Parent $PSScriptRoot
$root=[IO.Path]::GetFullPath($Scratch)
if(Test-Path -LiteralPath $root){throw 'Use a new isolated scratch directory.'}
[IO.Directory]::CreateDirectory($root)|Out-Null
$hostRoot=Join-Path $root 'host'
$fixture=Start-Process -FilePath (Join-Path $project 'ports/target/debug/examples/permanent_interop.exe') -ArgumentList @(('"'+(Join-Path $project 'datachannel.dll')+'"'),('"'+$hostRoot+'"'),'--windows-viewer') -WindowStyle Hidden -PassThru -RedirectStandardOutput (Join-Path $root 'host.out') -RedirectStandardError (Join-Path $root 'host.err')
$null=$fixture.Handle
try{
    $code=Join-Path $hostRoot 'private.pair';$timer=[Diagnostics.Stopwatch]::StartNew()
    while(-not(Test-Path -LiteralPath $code)){if($fixture.HasExited -or $timer.Elapsed.TotalSeconds-gt 40){throw 'Portable synthetic host did not become ready.'};Start-Sleep -Milliseconds 50}
    & (Join-Path $project 'tests/LumeTests.exe') --portable-paired-viewer $code
    if($LASTEXITCODE-ne 0){throw 'Windows viewer / portable permanent host failed.'}
}finally{
    if(Test-Path -LiteralPath $hostRoot){[IO.File]::WriteAllText((Join-Path $hostRoot 'private.stop'),'stop')}
    if(-not $fixture.WaitForExit(15000)){$fixture.Kill();throw 'Synthetic host did not stop.'}
    $fixture.Refresh();$exit=$fixture.ExitCode;$fixture.Dispose()
    Get-Content -LiteralPath (Join-Path $root 'host.out')
    if($exit-ne 0){Get-Content -LiteralPath (Join-Path $root 'host.err');throw 'Synthetic permanent host failed.'}
}
'PASS Bidirectional product interoperability; synthetic capture and isolated credentials only.'
