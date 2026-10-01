using System.Runtime.InteropServices;

namespace ClawCore.Power;

/// <summary>전원 연결 상태. 서비스(LocalSystem)와 앱 양쪽에서 쓴다.</summary>
public static class PowerSource
{
    /// <summary>배터리로 돌고 있는지. 알 수 없으면(255) 전원 연결로 본다 — 배터리 전용 설정은 확실할 때만.</summary>
    public static bool OnBattery => GetSystemPowerStatus(out var s) && s.ACLineStatus == 0;

    [StructLayout(LayoutKind.Sequential)]
    private struct SystemPowerStatus
    {
        public byte ACLineStatus, BatteryFlag, BatteryLifePercent, SystemStatusFlag;
        public int BatteryLifeTime, BatteryFullLifeTime;
    }

    [DllImport("kernel32.dll")]
    private static extern bool GetSystemPowerStatus(out SystemPowerStatus status);
}
