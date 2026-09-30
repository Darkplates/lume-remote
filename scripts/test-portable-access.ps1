param([Parameter(Mandatory=$true)][string]$Scratch,[switch]$SkipBuild,[switch]$Pairing)
$ErrorActionPreference='Stop'
$project=Split-Path -Parent $PSScriptRoot
$scratchRoot=[IO.Path]::GetFullPath($Scratch)
if(Test-Path -LiteralPath $scratchRoot){throw 'Use a new isolated scratch directory.'}
[IO.Directory]::CreateDirectory($scratchRoot)|Out-Null
if(-not $SkipBuild){
    & "$env:WINDIR\System32\WindowsPowerShell\v1.0\powershell.exe" -NoProfile -ExecutionPolicy Bypass -File (Join-Path $PSScriptRoot 'build.ps1') -Tests
    if($LASTEXITCODE -ne 0){throw 'Windows fixture build failed.'}
    & cargo build --manifest-path (Join-Path $project 'ports/Cargo.toml') -p lume-core --examples --locked -j 2
    if($LASTEXITCODE -ne 0){throw 'Rust fixture build failed.'}
}
$invite=Join-Path $scratchRoot 'private.invite'
$remote=Join-Path $scratchRoot 'remote'
$mode=if($Pairing){'--portable-paired-fixture'}else{'--portable-files-fixture'}
$arguments=@($mode,('"'+$invite+'"'));if(-not $Pairing){$arguments+=('"'+$remote+'"')}
$fixture=Start-Process -FilePath (Join-Path $project 'tests/LumeTests.exe') -ArgumentList $arguments -PassThru -WindowStyle Hidden -RedirectStandardOutput (Join-Path $scratchRoot 'host.out') -RedirectStandardError (Join-Path $scratchRoot 'host.err')
$null=$fixture.Handle
try{
    $timer=[Diagnostics.Stopwatch]::StartNew()
    while(-not (Test-Path -LiteralPath $invite)){if($fixture.HasExited -or $timer.Elapsed.TotalSeconds -gt 35){throw 'Synthetic fixture did not start.'};Start-Sleep -Milliseconds 50}
    $client=Join-Path $project 'ports/target/debug/examples/access_interop.exe'
    $native=Join-Path $project 'datachannel.dll'
    if($Pairing){& $client paired $invite $native}else{& $client files $invite $native (Join-Path $scratchRoot 'local')}
    if($LASTEXITCODE -ne 0){throw 'Portable access fixture failed.'}
    if(-not $Pairing){
        $sourceHash=(Get-FileHash -LiteralPath (Join-Path $scratchRoot 'local/upload.bin') -Algorithm SHA256).Hash
        $uploads=@(Get-ChildItem -LiteralPath $remote -Filter 'upload*.bin' -File)
        if($uploads.Count -ne 2){throw 'Remote upload collision handling failed.'}
        foreach($file in $uploads){if((Get-FileHash -LiteralPath $file.FullName -Algorithm SHA256).Hash -ne $sourceHash){throw 'Remote uploaded bytes differ.'}}
        foreach($name in @('folder-job','folder-job (1)')) {
            $tree=Join-Path $remote $name
            if(-not(Test-Path -LiteralPath (Join-Path $tree 'nested/empty') -PathType Container)){throw 'Remote empty folder is missing.'}
            $unicodeLeaf = 'nested/caf' + [char]0x00E9 + '.bin'
            if((Get-FileHash -LiteralPath (Join-Path $tree $unicodeLeaf) -Algorithm SHA256).Hash -ne $sourceHash){throw 'Remote folder bytes differ.'}
        }
        Write-Output 'PASS Independent Windows filesystem hashes match both uploaded copies.'
    }
}finally{
    [IO.File]::WriteAllText($invite+'.stop','stop')
    if(-not $fixture.WaitForExit(15000)){$fixture.Kill();throw 'Synthetic fixture did not stop.'}
    $fixture.Refresh();$code=$fixture.ExitCode;$fixture.Dispose()
    if($null -eq $code -or $code -ne 0){throw 'Synthetic host fixture failed. Inspect isolated host.err.'}
}
Write-Output 'PASS Portable access fixture completed. Synthetic hosts and isolated files only.'
