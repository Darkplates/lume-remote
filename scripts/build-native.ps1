$ErrorActionPreference = 'Stop'
$projectRoot = Split-Path -Parent $PSScriptRoot
$vswhere = Join-Path ${env:ProgramFiles(x86)} 'Microsoft Visual Studio\Installer\vswhere.exe'
if (-not (Test-Path -LiteralPath $vswhere)) { throw 'Building the optional DXGI bridge requires Visual Studio C++ Build Tools and the Windows SDK.' }
$installation = & $vswhere -latest -products '*' -requires Microsoft.VisualStudio.Component.VC.Tools.x86.x64 -property installationPath
if (-not $installation) { throw 'The Visual Studio C++ compiler is missing. The prebuilt DXGI DLL can still be used.' }
$msvcRoot = Get-ChildItem -LiteralPath (Join-Path $installation 'VC\Tools\MSVC') -Directory | Sort-Object Name -Descending | Select-Object -First 1
$sdkRoot = Join-Path ${env:ProgramFiles(x86)} 'Windows Kits\10'
$sdkVersion = Get-ChildItem -LiteralPath (Join-Path $sdkRoot 'Include') -Directory | Sort-Object Name -Descending | Select-Object -First 1
$compiler = Join-Path $msvcRoot.FullName 'bin\Hostx64\x64\cl.exe'
$nativeWork = Join-Path $projectRoot 'build\native'
New-Item -ItemType Directory -Path $nativeWork -Force | Out-Null
$buildArgs = @('/nologo', '/std:c++17', '/O2', '/EHsc', '/MT', '/LD', '/W4', '/guard:cf', '/DUNICODE', '/D_UNICODE', "/I$($msvcRoot.FullName)\include", "/I$($sdkVersion.FullName)\ucrt", "/I$($sdkVersion.FullName)\shared", "/I$($sdkVersion.FullName)\um", "/I$($sdkVersion.FullName)\winrt", "/Fo$nativeWork\capture.obj", "/Fe$projectRoot\LumeCapture.dll", "$projectRoot\native\capture.cpp", '/link', '/DYNAMICBASE', '/NXCOMPAT', '/HIGHENTROPYVA', "/IMPLIB:$nativeWork\LumeCapture.lib", "/LIBPATH:$($msvcRoot.FullName)\lib\x64", "/LIBPATH:$sdkRoot\Lib\$($sdkVersion.Name)\ucrt\x64", "/LIBPATH:$sdkRoot\Lib\$($sdkVersion.Name)\um\x64", 'd3d11.lib', 'dxgi.lib', 'user32.lib')
& $compiler @buildArgs
if ($LASTEXITCODE -ne 0) { throw "Native compiler failed with exit code $LASTEXITCODE" }
Get-Item -LiteralPath (Join-Path $projectRoot 'LumeCapture.dll') | Select-Object Name, Length
$videoArgs = @('/nologo', '/std:c++17', '/O2', '/EHsc', '/MT', '/LD', '/W4', '/guard:cf', '/DUNICODE', '/D_UNICODE', "/I$($msvcRoot.FullName)\include", "/I$($sdkVersion.FullName)\ucrt", "/I$($sdkVersion.FullName)\shared", "/I$($sdkVersion.FullName)\um", "/I$($sdkVersion.FullName)\winrt", "/Fo$nativeWork\video.obj", "/Fe$projectRoot\LumeVideo.dll", "$projectRoot\native\video.cpp", '/link', '/DYNAMICBASE', '/NXCOMPAT', '/HIGHENTROPYVA', "/IMPLIB:$nativeWork\LumeVideo.lib", "/LIBPATH:$($msvcRoot.FullName)\lib\x64", "/LIBPATH:$sdkRoot\Lib\$($sdkVersion.Name)\ucrt\x64", "/LIBPATH:$sdkRoot\Lib\$($sdkVersion.Name)\um\x64", 'mfreadwrite.lib', 'mfplat.lib', 'mf.lib', 'mfuuid.lib', 'wmcodecdspuuid.lib', 'ole32.lib', 'oleaut32.lib')
& $compiler @videoArgs
if ($LASTEXITCODE -ne 0) { throw "Video compiler failed with exit code $LASTEXITCODE" }
Get-Item -LiteralPath (Join-Path $projectRoot 'LumeVideo.dll') | Select-Object Name, Length
