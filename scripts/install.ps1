#Requires -RunAsAdministrator
<#
  Builds ClawHub and installs it to C:\Program Files\ClawHub.
  - ClawService : Windows service (LocalSystem, auto start) - EC/TDP/fan/battery, controller HID, MSI buttons
  - ClawApp     : launcher, registered to start at logon for the current user
  Re-run to update. Remove with uninstall.ps1.
#>
param(
    [string]$InstallDir = "$env:ProgramFiles\ClawHub",
    [switch]$NoAutoStart
)
$ErrorActionPreference = 'Stop'
$root = Split-Path $PSScriptRoot -Parent

$existing = Get-Service ClawHub -ErrorAction SilentlyContinue
if ($existing -and $existing.Status -ne 'Stopped') {
    Write-Host 'Stopping existing ClawHub service (controller mapping is restored on stop)...'
    Stop-Service ClawHub
}
Get-Process ClawApp -ErrorAction SilentlyContinue | Stop-Process -Force

Write-Host 'Publishing...'
dotnet publish "$root\src\ClawService" -c Release -o "$InstallDir\service" --nologo -v q
if ($LASTEXITCODE) { throw 'ClawService publish failed' }
dotnet publish "$root\src\ClawApp" -c Release -o "$InstallDir\app" --nologo -v q
if ($LASTEXITCODE) { throw 'ClawApp publish failed' }

# Service data (controller baseline, EC state): SYSTEM/Administrators full, Users read-only
$data = "$env:ProgramData\ClawHub"
New-Item -ItemType Directory -Force $data | Out-Null
icacls $data /inheritance:r /grant:r '*S-1-5-18:(OI)(CI)F' '*S-1-5-32-544:(OI)(CI)F' '*S-1-5-32-545:(OI)(CI)RX' | Out-Null

$svcExe = "$InstallDir\service\ClawService.exe"
if (-not $existing) {
    Write-Host 'Registering service...'
    New-Service -Name ClawHub -BinaryPathName "`"$svcExe`"" -DisplayName 'ClawHub' `
        -Description 'MSI Claw hardware control for ClawHub (TDP, fan, battery limit, controller mapping)' -StartupType Automatic | Out-Null
    # restart on crash: 5s, 5s, 30s
    sc.exe failure ClawHub reset= 86400 actions= restart/5000/restart/5000/restart/30000 | Out-Null
}
Start-Service ClawHub
Write-Host "Service: $((Get-Service ClawHub).Status)"

if (-not $NoAutoStart) {
    Set-ItemProperty 'HKCU:\Software\Microsoft\Windows\CurrentVersion\Run' -Name ClawHub -Value "`"$InstallDir\app\ClawApp.exe`""
    Write-Host 'ClawApp will start at logon.'
}

Write-Host ''
Write-Host "Installed to $InstallDir"
Write-Host "Start now:  & `"$InstallDir\app\ClawApp.exe`""
Write-Host 'Optional:   scripts\msi-center.ps1 -Disable   (stop MSI Center M so its overlay does not fight ClawHub)'
