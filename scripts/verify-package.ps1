param([Parameter(Mandatory=$true)][string]$Archive, [string]$ExtractDirectory = '')
$ErrorActionPreference = 'Stop'
Add-Type -AssemblyName System.IO.Compression
Add-Type -AssemblyName System.IO.Compression.FileSystem
$zip = [IO.Compression.ZipFile]::OpenRead([IO.Path]::GetFullPath($Archive))
try {
    $entries = @{}
    foreach ($entry in $zip.Entries) {
        $name = $entry.FullName
        if ($name -notmatch '^LumeRemote/[^\\:]+$' -or $name -match '(^|/)\.{1,2}(/|$)' -or $name -match '(^|/)(host\.dat|computers\.dat|setup\.log|connections\.log|status\.txt|\.git)(/|$)' -or $entry.Length -gt 536870912) { throw "Unsafe archive entry: $name" }
        if ($entries.ContainsKey($name)) { throw "Duplicate entry: $name" }
        $entries[$name] = $entry
    }
    if (-not $entries.ContainsKey('LumeRemote/SHA256SUMS.txt')) { throw 'The archive has no SHA256SUMS.txt manifest.' }
    # The user package (it carries the application) must not ship internal maintainer handoff notes.
    if ($entries.ContainsKey('LumeRemote/LumeRemote.exe') -and $entries.ContainsKey('LumeRemote/AGENTS.md')) { throw 'The user package must not contain AGENTS.md; it belongs in the source archive only.' }
    $reader = New-Object IO.StreamReader($entries['LumeRemote/SHA256SUMS.txt'].Open())
    try { $lines = $reader.ReadToEnd() -split '\r?\n' } finally { $reader.Dispose() }
    $verified = @{}
    foreach ($line in $lines) {
        if (-not $line) { continue }
        if ($line -notmatch '^([a-f0-9]{64})  (.+)$') { throw 'Invalid manifest record.' }
        $expected = $Matches[1]; $name = 'LumeRemote/' + $Matches[2]
        if ($verified.ContainsKey($name) -or -not $entries.ContainsKey($name)) { throw "Missing or duplicate manifest entry: $name" }
        $content = $entries[$name].Open(); $sha = [Security.Cryptography.SHA256]::Create()
        try { $actual = [BitConverter]::ToString($sha.ComputeHash($content)).Replace('-','').ToLowerInvariant() } finally { $content.Dispose(); $sha.Dispose() }
        if ($actual -ne $expected) { throw "Hash mismatch: $name" }
        $verified[$name] = $true
    }
    if ($verified.Count -ne $entries.Count - 1) { throw 'The archive contains unmanifested files.' }
    Write-Host "Verified $($verified.Count) files and manifest. No private settings or connection logs."
} finally { $zip.Dispose() }
if ($ExtractDirectory) {
    $target = [IO.Path]::GetFullPath($ExtractDirectory)
    if (Test-Path -LiteralPath $target) { throw 'Extraction target exists; choose a new folder.' }
    [IO.Compression.ZipFile]::ExtractToDirectory([IO.Path]::GetFullPath($Archive), $target)
    Write-Host "Fresh extraction: $target"
}
