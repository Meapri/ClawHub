using ClawCore.Gpu;

namespace ClawCore.Ipc;

/// <summary>Request.PowerSource 값 — SetPerformance가 어느 칸(전원 연결/배터리)을 바꿀지.</summary>
public enum PowerSourceKind { Ac, Battery }

/// <summary>Intel Endurance Gaming 상태 (GraphicsState.Endurance).</summary>
public sealed class EnduranceState
{
    /// <summary>intelGEDll.dll(드라이버 설정)을 쓸 수 있는지.</summary>
    public bool Available { get; set; }
    /// <summary>현재 값. 드라이버에서 못 읽으면 사용자가 마지막으로 정한 값, 그것도 없으면 null.</summary>
    public EnduranceControl? Control { get; set; }
    public EnduranceMode? Mode { get; set; }
    /// <summary>"Driver" = 드라이버에서 읽음, "Saved" = 읽을 수 없는 조합(On 등)이라 저장값, null = 모름.</summary>
    public string? Source { get; set; }
    /// <summary>사용자가 ClawHub에서 정한 값이 있어 시작·절전 복귀 때 다시 적용하는지.</summary>
    public bool Managed { get; set; }
}

/// <summary>배터리 수명 (읽기 전용, 60초 캐시). 모르는 항목은 null.</summary>
public sealed class BatteryHealth
{
    /// <summary>설계 용량 (mWh).</summary>
    public int? DesignedCapacityMWh { get; set; }
    /// <summary>현재 완충 용량 (mWh).</summary>
    public int? FullChargeCapacityMWh { get; set; }
    /// <summary>완충 ÷ 설계 × 100 (반올림). 새 배터리는 100을 넘을 수 있다.</summary>
    public int? HealthPercent { get; set; }
    /// <summary>충방전 횟수. 0은 펌웨어가 세지 않는 것으로 보고 null.</summary>
    public int? CycleCount { get; set; }
    public string? Manufacturer { get; set; }
}
