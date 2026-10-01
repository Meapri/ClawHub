#Requires -RunAsAdministrator
<#
  Registers ClawHub as an Xbox mode (full screen experience) home app candidate and adds it to
  the Xbox app "My Apps" list. Requires Developer Mode (unsigned SCCD for the gamingHome capability).

  1. Builds an external-location (sparse) identity package from packaging/home
  2. Signs it with the "CN=ClawHub Dev" certificate from CurrentUser\My (created if missing)
  3. Trusts the certificate (LocalMachine\TrustedPeople) and registers the package for the current user
  4. Adds ClawHub to the empty OEM slot of the Xbox app "My Apps" (HKLM GamingConfiguration\OEMApps)

  -SetOemWidget (separate step, after the ClawHub.Widget package is installed):
     Makes the ClawHub quick settings widget the OEM widget Game Bar features on handhelds
     (HKLM GamingConfiguration\OEMGameBarWidget). The previous value (MSI Quick Settings) is backed up
     to HKLM\SOFTWARE\ClawHub\Backup\OEMGameBarWidget. Game Bar picks it up after it restarts (sign out/in).

  Undo: scripts/register-home.ps1 -Remove   (also restores the OEM widget backup)
#>
param(
    [string]$AppDir = "$env:ProgramFiles\ClawHub\app",
    [switch]$Remove,
    [switch]$SetOemWidget
)
$ErrorActionPreference = 'Stop'
$root = Split-Path $PSScriptRoot -Parent
$sdk = 'C:\Program Files (x86)\Windows Kits\10\bin\10.0.26100.0\x64'
$oemKey = 'HKLM:\SOFTWARE\Microsoft\Windows\CurrentVersion\GamingConfiguration\OEMApps'
$gamingKey = 'HKLM:\SOFTWARE\Microsoft\Windows\CurrentVersion\GamingConfiguration'
$backupKey = 'HKLM:\SOFTWARE\ClawHub\Backup'
$widgetFamily = 'ClawHub.Widget_tj9ced57bhrww'
# Marker stored in the backup when there was no OEMGameBarWidget value at all (restore = delete the value)
$noValue = '<none>'

function Get-RegValue($key, $name) {
    $item = Get-ItemProperty $key -Name $name -ErrorAction SilentlyContinue
    if ($item) { return $item.$name }
    return $null
}

function Set-OemWidget {
    if (-not (Get-AppxPackage -Name ($widgetFamily -split '_')[0] | Where-Object PackageFamilyName -eq $widgetFamily)) {
        throw "Widget package $widgetFamily is not installed for this user. Install the ClawHub widget first."
    }
    $current = Get-RegValue $gamingKey 'OEMGameBarWidget'
    if ($current -eq $widgetFamily) {
        Write-Host "OEMGameBarWidget is already $widgetFamily."
        return
    }
    # Back up only once: never overwrite the original (e.g. MSI Quick Settings) with a later value
    if (-not (Test-Path $backupKey)) { New-Item -Path $backupKey -Force | Out-Null }
    if ($null -eq (Get-RegValue $backupKey 'OEMGameBarWidget')) {
        $saved = if ($null -eq $current) { $noValue } else { $current }
        New-ItemProperty $backupKey -Name OEMGameBarWidget -Value $saved -PropertyType String -Force | Out-Null
        Write-Host "Backed up OEMGameBarWidget: $saved"
    }
    if (-not (Test-Path $gamingKey)) { New-Item -Path $gamingKey -Force | Out-Null }
    New-ItemProperty $gamingKey -Name OEMGameBarWidget -Value $widgetFamily -PropertyType String -Force | Out-Null
    Write-Host "OEMGameBarWidget = $widgetFamily (restart Game Bar or sign out/in to apply)"
}

function Restore-OemWidget {
    $saved = Get-RegValue $backupKey 'OEMGameBarWidget'
    if ($null -eq $saved) { return }
    # Restore only if the value is still ours (someone else may have changed it since)
    if ((Get-RegValue $gamingKey 'OEMGameBarWidget') -eq $widgetFamily) {
        if ($saved -eq $noValue) { Remove-ItemProperty $gamingKey -Name OEMGameBarWidget -ErrorAction SilentlyContinue }
        else { Set-ItemProperty $gamingKey -Name OEMGameBarWidget -Value $saved }
        Write-Host "OEMGameBarWidget restored: $saved"
    }
    Remove-ItemProperty $backupKey -Name OEMGameBarWidget -ErrorAction SilentlyContinue
}

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
    Restore-OemWidget
    Write-Host 'ClawHub home app registration removed.'
    return
}

if ($SetOemWidget) {
    Set-OemWidget
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
