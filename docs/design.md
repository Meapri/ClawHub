# ClawHub 설계

## 목표

SteamOS 게임 모드처럼 **패드만으로 모든 걸 하는 일체형 셸**. MSI Center M의 14개 프로세스를 서비스 1개 + 앱 1개로 대체.

## 프로세스 구조

```
┌──────────────── 사용자 세션 ────────────────┐      ┌──────── LocalSystem ────────┐
│ ClawApp (WPF, 비관리자)                      │      │ ClawService                  │
│  · 전체화면 셸: 홈 / 라이브러리 / 게임 상세   │ pipe │  · MsiWmi (EC: TDP·팬·배터리) │
│  · 본체 버튼 → Xbox 홈 / Game Bar 위젯        │◄────►│  · ClawController (HID)       │
│  · XInput 내비게이션, 게임 실행/감시          │ JSON │  · MSI_Event 버튼 감시        │
│  · 밝기·볼륨 (사용자 권한으로 충분)           │      │  · 컨트롤러 baseline 백업     │
└──────────────────────────────────────────────┘      └──────────────────────────────┘
        │ ms-gamebar:activate
        ▼
  ClawWidget (Game Bar 위젯, ClawHub.Widget_tj9ced57bhrww) — 게임 위 빠른 설정
```

- 앱이 비관리자로 돌아야 게임/Steam이 관리자 권한을 상속하지 않음.
- 하드웨어 쓰기는 전부 서비스에서, 화이트리스트를 통과한 것만.
- **Windows Xbox 환경을 존중**: ClawHub는 게임 위에 자체 오버레이 메뉴를 만들지 않는다. 게임 위 UI는 Game Bar(위젯),
  홈은 Xbox 모드 홈 앱(ClawHub 런처, 희소 ID 패키지 `ClawHub_tj9ced57bhrww`) 또는 Xbox 앱이 맡는다.

## 컨트롤러 흐름 (방식 1: 펌웨어 매핑)

| 상황 | 컨트롤러 상태 |
|---|---|
| 셸 / 데스크톱 | baseline (기본 게임패드 매핑) |
| 매핑 프로필 있는 게임 실행 | 프로필의 키 바인딩을 **RAM에만** 기록 (`0x21`) |
| 게임 중 홈 버튼 (런처 표시) | 런처가 보이는 동안 baseline 복원 → 게임으로 돌아가면 프로필 재적용 |
| 게임 중 Game Bar 위젯 / Xbox 앱이 앞에 있음 | 임시로 baseline + XInput → 그 창이 닫히면(다른 창이 앞으로 오면) 프로필 재적용 |
| 게임 종료 | baseline 복원 |
| 절전 복귀 / 서비스 재시작 | 현재 상태 재적용 |

플래시 커밋(`0x22`)은 하지 않음 → 꺼지거나 문제가 생겨도 재부팅하면 원래대로.

## 화면 구성 (1280×800 기준)

- **상단 바**: 현재 화면 이름 · 시계 · 배터리 · (터치용) 빠른 설정 버튼
- **홈**: Xbox 홈처럼 런처의 중심. 마지막 플레이 게임 히어로 + 최근 게임 가로 줄, 그리고
  - 게임 실행 중이면 **"지금 플레이 중" 타일**: 게임으로 돌아가기 · 게임 종료
  - 예전 메인 메뉴에만 있던 항목: 라이브러리 · 매핑 · 설정 · Xbox 앱 · 데스크톱으로 · 전원(절전/다시 시작/시스템 종료) · ClawHub 종료
- **라이브러리**: 커버 그리드 (Steam/Epic/설치 프로그램 감지 + 직접 추가)
- **게임 상세**: 플레이, 매핑 프로필 선택, 매핑 편집
- **매핑 편집기**: 버튼 목록 ↔ 키 선택(최대 5개 조합), 메이플 기본 프리셋
- **빠른 설정 = Game Bar 위젯** (`ClawHubQuick`): 성능 모드 · TDP · 팬 · 밝기 · 볼륨 · 충전 한도 · FPS 등. 게임 위에서도 여기서.
  - 위젯이 설치되지 않았을 때만 **앱 안 빠른 설정 패널**(오른쪽 슬라이드)을 대체로 쓰고, 그것도 **런처가 보일 때만** 연다.
- 왼쪽 메인 메뉴 패널은 없앴다 (홈 화면이 대신함).
- **하단 힌트 바**: Ⓐ 선택 Ⓑ 뒤로/게임으로 등 상황별

## 입력 (Xbox 연동)

- XInput 폴링 (60Hz, 반복 입력) → 셸 내비게이션. WPF 포커스 이동(`MoveFocus`)으로 구현해 모든 화면 공통.
- 키보드 화살표/Enter/Esc도 동일 동작 (Desktop 모드 대비).
- 전용 버튼: 서비스가 `MSI_Event`(41 = 왼쪽, 88 = 오른쪽)를 받아 앱에 푸시 → `ShellViewModel.OnMsiButton`.
  런처 안에서는 패드 Menu/View, 키보드 F1/F2가 같은 경로.

### 왼쪽 버튼 = 홈 (Xbox 버튼처럼)

| 상태 | 동작 |
|---|---|
| Xbox 모드 동반(`IsCompanion`, Xbox 앱이 홈) | Xbox 앱 홈을 앞으로 (`shell:AppsFolder\Microsoft.GamingApp_8wekyb3d8bbwe!Microsoft.Xbox.App`). 실패하면 아래처럼 ClawHub 홈 |
| 런처 숨김 (게임/데스크톱 중) | ClawHub 런처를 띄우고 홈으로 (`GoHome`). 게임은 계속 돌고, 런처가 보이는 동안 매핑 해제 |
| 런처 표시, 홈이 아닌 화면 | 홈으로 |
| 런처 표시, 이미 홈 + 게임 실행 중 | 게임으로 돌아감 (B도 같음) |

### 오른쪽 버튼 = 빠른 설정 (Game Bar 위젯)

1. 앱 안 패널이 열려 있으면 닫기. 우리가 연 Game Bar가 앞에 있으면 Win+G로 닫기.
2. 위젯 패키지가 설치돼 있으면(`GetPackagesByPackageFamily`) `ms-gamebar:activate/ClawHub.Widget_tj9ced57bhrww_App_ClawHubQuick`
   → `ms-gamebar://compactmode?enable=true`. 1.5초 안에 Game Bar가 앞에 안 뜨면 MSI 도우미처럼 Win+G 후 한 번 더 활성화.
   5초가 지나도 안 뜨고(Game Bar 꺼짐) 런처가 보이고 있었다면 앱 안 패널로 대체.
3. 위젯이 없고 런처가 보이면 → 앱 안 빠른 설정 패널.
4. 위젯이 없고 런처가 숨어 있으면(게임 중) → 우리 오버레이 대신 Windows Game Bar 자체(Win+G, OEM 위젯 등).

Game Bar/Xbox 앱에 화면을 넘긴 동안(handoff)은 0.4초마다 앞 창을 확인한다. 그 창이 앞에 뜬 걸 본 뒤 다른 창이
앞으로 오면 닫힌 것으로 보고 매핑을 다시 맞춘다 (Game Bar 5초, Xbox 앱 15초 안에 끝내 안 뜨면 포기).

### OEM 위젯 슬롯

핸드헬드에서 Game Bar가 기본으로 내세우는 위젯은 `HKLM\...\GamingConfiguration\OEMGameBarWidget`(기본: MSI Quick Settings).
`scripts\register-home.ps1 -SetOemWidget`이 원래 값을 `HKLM\SOFTWARE\ClawHub\Backup\OEMGameBarWidget`에 백업하고
`ClawHub.Widget_tj9ced57bhrww`로 바꾼다. `-Remove`가 (값이 아직 우리 것이면) 백업으로 되돌린다.
