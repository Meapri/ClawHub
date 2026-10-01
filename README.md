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

설계: [docs/design.md](docs/design.md) · 프로토콜: [docs/protocol-controller.md](docs/protocol-controller.md), [docs/protocol-ec.md](docs/protocol-ec.md)

## 설치 (관리자 PowerShell)

```powershell
scripts\install.ps1            # 빌드 + 서비스 등록 + 로그인 시 자동 실행
scripts\msi-center.ps1 -Disable   # (선택) MSI Center M 끄기 — 제거는 하지 말 것(EC WMI 클래스가 MSI 소속)
```

되돌리기: `scripts\uninstall.ps1`, `scripts\msi-center.ps1 -Enable`

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
