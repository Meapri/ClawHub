#Requires -RunAsAdministrator
<# Removes the ClawHub service, app autostart and install folder. User data (%LocalAppData%\ClawHub) is kept. #>
param([string]$InstallDir = "$env:ProgramFiles\ClawHub")
$ErrorActionPreference = 'Stop'

Get-Process ClawApp, ClawHub, ClawOverlay -ErrorAction SilentlyContinue | Stop-Process -Force
if (Get-Service ClawHub -ErrorAction SilentlyContinue) {
    Stop-Service ClawHub -ErrorAction SilentlyContinue   # restores the controller baseline mapping
    sc.exe delete ClawHub | Out-Null
}
Remove-ItemProperty 'HKCU:\Software\Microsoft\Windows\CurrentVersion\Run' -Name ClawHub -ErrorAction SilentlyContinue
if (Test-Path $InstallDir) { Remove-Item $InstallDir -Recurse -Force }

# Machine-wide trust added by install.ps1 for UIAccess signing (the widget keeps its own TrustedPeople entry)
foreach ($storeName in 'Root', 'TrustedPublisher') {
    $store = New-Object Security.Cryptography.X509Certificates.X509Store $storeName, 'LocalMachine'
    $store.Open('ReadWrite')
    $store.Certificates | Where-Object Subject -eq 'CN=ClawHub Dev' | ForEach-Object { $store.Remove($_) }
    $store.Close()
}

Write-Host 'ClawHub removed. If you disabled MSI Center M, re-enable it with: scripts\msi-center.ps1 -Enable'
