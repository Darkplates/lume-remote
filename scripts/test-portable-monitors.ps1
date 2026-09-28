param([switch]$SkipBuild)
$ErrorActionPreference='Stop'
$project=Split-Path -Parent $PSScriptRoot
if(-not $SkipBuild){
    & "$env:WINDIR\System32\WindowsPowerShell\v1.0\powershell.exe" -NoProfile -ExecutionPolicy Bypass -File (Join-Path $PSScriptRoot 'build.ps1') -Tests
    if($LASTEXITCODE -ne 0){throw 'Windows fixtures failed to build.'}
    & cargo build --manifest-path (Join-Path $project 'ports/Cargo.toml') -p lume-core --example monitor_interop --locked -j 2
    if($LASTEXITCODE -ne 0){throw 'Rust display fixture failed to build.'}
}
$windows=Join-Path $project 'tests/LumeTests.exe'
$rust=Join-Path $project 'ports/target/debug/examples/monitor_interop.exe'
$scratch=Join-Path ([IO.Path]::GetTempPath()) ('lume-displays-'+[Guid]::NewGuid().ToString('N'))
[IO.Directory]::CreateDirectory($scratch)|Out-Null
foreach($direction in @('windows-host','rust-host')){
    $invite=Join-Path $scratch ($direction+'.invite')
    $program=if($direction -eq 'windows-host'){$windows}else{$rust}
    $mode=if($direction -eq 'windows-host'){'--portable-monitors-fixture'}else{'host'}
    $process=Start-Process -FilePath $program -ArgumentList @($mode,('"'+$invite+'"')) -WindowStyle Hidden -PassThru -RedirectStandardOutput ($invite+'.out') -RedirectStandardError ($invite+'.err')
    $null=$process.Handle
    try{
        $clock=[Diagnostics.Stopwatch]::StartNew()
        while(-not [IO.File]::Exists($invite)){if($process.HasExited -or $clock.Elapsed.TotalSeconds -gt 30){throw 'Display host did not start.'};Start-Sleep -Milliseconds 50}
        if($direction -eq 'windows-host'){& $rust viewer $invite}else{& $windows --portable-monitors-viewer $invite}
        if($LASTEXITCODE -ne 0){throw ('Display viewer failed: '+$direction)}
    }finally{
        [IO.File]::WriteAllText($invite+'.stop','stop')
        if(-not $process.WaitForExit(10000)){$process.Kill();throw 'Owned display fixture did not stop.'}
        $process.Refresh();$code=$process.ExitCode;$process.Dispose()
        if($null -eq $code -or $code -ne 0){throw 'Owned display fixture failed.'}
    }
}
