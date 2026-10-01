#Requires -RunAsAdministrator
<# Removes the ClawHub service, app autostart and install folder. User data (%LocalAppData%\ClawHub) is kept. #>
param([string]$InstallDir = "$env:ProgramFiles\ClawHub")
$ErrorActionPreference = 'Stop'

Get-Process ClawApp -ErrorAction SilentlyContinue | Stop-Process -Force
if (Get-Service ClawHub -ErrorAction SilentlyContinue) {
    Stop-Service ClawHub -ErrorAction SilentlyContinue   # restores the controller baseline mapping
    sc.exe delete ClawHub | Out-Null
}
Remove-ItemProperty 'HKCU:\Software\Microsoft\Windows\CurrentVersion\Run' -Name ClawHub -ErrorAction SilentlyContinue
if (Test-Path $InstallDir) { Remove-Item $InstallDir -Recurse -Force }

Write-Host 'ClawHub removed. If you disabled MSI Center M, re-enable it with: scripts\msi-center.ps1 -Enable'
