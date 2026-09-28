$ErrorActionPreference = 'Continue'
Write-Host 'Lume Remote - wake readiness (read-only)'
Write-Host 'No router, firmware, firewall or power setting is changed.'
Get-CimInstance Win32_ComputerSystem | Select-Object Manufacturer, Model
Get-NetAdapter -Physical | Select-Object Name, InterfaceDescription, Status, MacAddress, LinkSpeed | Format-Table -AutoSize
Write-Host 'Supported sleep states:'
& "$env:WINDIR\System32\powercfg.exe" /a
Write-Host 'Devices currently allowed to wake this PC:'
& "$env:WINDIR\System32\powercfg.exe" /devicequery wake_armed
Write-Host 'Devices reporting wake support:'
& "$env:WINDIR\System32\powercfg.exe" /devicequery wake_from_any
Write-Host 'Across different networks, a wake packet needs a reachable router feature or an always-on helper inside the target network.'
Write-Host 'Use Ethernet, enable Wake on LAN in the supported firmware/driver, and retain standby power. Normal shutdown support depends on the model and firmware.'
