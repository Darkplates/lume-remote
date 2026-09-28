param([switch]$RequireReady)
$ErrorActionPreference = 'Stop'
$projectRoot = Split-Path -Parent $PSScriptRoot
$report = Get-Content -LiteralPath (Join-Path $projectRoot 'docs\release-gates.json') -Raw | ConvertFrom-Json
if ($report.schema -ne 1 -or -not $report.gates) { throw 'Invalid release gate report.' }
$allowed = @('passed','pending','missing','untested')
$ids = @{}
foreach ($gate in $report.gates) {
    if (-not $gate.id -or $ids.ContainsKey($gate.id) -or $allowed -notcontains $gate.status -or $gate.required -isnot [bool]) { throw 'Invalid or duplicate release gate.' }
    $ids[$gate.id] = $true
}
$blockers = @($report.gates | Where-Object { $_.required -and $_.status -ne 'passed' })
if ($report.public_release_ready -and $blockers.Count -gt 0) { throw 'Release summary contradicts its required gates.' }
$report.gates | Select-Object name, status, required | Format-Table -AutoSize
if ($blockers.Count -gt 0) {
    Write-Host "PUBLIC RELEASE BLOCKED: $($blockers.Count) required gates remain open."
    if ($RequireReady) { exit 2 }
} else { Write-Host 'All required gates are marked passed. Verify referenced evidence and obtain publication authorization.' }
