param(
    [Parameter(Mandatory = $true)][string]$SourceDirectory,
    [string]$ManifestPath = ''
)
$ErrorActionPreference = 'Stop'
Set-StrictMode -Version Latest
if (-not $ManifestPath) { $ManifestPath = Join-Path $PSScriptRoot '..\native\peer\patches\manual-signaling-v1.json' }
$ManifestPath = [System.IO.Path]::GetFullPath($ManifestPath)
$SourceDirectory = [System.IO.Path]::GetFullPath($SourceDirectory)
$patchDirectory = Split-Path -Parent $ManifestPath
$manifest = Get-Content -LiteralPath $ManifestPath -Raw | ConvertFrom-Json
if ($manifest.version -ne 1) { throw 'Unsupported native peer patch manifest version.' }
$git = (Get-Command git.exe -ErrorAction Stop).Source
$utf8 = [System.Text.UTF8Encoding]::new($false)
function Read-NormalizedText([string]$Path) {
    [System.IO.File]::ReadAllText($Path).Replace("`r`n", "`n")
}
function Get-TextDigest([string]$Value) {
    $sha = [System.Security.Cryptography.SHA256]::Create()
    try { ([System.BitConverter]::ToString($sha.ComputeHash($utf8.GetBytes($Value)))).Replace('-', '').ToLowerInvariant() }
    finally { $sha.Dispose() }
}
function Resolve-Child([string]$Root, [string]$Relative) {
    if ([System.IO.Path]::IsPathRooted($Relative)) { throw 'Patch paths must be relative.' }
    $resolved = [System.IO.Path]::GetFullPath((Join-Path $Root $Relative))
    if (-not $resolved.StartsWith($Root.TrimEnd('\', '/') + [System.IO.Path]::DirectorySeparatorChar, [System.StringComparison]::OrdinalIgnoreCase)) {
        throw 'Patch path leaves its intended source directory.'
    }
    $resolved
}
$pending = @()
# Validate every source and patch before changing any file. Accept only the exact
# pinned input or exact previously patched output, including on repeated runs.
foreach ($entry in $manifest.patches) {
    $patch = Resolve-Child $patchDirectory $entry.patch
    $source = Resolve-Child $SourceDirectory $entry.source
    if ((Get-TextDigest (Read-NormalizedText $patch)) -ne $entry.patchSha256) { throw "Native peer patch digest mismatch: $($entry.patch)" }
    $value = Read-NormalizedText $source
    $digest = Get-TextDigest $value
    if ($digest -eq $entry.afterSha256) { Write-Output "Already applied: $($entry.patch)"; continue }
    if ($digest -ne $entry.beforeSha256) { throw "Unexpected native peer source digest: $($entry.source). Existing files are preserved." }
    $pending += [pscustomobject]@{ Entry = $entry; Patch = $patch; Source = $source; Value = $value }
}
$previousCeiling = [System.Environment]::GetEnvironmentVariable('GIT_CEILING_DIRECTORIES', 'Process')
try {
    # The copied source may sit inside the application's checkout. Prevent git
    # from treating that ancestor repository as the patch's working directory.
    [System.Environment]::SetEnvironmentVariable('GIT_CEILING_DIRECTORIES', (Split-Path -Parent $SourceDirectory), 'Process')
    foreach ($item in $pending) {
        # Canonical line endings make patch matching reproducible across checkouts.
        [System.IO.File]::WriteAllText($item.Source, $item.Value, $utf8)
        & $git -C $SourceDirectory apply --check --whitespace=nowarn $item.Patch
        if ($LASTEXITCODE -ne 0) { throw "Native peer patch check failed: $($item.Entry.patch)" }
        & $git -C $SourceDirectory apply --whitespace=nowarn $item.Patch
        if ($LASTEXITCODE -ne 0) { throw "Native peer patch application failed: $($item.Entry.patch)" }
        if ((Get-TextDigest (Read-NormalizedText $item.Source)) -ne $item.Entry.afterSha256) { throw "Native peer patch output digest mismatch: $($item.Entry.source)" }
        Write-Output "Applied and verified: $($item.Entry.patch)"
    }
} finally {
    [System.Environment]::SetEnvironmentVariable('GIT_CEILING_DIRECTORIES', $previousCeiling, 'Process')
}
