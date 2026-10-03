using System.Runtime.InteropServices;
using Microsoft.Win32;

namespace ClawCore.Power;

/// <summary>
/// Windows 전원 구성표(powrprof.dll) 읽기·쓰기. 읽기는 일반 사용자도 되고, 쓰기·적용은 서비스(LocalSystem)가 한다.
/// 값은 "현재 활성 구성표"의 AC(전원 연결)/DC(배터리) 칸.
/// </summary>
public static class PowerPlanNative
{
    // 하위 그룹·설정 GUID (winnt.h / powercfg /qh 로 이 기기에서 확인)
    public static readonly Guid SubVideo = new("7516b95f-f776-4464-8c53-06167f40cc99");
    public static readonly Guid VideoIdle = new("3c0bc021-c8a8-4e07-a973-6b14cbcb2b7e");
    public static readonly Guid AdaptBright = new("fbd9aa66-9553-4097-ba44-ed6e9d65eab8");
    public static readonly Guid SubSleep = new("238c9fa8-0aad-41ed-83f4-97be242c8f20");
    public static readonly Guid StandbyIdle = new("29f6c1db-86da-48c5-9fdb-f2b67b1f44da");
    public static readonly Guid HibernateIdle = new("9d7815a6-7ee4-497e-8888-515a05f02364");
    public static readonly Guid SubButtons = new("4f971e89-eebd-4455-a8de-9e59040e7347");
    public static readonly Guid PButtonAction = new("7648efa3-dd9c-4e3e-b566-50f929386280");
    public static readonly Guid SubEnergySaver = new("de830923-a562-41af-a086-e3a2c6bad2da");
    public static readonly Guid EsBattThreshold = new("e69653ca-cf7f-4f05-aa73-cb833fa90ad4");
    public static readonly Guid EsBrightness = new("13d09884-f74e-474a-a852-b6bde8ad03a8");
    public static readonly Guid SubNone = new("fea3413e-7e05-4911-9a71-700331f1c294");
    public static readonly Guid ConnectivityInStandby = new("f15576e8-98b7-4186-b944-eafa664402d9");
    public static readonly Guid RtcWake = new("bd3b718a-0680-4d9d-8ab2-e1d2b4ac806d");

    private const uint ErrorSuccess = 0;

    /// <summary>현재 활성 전원 구성표. 실패하면 null.</summary>
    public static Guid? ActiveScheme()
    {
        if (PowerGetActiveScheme(IntPtr.Zero, out var p) != ErrorSuccess || p == IntPtr.Zero) return null;
        try { return Marshal.PtrToStructure<Guid>(p); }
        finally { LocalFree(p); }
    }

    /// <summary>구성표 이름 (Windows 표시 언어, 예: "균형 조정").</summary>
    public static string? SchemeName(Guid scheme)
    {
        uint size = 0;
        if (PowerReadFriendlyName(IntPtr.Zero, ref scheme, IntPtr.Zero, IntPtr.Zero, null, ref size) != ErrorSuccess || size == 0) return null;
        var buf = new byte[size];
        if (PowerReadFriendlyName(IntPtr.Zero, ref scheme, IntPtr.Zero, IntPtr.Zero, buf, ref size) != ErrorSuccess) return null;
        return System.Text.Encoding.Unicode.GetString(buf, 0, (int)size).TrimEnd('\0');
    }

    /// <summary>활성 구성표의 AC/DC 값. 설정이 이 기기에 없으면 null.</summary>
    public static (uint Ac, uint Dc)? Read(Guid scheme, Guid sub, Guid setting)
    {
        if (PowerReadACValueIndex(IntPtr.Zero, ref scheme, ref sub, ref setting, out var ac) != ErrorSuccess) return null;
        if (PowerReadDCValueIndex(IntPtr.Zero, ref scheme, ref sub, ref setting, out var dc) != ErrorSuccess) return null;
        return (ac, dc);
    }

    /// <summary>값 범위형 설정(초, %)의 최소·최대. 목록형 설정이면 null.</summary>
    public static (uint Min, uint Max)? Range(Guid sub, Guid setting)
    {
        if (PowerReadValueMin(IntPtr.Zero, ref sub, ref setting, out var min) != ErrorSuccess) return null;
        if (PowerReadValueMax(IntPtr.Zero, ref sub, ref setting, out var max) != ErrorSuccess) return null;
        return (min, max);
    }

    /// <summary>목록형 설정(전원 단추 동작 등)이 허용하는 항목 수 — 쓸 수 있는 값은 0 ~ 개수-1. 범위형이면 0.</summary>
    public static int PossibleValueCount(Guid sub, Guid setting)
    {
        var n = 0;
        while (n < 64)
        {
            uint size = 0;
            if (PowerReadPossibleValue(IntPtr.Zero, ref sub, ref setting, IntPtr.Zero, (uint)n, IntPtr.Zero, ref size) != ErrorSuccess) break;
            n++;
        }
        return n;
    }

    /// <summary>활성(또는 지정) 구성표의 AC 또는 DC 칸에 쓴다. 적용은 <see cref="Apply"/>.</summary>
    public static void Write(Guid scheme, Guid sub, Guid setting, bool ac, uint value)
    {
        var r = ac
            ? PowerWriteACValueIndex(IntPtr.Zero, ref scheme, ref sub, ref setting, value)
            : PowerWriteDCValueIndex(IntPtr.Zero, ref scheme, ref sub, ref setting, value);
        if (r != ErrorSuccess) throw new InvalidOperationException($"전원 설정을 쓰지 못했어요 (오류 {r})");
    }

    /// <summary>바꾼 값을 바로 반영 (같은 구성표를 다시 활성화).</summary>
    public static void Apply(Guid scheme)
    {
        var r = PowerSetActiveScheme(IntPtr.Zero, ref scheme);
        if (r != ErrorSuccess) throw new InvalidOperationException($"전원 구성표를 적용하지 못했어요 (오류 {r})");
    }

    // ── 기기 능력 ───────────────────────────────────────────

    /// <summary>SYSTEM_POWER_CAPABILITIES 중 필요한 것만.</summary>
    public readonly record struct Capabilities(bool ModernStandby, bool S4Supported, bool HiberFilePresent);

    public static Capabilities? ReadCapabilities()
    {
        // 구조체 전체는 76바이트. BOOLEAN 15개(0~14) → MinThrottle(15) MaxThrottle(16) FastSystemS4(17) Hiberboot(18)
        // WakeAlarmPresent(19) AoAc(20). SystemS4 = 6, HiberFilePresent = 8.
        var buf = new byte[128];
        if (!GetPwrCapabilities(buf)) return null;
        return new Capabilities(ModernStandby: buf[20] != 0, S4Supported: buf[6] != 0, HiberFilePresent: buf[8] != 0);
    }

    /// <summary>
    /// 최대 절전 모드를 쓸 수 있는지 (powercfg /h on|off). 레지스트리 HibernateEnabled(없으면 HibernateEnabledDefault)가
    /// 0이 아니고, 펌웨어가 S4를 지원하며 hiberfil이 있어야 한다. 읽을 수 없으면 레지스트리만 본다.
    /// </summary>
    public static bool HibernateEnabled(Capabilities? caps)
    {
        bool? reg = null;
        try
        {
            using var key = Registry.LocalMachine.OpenSubKey(@"SYSTEM\CurrentControlSet\Control\Power");
            var v = key?.GetValue("HibernateEnabled") ?? key?.GetValue("HibernateEnabledDefault");
            if (v is int i) reg = i != 0;
        }
        catch (Exception) { /* 권한 없음 */ }
        if (caps is { } c) return c.S4Supported && c.HiberFilePresent && reg != false;
        return reg ?? false;
    }

    /// <summary>
    /// 조도 센서가 있는지 (밝기 자동 조절은 센서가 있어야 의미가 있다). 센서 장치 인터페이스 중
    /// 조도 센서 종류(GUID_SensorType_AmbientLight), 빛 분류(SENSOR_CATEGORY_LIGHT)가 하나라도 연결돼 있으면 true.
    /// </summary>
    public static bool HasAmbientLightSensor()
    {
        Guid[] classes =
        [
            new("97f115c8-599a-4153-8894-d2d12899918a"), // GUID_SensorType_AmbientLight
            new("17a665c0-9063-4216-b202-5c7a255e18ce"), // SENSOR_CATEGORY_LIGHT
        ];
        foreach (var g in classes)
        {
            var guid = g;
            // 현재 연결된(PRESENT) 인터페이스 목록 크기 — 빈 목록은 끝 NUL 하나(1)
            if (CM_Get_Device_Interface_List_SizeW(out var len, ref guid, null, 0) == 0 && len > 1) return true;
        }
        return false;
    }

    // ── P/Invoke ───────────────────────────────────────────

    [DllImport("powrprof.dll")] private static extern uint PowerGetActiveScheme(IntPtr userRootPowerKey, out IntPtr activePolicyGuid);
    [DllImport("powrprof.dll")] private static extern uint PowerSetActiveScheme(IntPtr userRootPowerKey, ref Guid schemeGuid);
    [DllImport("powrprof.dll")]
    private static extern uint PowerReadFriendlyName(IntPtr rootPowerKey, ref Guid schemeGuid, IntPtr subGroup, IntPtr setting,
        byte[]? buffer, ref uint bufferSize);
    [DllImport("powrprof.dll")]
    private static extern uint PowerReadACValueIndex(IntPtr rootPowerKey, ref Guid schemeGuid, ref Guid subGroup, ref Guid setting, out uint value);
    [DllImport("powrprof.dll")]
    private static extern uint PowerReadDCValueIndex(IntPtr rootPowerKey, ref Guid schemeGuid, ref Guid subGroup, ref Guid setting, out uint value);
    [DllImport("powrprof.dll")]
    private static extern uint PowerWriteACValueIndex(IntPtr rootPowerKey, ref Guid schemeGuid, ref Guid subGroup, ref Guid setting, uint value);
    [DllImport("powrprof.dll")]
    private static extern uint PowerWriteDCValueIndex(IntPtr rootPowerKey, ref Guid schemeGuid, ref Guid subGroup, ref Guid setting, uint value);
    [DllImport("powrprof.dll")] private static extern uint PowerReadValueMin(IntPtr rootPowerKey, ref Guid subGroup, ref Guid setting, out uint value);
    [DllImport("powrprof.dll")] private static extern uint PowerReadValueMax(IntPtr rootPowerKey, ref Guid subGroup, ref Guid setting, out uint value);
    [DllImport("powrprof.dll")]
    private static extern uint PowerReadPossibleValue(IntPtr rootPowerKey, ref Guid subGroup, ref Guid setting, IntPtr type,
        uint index, IntPtr buffer, ref uint bufferSize);
    [DllImport("powrprof.dll")] private static extern bool GetPwrCapabilities(byte[] capabilities);
    [DllImport("kernel32.dll")] private static extern IntPtr LocalFree(IntPtr mem);
    [DllImport("cfgmgr32.dll", CharSet = CharSet.Unicode)]
    private static extern uint CM_Get_Device_Interface_List_SizeW(out uint length, ref Guid interfaceClass, string? deviceId, uint flags);
}
