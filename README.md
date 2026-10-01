# ClawHub

MSI Claw 8 EX AI+ (1T91)용 MSI Center M 대체 앱. SteamOS 게임 모드처럼 패드만으로 쓰는 일체형 런처 + 하드웨어 제어.

## 기능

- **런처**: 홈(최근 게임 히어로), 라이브러리(Steam·Epic·GOG·국내외 게임사 설치 게임 자동 감지 + 직접 추가), 게임 상세
- **키 매핑 (컨트롤러 펌웨어 방식)**: 버튼 16개(ABXY, D-pad, LB/RB/LT/RT, L3/R3, M1/M2) → 키보드/마우스/미디어 키, 버튼당 최대 5키 조합
  - 컨트롤러가 직접 키보드 신호를 내보내므로 SendInput 같은 소프트웨어 주입이 아님
  - 게임이 실행되면 자동 적용, 게임 종료·메뉴 열기 시 자동 복원. 플래시에는 쓰지 않음(재부팅하면 항상 원상태)
  - 템플릿: 방향키형, WASD형, 마우스 클릭형, 메이플스토리
  - 라이브러리 밖 앱(Xbox 앱 게임, 에뮬레이터 등)도 퀵 설정에서 "이 앱에 프로필 지정" 가능
- **퀵 설정** (오른쪽 버튼): 성능 모드(절전/균형/성능/수동 TDP 8–45W), 쿨러 부스트, 밝기, 볼륨, 충전 한도(100/80/60%), 실행 중 앱 매핑
- **메인 메뉴** (왼쪽 버튼): 홈·라이브러리·매핑·Xbox 앱·설정·데스크톱·전원
- **설정 허브** (런처 안에서 전부, 패드로): 일반(시작 화면·자동 실행·MSI Center M 켜기/끄기) · 컨트롤러(진동·스틱/트리거 데드존·ABXY 배치·대각선 잠금) · 조명(기본/단색/끄기·밝기·색·ABXY 밝기) · 성능 · 디스플레이(밝기·볼륨·주사율·해상도) · 배터리 · Windows 설정 바로가기 · 라이브러리 · 정보
- **게임별 설정**: 매핑 프로필, 성능 모드(게임 중에만 적용), 이름 변경 (터치 키보드, 한글 가능)
- **Xbox 모드 호환**: Windows Xbox 전체 화면 환경에서는 숨은 채 동반 동작(버튼·자동 매핑만)

## 구조

| 프로젝트 | 역할 |
|---|---|
| `src/ClawCore` | 하드웨어(EC WMI, 컨트롤러 HID), 매핑 프로필, 게임 라이브러리, IPC 메시지 |
| `src/ClawService` | LocalSystem 서비스. 하드웨어 단독 소유, Named Pipe `ClawHub.Service` |
| `src/ClawApp` | WPF 전체 화면 셸 (비관리자 실행 → 게임이 관리자 권한을 상속하지 않음) |
| `tools/ClawProbe` | 읽기 전용 하드웨어 점검 |
| `tools/ClawHubSetup` | 설치/업데이트/제거 프로그램 (`ClawHubSetup.exe`, 페이로드 내장 단일 파일) |

설계: [docs/design.md](docs/design.md) · 프로토콜: [docs/protocol-controller.md](docs/protocol-controller.md), [docs/protocol-ec.md](docs/protocol-ec.md)

## 설치 — ClawHubSetup.exe

설치·업데이트·제거를 파일 하나로. 빌드:

```powershell
tools\ClawHubSetup\build.ps1 -Version 1.0.0     # → artifacts\setup\ClawHubSetup.exe
```

- 실행하면 관리자 권한을 요청하고 설치 화면이 뜬다. 이미 설치돼 있으면 업데이트(같은 버전이면 복구).
  순서: ClawApp 종료 → 서비스 정지(컨트롤러 기준 매핑 복원, 안 멈추면 중단) → `C:\Program Files\ClawHub` 교체 →
  `C:\ProgramData\ClawHub` 권한 잠금 → 서비스 등록/갱신(자동 시작, 실패 시 재시작) → 서비스 시작 → 선택 항목.
- 선택 항목: 로그인 시 자동 실행 · Game Bar 위젯 · Xbox 모드 홈 앱(개발자 모드 필요 — 꺼져 있으면 건너뛰고 설정 열기 단추 표시) ·
  Game Bar OEM 위젯을 ClawHub로 · MSI Center M 끄기(제거는 안 함 — EC WMI 클래스가 MSI 소속).
- 제거: 설정 > 앱 > 설치된 앱 > ClawHub > 제거 (= `ClawHubSetup.exe /uninstall`). 설치 프로그램이 바꾼 것
  (`HKLM\SOFTWARE\ClawHub\Setup`에 기록: OEM 위젯, MSI Center M, 추가한 인증서, Run 키)만 되돌린다.
  사용자 데이터(`%LocalAppData%\ClawHub`, `C:\ProgramData\ClawHub`)는 "사용자 데이터도 삭제"를 체크할 때만 지운다.
- 드라이런(아무것도 바꾸지 않고 지금 상태에서 할 일 출력, `%TEMP%\ClawHubSetup-dryrun.log`에도 UTF-8로 기록):

  ```powershell
  $env:__COMPAT_LAYER = 'RunAsInvoker'   # requireAdministrator 매니페스트를 무시하고 일반 권한으로 실행
  Start-Process artifacts\setup\ClawHubSetup.exe /dryrun -Wait -NoNewWindow -RedirectStandardOutput dryrun.txt
  # 제거 계획: -ArgumentList '/dryrun','/uninstall'
  ```

  GUI 앱이라 `& .\ClawHubSetup.exe /dryrun`은 PowerShell이 끝나기를 기다리지 않는다 — `Start-Process -Wait`를 쓸 것.
- 설치 로그: `%TEMP%\ClawHubSetup-install-*.log` / `-uninstall-*.log`
- 런타임: 프레임워크 종속 단일 파일(.NET 10 Desktop Runtime 필요 — ClawApp·ClawService도 같은 런타임을 쓰므로 자체 포함보다 70MB가량 작다).
- 빌드 요구: .NET 10 SDK. 홈 신원 패키지는 Windows SDK(makeappx/signtool) + `CurrentUser\My`의 `CN=ClawHub Dev` 인증서,
  위젯은 먼저 `src\ClawWidget\build.ps1`로 빌드(`-WidgetPackages`로 다른 폴더 지정 가능). 없으면 그 단계 없이 만들어진다.
- 위젯·홈 신원 패키지는 사용자별 MSIX라 관리자 토큰의 사용자에게 설치된다. 표준 사용자가 다른 관리자 계정 암호로 올리면
  Run 키는 로그인 사용자(HKEY_USERS\SID)에 쓰지만 패키지 단계는 건너뛴다 (화면에 안내).
- 같은 버전의 위젯은 다시 설치하지 않는다 — 위젯을 바꾸면 `Package.appxmanifest` 버전을 올릴 것.

### 레거시 스크립트 (scripts\)

`install.ps1`, `uninstall.ps1`, `register-home.ps1`, `msi-center.ps1`은 ClawHubSetup 이전 방식이다. 참고용으로 남겨 두지만
새로 설치할 때는 쓰지 않는다. 스크립트로 설치한 PC에서 ClawHubSetup을 실행하면 그대로 업데이트되고 앱 및 기능에 등록된다
(스크립트로 바꾼 OEM 위젯은 백업 위치가 같아 제거 때 복원되지만, 스크립트가 신뢰시킨 인증서는 기록이 없어 남는다).

```powershell
scripts\install.ps1            # (레거시) 빌드 + 서비스 등록 + 로그인 시 자동 실행
scripts\msi-center.ps1 -Disable   # (레거시) MSI Center M 끄기
```

실행 옵션: `ClawApp.exe --shell` (전체 화면 강제) / `--companion` (Xbox 동반 강제)

## 조작

| 패드 | 키보드 | 동작 |
|---|---|---|
| 왼쪽 MSI 버튼 / Menu | F1 | 메인 메뉴 |
| 오른쪽 퀵 버튼 / View | F2 | 퀵 설정 |
| A / B | Enter / Esc | 선택 / 뒤로 |
| X / Y | – | 화면별 보조 동작 (힌트 바 참고) |
| LB / RB | PgUp / PgDn | 탭 전환 |

## 안전 원칙

- 컨트롤러: DFU(펌웨어 업데이트)·리셋·플래시 기록 명령은 코드에서 전송 자체를 차단. 프로필 쓰기 후 다시 읽어 검증.
- EC: 검증된 인덱스(PL1/PL2/Shift/쿨러부스트/팬모드/충전한도)만 쓰기 허용, TDP는 8–45W로 제한, WMI v2 외 버전이면 쓰기 비활성화.
- 서비스가 비정상 종료돼도 다음 시작 시 원래 매핑으로 복원.

## 한계

- 스틱은 펌웨어에서 키 입력으로 바꿀 수 없음 → 이동은 D-pad 사용
- 온라인 게임의 보안 프로그램 정책은 보장할 수 없음 (하드웨어 키보드 신호라 소프트웨어 주입보다는 안전한 방식)
