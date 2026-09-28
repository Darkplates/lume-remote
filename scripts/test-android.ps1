param(
    [Parameter(Mandatory=$true)][string]$Adb,
    [Parameter(Mandatory=$true)][string]$Serial,
    [Parameter(Mandatory=$true)][string]$FixtureDirectory
)
$ErrorActionPreference = 'Stop'
$project = Split-Path -Parent $PSScriptRoot
$scratch = [IO.Path]::GetFullPath($FixtureDirectory)
[IO.Directory]::CreateDirectory($scratch) | Out-Null
$app = Join-Path $project 'ports\android\app\build\outputs\apk\debug\app-debug.apk'
$test = Join-Path $project 'ports\android\app\build\outputs\apk\androidTest\debug\app-debug-androidTest.apk'
$windows = Join-Path $project 'tests\LumeTests.exe'
foreach ($path in @($app,$test,$windows,$Adb)) { if (-not (Test-Path -LiteralPath $path -PathType Leaf)) { throw ('Required test build is missing: ' + $path) } }
function Device {
    & $Adb -s $Serial @args
    if ($LASTEXITCODE -ne 0) { throw 'Android test command failed.' }
}
function Push-PrivateInvitation($Text, $Name) {
    $info = New-Object Diagnostics.ProcessStartInfo
    $info.FileName = $Adb
    $info.Arguments = '-s ' + $Serial + ' shell -T "run-as com.lume.remote sh -c ''cat > files/' + $Name + '''"'
    $info.UseShellExecute = $false; $info.CreateNoWindow = $true
    $info.RedirectStandardInput = $true; $info.RedirectStandardOutput = $true; $info.RedirectStandardError = $true
    $process = New-Object Diagnostics.Process
    $process.StartInfo = $info
    try {
        [void]$process.Start()
        $process.StandardInput.Write($Text); $process.StandardInput.Close()
        if (-not $process.WaitForExit(10000)) { $process.Kill(); throw 'Private fixture upload timed out.' }
        if ($process.ExitCode -ne 0) { throw 'Private fixture upload failed.' }
    } finally { $process.Dispose() }
}
# An explicit serial prevents accidentally operating a physical device alongside the emulator.
if ($Serial -notmatch '^emulator-\d+$') { throw 'This automated fixture is restricted to a local emulator.' }
Device install -r $app
Device install -r $test
Device shell run-as com.lume.remote mkdir -p files
$fixtures = @()
try {
    foreach ($name in @('contract-native.txt','contract-ui.txt')) {
        $invite = Join-Path $scratch ([Guid]::NewGuid().ToString('N') + '.invite')
        $process = Start-Process -FilePath $windows -ArgumentList @('--portable-fixture', ('"' + $invite + '"')) -WindowStyle Hidden -PassThru -RedirectStandardOutput ($invite + '.out') -RedirectStandardError ($invite + '.err')
        $fixture = @{path=$invite; process=$process; port=0}
        $fixtures += $fixture
        $timer = [Diagnostics.Stopwatch]::StartNew()
        while (-not [IO.File]::Exists($invite)) {
            if ($process.HasExited -or $timer.Elapsed.TotalSeconds -gt 20) { throw 'The synthetic Windows host did not start.' }
            Start-Sleep -Milliseconds 50
        }
        $encoded = [IO.File]::ReadAllText($invite).Trim().Substring(7).Replace('-','+').Replace('_','/')
        while ($encoded.Length % 4) { $encoded += '=' }
        $fields = [Text.Encoding]::UTF8.GetString([Convert]::FromBase64String($encoded)).Split('|')
        if ($fields.Length -ne 6 -or $fields[1] -ne '127.0.0.1' -or $fields[3]) { throw 'Only synthetic loopback invitations are permitted.' }
        $fixture.port = [int]$fields[2]
        Device reverse ('tcp:' + $fixture.port) ('tcp:' + $fixture.port)
        Push-PrivateInvitation ([IO.File]::ReadAllText($invite)) $name
    }
    $result = Device shell am instrument -w com.lume.remote.test/com.lume.remote.ContractInstrumentation
    $result
    if (($result -join "`n") -notmatch 'PASS Android runtime contract' -or ($result -join "`n") -match 'FAIL|INSTRUMENTATION_FAILED') { throw 'Android runtime contract did not pass.' }
} finally {
    foreach ($fixture in $fixtures) {
        [IO.File]::WriteAllText($fixture.path + '.stop','stop')
        if (-not $fixture.process.WaitForExit(10000)) { $fixture.process.Kill() }
        if ($fixture.port) { & $Adb -s $Serial reverse --remove ('tcp:' + $fixture.port) | Out-Null }
    }
    & $Adb -s $Serial shell run-as com.lume.remote rm -f files/contract-native.txt files/contract-ui.txt | Out-Null
}
