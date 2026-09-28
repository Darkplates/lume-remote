param([switch]$Tests, [string]$PeerSourceDirectory = '', [string]$PeerBuildDirectory = '')
$ErrorActionPreference = 'Stop'
& (Join-Path $PSScriptRoot 'build-peer.ps1') -SourceDirectory $PeerSourceDirectory -BuildDirectory $PeerBuildDirectory
& (Join-Path $PSScriptRoot 'build.ps1') -Native -Tests:$Tests
