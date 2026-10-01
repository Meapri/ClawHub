#Requires -RunAsAdministrator
<#
  Registers ClawHub as an Xbox mode (full screen experience) home app candidate and adds it to
  the Xbox app "My Apps" list. Requires Developer Mode (unsigned SCCD for the gamingHome capability).

  1. Builds an external-location (sparse) identity package from packaging/home
  2. Signs it with the "CN=ClawHub Dev" certificate from CurrentUser\My (created if missing)
  3. Trusts the certificate (LocalMachine\TrustedPeople) and registers the package for the current user
  4. Adds ClawHub to the empty OEM slot of the Xbox app "My Apps" (HKLM GamingConfiguration\OEMApps)

  Undo: scripts/register-home.ps1 -Remove
#>
param(
    [string]$AppDir = "$env:ProgramFiles\ClawHub\app",
    [switch]$Remove
)
$ErrorActionPreference = 'Stop'
$root = Split-Path $PSScriptRoot -Parent
$sdk = 'C:\Program Files (x86)\Windows Kits\10\bin\10.0.26100.0\x64'
$oemKey = 'HKLM:\SOFTWARE\Microsoft\Windows\CurrentVersion\GamingConfiguration\OEMApps'

function Find-OemSlot {
    foreach ($i in 1..3) {
        $k = Join-Path $oemKey "OEMAppPathsAndName$i"
        $name = (Get-ItemProperty $k -ErrorAction SilentlyContinue).OemAppName
        if ($name -eq 'ClawHub') { return @{ Key = $k; Mine = $true } }
    }
    foreach ($i in 1..3) {
        $k = Join-Path $oemKey "OEMAppPathsAndName$i"
        if (-not (Get-ItemProperty $k -ErrorAction SilentlyContinue).OemAppName) { return @{ Key = $k; Mine = $false } }
    }
    return $null
}

if ($Remove) {
    Get-AppxPackage -Name ClawHub | Remove-AppxPackage
    $slot = Find-OemSlot
    if ($slot -and $slot.Mine) { Remove-ItemProperty $slot.Key -Name OemAppName, OemAppExePath, OemAppIconPath -ErrorAction SilentlyContinue }
    Write-Host 'ClawHub home app registration removed.'
    return
}

if ((Get-ItemProperty 'HKLM:\SOFTWARE\Microsoft\Windows\CurrentVersion\AppModelUnlock' -ErrorAction SilentlyContinue).AllowDevelopmentWithoutDevLicense -ne 1) {
    throw 'Developer Mode is off. Turn it on: Settings > System > For developers > Developer Mode.'
}
if (-not (Test-Path "$AppDir\ClawApp.exe")) { throw "ClawApp.exe not found in $AppDir (run install.ps1 first)" }

# 1. package layout
$work = Join-Path $env:TEMP 'clawhub-home-pkg'
Remove-Item $work -Recurse -Force -ErrorAction SilentlyContinue
New-Item -ItemType Directory -Force "$work\Assets", "$work\Public" | Out-Null
Copy-Item "$root\packaging\home\AppxManifest.xml", "$root\packaging\home\CustomCapability.SCCD" $work
Copy-Item "$root\src\ClawApp\Assets\*.png" "$work\Assets"
$msix = Join-Path $env:TEMP 'ClawHub.Home.msix'
& "$sdk\makeappx.exe" pack /o /d $work /p $msix /nv | Out-Null
if ($LASTEXITCODE) { throw 'makeappx failed' }

# 2. sign
$cert = Get-ChildItem Cert:\CurrentUser\My | Where-Object Subject -eq 'CN=ClawHub Dev' | Select-Object -First 1
if (-not $cert) {
    $cert = New-SelfSignedCertificate -Type Custom -Subject 'CN=ClawHub Dev' -KeyUsage DigitalSignature -FriendlyName 'ClawHub Dev' `
        -CertStoreLocation Cert:\CurrentUser\My -TextExtension @('2.5.29.37={text}1.3.6.1.5.5.7.3.3', '2.5.29.19={text}') -NotAfter (Get-Date).AddYears(5)
}
& "$sdk\signtool.exe" sign /fd SHA256 /sha1 $cert.Thumbprint $msix | Out-Null
if ($LASTEXITCODE) { throw 'signtool failed' }

# External-location packages resolve content from the app folder: Windows looks for the SCCD
# (custom capability) as $AppDir\*.sccd and for the appExtension PublicFolder there too.
Copy-Item "$root\packaging\home\CustomCapability.SCCD" $AppDir -Force
New-Item -ItemType Directory -Force "$AppDir\Public" | Out-Null

# 3. trust + register (external location = installed app folder)
$cer = Join-Path $env:TEMP 'ClawHubDev.cer'
Export-Certificate -Cert $cert -FilePath $cer | Out-Null
Import-Certificate -FilePath $cer -CertStoreLocation Cert:\LocalMachine\TrustedPeople | Out-Null
Get-AppxPackage -Name ClawHub | Remove-AppxPackage -ErrorAction SilentlyContinue
Add-AppxPackage -Path $msix -ExternalLocation $AppDir
$pkg = Get-AppxPackage -Name ClawHub
Write-Host "Registered: $($pkg.PackageFamilyName)"

# 4. Xbox app "My Apps" OEM slot
$slot = Find-OemSlot
if ($slot) {
    New-Item -Force $slot.Key | Out-Null
    Set-ItemProperty $slot.Key -Name OemAppName -Value 'ClawHub'
    Set-ItemProperty $slot.Key -Name OemAppExePath -Value "$AppDir\ClawApp.exe"
    Set-ItemProperty $slot.Key -Name OemAppIconPath -Value "$AppDir\Assets\Square150x150Logo.png"
    Write-Host "Xbox app My Apps: $($slot.Key)"
} else {
    Write-Host 'No free OEM slot in the Xbox app (skipped).'
}

Write-Host ''
Write-Host 'Next: Settings > Gaming > Xbox mode > Choose home app > ClawHub'
