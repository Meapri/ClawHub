# Claw WMI / EC 프로토콜

MSI Center M (`MSIWMIACPI2`, `API_UserScenario`, `API_Battery`, `API_MysticLight`, `MSIAPService`, `Command Center`) 정적 분석.
기기: baseboard `MS-1T91` (Claw 8 EX AI+). ⚠ = 실기 검증 필요.

## WMI 호출

- 객체: `root\WMI` → `MSI_ACPI.InstanceName='ACPI\PNP0C14\0_0'` (관리자 권한 필요)
- 모든 메서드: in/out 파라미터 `Data` = `Package_32` 임베디드 인스턴스 (`Bytes: uint8[32]`)
- `Get_X(idx)`: in `[idx, 0…]` → out `[flag(1=OK), payload…]`
- `Set_X(idx, payload)`: in `[idx, payload(n)…]` → out `[flag]`. **n은 메서드/인덱스별 고정 길이 — 반드시 읽고 수정해서 전체를 다시 쓸 것.**
- `Get_WMI` (in 없음) → `[flag, major, minor]`. major 3이면 `_64`/길이 변경 → **ClawHub는 major==2만 지원, 아니면 쓰기 비활성화.**
- `Get_EC` (in 없음) → `[flag, bits, 28자 ASCII EC 버전]`
- `MSI_ACPI` 클래스는 MSI가 `msiapcfg.dll` MOF로 등록함 → MSI Center M 제거 시 사라질 수 있음 ⚠. **MSI Center M은 제거하지 말고 비활성화만.**

| 메서드 | 페이로드 길이 |
|---|---|
| `Get/Set_Data` | 1 (idx = EC RAM 오프셋 0–255) |
| `Get/Set_Fan` | 8 (idx 1 CPU, 2 GPU) |
| `Get/Set_Temperature` | idx0: 3, idx1–2: 8 |
| `Get/Set_AP` | idx0: 6, 1: 3, 2: 7, 3: 8 |
| `Get/Set_MasterBattery` | idx0: 2, 1–4: 8 |

## EC 바이트 맵 (`Get_Data`/`Set_Data` 인덱스)

| Idx | 의미 | ClawHub |
|---|---|---|
| `0x50` | PL1 (W, 1바이트) | 쓰기 허용 (클램프) |
| `0x51` | PL2 (W) | 쓰기 허용 (클램프 ≤45) |
| `0x98` | bit7 Cooler Boost | 쓰기 허용 |
| `0xD2` | Shift 모드 | 쓰기 허용 (값 화이트리스트) |
| `0xD4` | bit7 커스텀 팬 커브 on | 쓰기 허용 |
| `0xD7` | 배터리 충전 한도 | 쓰기 허용 (bit7 설정된 경우만) |
| `0xD9` | bit5 비행기 모드 | 읽기만 |

그 외 모든 `Set_*`(BIOS/SMBUS/Thermal/Debug/Range/Package) 및 UEFI `MsiDCVarData`(OverBoost, 즉시 재부팅 유발)는 **금지**.

## 성능 모드 (1T91 값)

| 모드 | PL1 | PL2 | Shift(`0xD2`) | 팬 |
|---|---|---|---|---|
| 성능 | 30 | 37 | `0xC4` Sport | 설정 |
| 균형 | 25 | 37 | `0xC1` Green | 자동 |
| 절전 | 15 | 20 | `0xC2` ECO | 자동 |
| 수동 | 사용자(8–45) | 사용자(≤45) | `0xC6` Manual | 설정 |

TDP 적용 순서 (MSI와 동일): PL1을 8W로 낮춤 → PL2 쓰기 → PL1 최종값 쓰기.
Windows 전원 오버레이: `PowerSetActiveOverlayScheme` 계열 — 성능 `ded574b5-45a0-4f42-8737-46345c09c238`, 균형 `00000000-…`, 효율 `961cc777-2547-4f9d-8174-7d86181b8a7a`.

## 팬

- 커브: `Get_Fan(1|2)` 페이로드 `[1..6]` = 6개 듀티(%) 포인트. `[0]`,`[7]` 보존.
- 쓰기: `Get_Fan` → `[1..6]` 교체 → `Set_Fan` → `0xD4 |= 0x80`.
- 자동 복귀: `0xD4 &= 0x7F`.
- 온도 브레이크포인트: `Get_Temperature(1|2)` 페이로드 `[0],[3..7]` ⚠
- 실시간 RPM/온도 읽기 경로 미확인 ⚠ (`Get_Fan(0)`, `Get_Temperature(0)` 후보)

## 배터리 충전 한도 (`0xD7`)

bit7 = 지원, bit6..0 = 정지 %. 쓰기 값: 100% `0xE4`, 80% `0xD0`, 60% `0xBC`. 현재 값에 bit7 없으면 쓰지 않음.

## 전용 버튼 (WMI 이벤트)

- `SELECT * FROM MSI_Event` (`root\WMI`), 속성 `MSIEvt`, 하위 바이트:
  - **41** `CallMSICenter_X` — 왼쪽 MSI 버튼 → ClawHub 메인 메뉴/런처
  - **88** `CallMSICenter_X_OSD` — 오른쪽 퀵 버튼 → 퀵 설정 패널
- MSI Center M이 실행 중이면 같은 이벤트에 반응하므로 전환 시 MSI 프로세스 비활성화 필요.

## RGB

HID 컨트롤러 경유 (`docs/protocol-controller.md`의 LightProfile / `E0` / `09`).

## 동시성

MSIAPService(`\\.\pipe\MSI_SERVICE_2`)가 같은 WMI를 사용. 개발 중 병행 시 경쟁 가능 — ClawService는 모든 WMI 호출을 단일 락으로 직렬화하고, MSI UserScenario가 재적용하는 값과 충돌할 수 있음을 감안.
