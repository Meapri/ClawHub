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
    public GamepadMode? ControllerMode { get; set; }
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
}

public sealed class ServiceStatus
{
    public bool ControllerConnected { get; set; }
    public string? ControllerFirmware { get; set; }
    public GamepadMode? ControllerMode { get; set; }
    /// <summary>현재 RAM에 적용된 매핑이 baseline이 아닌지.</summary>
    public bool MappingActive { get; set; }

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
    public const string Subscribe = "Subscribe";

    public const string EventButton = "Button";
    public const string EventStatus = "Status";

    public const string PipeName = "ClawHub.Service";
}
