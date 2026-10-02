# Xbox Game Bar 위젯 (ClawWidget)

게임 중 `Win+G`(또는 Claw 오른쪽 하드웨어 버튼 → `ms-gamebar:activate/…ClawHubQuick`)로 여는 Game Bar 안의 ClawHub 퀵 설정.
ClawApp 자체 퀵 설정 오버레이를 대체한다. `src/ClawWidget` — 클래식 UWP(C#, .NET Native) + `Microsoft.Gaming.XboxGameBar` 7.3 + WinUI 2.8.

| 구역 | 내용 | 서비스 요청 |
|---|---|---|
| 머리 | 실행 중 게임 이름(없으면 "게임 실행 중 아님"), 연결 점, 매핑 프로필 칩(누르면 목록: 다음 프로필로 · 매핑 없음 · 프로필들), "ClawHub 열기"(`clawhub:`를 Game Bar 경유로 실행 → Game Bar가 닫힘, 실패 시 `AppCommand ShowClawHub`), "게임 종료"(확인 플라이아웃) | `AppCommand` `CycleProfile` / `SetProfile:<id>` / `QuitGame` |
| 캡처 | 스크린샷 · 녹화(시작/중지) · 30초 저장 한 줄. 런처 앱이 숨은 뒤 Win+Alt+PrtSc / Win+Alt+R / Win+Alt+G를 대신 누른다. 런처가 꺼져 있으면 비활성 | `AppCommand` `Screenshot` / `ToggleRecording` / `SaveClip` |
| 실시간 | CPU °C, CPU W, 배터리 %(충전 표시), 배터리 W, 남은 시간 + 최근 2분 그래프(CPU 온도 선·면, 배터리 W 점선). 숫자는 부드럽게 바뀜 | `GetTelemetry` (GpuTemp는 표시 안 함) |
| 성능 | 절전/균형/성능/수동 타일, 수동이면 PL1/PL2 슬라이더(400 ms 디바운스, PL1 ≤ PL2 자동 유지), 쿨러 부스트. 머리에 지금 전원(플러그/배터리 아이콘)과 실제 적용 PL. "배터리용 따로 설정" 스위치(끄면 배터리 칸 삭제), 켜면 "충전 중 / 배터리" 선택(기본 = 지금 전원)으로 타일이 바꿀 칸을 고름. 게임 전용 설정·게임별 임시 성능 안내 | `SetPerformance` (+ `PowerSource` `Ac`/`Battery`, 배터리 칸 삭제는 `Performance:null`) |
| 자동 TDP | Intel Endurance Gaming 끔/켬/자동(배터리일 때만) + 강도(성능 우선/균형/배터리 최대). `Graphics.Endurance.Available=false`면 숨김 | `SetEnduranceGaming` |
| 그래픽 | FPS 제한 끔/30/40/45/60/90/120, 프레임 생성 끔/2배/3배/4배. `Graphics.Available=false`면 숨김 | `SetGraphics` |
| 화면 · 배터리 | 밝기 슬라이더(디바운스), 충전 한도 100/80/60 %, 배터리 상태 줄("배터리 상태 102% · 79.1/77.8 Wh · 사이클 N", `BatteryHealth`가 null이면 숨김) | `SetBrightness`, `SetChargeLimit` |
| 조명 | 기본/단색/숨쉬기/무지개/웨이브/끄기, 색 7개(단색·숨쉬기), 속도(숨쉬기·무지개·웨이브), 밝기 | `SetControllerSettings` (받은 객체 전체를 복사해 Light·LightBrightness·LightColor·LightSpeed만 바꿔 보냄) |

패드: Game Bar가 위젯에 포커스를 주면 D-pad/스틱으로 XY 포커스 이동, A로 누르기, B로 플라이아웃 닫기.
슬라이더는 Xbox 방식(A로 잡고 좌우 조절, B로 놓기). 처음 열리면 현재 성능 모드 타일에 포커스. LB/RB는 Game Bar가 쓴다.

### Game Bar SDK 연동

- **고정(핀) + Game Bar 닫힘**(`Pinned` && `GameBarDisplayMode == PinnedOnly`): 창을 300×40으로 줄이고 미니 HUD
  (CPU °C · W | 배터리 % · W)만 표시. Game Bar를 다시 열면 원래 크기·전체 화면으로 돌아온다.
  HUD에서는 `GetTelemetry`만 폴링한다.
- **컴팩트 모드**(`CompactModeEnabled`, 핸드헬드): 여백을 줄이고 버튼 높이를 36 → 44(타일 56 → 64)로 키운다.
- `RequestedTheme`(라이트/다크), `RequestedOpacity`(배경에만 적용), `Visible`/`WindowState`(안 보이거나 최소화면 폴링 중지).
- 움직임: 처음 열 때 카드가 차례로 올라오고(EntranceThemeTransition), 수동 슬라이더·색·알림 등은 나타나고 사라질 때
  페이드 + 살짝 미끄러짐(Composition implicit show/hide).

## 동작 방식

- **IPC**: `\\.\pipe\ClawHub.Service`, 한 줄에 JSON 하나 (UTF-8). AppContainer에서는 `NamedPipeClientStream`이 안 되므로
  `CreateFile2`(실패 시 `CreateFileFromApp`)로 열고 `ReadFile`/`WriteFile` P/Invoke로 읽고 쓴다. JSON은 `Windows.Data.Json`.
  요청은 한 번에 하나씩(요청 줄 쓰기 → 같은 Id 응답 읽기).
- **열림·닫힘 알림**: Game Bar가 열려 위젯이 보이면(`Visible`, 최소화 아님, `GameBarDisplayMode = Foreground` — 고정 HUD만 남은 것은 닫힘)
  `AppCommand WidgetShown`, 닫히면(일시 중단 포함) `WidgetHidden`을 보낸다. Game Bar는 떠 있어도 앞 창으로 잡히지 않을 때가 많아,
  ClawApp은 이 신호로 "Game Bar에 넘겨준 동안"(게임 매핑 해제 → 패드로 위젯 조작)을 시작·끝내고, 오른쪽 버튼을 다시 누르면 닫는다.
  앞 창 판별만 쓰던 때는 1.5초 뒤 Win+G 재시도가 방금 연 위젯을 닫거나, 5초 뒤 매핑이 되살아났다. ClawApp이 꺼져 있으면 서비스가 거절하고 위젯은 무시한다.
- **폴링만, 구독 안 함**: 2초마다 `GetTelemetry`, 전체 화면에선 4초마다 `GetStatus`. 연결이 없으면 매 주기 재접속.
  `Subscribe`는 쓰지 않는다 — 서비스는 마지막 구독자가 끊길 때 게임 매핑·게임별 성능을 복원하는데,
  위젯이 구독자로 남아 있으면 ClawApp이 꺼져도 복원이 안 된다.
- 명령 응답의 `Status`로 바로 화면을 갱신한다. 선택 표시는 응답이 와야 바뀐다(누르는 순간 바꾸지 않음).
  사용자가 움직인 슬라이더는 2.5초 동안 폴링 값으로 덮어쓰지 않는다.
- **오류 표시**: 서비스 연결 안 됨 → 머리 아래 빨간 카드("서비스 연결 안 됨" + 원인 + "지금 다시 연결"), 조작 카드는 숨김.
  `Ok=false` → 아래쪽에 3.5초 알림("성능 모드를 바꾸지 못했어요 · <서비스 Error>"), 응답의 Status로 컨트롤을 실제 값으로 되돌림.
- 게임별 임시 성능(`TemporaryPerformance`)이 걸려 있으면 그 값을 보여 주고, 위젯에서 바꾸면 `SetPerformance`라
  사용자 기본값이 바뀌고 임시 성능은 해제된다 (화면에 안내 문구).
- ClawHub 앱이 꺼져 있으면(`AppState=null`) 프로필·게임 종료는 비활성화, "ClawHub 열기"는 항상 `clawhub:` 프로토콜(Game Bar 경유)로 연다.
- **서비스 쪽 요구사항** (ClawService `PipeServer`가 담당): 파이프 DACL에 `ALL APPLICATION PACKAGES` ReadWrite,
  SACL에 Low 무결성 레이블(`S:(ML;;NW;;;LW)`), 그리고 패키지 패밀리 이름이 `ClawHub.Widget_`로 시작하는
  AppContainer 클라이언트만 허용. 그래서 매니페스트의 `Identity Name="ClawHub.Widget"`은 바꾸면 안 된다.

## 코드 구조 · 새 섹션 추가

```
src/ClawWidget/
  App.xaml(.cs)          Game Bar 활성화(ms-gamebarwidget) → XboxGameBarWidget 생성, 테마 색·공용 스타일
  WidgetPage.xaml(.cs)   껍데기: 연결·폴링, Game Bar 상태(테마·투명도·컴팩트·핀·표시), 알림, 미니 HUD, 섹션 목록
  Sections/
    WidgetSection.cs     섹션 기반 클래스 + ISectionHost (페이지가 제공: Status, RunAsync, ShowToast, ThemeBrush, LaunchUriAsync)
    HeaderSection        게임 · 연결 · 매핑 프로필 · ClawHub 열기 · 게임 종료
    CaptureSection       스크린샷 · 녹화 · 30초 저장
    TelemetrySection     실시간 숫자 + 그래프
    PerformanceSection   성능 모드 · 수동 PL · 쿨러 부스트 · 전원별(충전 중/배터리) 성능
    EnduranceSection     자동 TDP (Intel Endurance Gaming)
    GraphicsSection      FPS 제한 · 프레임 생성
    DisplaySection       밝기 · 충전 한도 · 배터리 상태
    LightingSection      컨트롤러 조명
  Ipc/Models.cs          서비스 JSON 모델 (Windows.Data.Json) · 요청 생성기
  Ipc/PipeClient.cs      파이프 클라이언트 (CreateFile2, 한 번에 요청 하나)
  UI/                    ChoiceGroup(세그먼트 버튼), Debouncer, AnimatedNumber, Sparkline, Motion, Icons(Lucide)
```

새 기능(예: 자동 TDP, 배터리 수명, 실제 FPS, 화면 품질, 캡처) 추가 순서:

1. `Ipc/Models.cs` — `ServiceStatus`/`Telemetry`에 필드와 `FromJson` 파싱, `Requests`에 요청 생성기.
2. `Sections/<이름>Section.xaml(.cs)` — 루트를 `<local:WidgetSection>`으로, 카드는 `Border Style="{StaticResource CardStyle}"` +
   `<ui:SectionHeader Icon="&#x…;" Title="…" />`. `OnStatus` / `OnTelemetry`로 화면을 맞추고, 명령은 `Run(요청, "실패 문구")`.
   이벤트 처리기에서는 `if (IgnoreInput) return;` (상태 반영 중의 값 변경 무시). 데이터가 없으면 `IsAvailable`에서 false → 카드 숨김.
   세그먼트 선택은 `UI/ChoiceGroup`, 슬라이더는 `UI/Debouncer`(400 ms) 재사용.
3. `WidgetPage.CreateSections()`에 `Add(new <이름>Section());` 한 줄, `ClawWidget.csproj`에 `Page`/`Compile` 항목 추가.

아이콘은 Lucide 글꼴(`WidgetIconFont`, 단색)이다. 코드포인트는 lucide-static의 `font/codepoints.json`, 자주 쓰는 것은 `UI/Icons.cs`.

## 패키지 정보

| | |
|---|---|
| Identity | `ClawHub.Widget`, Publisher `CN=ClawHub Dev`, 1.2.0.0, x64 |
| 패키지 패밀리 이름 (PFN) | `ClawHub.Widget_tj9ced57bhrww` |
| 위젯(AppExtension) Id | `ClawHubQuick` |
| 바로 열기 URI | `ms-gamebar:activate/ClawHub.Widget_tj9ced57bhrww_App_ClawHubQuick` |

PFN 해시는 Publisher 문자열에서 정해진다 — Publisher를 바꾸면 PFN과 위 URI도 바뀐다.

## 빌드

필요: Visual Studio Build Tools 18 + UWP 워크로드(`Microsoft.VisualStudio.Workload.UniversalBuildTools`, .NET Native 포함)
+ Windows SDK 10.0.26100. VS IDE는 필요 없다. 이 프로젝트는 `ClawHub.slnx`에 들어 있지 않다 (`dotnet build` 불가).

서명 인증서는 `Cert:\CurrentUser\My`의 `CN=ClawHub Dev` 코드 서명 인증서를 쓴다. 처음이라면:

```powershell
src\ClawWidget\build.ps1 -CreateCert   # 인증서가 없으면 만들고 공개 인증서를 ClawHub.Widget.cer로 내보냄
```

이후에는 `src\ClawWidget\build.ps1`. 내부적으로 실행하는 명령:

```powershell
& "C:\Program Files (x86)\Microsoft Visual Studio\18\BuildTools\MSBuild\Current\Bin\amd64\MSBuild.exe" `
    src\ClawWidget\ClawWidget.csproj /restore /m /nologo /v:minimal `
    /p:Configuration=Release /p:Platform=x64 `
    /p:AppxBundle=Never /p:UapAppxPackageBuildMode=SideloadOnly `
    /p:AppxPackageSigningEnabled=true /p:PackageCertificateThumbprint=<CN=ClawHub Dev 인증서 지문>
```

결과물:

```
src\ClawWidget\AppPackages\ClawWidget_1.2.0.0_x64_Test\
  ClawWidget_1.2.0.0_x64.msix
  Dependencies\x64\Microsoft.NET.Native.Framework.2.2.appx
  Dependencies\x64\Microsoft.NET.Native.Runtime.2.2.appx
  Dependencies\x64\Microsoft.UI.Xaml.2.8.appx
  Dependencies\x64\Microsoft.VCLibs.x64.14.00.appx
  Dependencies\x64\Microsoft.VCLibs.x64.14.00.Desktop.appx
```

`.pfx`는 저장소에 절대 넣지 않는다 (`.gitignore`에 포함). 저장소에는 공개 인증서 `ClawHub.Widget.cer`만 있다.
다른 PC에서 새 인증서로 빌드하면 `ClawHub.Widget.cer`도 새로 내보낸 것으로 바뀐다.

## 설치 (관리자 PowerShell, 저장소 루트에서)

1. 개발자 모드가 켜져 있거나 사이드로드가 허용돼 있어야 한다.
2. 서명 인증서를 신뢰 (한 번만):

   ```powershell
   Import-Certificate -FilePath src\ClawWidget\ClawHub.Widget.cer -CertStoreLocation Cert:\LocalMachine\TrustedPeople
   ```

3. 패키지와 의존 프레임워크 설치:

   ```powershell
   $out = 'src\ClawWidget\AppPackages\ClawWidget_1.2.0.0_x64_Test'
   Add-AppxPackage -Path "$out\ClawWidget_1.2.0.0_x64.msix" `
       -DependencyPath (Get-ChildItem "$out\Dependencies\x64\*.appx").FullName
   ```

   이미 설치된 의존 패키지가 더 새 버전이면 그대로 두고 넘어간다.
   다시 설치할 때 같은 버전이면 `-ForceUpdateFromAnyVersion`을 붙이거나 먼저 제거한다.

4. ClawService가 실행 중인지 확인 (위젯 파이프 허용이 들어간 버전이어야 함).

제거: `Get-AppxPackage ClawHub.Widget | Remove-AppxPackage`

## Game Bar에서 열기

- `Win+G` → 위쪽 홈 바의 **위젯 메뉴** → **ClawHub**. 설치 직후 즐겨찾기로 등록되도록 해 두었으므로
  (`FavoriteAfterInstall`) 홈 바에도 아이콘이 보인다. 핀 아이콘으로 게임 위에 고정할 수 있다.
- 컴팩트 모드(핸드헬드)에서는 `CompactModePriorityPlacement`로 앞쪽에 배치된다.
- 바로 열기: `Start-Process "ms-gamebar:activate/ClawHub.Widget_tj9ced57bhrww_App_ClawHubQuick"`
- 위젯이 목록에 안 보이면 Game Bar를 완전히 닫았다가(작업 관리자에서 `GameBar.exe` 종료) 다시 연다.

## 문제 해결

| 증상 | 확인 |
|---|---|
| "서비스 연결 안 됨 · 서비스가 실행 중이 아니에요" | `Get-Service ClawHub` — 서비스가 꺼져 있음 (파이프 없음, 오류 2) |
| "… 서비스가 위젯 접속을 허용하지 않아요 (오류 5)" | 파이프 DACL에 ALL APPLICATION PACKAGES / Low IL 레이블이 없는 이전 서비스 버전, 또는 PFN이 `ClawHub.Widget_`로 시작하지 않음 |
| "EC 쓰기가 막혀 있어 읽기만 할 수 있어요" | 서비스가 EC 쓰기를 막은 상태 (`EcWritable=false`) — 성능·충전 한도 버튼은 비활성화된다 |
| 충전 한도 "이 기기에서 지원 안 됨" | EC가 충전 한도를 지원하지 않음 (`ChargeLimit=null`) |
| 그래픽 카드가 안 보임 | `Status.Graphics` 없음 또는 `Available=false` (Intel 그래픽 드라이버 기능 없음) |
| 프로필 칩·게임 종료가 비활성 | ClawHub 앱이 꺼져 있음 (`AppState=null`) |
| "ClawHub를 열지 못했어요 · …" | `clawhub:` 실행이 실패했고 대체 경로(`AppCommand ShowClawHub`)도 실패 — 런처 신원 패키지(`ClawHub_tj9ced57bhrww`) 설치 확인 |
| "… 바꾸지 못했어요 · <오류>" | 서비스가 `Ok=false`로 거절 — 뒤의 문구는 서비스 `Error` 그대로 |
| 위젯이 바로 꺼짐 | 매니페스트 proxyStub IID 확인 (아래) |

### proxyStub 등록에 대해

Game Bar 비공개 인터페이스의 COM 마샬링을 위해 패키지 수준 `windows.activatableClass.proxyStub` 항목이 필요하다
(NuGet `readme.txt`에서 복사). 7.3.x readme에는 `IXboxGameBarWidgetNotificationHost`의 IID가
`IXboxGameBarWidgetHost10`과 같은 값(`3284F0E0-…`)으로 잘못 적혀 있다 (XboxGameBarSamples issue #155 계열).
매니페스트에는 패키지의 `Microsoft.Gaming.XboxGameBar.Private.winmd`에서 `winmdidl`로 직접 뽑은 IID를 넣었다
(`IXboxGameBarWidgetNotificationHost` = `6F68D392-E4A9-46F7-A024-5275BC2FE7BA`). NuGet 버전을 올리면 다시 대조할 것:

```powershell
$p = "$env:USERPROFILE\.nuget\packages\microsoft.gaming.xboxgamebar\<버전>"
winmdidl /nosystemdeclares /metadata_dir:"C:\Program Files (x86)\Windows Kits\10\UnionMetadata\10.0.26100.0" `
    /metadata_dir:"$p\lib\uap10.0" /outdir:<짧은 경로> "$p\private\Microsoft.Gaming.XboxGameBar.Private.winmd"
```

## 타사 고지

- **Lucide** 아이콘 글꼴 v1.49.0 (`src/ClawWidget/Assets/Fonts/lucide.ttf`) — ISC License, Copyright (c) Lucide Icons and Contributors.
  Feather에서 유래한 일부 아이콘은 MIT License, Copyright (c) 2013-present Cole Bemis.
  전문: `src/ClawWidget/Assets/Fonts/Lucide-LICENSE.txt` (패키지에도 포함). 출처: https://lucide.dev
- 앱 아이콘(로고·스플래시·`GameBar/Icons`)은 `src/ClawApp/Assets/ClawHub-256.png`(Lucide `gamepad-2` 기반)를 크기만 바꾼 것.
- NuGet: `Microsoft.Gaming.XboxGameBar`, `Microsoft.UI.Xaml`, `Microsoft.NETCore.UniversalWindowsPlatform` — 각 패키지의 Microsoft 라이선스.

## 캡처의 한계

위젯 SDK에는 Game Bar 오버레이 자체를 닫는 API가 없다 (`XboxGameBarWidget.MinimizeAsync`는 위젯 창만 접고, URI 실행으로 닫으면 다른 앱이 앞으로 온다).
그래서 캡처 버튼은 Game Bar가 열린 채로 명령을 보낸다. Game Bar 캡처 단축키는 마지막 게임 창을 대상으로 하므로 보통 문제없지만,
오버레이가 찍히거나 단축키가 무시되면 Game Bar를 닫은 뒤 하드웨어 버튼/단축키로 찍어야 한다 (실기 확인 필요).
