param([Parameter(Mandatory=$true)][ValidatePattern('^S-1-5-21-\d+-\d+-\d+-\d+$')][string]$OwnerSid, [switch]$Remove, [switch]$Update)
$ErrorActionPreference = 'Stop'
$sourceRoot = [IO.Path]::GetFullPath((Split-Path -Parent $PSScriptRoot))
$installRoot = Join-Path $env:ProgramFiles 'LumeRemote'
$dataRoot = Join-Path $env:ProgramData 'LumeRemote'
$hostRoot = Join-Path $dataRoot 'Host'
$serviceName = 'LumeRemoteHost'
$systemSid = [Security.Principal.SecurityIdentifier]'S-1-5-18'
$adminSid = [Security.Principal.SecurityIdentifier]'S-1-5-32-544'
$owner = [Security.Principal.SecurityIdentifier]$OwnerSid
function Assert-RealPath([string]$Path) {
    $item = $Path
    while ($item) {
        if ((Test-Path -LiteralPath $item) -and ((Get-Item -LiteralPath $item -Force).Attributes -band [IO.FileAttributes]::ReparsePoint)) { throw "Refusing reparse point: $item" }
        $item = Split-Path -Parent $item
    }
}
function Test-TrustedOwner([string]$Path) {
    $sid = (Get-Acl -LiteralPath $Path).GetOwner([Security.Principal.SecurityIdentifier])
    return ($sid -eq $systemSid) -or ($sid -eq $adminSid)
}
# Writes the result for the dashboard. Best effort: it never follows a link planted as
# setup.log, and a logging problem (e.g. a cloud-synced folder) never undoes the setup.
function Write-SetupLog([string]$Text) {
    try {
        $log = Join-Path $sourceRoot 'setup.log'
        if ((Test-Path -LiteralPath $log) -and ((Get-Item -LiteralPath $log -Force).Attributes -band [IO.FileAttributes]::ReparsePoint)) { return }
        $Text | Set-Content -LiteralPath $log
    } catch { }
}
function Protect-Directory([string]$Path, [bool]$OwnerWrite) {
    Assert-RealPath $Path
    [IO.Directory]::CreateDirectory($Path) | Out-Null
    $acl = New-Object Security.AccessControl.DirectorySecurity
    $acl.SetAccessRuleProtection($true, $false)
    foreach ($identity in @($systemSid, $adminSid)) { $acl.AddAccessRule((New-Object Security.AccessControl.FileSystemAccessRule($identity, 'FullControl', 'ContainerInherit,ObjectInherit', 'None', 'Allow'))) }
    $rights = if ($OwnerWrite) { 'Modify' } else { 'ReadAndExecute' }
    $acl.AddAccessRule((New-Object Security.AccessControl.FileSystemAccessRule($owner, $rights, 'ContainerInherit,ObjectInherit', 'None', 'Allow')))
    $acl.SetOwner($adminSid)
    Set-Acl -LiteralPath $Path -AclObject $acl
}
function Protect-File([string]$Path, [bool]$OwnerWrite, [bool]$NoOwnerAccess = $false) {
    Assert-RealPath $Path
    $acl = New-Object Security.AccessControl.FileSecurity
    $acl.SetAccessRuleProtection($true, $false)
    foreach ($identity in @($systemSid, $adminSid)) { $acl.AddAccessRule((New-Object Security.AccessControl.FileSystemAccessRule($identity, 'FullControl', 'Allow'))) }
    $rights = if ($OwnerWrite) { 'Modify' } else { 'ReadAndExecute' }
    if (-not $NoOwnerAccess) { $acl.AddAccessRule((New-Object Security.AccessControl.FileSystemAccessRule($owner, $rights, 'Allow'))) }
    $acl.SetOwner($adminSid)
    Set-Acl -LiteralPath $Path -AclObject $acl
}
$createdService = $false
$wasRunning = $false
$backup = $null
$changedFiles = @()
try {
    if (-not ([Security.Principal.WindowsPrincipal][Security.Principal.WindowsIdentity]::GetCurrent()).IsInRole([Security.Principal.WindowsBuiltInRole]::Administrator)) { throw 'Use Enable permanent access in Lume, then approve the Windows administrator prompt.' }
    Assert-RealPath $installRoot
    Assert-RealPath $hostRoot
    $service = Get-CimInstance Win32_Service -Filter "Name='$serviceName'"
    $serviceCommand = '"' + (Join-Path $installRoot 'LumeRemote.exe') + '" --service'
    if ($service -and $service.PathName -ne $serviceCommand) { throw 'A different service already uses the LumeRemoteHost name. It was not changed.' }
    if ($Remove -and $Update) { throw 'Choose removal or update, not both.' }
    if ($Update -and (-not $service -or -not (Test-Path -LiteralPath (Join-Path $hostRoot 'host.dat')))) { throw 'No installed host was found. Use Enable permanent access first.' }
    if ($Update -and $sourceRoot.TrimEnd('\') -eq $installRoot.TrimEnd('\')) { throw 'Extract the new release into a separate folder, open it and choose Update installed host.' }
    $runKey = "Registry::HKEY_USERS\$OwnerSid\Software\Microsoft\Windows\CurrentVersion\Run"
    if ($Remove) {
        if (Test-Path -LiteralPath (Join-Path $installRoot 'LumeRemote.exe')) {
            $disabler = Start-Process -FilePath (Join-Path $installRoot 'LumeRemote.exe') -ArgumentList @('--disable-host') -WindowStyle Hidden -Wait -PassThru
            if ($disabler.ExitCode -ne 0) { throw 'Could not disable the protected host before removal.' }
        }
        if ($service) { Stop-Service -Name $serviceName -Force; & "$env:WINDIR\System32\sc.exe" delete $serviceName | Out-Null; if ($LASTEXITCODE -ne 0) { throw 'Could not remove the host service.' } }
        if (Test-Path -LiteralPath $runKey) { Remove-ItemProperty -LiteralPath $runKey -Name LumeRemote -ErrorAction SilentlyContinue }
        Write-SetupLog 'Permanent-access service removed. Protected files and saved pairings were preserved.'
        exit 0
    }
    $files = @('LumeRemote.exe','LumeRemote.exe.config','LumeCapture.dll','LumeVideo.dll','datachannel.dll','START-HERE.txt','README.md','LICENSE.txt','THIRD-PARTY-NOTICES.txt','scripts\permanent-access.ps1','SHA256SUMS.txt')
    $files += @('everest-NOTICE.txt','libdatachannel-MPL-2.0.txt','libjuice-MPL-2.0.txt','mbedtls-LICENSE.txt','p256-m-NOTICE.txt','plog-MIT.txt','usrsctp-BSD.txt') | ForEach-Object { 'third-party\' + $_ }
    $hashes = @{}
    foreach ($line in [IO.File]::ReadAllLines((Join-Path $sourceRoot 'SHA256SUMS.txt'))) { if ($line -match '^([a-fA-F0-9]{64})\s+(.+)$') { $hashes[$Matches[2].Replace('/','\')] = $Matches[1] } }
    foreach ($relative in $files) {
        $path = Join-Path $sourceRoot $relative
        if (-not (Test-Path -LiteralPath $path -PathType Leaf)) { throw "Package file missing: $relative" }
        if ($relative -ne 'SHA256SUMS.txt' -and (-not $hashes.ContainsKey($relative) -or (Get-FileHash -LiteralPath $path -Algorithm SHA256).Hash -ne $hashes[$relative])) { throw "Package verification failed: $relative" }
    }
    if ($Update) {
        # Close only dashboards belonging to this exact installation, before any
        # service interruption. Never kill a process or touch another app copy.
        $dashboards = @(Get-CimInstance Win32_Process -Filter "Name='LumeRemote.exe'" | Where-Object {
            $_.ExecutablePath -eq (Join-Path $installRoot 'LumeRemote.exe') -and $_.CommandLine -notmatch '\s--(service|host-agent)(\s|$)'
        })
        foreach ($dashboard in $dashboards) {
            $process = Get-Process -Id $dashboard.ProcessId -ErrorAction SilentlyContinue
            if ($process -and -not $process.HasExited) {
                if (-not $process.CloseMainWindow() -or -not $process.WaitForExit(5000)) { throw 'Close the installed Lume dashboard from its notification-area icon, then retry. The host service has not been stopped.' }
            }
        }
    }
    # Standard users can create folders under ProgramData. On a first install, host state that
    # SYSTEM or Administrators did not create is never trusted: it is moved aside unread.
    if (-not $service -and (Test-Path -LiteralPath $dataRoot)) {
        $untrusted = -not (Test-TrustedOwner $dataRoot)
        foreach ($item in @($hostRoot, (Join-Path $hostRoot 'host.dat'))) { if ((Test-Path -LiteralPath $item) -and -not (Test-TrustedOwner $item)) { $untrusted = $true } }
        if ($untrusted) { Move-Item -LiteralPath $dataRoot -Destination ($dataRoot + '.untrusted-' + [Guid]::NewGuid().ToString('N')) }
    }
    Protect-Directory $installRoot $false
    Protect-Directory (Join-Path $installRoot 'scripts') $false
    Protect-Directory $dataRoot $false
    # The Host directory holds only SYSTEM-written state (host.dat, status.txt,
    # connections.log*, host.lock). The owner gets read-only access so it cannot
    # plant a mount point/symlink to redirect SYSTEM writes. Owner setting changes
    # go through the SYSTEM worker's authenticated control pipe, not this folder.
    Protect-Directory $hostRoot $false
    foreach ($entry in Get-ChildItem -LiteralPath $hostRoot -File -Force) {
        # Lock files get no owner access, so a process running as the owner cannot hold them.
        if ($entry.Extension -eq '.lock') { Protect-File $entry.FullName $false $true } else { Protect-File $entry.FullName $false }
    }
    if ($service) { $wasRunning = $service.State -eq 'Running'; Stop-Service -Name $serviceName -Force }
    if ($sourceRoot -ne $installRoot) {
        $backup = Join-Path $installRoot ('backup-' + [Guid]::NewGuid().ToString('N'))
        Protect-Directory $backup $false
        foreach ($relative in $files) {
            $destination = Join-Path $installRoot $relative
            Assert-RealPath $destination
            [IO.Directory]::CreateDirectory((Split-Path -Parent $destination)) | Out-Null
            if (Test-Path -LiteralPath $destination) { $old = Join-Path $backup $relative; [IO.Directory]::CreateDirectory((Split-Path -Parent $old)) | Out-Null; Copy-Item -LiteralPath $destination -Destination $old }
            Copy-Item -LiteralPath (Join-Path $sourceRoot $relative) -Destination $destination -Force
            Protect-File $destination $false
            $changedFiles += $relative
            if ($relative -ne 'SHA256SUMS.txt' -and (Get-FileHash -LiteralPath $destination -Algorithm SHA256).Hash -ne $hashes[$relative]) { throw "Installed file verification failed: $relative" }
        }
    }
    foreach ($relative in $files) { Protect-File (Join-Path $installRoot $relative) $false }
    $initializeAction = if ($Update) { '--update-host' } else { '--initialize-host' }
    $initializer = Start-Process -FilePath (Join-Path $installRoot 'LumeRemote.exe') -ArgumentList @($initializeAction, $OwnerSid) -WindowStyle Hidden -Wait -PassThru
    if ($initializer.ExitCode -ne 0) { throw 'Could not initialize the protected host settings. Another user may own this installation.' }
    if (-not $service) { New-Service -Name $serviceName -DisplayName 'Lume Remote - paired computer access' -BinaryPathName $serviceCommand -StartupType Automatic -Description 'Owner-enabled remote desktop for explicitly paired computers. Disable or revoke access in Lume Remote.' | Out-Null; $createdService = $true }
    Start-Service -Name $serviceName
    if (-not (Test-Path -LiteralPath $runKey)) { New-Item -Path $runKey -Force | Out-Null }
    New-ItemProperty -LiteralPath $runKey -Name LumeRemote -Value ('"' + (Join-Path $installRoot 'LumeRemote.exe') + '" --tray') -PropertyType String -Force | Out-Null
    Write-SetupLog 'Installed. Automatic Windows service enabled; dashboard starts in the notification area. No firewall, router or Windows password setting was changed.'
    exit 0
} catch {
    $reason = $_.Exception.Message
    try {
        if ($createdService) { Stop-Service -Name $serviceName -Force -ErrorAction SilentlyContinue; & "$env:WINDIR\System32\sc.exe" delete $serviceName | Out-Null }
        if ($backup) { foreach ($relative in $changedFiles) { $old = Join-Path $backup $relative; if (Test-Path -LiteralPath $old) { Copy-Item -LiteralPath $old -Destination (Join-Path $installRoot $relative) -Force } } }
        if ($wasRunning) { Start-Service -Name $serviceName }
    } catch { $reason += " Rollback also needs attention: $($_.Exception.Message)" }
    try { Write-SetupLog $reason } catch { }
    exit 1
}
