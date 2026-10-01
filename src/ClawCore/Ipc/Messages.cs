using ClawCore.Controller;
using ClawCore.Ec;

namespace ClawCore.Ipc;

/// <summary>앱 → 서비스 요청. 한 줄에 JSON 하나 (줄바꿈 구분).</summary>
public sealed class Request
{
    public int Id { get; set; }
    public string Op { get; set; } = "";

    public PerformanceState? Performance { get; set; }
    public ChargeLimit? ChargeLimit { get; set; }
    /// <summary>ApplyMapping: 버튼 → 펌웨어 키 코드.</summary>
    public Dictionary<ClawButton, byte[]>? Bindings { get; set; }
    /// <summary>ApplyMapping: M1/M2 매크로.</summary>
    public Dictionary<ClawButton, MacroStep[]>? Macros { get; set; }
    public GamepadMode? ControllerMode { get; set; }
    public ControllerSettings? ControllerSettings { get; set; }
    /// <summary>SetMsiCenter: true = 켜기, false = 끄기.</summary>
    public bool? Enabled { get; set; }

    /// <summary>SetGraphics: FPS 제한 (0 = 끔). null이면 그대로.</summary>
    public int? FpsLimit { get; set; }
    /// <summary>SetGraphics: XeSS 프레임 생성 0 끔, 1 = 2배, 2 = 3배, 3 = 4배. null이면 그대로.</summary>
    public int? FrameGeneration { get; set; }
    /// <summary>SetBrightness: 0–100.</summary>
    public int? Brightness { get; set; }
    /// <summary>SetDisplayQuality: 바꿀 항목만 (null = 그대로).</summary>
    public DisplayQualityChange? DisplayQuality { get; set; }
    /// <summary>SetAppState: 런처 앱이 알려 주는 현재 게임·매핑 정보 (위젯 표시용).</summary>
    public AppState? AppState { get; set; }
    /// <summary>AppCommand: 위젯 → 런처 앱 명령 (서비스가 구독 중인 앱에 중계). <see cref="AppCommands"/>.</summary>
    public string? Command { get; set; }

    /// <summary>SetEnduranceGaming: "Off" | "On" | "Auto".</summary>
    public Gpu.EnduranceControl? EnduranceControl { get; set; }
    /// <summary>SetEnduranceGaming: "Performance" | "Balanced" | "MaxBattery". null이면 지금 값(없으면 Balanced).</summary>
    public Gpu.EnduranceMode? EnduranceMode { get; set; }
    /// <summary>
    /// SetPerformance: 바꿀 칸 "Ac" | "Battery". 생략하면 지금 적용 중인 칸
    /// (배터리 사용 중이고 배터리 전용 값이 있으면 그 값, 아니면 기본값). "Battery" + Performance null = 배터리 전용 값 지우기.
    /// </summary>
    public PowerSourceKind? PowerSource { get; set; }
}

/// <summary>위젯이 런처 앱에 보내는 명령 (AppCommand.Command 값).</summary>
public static class AppCommands
{
    public const string CycleProfile = "CycleProfile";
    /// <summary>"SetProfile:&lt;프로필 Id&gt;" — 빈 Id면 매핑 사용 안 함.</summary>
    public const string SetProfilePrefix = "SetProfile:";
    public const string QuitGame = "QuitGame";
    public const string ShowClawHub = "ShowClawHub";
    /// <summary>Game Bar 스크린샷 (Win+Alt+PrtSc). 런처가 떠 있으면 숨긴 뒤 보낸다.</summary>
    public const string Screenshot = "Screenshot";
    /// <summary>Game Bar 녹화 시작/중지 (Win+Alt+R).</summary>
    public const string ToggleRecording = "ToggleRecording";
    /// <summary>Game Bar "방금 일어난 일 기록" — 최근 30초 저장 (Win+Alt+G, 백그라운드 녹화가 켜져 있어야 함).</summary>
    public const string SaveClip = "SaveClip";
}

/// <summary>런처 앱이 서비스에 맡겨 두는 상태. 위젯은 GetStatus로 읽는다.</summary>
public sealed class AppState
{
    public string? RunningGameTitle { get; set; }
    public string? ProfileId { get; set; }
    public string? ProfileName { get; set; }
    public List<ProfileItem> Profiles { get; set; } = [];
    /// <summary>게임 전용 성능 모드 이름 (null = 기본값 따름).</summary>
    public string? GamePerformanceMode { get; set; }
    /// <summary>게임 전용 FPS 제한 (null = 기본값 따름).</summary>
    public int? GameFpsLimit { get; set; }
    /// <summary>게임 전용 배터리 성능 모드 (null = 배터리여도 GamePerformanceMode/기본값 따름).</summary>
    public string? GamePerformanceModeOnBattery { get; set; }
    /// <summary>게임 전용 배터리 FPS 제한 (null = 배터리여도 GameFpsLimit/기본값 따름, 0 = 끔).</summary>
    public int? GameFpsLimitOnBattery { get; set; }
    /// <summary>앱이 본 전원 상태 (true = 배터리 사용 중).</summary>
    public bool OnBattery { get; set; }
    /// <summary>실행 중 게임의 렌더링 프로세스 ID (FPS 측정 대상). null = 모름/게임 없음.</summary>
    public int? RunningGamePid { get; set; }
}

public sealed class ProfileItem
{
    public string Id { get; set; } = "";
    public string Name { get; set; } = "";
}

public sealed class GraphicsState
{
    public bool Available { get; set; }
    public int FpsLimit { get; set; }
    public int FrameGeneration { get; set; }
    /// <summary>Intel Endurance Gaming (Auto TDP).</summary>
    public EnduranceState? Endurance { get; set; }
}

public sealed class TelemetrySample
{
    /// <summary>수집 시각 (Unix 초).</summary>
    public long Time { get; set; }
    public int? CpuTemp { get; set; }
    public int? CpuWatts { get; set; }
    public double? BatteryWatts { get; set; }
    /// <summary>게임 FPS (측정 중일 때만).</summary>
    public double? Fps { get; set; }
}

/// <summary>서비스 → 앱. Id가 있으면 응답, 0이면 푸시 이벤트.</summary>
public sealed class Response
{
    public int Id { get; set; }
    public bool Ok { get; set; } = true;
    public string? Error { get; set; }

    public string? Event { get; set; }
    public MsiButton? Button { get; set; }
    public ServiceStatus? Status { get; set; }
    public Telemetry? Telemetry { get; set; }
    /// <summary>Event = AppCommand 일 때 명령 문자열.</summary>
    public string? Command { get; set; }
}

/// <summary>EC 실시간 값 (읽기 전용). 출처: MSI OSD 코드 정적 분석, 값의 의미는 실기 확인 필요.</summary>
public sealed class Telemetry
{
    public int? CpuTemp { get; set; }
    public int? GpuTemp { get; set; }
    /// <summary>CPU 패키지 전력 (W, EC 0xDD).</summary>
    public int? CpuWatts { get; set; }
    /// <summary>Get_Fan(0) 원시값 — RPM 변환식 검증 전.</summary>
    public byte[]? FanRaw { get; set; }

    public int? BatteryPercent { get; set; }
    public bool Charging { get; set; }
    /// <summary>방전 전력 (W). 충전 중이면 null.</summary>
    public double? BatteryWatts { get; set; }
    public int? RemainingMinutes { get; set; }
    /// <summary>게임 FPS (최근 1초 평균, PresentMon). 게임이 없거나 측정 전이면 null.</summary>
    public double? Fps { get; set; }
    /// <summary>평균 프레임 시간 (ms, 최근 1초).</summary>
    public double? FrameTimeMs { get; set; }
    /// <summary>1% low FPS (최근 30초 프레임 시간 99백분위).</summary>
    public double? FpsLow1 { get; set; }
    /// <summary>최근 2분 (2초 간격, 오래된 것부터). 그래프용.</summary>
    public List<TelemetrySample> History { get; set; } = [];
}

public sealed class ServiceStatus
{
    public bool ControllerConnected { get; set; }
    public string? ControllerFirmware { get; set; }
    public GamepadMode? ControllerMode { get; set; }
    /// <summary>현재 RAM에 적용된 매핑이 baseline이 아닌지.</summary>
    public bool MappingActive { get; set; }
    /// <summary>사용자가 정한 컨트롤러 설정 (null 항목 = 원래 값).</summary>
    public ControllerSettings? ControllerSettings { get; set; }
    /// <summary>원래(baseline) 컨트롤러 값 — 설정 화면의 기본값 표시용.</summary>
    public ControllerSettings? ControllerDefaults { get; set; }

    /// <summary>게임별 성능 모드가 임시로 적용 중이면 그 상태 (사용자 기본값은 Performance).</summary>
    public PerformanceState? TemporaryPerformance { get; set; }
    public bool? MsiCenterEnabled { get; set; }
    public string? BiosVersion { get; set; }

    public bool EcAvailable { get; set; }
    public bool EcWritable { get; set; }
    public string? EcVersion { get; set; }
    public PerformanceState? Performance { get; set; }
    public ChargeLimit? ChargeLimit { get; set; }

    public GraphicsState? Graphics { get; set; }
    /// <summary>Intel 화면 품질 (레트로 스케일링·선명도·저지연·VRR).</summary>
    public DisplayQualityState? DisplayQuality { get; set; }
    public int? Brightness { get; set; }
    public AppState? AppState { get; set; }

    /// <summary>배터리로 돌고 있는지 (서비스가 본 값).</summary>
    public bool OnBattery { get; set; }
    /// <summary>전원 연결 때의 사용자 기본 성능 (Performance는 지금 적용 중인 쪽).</summary>
    public PerformanceState? PerformanceOnAc { get; set; }
    /// <summary>배터리 전용 사용자 기본 성능 (null = 배터리여도 PerformanceOnAc).</summary>
    public PerformanceState? PerformanceOnBattery { get; set; }
    public BatteryHealth? BatteryHealth { get; set; }

    public List<string> Warnings { get; set; } = [];
}

public static class Ops
{
    public const string GetStatus = "GetStatus";
    public const string SetPerformance = "SetPerformance";
    public const string SetChargeLimit = "SetChargeLimit";
    public const string ApplyMapping = "ApplyMapping";
    public const string RestoreMapping = "RestoreMapping";
    public const string SetControllerMode = "SetControllerMode";
    public const string SetControllerSettings = "SetControllerSettings";
    /// <summary>저장하지 않고 성능 모드만 바꾼다 (게임별 성능). RestorePerformance로 사용자 기본값 복귀.</summary>
    public const string ApplyTemporaryPerformance = "ApplyTemporaryPerformance";
    public const string RestorePerformance = "RestorePerformance";
    public const string SetMsiCenter = "SetMsiCenter";
    public const string GetTelemetry = "GetTelemetry";
    public const string SetGraphics = "SetGraphics";
    public const string SetBrightness = "SetBrightness";
    public const string SetDisplayQuality = "SetDisplayQuality";
    public const string SetAppState = "SetAppState";
    /// <summary>Intel Endurance Gaming 설정 (EnduranceControl, EnduranceMode). 서비스가 저장하고 시작·절전 복귀 때 재적용.</summary>
    public const string SetEnduranceGaming = "SetEnduranceGaming";
    public const string AppCommand = "AppCommand";
    public const string Subscribe = "Subscribe";

    public const string EventButton = "Button";
    public const string EventStatus = "Status";
    public const string EventAppCommand = "AppCommand";

    public const string PipeName = "ClawHub.Service";
}
