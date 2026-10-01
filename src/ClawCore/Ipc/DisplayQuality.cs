namespace ClawCore.Ipc;

/// <summary>정수/최근접 이웃 스케일링 방식 (Intel "Retro Scaling").</summary>
public enum RetroScalingMode
{
    Integer,
    NearestNeighbour,
}

/// <summary>Intel 저지연 모드 (드라이버 전역 3D 설정).</summary>
public enum LowLatencyMode
{
    Off,
    On,
    /// <summary>저지연 + 부스트 (GPU 클럭을 높게 유지).</summary>
    Boost,
}

/// <summary>
/// Intel 드라이버(IGCL)의 화면 품질 설정. 항목이 null이면 이 기기/드라이버에서 지원하지 않거나 읽지 못한 것.
/// </summary>
public sealed class DisplayQualityState
{
    /// <summary>IGCL을 쓸 수 있는지 (ControlLib.dll 로드·초기화 성공).</summary>
    public bool Available { get; set; }
    /// <summary>마지막 읽기에서 난 문제 (있으면).</summary>
    public string? Error { get; set; }

    /// <summary>레트로 스케일링 켜짐 여부. 바꾸면 화면이 한 번 깜빡인다(모드 재설정).</summary>
    public bool? RetroScaling { get; set; }
    public RetroScalingMode? RetroScalingMode { get; set; }
    /// <summary>지원하는 레트로 스케일링 방식.</summary>
    public List<RetroScalingMode> RetroScalingModes { get; set; } = [];

    /// <summary>디스플레이 선명도(샤프닝) 필터 켜짐 여부.</summary>
    public bool? Sharpening { get; set; }
    /// <summary>선명도 강도 (SharpeningMin~SharpeningMax).</summary>
    public int? SharpeningLevel { get; set; }
    public int? SharpeningMin { get; set; }
    public int? SharpeningMax { get; set; }

    /// <summary>저지연 모드 (전역).</summary>
    public LowLatencyMode? LowLatency { get; set; }
    public List<LowLatencyMode> LowLatencyModes { get; set; } = [];

    /// <summary>가변 주사율(Adaptive Sync/VRR)이 켜져 있는지 — 읽기 전용.</summary>
    public bool? AdaptiveSync { get; set; }
    /// <summary>패널이 VRR을 지원하는지.</summary>
    public bool? AdaptiveSyncSupported { get; set; }
    /// <summary>Intel Arc Sync 프로필: Recommended, Excellent, Good, Compatible, Off, Vesa, Custom.</summary>
    public string? VrrProfile { get; set; }
    /// <summary>드라이버가 실제로 쓰는 VRR 범위 (프로필 적용 후).</summary>
    public double? VrrMinHz { get; set; }
    public double? VrrMaxHz { get; set; }
    /// <summary>패널이 알려 준 VRR 범위.</summary>
    public double? PanelVrrMinHz { get; set; }
    public double? PanelVrrMaxHz { get; set; }
    /// <summary>현재 주사율 (Hz).</summary>
    public double? RefreshRate { get; set; }
}

/// <summary>SetDisplayQuality 요청. null인 항목은 그대로 둔다.</summary>
public sealed class DisplayQualityChange
{
    public bool? RetroScaling { get; set; }
    public RetroScalingMode? RetroScalingMode { get; set; }
    public bool? Sharpening { get; set; }
    public int? SharpeningLevel { get; set; }
    public LowLatencyMode? LowLatency { get; set; }
}
