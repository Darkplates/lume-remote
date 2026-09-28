param([string]$ReportDirectory = '', [switch]$Safe)
$ErrorActionPreference = 'Stop'
$projectRoot = Split-Path -Parent $PSScriptRoot
if (-not $ReportDirectory) { $ReportDirectory = Join-Path $projectRoot 'verification' }
New-Item -ItemType Directory -Path $ReportDirectory -Force | Out-Null
$ReportDirectory = (Resolve-Path -LiteralPath $ReportDirectory).Path
& (Join-Path $PSScriptRoot 'build.ps1') -TestsOnly
$python = Get-Command python.exe -ErrorAction SilentlyContinue
$relayProcess = $null
try {
    $testArgs = @()
    if ($python) {
        $relayTests = Start-Process -FilePath $python.Source -ArgumentList @('-m', 'unittest', '-v', 'test_relay.py') -WorkingDirectory (Join-Path $projectRoot 'relay') -WindowStyle Hidden -Wait -PassThru -RedirectStandardOutput (Join-Path $ReportDirectory 'relay-tests-stdout.txt') -RedirectStandardError (Join-Path $ReportDirectory 'relay-tests.txt')
        Get-Content -LiteralPath (Join-Path $ReportDirectory 'relay-tests.txt')
        if ($relayTests.ExitCode -ne 0) { throw 'Relay tests failed.' }
        $probe = New-Object System.Net.Sockets.TcpListener([System.Net.IPAddress]::Loopback, 0)
        $probe.Start()
        $relayPort = $probe.LocalEndpoint.Port
        $probe.Stop()
        $relayProcess = Start-Process -FilePath $python.Source -ArgumentList @('-u', 'relay.py', '--bind', '127.0.0.1', '--port', "$relayPort") -WorkingDirectory (Join-Path $projectRoot 'relay') -WindowStyle Hidden -PassThru -RedirectStandardOutput (Join-Path $ReportDirectory 'relay-service.txt') -RedirectStandardError (Join-Path $ReportDirectory 'relay-errors.txt')
        $ready = $false
        $deadline = [DateTime]::UtcNow.AddSeconds(20)
        while ([DateTime]::UtcNow -lt $deadline) {
            if ($relayProcess.HasExited) { throw 'The temporary relay did not start.' }
            $socketProbe = New-Object System.Net.Sockets.TcpClient
            try {
                $pendingConnect = $socketProbe.BeginConnect('127.0.0.1', $relayPort, $null, $null)
                if ($pendingConnect.AsyncWaitHandle.WaitOne(200)) {
                    $socketProbe.EndConnect($pendingConnect)
                    if ($socketProbe.Connected) { $ready = $true }
                }
                $pendingConnect.AsyncWaitHandle.Close()
            } catch { } finally { $socketProbe.Close() }
            if ($ready) { break }
            Start-Sleep -Milliseconds 100
        }
        if (-not $ready) { throw 'Timed out waiting for the temporary relay.' }
        $testArgs = @('--relay-port', "$relayPort")
    } else { Write-Warning 'Python is unavailable: relay and relayed TLS checks are skipped.' }
    if ($Safe) { $testArgs += '--safe' }
    & (Join-Path $projectRoot 'tests\LumeTests.exe') @testArgs | Tee-Object -FilePath (Join-Path $ReportDirectory 'native-tests.txt')
    if ($LASTEXITCODE -ne 0) { throw 'Application tests failed.' }
    Write-Host "Reports: $ReportDirectory"
    Write-Host 'These checks do not prove second-PC, public-Internet or competitor performance acceptance.'
} finally {
    if ($relayProcess -and -not $relayProcess.HasExited) { Stop-Process -Id $relayProcess.Id }
}
