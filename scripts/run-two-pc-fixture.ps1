param(
    [Parameter(Mandatory=$true)][ValidateSet('Deploy','Refresh','Start','Poll','Cleanup')][string]$Action,
    [ValidateSet('automatic','manual')][string]$Mode = 'automatic',
    [ValidatePattern('^[a-z0-9-]+$')][string]$RunName = 'automatic-v1',
    [ValidateRange(0,180)][int]$ReplyDelay = 0,
    [ValidateRange(5,45)][int]$IdleSeconds = 15,
    [switch]$DelayBeforeAnswer,
    [ValidatePattern('^[A-Za-z]:\\[A-Za-z0-9 _\\.-]+$')][string]$LocalRoot = 'C:\AI\lume-two-pc-fixture-a',
    [ValidatePattern('^[A-Za-z]:\\[A-Za-z0-9 _\\.-]+$')][string]$RemoteRoot = 'C:\AI\lume-two-pc-fixture-b'
)
$ErrorActionPreference = 'Stop'
$evidence = Join-Path (Split-Path -Parent $PSScriptRoot) 'verification\two-pc-runtime'
$built = Join-Path $evidence 'autonomous-fixture'
$a = Join-Path ([IO.Path]::GetFullPath($LocalRoot)) 'autonomous-fixture'
$b = Join-Path ([IO.Path]::GetFullPath($RemoteRoot)) 'autonomous-fixture'
$caseA = Join-Path $a ('cases\' + $RunName)
$caseB = Join-Path $b ('cases\' + $RunName)
$peer = @((Get-Item -LiteralPath 'WSMan:\localhost\Client\TrustedHosts').Value -split ',' | Where-Object { $_ -and $_ -notmatch '[*?/]' })
if ($peer.Count -ne 1) { throw 'Exactly one configured peer is required.' }
$session = $null
$launch = {
    param($folder,$caseFolder,$role,$mode,$hostFolder,$delay,$idle,$beforeAnswer)
    $ErrorActionPreference = 'Stop'
    if (Test-Path -LiteralPath $caseFolder) { throw 'This case already exists; choose a fresh run name.' }
    New-Item -ItemType Directory -Path $caseFolder | Out-Null
    $exe = Join-Path $folder 'TwoPcScenario.exe'
    $argumentString = $role + ' ' + $mode + ' "' + $caseFolder + '" "' + $hostFolder + '" ' + $delay + ' ' + $idle
    $users = @(Get-CimInstance Win32_Process -Filter "Name='explorer.exe'" | ForEach-Object {
        $owner = Invoke-CimMethod -InputObject $_ -MethodName GetOwner
        if ($owner.ReturnValue -eq 0) { $owner.Domain + '\' + $owner.User }
    } | Select-Object -Unique)
    if ($users.Count -ne 1) { throw 'Exactly one signed-in desktop user is required.' }
    $startScript = Join-Path $caseFolder 'start-fixture.ps1'
    $pidFile = Join-Path $caseFolder 'owned-process.pid'
    $script = @"
`$ErrorActionPreference='Stop'
`$env:LUME_FIXTURE_DELAY_BEFORE_ANSWER='$(if ($beforeAnswer) { '1' } else { '0' })'
`$process = Start-Process -FilePath '$exe' -ArgumentList '$argumentString' -WorkingDirectory '$folder' -WindowStyle Hidden -PassThru -RedirectStandardOutput '$(Join-Path $caseFolder 'console.txt')' -RedirectStandardError '$(Join-Path $caseFolder 'stderr.txt')'
`$process.Id | Set-Content -LiteralPath '$pidFile' -Encoding Ascii
"@
    [IO.File]::WriteAllText($startScript,$script,(New-Object Text.UTF8Encoding($false)))
    $taskName = 'LumeFixture-' + [guid]::NewGuid().ToString('N')
    $powershell = Join-Path $env:WINDIR 'System32\WindowsPowerShell\v1.0\powershell.exe'
    $taskAction = New-ScheduledTaskAction -Execute $powershell -Argument ('-NoProfile -ExecutionPolicy Bypass -WindowStyle Hidden -File "' + $startScript + '"')
    $principal = New-ScheduledTaskPrincipal -UserId $users[0] -LogonType Interactive -RunLevel Limited
    $settings = New-ScheduledTaskSettingsSet -ExecutionTimeLimit (New-TimeSpan -Minutes 2)
    Register-ScheduledTask -TaskName $taskName -Action $taskAction -Principal $principal -Settings $settings | Out-Null
    try {
        Start-ScheduledTask -TaskName $taskName
        $deadline = [DateTime]::UtcNow.AddSeconds(15)
        while (-not (Test-Path -LiteralPath $pidFile)) {
            if ([DateTime]::UtcNow -gt $deadline) { throw 'The fixture launcher did not publish its owned PID.' }
            Start-Sleep -Milliseconds 200
        }
        [pscustomobject]@{Role=$role;ProcessId=[int](Get-Content -LiteralPath $pidFile -Raw);Started=$true} | ConvertTo-Json -Compress
    } finally { Unregister-ScheduledTask -TaskName $taskName -Confirm:$false }
}
$read = {
    param($folder,$caseFolder)
    $ErrorActionPreference = 'Stop'
    $state = $null
    if (Test-Path -LiteralPath (Join-Path $caseFolder 'status.json')) { $state = Get-Content -LiteralPath (Join-Path $caseFolder 'status.json') -Raw | ConvertFrom-Json }
    $running = $false
    if (Test-Path -LiteralPath (Join-Path $caseFolder 'owned-process.pid')) {
        $processId = [int](Get-Content -LiteralPath (Join-Path $caseFolder 'owned-process.pid') -Raw)
        $process = Get-CimInstance Win32_Process -Filter "ProcessId=$processId"
        $running = $null -ne $process -and $process.ExecutablePath -eq (Join-Path $folder 'TwoPcScenario.exe')
    }
    [pscustomobject]@{Running=$running;State=$state;PrivateReplyReady=(Test-Path -LiteralPath (Join-Path $caseFolder 'private-reply.tmp'))} | ConvertTo-Json -Depth 8 -Compress
}
$cleanup = {
    param($folder,$caseFolder)
    $ErrorActionPreference = 'Stop'
    $pidFile = Join-Path $caseFolder 'owned-process.pid'
    $stopped = $false
    if (Test-Path -LiteralPath $pidFile) {
        $processId = [int](Get-Content -LiteralPath $pidFile -Raw)
        $process = Get-CimInstance Win32_Process -Filter "ProcessId=$processId"
        if ($process -and $process.ExecutablePath -eq (Join-Path $folder 'TwoPcScenario.exe')) { Stop-Process -Id $processId -Force; $stopped = $true }
    }
    foreach ($name in @('private-offer.tmp','private-offer.tmp.new','private-reply.tmp','private-reply.tmp.new')) {
        $path = Join-Path $caseFolder $name
        if (Test-Path -LiteralPath $path) { Remove-Item -LiteralPath $path }
    }
    [pscustomobject]@{StoppedOwnedProcess=$stopped;PrivateFilesRemaining=@(Get-ChildItem -LiteralPath $caseFolder -Filter 'private-*.tmp*' -ErrorAction SilentlyContinue).Count} | ConvertTo-Json -Compress
}
try {
    try { $session = New-PSSession -ComputerName $peer[0] -Authentication Negotiate -SessionOption (New-PSSessionOption -OpenTimeout 8000 -OperationTimeout 120000) }
    catch { throw 'The configured WinRM peer is unavailable. Peer identity suppressed.' }
    switch ($Action) {
        'Refresh' {
            $exeA = Join-Path $a 'TwoPcScenario.exe'
            $exeB = Join-Path $b 'TwoPcScenario.exe'
            if (@(Get-CimInstance Win32_Process | Where-Object ExecutablePath -eq $exeA).Count) { throw 'An owned fixture is still running on A.' }
            Invoke-Command -Session $session -ScriptBlock {
                param($folder)
                $exe = Join-Path $folder 'TwoPcScenario.exe'
                if (@(Get-CimInstance Win32_Process | Where-Object ExecutablePath -eq $exe).Count) { throw 'An owned fixture is still running on B.' }
                $backup = Join-Path $folder 'TwoPcScenario.v1.exe'
                if (-not (Test-Path -LiteralPath $backup)) { Copy-Item -LiteralPath $exe -Destination $backup }
            } -ArgumentList $b
            $backup = Join-Path $a 'TwoPcScenario.v1.exe'
            if (-not (Test-Path -LiteralPath $backup)) { Copy-Item -LiteralPath $exeA -Destination $backup }
            Copy-Item -LiteralPath (Join-Path $built 'TwoPcScenario.exe') -Destination $exeA
            Copy-Item -LiteralPath (Join-Path $built 'TwoPcScenario.exe') -Destination $exeB -ToSession $session
            $expected = (Get-FileHash -LiteralPath (Join-Path $built 'TwoPcScenario.exe')).Hash
            $remote = Invoke-Command -Session $session -ScriptBlock { param($path) (Get-FileHash -LiteralPath $path).Hash } -ArgumentList $exeB
            if ($expected -ne $remote -or $expected -ne (Get-FileHash -LiteralPath $exeA).Hash) { throw 'Updated fixture hashes differ.' }
            [pscustomobject]@{Refreshed=$true;Sha256=$expected;BothMatch=$true} | ConvertTo-Json -Compress
        }
        'Deploy' {
            if ((Test-Path -LiteralPath $a) -or (Invoke-Command -Session $session -ScriptBlock { param($folder) Test-Path -LiteralPath $folder } -ArgumentList $b)) { throw 'Fixture folder exists; inspect before redeployment.' }
            New-Item -ItemType Directory -Path $a | Out-Null
            Invoke-Command -Session $session -ScriptBlock { param($folder) New-Item -ItemType Directory -Path $folder | Out-Null } -ArgumentList $b
            $names = @('TwoPcScenario.exe','TwoPcScenario.exe.config','datachannel.dll','LumeCapture.dll','LumeVideo.dll')
            $checks = foreach ($name in $names) {
                Copy-Item -LiteralPath (Join-Path $built $name) -Destination $a
                Copy-Item -LiteralPath (Join-Path $built $name) -Destination $b -ToSession $session
                $expected = (Get-FileHash -LiteralPath (Join-Path $built $name) -Algorithm SHA256).Hash
                $remote = Invoke-Command -Session $session -ScriptBlock { param($path) (Get-FileHash -LiteralPath $path -Algorithm SHA256).Hash } -ArgumentList (Join-Path $b $name)
                if ($expected -ne $remote -or $expected -ne (Get-FileHash -LiteralPath (Join-Path $a $name)).Hash) { throw 'Fixture transfer hash mismatch.' }
                [pscustomobject]@{Name=$name;Sha256=$expected;BothMatch=$true}
            }
            $checks | ConvertTo-Json -Depth 4 | Set-Content -LiteralPath (Join-Path $evidence 'fixture-deployment.json') -Encoding Utf8
            [pscustomobject]@{Deployed=$true;Files=$names.Count;AllHashesMatch=$true} | ConvertTo-Json -Compress
        }
        'Start' {
            $started = & $launch $a $caseA 'host' $Mode $caseA $ReplyDelay $IdleSeconds ([bool]$DelayBeforeAnswer) | ConvertFrom-Json
            $offer = Join-Path $caseA 'private-offer.tmp'
            $deadline = [DateTime]::UtcNow.AddSeconds(45)
            while (-not (Test-Path -LiteralPath $offer)) {
                if ([DateTime]::UtcNow -gt $deadline) { throw 'The host did not prepare its invitation in time.' }
                Start-Sleep -Milliseconds 250
            }
            $viewer = Invoke-Command -Session $session -ScriptBlock $launch -ArgumentList $b,$caseB,'viewer',$Mode,$caseA,$ReplyDelay,$IdleSeconds,([bool]$DelayBeforeAnswer) | ConvertFrom-Json
            Copy-Item -LiteralPath $offer -Destination (Join-Path $caseB 'private-offer.tmp.new') -ToSession $session
            Invoke-Command -Session $session -ScriptBlock { param($folder) Move-Item -LiteralPath (Join-Path $folder 'private-offer.tmp.new') -Destination (Join-Path $folder 'private-offer.tmp') } -ArgumentList $caseB
            Remove-Item -LiteralPath $offer
            [pscustomobject]@{Run=$RunName;Mode=$Mode;ReplyDelay=$ReplyDelay;DelayBeforeAnswer=[bool]$DelayBeforeAnswer;IdleSeconds=$IdleSeconds;Host=$started;Viewer=$viewer} | ConvertTo-Json -Depth 5 -Compress
        }
        'Poll' {
            $remote = Invoke-Command -Session $session -ScriptBlock $read -ArgumentList $b,$caseB | ConvertFrom-Json
            $transferred = $false
            if ($remote.PrivateReplyReady) {
                $staged = Join-Path $caseA 'private-reply.tmp.new'
                Copy-Item -LiteralPath (Join-Path $caseB 'private-reply.tmp') -Destination $staged -FromSession $session
                Move-Item -LiteralPath $staged -Destination (Join-Path $caseA 'private-reply.tmp')
                Invoke-Command -Session $session -ScriptBlock { param($folder) Remove-Item -LiteralPath (Join-Path $folder 'private-reply.tmp') } -ArgumentList $caseB
                $transferred = $true
            }
            $local = & $read $a $caseA | ConvertFrom-Json
            $result = [pscustomobject]@{Run=$RunName;ReplyTransferred=$transferred;Host=$local;Viewer=$remote}
            $result | ConvertTo-Json -Depth 10 | Set-Content -LiteralPath (Join-Path $evidence ($RunName + '-fixture.json')) -Encoding Utf8
            $result | ConvertTo-Json -Depth 10 -Compress
        }
        'Cleanup' {
            $local = & $cleanup $a $caseA | ConvertFrom-Json
            $remote = Invoke-Command -Session $session -ScriptBlock $cleanup -ArgumentList $b,$caseB | ConvertFrom-Json
            [pscustomobject]@{Run=$RunName;Host=$local;Viewer=$remote} | ConvertTo-Json -Depth 5 -Compress
        }
    }
} finally { if ($session) { Remove-PSSession -Session $session } }
