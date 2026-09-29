param([string]$OutputRoot = '', [switch]$SkipBuild)
# Renders every owned form at the current Windows display scale and runs the UI checks.
# Repeat at 100, 125, 150 and 200 percent. Lume is system-DPI-aware: after changing the scale,
# sign out and back in, then confirm the DPI printed below matches the scale you chose.
$ErrorActionPreference = 'Stop'
$projectRoot = Split-Path -Parent $PSScriptRoot
if (-not $OutputRoot) { $OutputRoot = Join-Path $projectRoot 'verification\ui-dpi' }
if (-not $SkipBuild) { & (Join-Path $PSScriptRoot 'build.ps1') -TestsOnly }
$tests = Join-Path $projectRoot 'tests\LumeTests.exe'
if (-not (Test-Path -LiteralPath $tests)) { throw 'tests\LumeTests.exe is missing. Run scripts\build-all.ps1 -Tests first.' }
$stamp = Get-Date -Format 'yyyyMMdd-HHmmss'
$output = Join-Path $OutputRoot "pending-$stamp"
New-Item -ItemType Directory -Path $output -Force | Out-Null
& $tests --render-ui (Join-Path $output 'render') | Tee-Object -FilePath (Join-Path $output 'render.txt')
if ($LASTEXITCODE -ne 0) { throw 'Form rendering failed.' }
& $tests --ui-preview (Join-Path $output 'preview') | Tee-Object -FilePath (Join-Path $output 'preview.txt')
if ($LASTEXITCODE -ne 0) { throw 'Interface preview failed.' }
& $tests --ui | Tee-Object -FilePath (Join-Path $output 'ui-checks.txt')
$uiResult = $LASTEXITCODE
$scale = (Get-Content -LiteralPath (Join-Path $output 'render\dpi.txt') -Raw).Trim()
$percent = if ($scale -match '\((\d+)% scale\)') { $Matches[1] } else { 'unknown' }
$final = Join-Path $OutputRoot "$percent-percent-$stamp"
Move-Item -LiteralPath $output -Destination $final
Write-Host "$scale"
Write-Host "Screenshots and logs: $final"
if ($uiResult -ne 0) { throw "UI checks failed at $percent% scale. See ui-checks.txt." }
Write-Host 'UI checks passed at this scale. Compare the screenshots with the other scales before calling a scale fixed.'
