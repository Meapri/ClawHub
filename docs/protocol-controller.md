# Claw 컨트롤러 HID 프로토콜

MSI Center M `API_ControlMode` 1.0.2608.3101 정적 분석 결과. 기기: Claw 8 EX AI+ (BIOS `E1T91IMS` → 모델 코드 **1T91**).
⚠ 표시는 실기 검증 필요.

## 장치

- VID `0x0DB0`, PID `0x1901` / `0x1902` (어느 PID가 어느 모드인지 ⚠)
- 명령 채널: **출력 리포트 ID `0x0F`** 를 가진 HID 컬렉션 (MI_01 vendor-defined로 추정 ⚠). 입력 리포트는 64바이트.
- 펌웨어 버전: USB `bcdDevice` (예: `0x0131` → "1.31"). HID 명령 없음.

## 패킷

```
출력: [0]=0x0F [1]=0x00 [2]=0x00 [3]=0x3C [4]=CMD [5..]=인자   (64바이트, 체크섬 없음)
입력: [4]=응답 CMD [5..]=데이터
```

| CMD | 이름 | 인자 / 응답 | 비고 |
|---|---|---|---|
| `04` | ReadProfile | `[5]=0 [6..7]=offset(BE) [8]=len≤55` | 응답 `05` + `[9..]` 데이터 |
| `21` | WriteProfile (RAM) | `[5]=1 [6..7]=offset(BE) [8]=len≤55 [9..]=data` | 휘발성 |
| `22` | SyncToROM | – | **플래시 기록** — 마모 주의, 사용 최소화 |
| `24` | SwitchMode | `[5]=mode [6]=MKeysFunction` | 응답 없음, 20ms 후 `26`으로 확인 |
| `26` / `27` | ReadGamepadMode / Ack | 응답 `[5]=mode [6]=mk` | |
| `09` / `E0` | SyncRGB / RGBControl | | LED |
| `2F` / `30` | 자이로 스트림 on / 데이터 | | |
| `FD` / `FE` | 캘리브레이션 | | |

- 큐 명령(04/21/22) ack: 타임아웃 1000ms. ReadProfile은 offset/len 일치하는 `05` 응답, 나머지는 `F4/0A/27/30` 이외의 아무 입력 리포트.
- 모드: 0 Offline, 1 **XInput**, 2 DInput, 3 MSI, 4 **Desktop**, 5 BIOS, 6 TESTING
- MKeysFunction: 0 Macro, 1 Combination, 2 Disabled

### 🚫 블랙리스트 (ClawHub는 절대 전송 안 함)

`F0`(DFU 펌웨어 업데이트), `28`(ResetDevice), `08`, `23`, `01`, `03`, `07`, `0B`, `0D`, `2C`, `2D`, `24`+mode 5/6, 실제 캘리브레이션 없는 `FD 01`, XInput 진동 "MSIAPP" 시퀀스.

## 프로필 이미지 (1478바이트, 55바이트 청크 27개)

| Offset | 길이 | 필드 |
|---|---|---|
| `0x00` | 32 | CustomData |
| `0x20` | 1 | LiftToWakeup (반전) |
| `0x21` | 1 | ReportRate |
| `0x22` | 2 | 진동 L, R (기본 50/50) |
| `0x24` | 5 | Audio |
| `0x29` | 10 | 자이로 |
| `0x33` | 2 | Shift |
| `0x35` | 5 | QuickLT, QuickRT, DpadDiagonalLock, SwitchABXY, StandbyTime |
| `0x3A` | 16×8 | KeyProfile: Up `3A`, Down `42`, Left `4A`, Right `52`, LB `5A`, RB `62`, L3 `6A`, R3 `72`, A `7A`, B `82`, X `8A`, Y `92`, Home `9A`, View `A2`, Menu `AA`, Camera `B2` |
| `0xBA` | 8+161 | M1 KeyProfile + Macro(`0xC2`) |
| `0x163` | 8+161 | M2 KeyProfile + Macro(`0x16B`) |
| `0x20C` / `0x21B` | 15 | LT / RT TriggerKeyProfile |
| `0x22A` / `0x23A` | 16 | 좌/우 스틱 |
| `0x24A` | 883 | LightProfile |
| `0x5BD` | 9 | ABXY 밝기 등 |

구 펌웨어는 레이아웃이 다름(KeyProfile 4바이트, 총 1398바이트, M1 `0x7A`). **읽은 길이/펌웨어 버전으로 레이아웃 확인 후에만 쓰기.**

### KeyProfile (8바이트)

`[Index][Flag][Turbo][Map0][Map1][Map2][Map3][Map4]`

- Index: 1 매핑, 2 매크로(M1/M2만)
- Flag: bit2 = MapOrMacroEn (MSI 코드 기준 MSB-first 직렬화 ⚠ — 실기 이미지로 확인)
- Map0..4: **동시에 눌리는 최대 5개 코드** (예: Ctrl+1 → `74 40 00 00 00`)

TriggerKeyProfile (15바이트): `[Index][Flag][Turbo][DeadRange][EdgeDeadRange][Map0..4][Linear×5]`

### 키 코드 (벤더 인덱스, HID usage 아님)

| 범위 | 코드 |
|---|---|
| 게임패드 | Up 1, Down 2, Left 3, Right 4, LB 5, RB 6, L3 7, R3 8, A 9, B 10, X 11, Y 12, Home 13, View 14, Menu 15, Camera 16, M1 17, M2 18, LT 19, RT 20, LS↑↓←→ 21–24, RS↑↓←→ 25–28 |
| 키보드 | Esc 50, F1–F12 51–62, \` 63, 1–9,0 64–73, - 74, = 75, Bksp 76, Tab 77, Q–P 78–87, [ 88, ] 89, \\ 90, Caps 91, A–L 92–100, ; 101, ' 102, Enter 103, LShift 104, Z–M 105–111, , 112, . 113, / 114, RShift 115, LCtrl 116, Win 117, LAlt 118, Space 119, RAlt 120, RCtrl 121, Ins 122, Home 123, PgUp 124, Del 125, End 126, PgDn 127, ↑↓←→ 128–131, NumLock 132, Num/ 133, Num* 134, Num- 135, Num+ 136, Num. 137, NumEnter 138, Num0–9 139–148, F23 156, F22 157 |
| 미디어 | Play 149, Stop 150, Next 151, Prev 152, Vol+ 153, Vol- 154, Mute 155 |
| 마우스 | L 200, M 201, R 202, B4 203, B5 204, 휠↑ 205, 휠↓ 206 |
| 없음 | 255 |

## 실기 검증 (2026-10-01, 펌웨어 4.19, ClawProbe)

- HID 명령 채널 = MI_01, 관리자 권한 불필요. `26`/`27` 모드 조회, `04`/`05` 프로필 읽기 정상.
- 프로필 길이 1478 확인 → 신형 레이아웃. 16개 버튼 오프셋 전부 일치 (기본 코드 1~20).
- Flag: 기본 버튼 `0x00`, 키보드로 매핑된 M1/M2 `0x04` → **bit2(0x04)가 재매핑 활성 비트** (LSB 기준). 트리거 Flag `0x02`.
- 1T91 기본값 확인: M1 = F22(157), M2 = LCtrl+1(116, 64).
- RAM 쓰기(`21`) + 재읽기 검증 정상: 적용 ~220ms, 복원 ~140ms.
- **XInput 모드에서 키 출력 확인** (`tools/keytest.ps1`): A→Z, X→LCtrl, M1→1 모두 저수준 훅에서 `injected=False`(하드웨어 입력). 매핑된 버튼은 XInput 버튼 신호를 내지 않음(중복 입력 없음).
- Desktop 모드는 같은 테스트에서 이벤트 0건 — 미입력인지 미지원인지 미확정. 기본값은 XInput 유지.

### 설정 바이트 (펌웨어 4.19 덤프 기본값)

| Offset | 의미 | 기본 |
|---|---|---|
| `0x22`/`0x23` | 진동 L/R (%) | 100/100 |
| `0x37` | D-pad 대각선 잠금 | 0 |
| `0x38` | ABXY 위치 바꾸기 | 0 |
| `0x39` | 대기 시간 | 10 |
| `0x20C+3`/`0x21B+3` | LT/RT 안쪽 데드존 (+4 바깥쪽) | 5 (95) |
| `0x22A+1..2`/`0x23A+1..2` | 스틱 데드존 사용/값 (+9..10 바깥쪽) | 1, 10 (1, 100) |
| `0x24A` | 활성 조명 애니메이션 (0–3) | 0 |
| `0x24B + n·220` | 애니메이션 n: [키프레임 수][효과=9][속도][밝기] + 8×9 LED RGB | anim0 = 4, 9, 3, 100 |
| `0x5BD..0x5C0` | ABXY 백라이트 밝기 | 50 |

ClawHub 단색 조명은 애니메이션 슬롯 3을 덮어쓰고 활성 인덱스를 3으로 바꾼 뒤 `09` SyncRGB. ⚠ 조명 반영은 실기 육안 확인 필요.

## 범용 키 매핑 관련 결론

- **펌웨어 키보드 매핑 가능 버튼**: D-pad 4방향, LB/RB, LT/RT, L3/R3, A/B/X/Y, M1/M2 (+ Home/View/Menu/Camera 슬롯 존재 ⚠).
- **스틱은 키 입력 소스로 불가** → 이동은 D-pad 사용.
- 조합키 최대 5개, 시간 매크로는 M1/M2만 (32스텝).
- XInput 모드에서 비-M 버튼의 키보드 출력 여부 ⚠ (1T91 기본값이 XInput 중 M1=F22, M2=Ctrl+1이라 하이브리드 가능성 높음).
- MSI는 게임별 자동 전환 기능이 없음 → ClawHub가 직접 구현.

## ClawHub 쓰기 정책

1. 시작 시 프로필 전체 읽기 → `%AppData%\ClawHub\controller-baseline.bin` 백업 (최초 1회).
2. 게임 프로필 적용은 **RAM 쓰기(`21`)만**. 플래시 커밋(`22`)은 사용자가 "기본값으로 저장"할 때만.
3. 게임 종료 시 baseline의 해당 범위를 RAM에 다시 써서 복원.
4. 쓰기 전 이미지 길이 1478 확인, 아니면 쓰기 거부.
