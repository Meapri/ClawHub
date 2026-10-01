using System.Diagnostics;
using System.Management;
using System.Runtime.InteropServices;
using NAudio.CoreAudioApi;

namespace ClawApp.Services;

/// <summary>사용자 권한으로 되는 시스템 제어: 밝기, 볼륨, 배터리 상태, 전원, 창 포커스.</summary>
public static class SystemControls
{
    public static int? GetBrightness()
    {
        try
        {
            using var s = new ManagementObjectSearcher(@"root\WMI", "SELECT CurrentBrightness FROM WmiMonitorBrightness");
            return s.Get().Cast<ManagementObject>().Select(o => (int?)Convert.ToInt32(o["CurrentBrightness"])).FirstOrDefault();
        }
        catch (Exception) { return null; }
    }

    public static void SetBrightness(int percent)
    {
        try
        {
            using var s = new ManagementObjectSearcher(@"root\WMI", "SELECT * FROM WmiMonitorBrightnessMethods");
            foreach (ManagementObject o in s.Get())
                o.InvokeMethod("WmiSetBrightness", [1u, (byte)Math.Clamp(percent, 0, 100)]);
        }
        catch (Exception) { /* 외부 모니터 등 미지원 */ }
    }

    private static MMDevice? Speaker()
    {
        try { return new MMDeviceEnumerator().GetDefaultAudioEndpoint(DataFlow.Render, Role.Multimedia); }
        catch (Exception) { return null; }
    }

    public static int? GetVolume() => Speaker() is { } d ? (int)Math.Round(d.AudioEndpointVolume.MasterVolumeLevelScalar * 100) : null;

    public static void SetVolume(int percent)
    {
        if (Speaker() is not { } d) return;
        d.AudioEndpointVolume.Mute = percent == 0;
        d.AudioEndpointVolume.MasterVolumeLevelScalar = Math.Clamp(percent, 0, 100) / 100f;
    }

    public readonly record struct BatteryInfo(int? Percent, bool Charging);

    public static BatteryInfo GetBattery()
    {
        if (!GetSystemPowerStatus(out var s)) return new(null, false);
        return new(s.BatteryLifePercent == 255 ? null : s.BatteryLifePercent, s.ACLineStatus == 1);
    }

    public static void Sleep() => SetSuspendState(false, false, false);
    public static void Restart() => Process.Start(new ProcessStartInfo("shutdown", "/r /t 0") { CreateNoWindow = true });
    public static void Shutdown() => Process.Start(new ProcessStartInfo("shutdown", "/s /t 0") { CreateNoWindow = true });

    public static IntPtr ForegroundWindow() => GetForegroundWindow();

    public static void Activate(IntPtr hwnd)
    {
        if (hwnd == IntPtr.Zero) return;
        if (IsIconic(hwnd)) ShowWindow(hwnd, 9 /* SW_RESTORE */);
        SetForegroundWindow(hwnd);
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct SystemPowerStatus
    {
        public byte ACLineStatus, BatteryFlag, BatteryLifePercent, SystemStatusFlag;
        public int BatteryLifeTime, BatteryFullLifeTime;
    }

    [DllImport("kernel32.dll")] private static extern bool GetSystemPowerStatus(out SystemPowerStatus status);
    [DllImport("powrprof.dll")] private static extern bool SetSuspendState(bool hibernate, bool force, bool disableWake);
    [DllImport("user32.dll")] private static extern IntPtr GetForegroundWindow();
    [DllImport("user32.dll")] private static extern bool SetForegroundWindow(IntPtr hwnd);
    [DllImport("user32.dll")] private static extern bool IsIconic(IntPtr hwnd);
    [DllImport("user32.dll")] private static extern bool ShowWindow(IntPtr hwnd, int cmd);
}
