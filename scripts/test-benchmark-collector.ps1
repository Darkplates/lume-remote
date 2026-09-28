$ErrorActionPreference = 'Stop'
$root = Split-Path -Parent $PSScriptRoot
$scratch = Join-Path $root ('verification\collector-test-' + [Guid]::NewGuid().ToString('N'))
New-Item -ItemType Directory -Path $scratch | Out-Null
$fixtureName = 'LumeCounterFixture' + [Guid]::NewGuid().ToString('N').Substring(0,8)
$source = Join-Path $scratch 'fixture.cs'
$executable = Join-Path $scratch ($fixtureName + '.exe')
[IO.File]::WriteAllText($source, 'class CounterFixture { static void Main() { System.Threading.Thread.Sleep(60000); } }')
& "$env:WINDIR\Microsoft.NET\Framework64\v4.0.30319\csc.exe" /nologo /target:exe "/out:$executable" $source
if ($LASTEXITCODE -ne 0) { throw 'Counter fixture compilation failed.' }
$fixture = Start-Process -FilePath $executable -WindowStyle Hidden -PassThru
$labels = @{ Product = 'Custom'; ProductVersion = 'collector-fixture'; Role = 'Viewer'; Scenario = 'Idle'; CaseId = 'collector-smoke'; DeviceProfile = 'synthetic helper, not product evidence'; NetworkProfile = 'none'; QualityProfile = 'none'; DurationSeconds = 5; IntervalMilliseconds = 500; OutputDirectory = $scratch }
try {
    & (Join-Path $PSScriptRoot 'measure-resources.ps1') @labels -ProcessName $fixtureName
    $valid = @(Get-ChildItem -LiteralPath $scratch -Directory | ForEach-Object { Get-Content -Raw -LiteralPath (Join-Path $_.FullName 'run.json') | ConvertFrom-Json })
    if ($valid.Count -ne 1 -or $valid[0].status -ne 'complete' -or $valid[0].valid_samples -lt 8) { throw 'Live helper counters were not collected.' }
} finally { if (-not $fixture.HasExited) { Stop-Process -Id $fixture.Id }; $fixture.Dispose() }
$rejected = $false
try { & (Join-Path $PSScriptRoot 'measure-resources.ps1') @labels -ProcessName ('Absent' + [Guid]::NewGuid().ToString('N')) }
catch { $rejected = $true }
if (-not $rejected) { throw 'An absent process was incorrectly reported as a complete measurement.' }
$runs = @(Get-ChildItem -LiteralPath $scratch -Directory)
if ($runs.Count -ne 2) { throw 'Both counter test runs must be retained.' }
$reports = @($runs | ForEach-Object { Join-Path $_.FullName 'run.json' })
python (Join-Path $PSScriptRoot 'benchmark-report.py') --runs @reports --output (Join-Path $scratch 'report.md')
if ($LASTEXITCODE -ne 0) { throw 'Collected evidence could not be analyzed.' }
Write-Output 'PASS Live helper collection, explicit missing-process failure and end-to-end report. Synthetic collector QA only.'
