#Requires -RunAsAdministrator
<#
  Turns MSI Center M off/on without uninstalling it.
  It is NOT uninstalled on purpose: the MSI_ACPI WMI class that ClawHub uses for EC access is registered by MSI's software.

  -Disable : disables its 2 scheduled tasks, sets "MSI Foundation Service" to Manual, stops its processes
  -Enable  : reverses all of the above
#>
param([switch]$Disable, [switch]$Enable)
if ($Disable -eq $Enable) { throw 'Use exactly one of -Disable or -Enable' }

$tasks = 'MSI_Center_M_Server', 'MSI_Center_M_Updater'
$service = 'MSI Foundation Service'

if ($Disable) {
    foreach ($t in $tasks) { Disable-ScheduledTask -TaskName $t -ErrorAction SilentlyContinue | Out-Null }
    Get-Process | Where-Object { $_.ProcessName -like 'MSI_Center_M*' -or $_.ProcessName -in 'MSI Center M', 'Command Center' } |
        Stop-Process -Force -ErrorAction SilentlyContinue
    Set-Service $service -StartupType Manual
    Stop-Service $service -Force -ErrorAction SilentlyContinue
    Write-Host 'MSI Center M disabled. Undo: scripts\msi-center.ps1 -Enable'
}
else {
    foreach ($t in $tasks) { Enable-ScheduledTask -TaskName $t -ErrorAction SilentlyContinue | Out-Null }
    Set-Service $service -StartupType Automatic
    Start-Service $service
    Start-ScheduledTask -TaskName 'MSI_Center_M_Server' -ErrorAction SilentlyContinue
    Write-Host 'MSI Center M re-enabled.'
}
