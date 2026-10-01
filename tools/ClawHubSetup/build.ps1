<#
.SYNOPSIS
  ClawHubSetup.exe 빌드 — 설치할 파일 전부(payload.zip)를 리소스로 넣은 단일 파일 설치/업데이트/제거 프로그램.
.DESCRIPTION
  1. ClawService, ClawApp 를 dotnet publish (프레임워크 종속, .NET 10)
  2. Xbox 모드 홈 신원 패키지(packaging\home)를 makeappx로 만들고 CurrentUser\My의 "CN=ClawHub Dev" 인증서로 서명
     (Windows SDK 또는 인증서가 없으면 경고 후 건너뜀 — 설치 프로그램은 그 단계를 건너뛴다)
  3. Game Bar 위젯: -WidgetPackages 폴더(기본 src\ClawWidget\AppPackages)에서 가장 최근 ClawWidget_*_x64.msix
     + 같은 폴더의 Dependencies\x64\*.appx + 서명 공개 인증서(*.cer). 위젯 빌드는 src\ClawWidget\build.ps1 (VS Build Tools 필요)
  4. 공개 인증서, ClawHub.ico, version.txt 와 함께 artifacts\setup\payload.zip 으로 묶고
  5. ClawHubSetup 을 프레임워크 종속 단일 파일로 publish → artifacts\setup\ClawHubSetup.exe
  이 스크립트는 아무것도 설치하지 않는다 (인증서 저장소도 읽기만; -CreateCert 를 줄 때만 인증서를 만든다).
.EXAMPLE
  tools\ClawHubSetup\build.ps1 -Version 1.0.1
#>
param(
    [string]$Version = '1.0.0',
    [string]$Configuration = 'Release',
    [string]$WidgetPackages,
    [switch]$NoWidget,
    [switch]$NoHome,
    [switch]$CreateCert
)
$ErrorActionPreference = 'Stop'
$root = Split-Path (Split-Path $PSScriptRoot -Parent) -Parent
if (-not $WidgetPackages) { $WidgetPackages = Join-Path $root 'src\ClawWidget\AppPackages' }
$out = Join-Path $root 'artifacts\setup'
$stage = Join-Path $out 'stage'
$subject = 'CN=ClawHub Dev'

Remove-Item $stage, (Join-Path $out 'home-work'), (Join-Path $out 'publish') -Recurse -Force -ErrorAction SilentlyContinue
New-Item -ItemType Directory -Force $stage, "$stage\certs" | Out-Null

# 1. 서비스 / 앱
Write-Host '[1/5] ClawService, ClawApp publish'
dotnet publish "$root\src\ClawService" -c $Configuration -o "$stage\service" --nologo -v q "-p:Version=$Version"
if ($LASTEXITCODE) { throw 'ClawService publish 실패' }
dotnet publish "$root\src\ClawApp" -c $Configuration -o "$stage\app" --nologo -v q "-p:Version=$Version"
if ($LASTEXITCODE) { throw 'ClawApp publish 실패' }
Get-ChildItem $stage -Recurse -Filter *.pdb | Remove-Item
# 외부 위치(sparse) 신원 패키지는 앱 폴더에서 *.sccd 와 Public 폴더를 찾는다
Copy-Item "$root\packaging\home\CustomCapability.SCCD" "$stage\app"
New-Item -ItemType Directory -Force "$stage\app\Public" | Out-Null
Copy-Item "$root\src\ClawApp\Assets\ClawHub.ico" $stage
[IO.File]::WriteAllText("$stage\version.txt", $Version)

# 2. 서명 인증서 (홈 패키지 서명용, 공개 인증서는 설치 프로그램이 TrustedPeople에 넣는다)
$cert = Get-ChildItem Cert:\CurrentUser\My | Where-Object { $_.Subject -eq $subject -and $_.HasPrivateKey -and $_.NotAfter -gt (Get-Date) } |
    Sort-Object NotAfter -Descending | Select-Object -First 1
if (-not $cert -and $CreateCert) {
    $cert = New-SelfSignedCertificate -Type Custom -Subject $subject -KeyUsage DigitalSignature -FriendlyName 'ClawHub Dev' `
        -CertStoreLocation Cert:\CurrentUser\My -TextExtension @('2.5.29.37={text}1.3.6.1.5.5.7.3.3', '2.5.29.19={text}') -NotAfter (Get-Date).AddYears(5)
    Write-Host "새 인증서 $($cert.Thumbprint)"
}

# 3. Xbox 모드 홈 신원 패키지
Write-Host '[2/5] 홈 신원 패키지'
if ($NoHome) {
    Write-Host '  -NoHome: 건너뜀'
} else {
    $sdk = Get-ChildItem 'C:\Program Files (x86)\Windows Kits\10\bin\10.*\x64' -Directory -ErrorAction SilentlyContinue |
        Where-Object { (Test-Path "$($_.FullName)\makeappx.exe") -and (Test-Path "$($_.FullName)\signtool.exe") } |
        Sort-Object { [version]($_.Parent.Name) } -Descending | Select-Object -First 1
    if (-not $sdk) { Write-Warning 'Windows SDK(makeappx/signtool)가 없어 홈 패키지를 건너뜁니다.' }
    elseif (-not $cert) { Write-Warning "CurrentUser\My에 '$subject' 인증서가 없어 홈 패키지를 건너뜁니다 (-CreateCert 로 만들 수 있음)." }
    else {
        $work = Join-Path $out 'home-work'
        New-Item -ItemType Directory -Force "$work\Assets", "$stage\home" | Out-Null
        Copy-Item "$root\packaging\home\AppxManifest.xml", "$root\packaging\home\CustomCapability.SCCD" $work
        Copy-Item "$root\src\ClawApp\Assets\*.png" "$work\Assets"
        $msix = "$stage\home\ClawHub.Home.msix"
        & "$($sdk.FullName)\makeappx.exe" pack /o /d $work /p $msix /nv | Out-Null
        if ($LASTEXITCODE) { throw 'makeappx 실패' }
        & "$($sdk.FullName)\signtool.exe" sign /fd SHA256 /sha1 $cert.Thumbprint $msix | Out-Null
        if ($LASTEXITCODE) { throw 'signtool 실패' }
        Export-Certificate -Cert $cert -FilePath "$stage\certs\ClawHubDev.cer" | Out-Null
        Remove-Item $work -Recurse -Force
        Write-Host "  $msix (서명 $($cert.Thumbprint))"
    }
}

# 4. Game Bar 위젯
Write-Host '[3/5] Game Bar 위젯'
if ($NoWidget) {
    Write-Host '  -NoWidget: 건너뜀'
} else {
    $widget = Get-ChildItem $WidgetPackages -Recurse -Filter 'ClawWidget_*_x64.msix' -ErrorAction SilentlyContinue |
        Sort-Object LastWriteTime -Descending | Select-Object -First 1
    if (-not $widget) {
        Write-Warning "위젯 MSIX가 없어 건너뜁니다: $WidgetPackages (src\ClawWidget\build.ps1 로 먼저 빌드)"
    } else {
        New-Item -ItemType Directory -Force "$stage\widget\deps" | Out-Null
        Copy-Item $widget.FullName "$stage\widget"
        Get-ChildItem (Join-Path $widget.DirectoryName 'Dependencies\x64') -Filter *.appx -ErrorAction SilentlyContinue |
            Copy-Item -Destination "$stage\widget\deps"
        Get-ChildItem $widget.DirectoryName -Filter *.cer | Copy-Item -Destination "$stage\certs"
        Write-Host "  $($widget.FullName) ($($widget.LastWriteTime))"
        Get-ChildItem "$stage\widget\deps" | ForEach-Object { Write-Host "  의존: $($_.Name)" }
    }
}

# 5. payload.zip + 단일 파일 publish
Write-Host '[4/5] payload.zip'
Add-Type -AssemblyName System.IO.Compression.FileSystem
$zip = Join-Path $out 'payload.zip'
Remove-Item $zip -ErrorAction SilentlyContinue
[IO.Compression.ZipFile]::CreateFromDirectory($stage, $zip, [IO.Compression.CompressionLevel]::Optimal, $false)
Write-Host ("  {0:N1} MB" -f ((Get-Item $zip).Length / 1MB))

Write-Host '[5/5] ClawHubSetup publish (프레임워크 종속 단일 파일)'
dotnet publish "$PSScriptRoot\ClawHubSetup.csproj" -c $Configuration -r win-x64 --self-contained false `
    -p:PublishSingleFile=true "-p:ClawHubPayload=$zip" "-p:Version=$Version" -o "$out\publish" --nologo -v q
if ($LASTEXITCODE) { throw 'ClawHubSetup publish 실패' }
Copy-Item "$out\publish\ClawHubSetup.exe" "$out\ClawHubSetup.exe" -Force
$exe = Get-Item "$out\ClawHubSetup.exe"
Write-Host ("완료: {0} ({1:N1} MB)" -f $exe.FullName, ($exe.Length / 1MB))
