<#
.SYNOPSIS
  ClawHub Game Bar 위젯 MSIX 빌드 (Release x64, 사이드로드).
.DESCRIPTION
  필요: Visual Studio Build Tools 18 + UWP 워크로드(Microsoft.VisualStudio.Workload.UniversalBuildTools) + Windows SDK 10.0.26100.
  서명 인증서: CurrentUser\My 에 Subject "CN=ClawHub Dev"인 코드 서명 인증서 (Package.appxmanifest Publisher와 같아야 함).
  -CreateCert 를 주면 없을 때 새로 만들고 공개 인증서를 ClawHub.Widget.cer로 내보낸다 (pfx는 만들지 않음).
  결과: AppPackages\ClawWidget_<버전>_x64_Test\ClawWidget_<버전>_x64.msix + Dependencies\x64\*.appx
#>
param(
    [string]$Configuration = 'Release',
    [switch]$CreateCert
)
$ErrorActionPreference = 'Stop'
$here = $PSScriptRoot
$subject = 'CN=ClawHub Dev'

$cert = Get-ChildItem Cert:\CurrentUser\My | Where-Object { $_.Subject -eq $subject -and $_.HasPrivateKey -and $_.NotAfter -gt (Get-Date) } |
    Sort-Object NotAfter -Descending | Select-Object -First 1
if (-not $cert) {
    if (-not $CreateCert) { throw "CurrentUser\My에 '$subject' 인증서가 없습니다. -CreateCert 로 새로 만드세요." }
    $cert = New-SelfSignedCertificate -Type Custom -Subject $subject -KeyUsage DigitalSignature `
        -FriendlyName 'ClawHub Widget Dev Signing' -CertStoreLocation 'Cert:\CurrentUser\My' `
        -TextExtension @('2.5.29.37={text}1.3.6.1.5.5.7.3.3', '2.5.29.19={text}') -NotAfter (Get-Date).AddYears(5)
    Export-Certificate -Cert $cert -FilePath (Join-Path $here 'ClawHub.Widget.cer') | Out-Null
    Write-Host "새 인증서 $($cert.Thumbprint) → ClawHub.Widget.cer"
}

$vs = 'C:\Program Files (x86)\Microsoft Visual Studio\18\BuildTools'
$vswhere = 'C:\Program Files (x86)\Microsoft Visual Studio\Installer\vswhere.exe'
if (Test-Path $vswhere) {
    $found = & $vswhere -latest -products * -requires Microsoft.VisualStudio.Workload.UniversalBuildTools -property installationPath
    if ($found) { $vs = $found }
}
$msbuild = Join-Path $vs 'MSBuild\Current\Bin\amd64\MSBuild.exe'
if (-not (Test-Path $msbuild)) { throw "MSBuild를 찾을 수 없습니다: $msbuild" }

& $msbuild (Join-Path $here 'ClawWidget.csproj') /restore /m /nologo /v:minimal `
    /p:Configuration=$Configuration /p:Platform=x64 `
    /p:AppxBundle=Never /p:UapAppxPackageBuildMode=SideloadOnly `
    /p:AppxPackageSigningEnabled=true /p:PackageCertificateThumbprint=$($cert.Thumbprint)
if ($LASTEXITCODE -ne 0) { throw "빌드 실패 ($LASTEXITCODE)" }

Get-ChildItem (Join-Path $here 'AppPackages') -Recurse -Include *.msix, *.appx | Select-Object -ExpandProperty FullName
