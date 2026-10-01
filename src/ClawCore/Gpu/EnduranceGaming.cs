using System.Runtime.InteropServices;

namespace ClawCore.Gpu;

/// <summary>
/// Intel Endurance Gaming 켜기/끄기. 값은 Intel IGCL <c>ctl_3d_endurance_gaming_control_t</c>와 같다
/// (github.com/intel/drivers.gpu.control-library, include/igcl_api.h).
/// </summary>
public enum EnduranceControl
{
    /// <summary>CTL_3D_ENDURANCE_GAMING_CONTROL_TURN_OFF = 0</summary>
    Off = 0,
    /// <summary>CTL_3D_ENDURANCE_GAMING_CONTROL_TURN_ON = 1 — 전원과 상관없이 항상.</summary>
    On = 1,
    /// <summary>CTL_3D_ENDURANCE_GAMING_CONTROL_AUTO = 2 — 배터리로 돌 때만 (드라이버가 판단).</summary>
    Auto = 2,
}

/// <summary>
/// Endurance Gaming 강도. 값은 IGCL <c>ctl_3d_endurance_gaming_mode_t</c>와 같다.
/// </summary>
public enum EnduranceMode
{
    /// <summary>CTL_3D_ENDURANCE_GAMING_MODE_BETTER_PERFORMANCE = 0 ("better performance mode")</summary>
    Performance = 0,
    /// <summary>CTL_3D_ENDURANCE_GAMING_MODE_BALANCED = 1</summary>
    Balanced = 1,
    /// <summary>CTL_3D_ENDURANCE_GAMING_MODE_MAXIMUM_BATTERY = 2</summary>
    MaxBattery = 2,
}

public sealed record EnduranceSetting(EnduranceControl Control, EnduranceMode Mode);

/// <summary>
/// Intel Endurance Gaming(배터리일 때 프레임·전력을 드라이버가 자동으로 줄이는 기능, "Auto TDP").
/// MSI Center M의 intelGEDll.dll이 내보내는 함수를 쓴다 (MSI와 같은 경로, 관리자 권한 불필요).
///
/// DLL 분석 (x64, 2026-10 MSI Center M에 들어 있는 판):
/// - <c>int setEGControlMode(int control, int mode)</c>: ctlGetSet3DFeature(CTL_3D_FEATURE_ENDURANCE_GAMING, bSet=1,
///   CUSTOM, ctl_endurance_gaming_t{control, mode})를 모든 어댑터에 호출. 성공 0, 실패 255.
/// - <c>int getEGmode()</c>: 같은 기능을 Get으로 읽어 디버그 출력(stdout)하고 MSI 방식 코드로 돌려준다 —
///   0 = Off+MaxBattery(드라이버 기본), 1 = Auto+MaxBattery, 2 = Auto+Balanced, 3 = Auto+Performance,
///   255 = 그 밖의 조합(On, Off+다른 강도 등) 또는 실패. 따라서 On과 일부 조합은 읽을 수 없다.
/// - <c>int setEGmode(int)</c>: MSI 코드(위 0~3)로 쓰는 함수. 여기서는 쓰지 않는다 (setEGControlMode가 모든 조합을 다룸).
/// </summary>
public static class EnduranceGaming
{
    public static bool Available => IntelGraphics.Available;

    /// <summary>드라이버에서 읽은 값. MSI 코드로 표현할 수 없는 조합(255)이거나 실패하면 null.</summary>
    public static EnduranceSetting? Read()
    {
        if (!Available) return null;
        int code;
        try
        {
            lock (IntelGraphics.DriverLock) code = GetEGmodeNative();
        }
        catch (Exception ex) when (ex is EntryPointNotFoundException or DllNotFoundException or SEHException) { return null; }

        return code switch
        {
            0 => new EnduranceSetting(EnduranceControl.Off, EnduranceMode.MaxBattery),
            1 => new EnduranceSetting(EnduranceControl.Auto, EnduranceMode.MaxBattery),
            2 => new EnduranceSetting(EnduranceControl.Auto, EnduranceMode.Balanced),
            3 => new EnduranceSetting(EnduranceControl.Auto, EnduranceMode.Performance),
            _ => null,
        };
    }

    public static bool Set(EnduranceSetting setting)
    {
        if (!Available || !Enum.IsDefined(setting.Control) || !Enum.IsDefined(setting.Mode)) return false;
        try
        {
            lock (IntelGraphics.DriverLock) return SetEGControlModeNative((int)setting.Control, (int)setting.Mode) == 0;
        }
        catch (Exception ex) when (ex is EntryPointNotFoundException or DllNotFoundException or SEHException) { return false; }
    }

    [DllImport(IntelGraphics.DllPath, EntryPoint = "getEGmode", CallingConvention = CallingConvention.Cdecl)]
    private static extern int GetEGmodeNative();

    [DllImport(IntelGraphics.DllPath, EntryPoint = "setEGControlMode", CallingConvention = CallingConvention.Cdecl)]
    private static extern int SetEGControlModeNative(int gamingControl, int gamingMode);
}
