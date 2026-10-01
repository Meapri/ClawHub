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
    public const string Subscribe = "Subscribe";

    public const string EventButton = "Button";
    public const string EventStatus = "Status";

    public const string PipeName = "ClawHub.Service";
}
