param([string]$OutputDirectory = '')
$ErrorActionPreference = 'Stop'
$repo = Split-Path -Parent $PSScriptRoot
$output = if ($OutputDirectory) { [IO.Path]::GetFullPath($OutputDirectory) } else { Join-Path $repo 'verification\two-pc-runtime\autonomous-fixture' }
if (@(Get-CimInstance Win32_Process | Where-Object ExecutablePath -eq (Join-Path $output 'TwoPcScenario.exe')).Count) { throw 'Close the owned fixture before rebuilding it.' }
New-Item -ItemType Directory -Path $output -Force | Out-Null
$compiler = Join-Path $env:WINDIR 'Microsoft.NET\Framework64\v4.0.30319\csc.exe'
$frameworkDir = Split-Path -Parent $compiler
$metadataRoot = Join-Path ${env:ProgramFiles(x86)} 'Windows Kits\10\UnionMetadata'
$metadata = Get-ChildItem -LiteralPath $metadataRoot -Directory | Where-Object Name -match '^\d+\.' | Sort-Object { [version]$_.Name } -Descending | ForEach-Object { Join-Path $_.FullName 'Windows.winmd' } | Where-Object { Test-Path -LiteralPath $_ } | Select-Object -First 1
$runtime = Join-Path $env:WINDIR 'Microsoft.NET\assembly\GAC_MSIL\System.Runtime\v4.0_4.0.0.0__b03f5f7f11d50a3a\System.Runtime.dll'
$sources = @(Get-ChildItem -LiteralPath (Join-Path $repo 'src') -Filter '*.cs' | Where-Object Name -ne 'Program.cs' | ForEach-Object FullName)
$arguments = @('/nologo','/optimize+','/unsafe','/langversion:5','/target:exe','/platform:x64',"/out:$output\TwoPcScenario.exe",'/main:TwoPcScenario','/r:System.dll','/r:System.Core.dll','/r:System.Drawing.dll','/r:System.Windows.Forms.dll','/r:System.Security.dll','/r:System.Web.Extensions.dll','/r:System.ServiceProcess.dll',"/r:$metadata","/r:$runtime","/r:$frameworkDir\System.Runtime.WindowsRuntime.dll","/resource:$repo\assets\brand.png,LumeRemote.Brand.png") + $sources + (Join-Path $repo 'tests\two-pc\TwoPcScenario.cs')
& $compiler @arguments
if ($LASTEXITCODE -ne 0) { throw 'Fixture compilation failed.' }
Copy-Item -LiteralPath (Join-Path $repo 'LumeRemote.exe.config') -Destination (Join-Path $output 'TwoPcScenario.exe.config')
foreach ($name in @('datachannel.dll','LumeCapture.dll','LumeVideo.dll')) { Copy-Item -LiteralPath (Join-Path $repo $name) -Destination $output }
Get-Item -LiteralPath (Join-Path $output 'TwoPcScenario.exe') | Select-Object Name,Length
