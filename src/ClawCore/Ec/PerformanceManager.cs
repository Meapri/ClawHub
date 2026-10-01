using System.Runtime.InteropServices;

namespace ClawCore.Ec;

public enum PerformanceMode { Battery, Balanced, Performance, Manual }

public enum FanMode { Auto, CoolerBoost }

public enum ChargeLimit { Full = 100, Balanced = 80, Battery = 60 }

public sealed record PerformanceState(PerformanceMode Mode, int Pl1, int Pl2, FanMode Fan);

/// <summary>1T91 (Claw 8 EX) 성능/팬/배터리 정책. 값은 MSI Center M의 1T91 분기와 동일.</summary>
public sealed class PerformanceManager(MsiWmi wmi)
{
    public const int MinPl = 8;
    public const int MaxPl = 45;

    private static readonly Guid OverlayBestPerformance = new("ded574b5-45a0-4f42-8737-46345c09c238");
    private static readonly Guid OverlayBalanced = Guid.Empty;
    private static readonly Guid OverlayBestEfficiency = new("961cc777-2547-4f9d-8174-7d86181b8a7a");

    public static (int Pl1, int Pl2, byte Shift, Guid Overlay) Preset(PerformanceMode mode) => mode switch
    {
        PerformanceMode.Performance => (30, 37, 0xC4, OverlayBestPerformance),
        PerformanceMode.Balanced => (25, 37, 0xC1, OverlayBalanced),
        PerformanceMode.Battery => (15, 20, 0xC2, OverlayBestEfficiency),
        _ => (17, 37, 0xC6, OverlayBestPerformance),
    };

    public void Apply(PerformanceState state)
    {
        var preset = Preset(state.Mode);
        var (pl1, pl2) = state.Mode == PerformanceMode.Manual ? (state.Pl1, state.Pl2) : (preset.Pl1, preset.Pl2);
        pl2 = Math.Clamp(pl2, MinPl, MaxPl);
        pl1 = Math.Clamp(pl1, MinPl, pl2);

        wmi.SetData(EcIndex.ShiftMode, preset.Shift);
        // MSI와 같은 순서: PL1을 바닥으로 내린 뒤 PL2, 그다음 PL1 최종값 (항상 PL1 ≤ PL2 유지)
        wmi.SetData(EcIndex.Pl1, MinPl);
        wmi.SetData(EcIndex.Pl2, (byte)pl2);
        wmi.SetData(EcIndex.Pl1, (byte)pl1);

        SetCoolerBoost(state.Fan == FanMode.CoolerBoost);
        SetWindowsPowerMode(preset.Overlay);
    }

    private static void SetWindowsPowerMode(Guid mode)
    {
        try
        {
            // MSI와 같은 API (Windows 11). 없으면 구형 오버레이 API로 폴백.
            PowerSetUserConfiguredACPowerMode(mode);
            PowerSetUserConfiguredDCPowerMode(mode);
        }
        catch (EntryPointNotFoundException)
        {
            PowerSetActiveOverlayScheme(mode);
        }
    }

    public void SetCoolerBoost(bool on)
    {
        var v = wmi.GetData(EcIndex.CoolerBoost);
        var next = on ? (byte)(v | 0x80) : (byte)(v & 0x7F);
        if (next != v) wmi.SetData(EcIndex.CoolerBoost, next);
    }

    public bool ChargeLimitSupported => (wmi.GetData(EcIndex.BatteryLimit) & 0x80) != 0;

    public ChargeLimit? GetChargeLimit()
    {
        var v = wmi.GetData(EcIndex.BatteryLimit);
        if ((v & 0x80) == 0) return null;
        return (v & 0x7F) switch { 80 => ChargeLimit.Balanced, 60 => ChargeLimit.Battery, _ => ChargeLimit.Full };
    }

    public void SetChargeLimit(ChargeLimit limit)
    {
        if (!ChargeLimitSupported) throw new NotSupportedException("이 EC는 충전 한도를 지원하지 않습니다");
        wmi.SetData(EcIndex.BatteryLimit, (byte)(0x80 | (int)limit));
    }

    [DllImport("powrprof.dll")]
    private static extern uint PowerSetUserConfiguredACPowerMode(in Guid mode);

    [DllImport("powrprof.dll")]
    private static extern uint PowerSetUserConfiguredDCPowerMode(in Guid mode);

    [DllImport("powrprof.dll")]
    private static extern uint PowerSetActiveOverlayScheme(Guid overlaySchemeGuid);
}
