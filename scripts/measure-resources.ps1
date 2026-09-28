param(
    [ValidateSet('Lume','TeamViewer','AnyDesk','RustDesk','Custom')][string]$Product = 'Lume',
    [string]$ProductVersion = '',
    [ValidateSet('Viewer','Host')][string]$Role = 'Viewer',
    [ValidateSet('Idle','Motion','FileCopy')][string]$Scenario = 'Motion',
    [string]$CaseId = '',
    [string]$DeviceProfile = '',
    [string]$NetworkProfile = '',
    [string]$QualityProfile = '',
    [string[]]$ProcessName = @(),
    [ValidateRange(5,3600)][int]$DurationSeconds = 60,
    [ValidateRange(250,10000)][int]$IntervalMilliseconds = 1000,
    [string]$OutputDirectory = ''
)
$ErrorActionPreference = 'Stop'
if ($env:OS -ne 'Windows_NT') { throw 'This collector requires Windows.' }

# These are labels supplied by the tester, not machine identifiers discovered by the tool.
function Read-Label([string]$Value, [string]$Prompt) {
    if (-not $Value.Trim()) { $Value = Read-Host $Prompt }
    if (-not $Value.Trim() -or $Value.Length -gt 240 -or $Value -match '[\r\n\x00-\x1f]') {
        throw 'Use a nonempty label of at most 240 characters, without control characters.'
    }
    return $Value.Trim()
}
$ProductVersion = Read-Label $ProductVersion 'Exact product version (and Lume commit when available)'
$CaseId = Read-Label $CaseId 'Anonymous comparison case, e.g. wan-a-1080p'
$DeviceProfile = Read-Label $DeviceProfile 'CPU, GPU, RAM and Windows version (no PC name)'
$NetworkProfile = Read-Label $NetworkProfile 'Network and actual route, e.g. separate fibre networks, direct P2P'
$QualityProfile = Read-Label $QualityProfile 'Actual resolution, FPS target, encoding and product quality setting'
if (-not $ProcessName.Count) {
    $ProcessName = switch ($Product) {
        'Lume' { @('LumeRemote') }
        'TeamViewer' { @('TeamViewer','TeamViewer_Service','TeamViewer_Desktop') }
        'AnyDesk' { @('AnyDesk') }
        'RustDesk' { @('rustdesk') }
        'Custom' { (Read-Host 'Exact process names without .exe, separated by commas').Split(',') }
    }
}
$ProcessName = @($ProcessName | ForEach-Object { $_.Trim() } | Sort-Object -Unique)
foreach ($name in $ProcessName) {
    if ($name -notmatch '^[A-Za-z0-9_.-]{1,80}$' -or $name.EndsWith('.exe')) { throw 'Supply exact process names without paths or .exe.' }
}
if (-not $ProcessName.Count) { throw 'At least one process name is required.' }
if (-not $OutputDirectory) { $OutputDirectory = Join-Path (Split-Path -Parent $PSScriptRoot) 'verification\benchmarks' }
$runId = [DateTime]::UtcNow.ToString('yyyyMMddTHHmmssZ') + '-' + [Guid]::NewGuid().ToString('N').Substring(0,8)
$runDirectory = Join-Path ([IO.Path]::GetFullPath($OutputDirectory)) $runId
New-Item -ItemType Directory -Path $runDirectory | Out-Null
$encoding = New-Object Text.UTF8Encoding($false)
$invariant = [Globalization.CultureInfo]::InvariantCulture
$logicalCpus = [Environment]::ProcessorCount

function Get-Counters {
    $items = @{}
    $unreadable = 0
    foreach ($name in $ProcessName) {
        foreach ($process in [Diagnostics.Process]::GetProcessesByName($name)) {
            try {
                $process.Refresh()
                # The start time makes PID reuse a changed process set, never a negative CPU delta.
                $identity = '{0}:{1}' -f $process.Id, $process.StartTime.ToUniversalTime().Ticks
                $items[$identity] = @{
                    cpu = $process.TotalProcessorTime.TotalSeconds
                    working = $process.WorkingSet64
                    private = $process.PrivateMemorySize64
                }
            } catch { $unreadable++ } finally { $process.Dispose() }
        }
    }
    return @{ items = $items; unreadable = $unreadable }
}
function Format-Number([double]$Value) { return $Value.ToString('R', $invariant) }
$manifest = [ordered]@{
    schema = 1; run_id = $runId; product = $Product; version = $ProductVersion
    role = $Role; scenario = $Scenario; case_id = $CaseId
    device_profile = $DeviceProfile; network_profile = $NetworkProfile; quality_profile = $QualityProfile
    process_names = $ProcessName; logical_cpus = $logicalCpus
    requested_seconds = $DurationSeconds; interval_ms = $IntervalMilliseconds
    started_utc = [DateTime]::UtcNow.ToString('o'); status = 'interrupted'
    samples = 0; valid_samples = 0; actual_seconds = 0
    scope = 'Named processes only; service coverage must be checked manually. No FPS, bandwidth or connection latency is measured.'
}
$manifestPath = Join-Path $runDirectory 'run.json'
[IO.File]::WriteAllText($manifestPath, ($manifest | ConvertTo-Json -Depth 4), $encoding)
$writer = New-Object IO.StreamWriter((Join-Path $runDirectory 'samples.csv'), $false, $encoding)
$clock = [Diagnostics.Stopwatch]::StartNew()
try {
    $writer.WriteLine('elapsed_s,interval_s,status,process_count,cpu_percent,working_set_mib,private_bytes_mib')
    $previous = Get-Counters
    $last = $clock.Elapsed.TotalSeconds
    Write-Host "Collecting $Product $Role/$Scenario for $DurationSeconds seconds. Processes: $($ProcessName -join ', ')"
    Write-Host 'Keep the scenario and all quality settings unchanged. This tool does not capture your screen.'
    while ($clock.Elapsed.TotalSeconds -lt $DurationSeconds) {
        Start-Sleep -Milliseconds $IntervalMilliseconds
        $current = Get-Counters
        $now = $clock.Elapsed.TotalSeconds
        $elapsed = $now - $last
        $status = 'ok'
        $cpu = ''; $working = ''; $private = ''
        if ($current.unreadable -or $previous.unreadable) { $status = 'unreadable' }
        elseif (-not $current.items.Count -or -not $previous.items.Count) { $status = 'missing' }
        elseif (@(Compare-Object @($previous.items.Keys) @($current.items.Keys)).Count) { $status = 'process_changed' }
        else {
            $delta = 0.0; $workingBytes = 0.0; $privateBytes = 0.0
            foreach ($identity in $current.items.Keys) {
                $delta += $current.items[$identity].cpu - $previous.items[$identity].cpu
                $workingBytes += $current.items[$identity].working
                $privateBytes += $current.items[$identity].private
            }
            if ($delta -lt 0 -or $elapsed -le 0) { $status = 'invalid_counter' }
            else {
                $cpu = Format-Number (100 * $delta / $elapsed / $logicalCpus)
                $working = Format-Number ($workingBytes / 1048576)
                $private = Format-Number ($privateBytes / 1048576)
                $manifest.valid_samples++
            }
        }
        $writer.WriteLine((@((Format-Number $now),(Format-Number $elapsed),$status,$current.items.Count,$cpu,$working,$private) -join ','))
        $writer.Flush()
        $manifest.samples++
        $previous = $current; $last = $now
    }
    $manifest.status = if ($manifest.samples -eq $manifest.valid_samples -and $manifest.samples -gt 0) { 'complete' } else { 'incomplete' }
} finally {
    $clock.Stop(); $writer.Dispose()
    $manifest.actual_seconds = $clock.Elapsed.TotalSeconds
    [IO.File]::WriteAllText($manifestPath, ($manifest | ConvertTo-Json -Depth 4), $encoding)
    Write-Host "Saved local evidence: $runDirectory"
}
if ($manifest.status -ne 'complete') { throw 'Collection is incomplete: missing, inaccessible or changed processes. Inspect samples.csv; missing intervals are not zero use.' }
Write-Host 'Coverage is complete for the selected process names only. Verify helper/service coverage before a comparison.'
