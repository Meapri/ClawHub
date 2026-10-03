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
Get-Process ClawApp, ClawHub, ClawOverlay -ErrorAction SilentlyContinue | Stop-Process -Force

Write-Host 'Publishing...'
dotnet publish "$root\src\ClawService" -c Release -o "$InstallDir\service" --nologo -v q
if ($LASTEXITCODE) { throw 'ClawService publish failed' }
dotnet publish "$root\src\ClawApp" -c Release -o "$InstallDir\app" --nologo -v q
if ($LASTEXITCODE) { throw 'ClawApp publish failed' }

# ClawHub.exe = the real app with UIAccess: needed so input mapping, the radial menu and other overlays work
# while an elevated game (anti-cheat) or a fullscreen game is in front. Windows only grants UIAccess to signed
# programs in Program Files, and never through package activation (Xbox mode home app) or CreateProcess, so
# ClawApp.exe stays a normal launcher that hands off to ClawHub.exe (a copy with the UIAccess manifest embedded
# and signed). Needs the ClawHub Dev code-signing cert (created by srcClawWidgetuild.ps1 -CreateCert; not a
# CA, so it can only vouch for files it signs itself), trusted machine-wide below.
$cert = Get-ChildItem Cert:\CurrentUser\My | Where-Object { $_.Subject -eq 'CN=ClawHub Dev' -and $_.HasPrivateKey -and $_.NotAfter -gt (Get-Date) } |
    Sort-Object NotAfter -Descending | Select-Object -First 1
$kit = Get-ChildItem "${env:ProgramFiles(x86)}\Windows Kits\10\bin\*\x64" -Directory -ErrorAction SilentlyContinue |
    Where-Object { (Test-Path "$($_.FullName)\signtool.exe") -and (Test-Path "$($_.FullName)\mt.exe") } |
    Sort-Object FullName -Descending | Select-Object -First 1 -ExpandProperty FullName
$uiExe = "$InstallDir\app\ClawHub.exe"
Remove-Item "$InstallDir\app\ClawOverlay.exe" -Force -ErrorAction SilentlyContinue # earlier overlay-only helper
if ($cert -and $kit) {
    Copy-Item "$InstallDir\app\ClawApp.exe" $uiExe -Force
    & "$kit\mt.exe" -nologo -manifest "$root\src\ClawApp\app.uiaccess.manifest" "-outputresource:$uiExe;#1"
    if ($LASTEXITCODE) { throw 'Embedding the UIAccess manifest failed' }

    foreach ($storeName in 'Root', 'TrustedPublisher') {
        $store = New-Object Security.Cryptography.X509Certificates.X509Store $storeName, 'LocalMachine'
        $store.Open('ReadWrite')
        if (-not ($store.Certificates | Where-Object Thumbprint -eq $cert.Thumbprint)) {
            $store.Add([Security.Cryptography.X509Certificates.X509Certificate2]::new($cert.RawData))
            Write-Host "Trusted ClawHub Dev certificate in LocalMachine\$storeName"
        }
        $store.Close()
    }
    & "$kit\signtool.exe" sign /sha1 $cert.Thumbprint /s My /fd SHA256 /q $uiExe
    if ($LASTEXITCODE) { throw 'Signing ClawHub.exe failed' }
    Write-Host 'ClawHub.exe signed (UIAccess: works over elevated and fullscreen games)'
}
else {
    Write-Warning 'No ClawHub Dev signing cert or Windows SDK (mt/signtool): ClawApp runs without UIAccess (no input or overlays over elevated or fullscreen games).'
    if (Test-Path $uiExe) { Remove-Item $uiExe -Force }
}

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
