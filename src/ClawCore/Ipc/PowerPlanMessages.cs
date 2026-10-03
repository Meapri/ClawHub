namespace ClawCore.Ipc;

/// <summary>Windows 전원 구성표의 설정 항목 (Request.PowerSetting, PowerPlanSettingState.PowerSetting).</summary>
public enum PowerPlanSetting
{
    /// <summary>화면 끄기 (초, 0 = 사용 안 함). SUB_VIDEO/VIDEOIDLE.</summary>
    DisplayOff,
    /// <summary>절전 전환 (초, 0 = 사용 안 함). SUB_SLEEP/STANDBYIDLE.</summary>
    Sleep,
    /// <summary>최대 절전 전환 (초, 0 = 사용 안 함). SUB_SLEEP/HIBERNATEIDLE. 최대 절전이 꺼져 있으면 바꿀 수 없다.</summary>
    Hibernate,
    /// <summary>전원 단추 동작 (목록: 0 아무것도 안 함, 1 절전, 2 최대 절전, 3 시스템 종료, 4 화면 끄기). SUB_BUTTONS/PBUTTONACTION.</summary>
    PowerButton,
    /// <summary>절약 모드 자동 켜짐 배터리 % (0 = 끔, 100 = 항상). 배터리 칸만. SUB_ENERGYSAVER/ESBATTTHRESHOLD.</summary>
    EnergySaverThreshold,
    /// <summary>절약 모드일 때 화면 밝기 (평소 밝기의 %, 100 = 그대로). 배터리 칸만. SUB_ENERGYSAVER/ESBRIGHTNESS.</summary>
    EnergySaverBrightness,
    /// <summary>밝기 자동 조절 (목록: 0 끔, 1 켬). 조도 센서가 있을 때만. SUB_VIDEO/ADAPTBRIGHT.</summary>
    AdaptiveBrightness,
    /// <summary>대기(모던 스탠바이) 중 네트워크 (목록: 0 끊기, 1 연결 유지, 2 Windows가 관리). 모던 스탠바이 기기만. SUB_NONE/CONNECTIVITYINSTANDBY.</summary>
    StandbyNetwork,
    /// <summary>절전 해제 타이머 (목록: 0 사용 안 함, 1 사용, 2 중요한 것만). SUB_SLEEP/RTCWAKE.</summary>
    WakeTimers,
}

/// <summary>구성표가 아니라 기기 전체에 하나인 전원 기능 (SetPowerFeature).</summary>
public enum PowerFeature
{
    /// <summary>최대 절전 모드 (powercfg /hibernate on|off). 끄면 최대 절전 전환·빠른 시작도 못 쓴다.</summary>
    Hibernate,
    /// <summary>빠른 시작 (종료할 때 커널을 최대 절전처럼 저장해 다음 부팅이 빠름). 최대 절전 모드가 켜져 있어야 한다.</summary>
    FastStartup,
}

/// <summary>값의 단위. Choice면 Choices에 있는 값만 쓸 수 있다.</summary>
public enum PowerPlanUnit { Seconds, Percent, Choice }

/// <summary>고를 수 있는 값 하나 (칩). Label은 서비스가 만든 한국어 표시 문자열.</summary>
public sealed class PowerPlanChoice
{
    public int Value { get; set; }
    public string Label { get; set; } = "";
}

/// <summary>설정 항목 하나의 현재 값과 고를 수 있는 값.</summary>
public sealed class PowerPlanSettingState
{
    public PowerPlanSetting PowerSetting { get; set; }
    public PowerPlanUnit Unit { get; set; }
    /// <summary>전원 연결(충전 중) 칸 값.</summary>
    public int Ac { get; set; }
    /// <summary>배터리 칸 값.</summary>
    public int Dc { get; set; }
    public string AcText { get; set; } = "";
    public string DcText { get; set; } = "";
    /// <summary>false면 배터리 칸만 의미가 있다 (절약 모드 항목). PowerSource "Ac"로 바꾸려 하면 거부.</summary>
    public bool AcApplies { get; set; } = true;
    /// <summary>Seconds/Percent: 기기가 허용하는 최소·최대.</summary>
    public int Min { get; set; }
    public int Max { get; set; }
    /// <summary>
    /// 칩으로 보여 줄 값 (작은 값부터, Seconds는 "사용 안 함"(0)이 맨 끝). Seconds/Percent는 추천 값 + 지금 쓰는 값이 추천에 없으면 그 값도.
    /// Choice는 허용되는 값 전부.
    /// </summary>
    public List<PowerPlanChoice> Choices { get; set; } = [];
}

/// <summary>Windows 전원 구성표 상태 (ServiceStatus.PowerPlan). 활성 구성표의 값이며 10초 캐시.</summary>
public sealed class PowerPlanState
{
    public bool Available { get; set; }
    public string? Error { get; set; }
    public string? SchemeGuid { get; set; }
    /// <summary>활성 구성표 이름 (Windows 표시 언어, 예: "균형 조정").</summary>
    public string? SchemeName { get; set; }
    /// <summary>모던 스탠바이(S0 저전원 유휴) 기기인지 — 이 기기의 "절전"은 S0 대기 상태.</summary>
    public bool ModernStandby { get; set; }
    /// <summary>최대 절전 모드를 쓸 수 있는지 (powercfg /h). 꺼져 있으면 Hibernate는 바꿀 수 없고 전원 단추에서 최대 절전이 빠진다.</summary>
    public bool HibernateEnabled { get; set; }
    /// <summary>빠른 시작이 켜져 있는지 (HiberbootEnabled — 최대 절전이 꺼져 있으면 켜져 있어도 동작하지 않는다).</summary>
    public bool FastStartupEnabled { get; set; }
    /// <summary>조도 센서가 있는지 (없으면 AdaptiveBrightness 항목을 보내지 않는다).</summary>
    public bool AmbientLightSensor { get; set; }
    /// <summary>이 기기에 있는 항목만 (없는 GUID는 빠짐).</summary>
    public List<PowerPlanSettingState> Settings { get; set; } = [];
}
