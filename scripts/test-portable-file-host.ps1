param([Parameter(Mandatory=$true)][string]$Scratch)
$ErrorActionPreference='Stop'
$project=Split-Path -Parent $PSScriptRoot
$scratchRoot=[IO.Path]::GetFullPath($Scratch)
if(Test-Path -LiteralPath $scratchRoot){throw 'Use a new isolated scratch directory.'}
[IO.Directory]::CreateDirectory($scratchRoot)|Out-Null
$invite=Join-Path $scratchRoot 'private.invite';$remote=Join-Path $scratchRoot 'remote'
$hostProcess=Start-Process -FilePath (Join-Path $project 'ports/target/debug/examples/file_host_fixture.exe') -ArgumentList @(('"'+$invite+'"'),('"'+$remote+'"')) -WindowStyle Hidden -PassThru -RedirectStandardOutput (Join-Path $scratchRoot 'host.out') -RedirectStandardError (Join-Path $scratchRoot 'host.err')
$null=$hostProcess.Handle
try{
    $timer=[Diagnostics.Stopwatch]::StartNew()
    while(-not(Test-Path -LiteralPath $invite)){if($hostProcess.HasExited -or $timer.Elapsed.TotalSeconds -gt 20){throw 'Synthetic portable host did not start.'};Start-Sleep -Milliseconds 50}
    & (Join-Path $project 'tests/LumeTests.exe') --portable-files-viewer $invite (Join-Path $scratchRoot 'local')
    if($LASTEXITCODE -ne 0){throw 'Windows viewer / portable file host failed.'}
    $expected=(Get-FileHash -LiteralPath (Join-Path $scratchRoot 'local/upload.bin') -Algorithm SHA256).Hash
    if((Get-FileHash -LiteralPath (Join-Path $remote 'roundtrip/upload.bin') -Algorithm SHA256).Hash -ne $expected){throw 'Host filesystem hash differs.'}
    Write-Output 'PASS Independent host filesystem confirms uploaded bytes.'
}finally{
    [IO.File]::WriteAllText($invite+'.stop','stop')
    if(-not $hostProcess.WaitForExit(15000)){$hostProcess.Kill();throw 'Synthetic host did not stop.'}
    $hostProcess.Refresh();$code=$hostProcess.ExitCode;$hostProcess.Dispose();if($null -eq $code -or $code -ne 0){throw 'Synthetic host failed.'}
}
