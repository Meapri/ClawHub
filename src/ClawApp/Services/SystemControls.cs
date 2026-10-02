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

    /// <summary>
    /// 기본 출력 장치로 <paramref name="use"/>를 실행한다 (장치가 없으면 <paramref name="none"/>).
    /// 상태 표시줄이 10초마다 부르므로 COM 개체(열거기·장치·볼륨)는 쓰고 바로 놓는다
    /// (예전에는 GC·종료자가 돌 때까지 쌓였다).
    /// </summary>
    private static T? WithSpeaker<T>(Func<MMDevice, T> use, T? none)
    {
        MMDeviceEnumerator? enumerator = null;
        MMDevice? device = null;
        try
        {
            try
            {
                enumerator = new MMDeviceEnumerator();
                device = enumerator.GetDefaultAudioEndpoint(DataFlow.Render, Role.Multimedia);
            }
            catch (Exception) { return none; } // 출력 장치 없음
            return use(device);
        }
        finally
        {
            device?.Dispose();
            enumerator?.Dispose();
        }
    }

    public static int? GetVolume() =>
        WithSpeaker<int?>(d => (int)Math.Round(d.AudioEndpointVolume.MasterVolumeLevelScalar * 100), null);

    public static void SetVolume(int percent) => WithSpeaker(d =>
    {
        d.AudioEndpointVolume.Mute = percent == 0;
        d.AudioEndpointVolume.MasterVolumeLevelScalar = Math.Clamp(percent, 0, 100) / 100f;
        return true;
    }, false);

    /// <summary>마이크가 켜져 있는지 (기본 녹음 장치가 음소거가 아니면 true, 마이크가 없으면 null).</summary>
    public static bool? IsMicrophoneOn()
    {
        try
        {
            using var e = new MMDeviceEnumerator();
            using var d = e.GetDefaultAudioEndpoint(DataFlow.Capture, Role.Communications);
            return !d.AudioEndpointVolume.Mute;
        }
        catch (Exception) { return null; }
    }

    /// <summary>
    /// 마이크 켜기/끄기: 기본 녹음 장치의 반대 상태로 <b>연결된 모든 마이크</b>를 맞춘다
    /// (Discord가 다른 마이크를 쓰고 있어도 함께 꺼지게). 새 상태(켜짐 = true), 마이크가 없으면 null.
    /// </summary>
    public static bool? ToggleMicrophone()
    {
        if (IsMicrophoneOn() is not { } on) return null;
        var mute = on;
        using var e = new MMDeviceEnumerator();
        foreach (var d in e.EnumerateAudioEndPoints(DataFlow.Capture, DeviceState.Active))
        {
            try { d.AudioEndpointVolume.Mute = mute; }
            catch (Exception) { /* 바꿀 수 없는 장치는 건너뛴다 */ }
            finally { d.Dispose(); }
        }
        return !mute;
    }

    public readonly record struct BatteryInfo(int? Percent, bool Charging);

    public static BatteryInfo GetBattery()
    {
        if (!GetSystemPowerStatus(out var s)) return new(null, false);
        return new(s.BatteryLifePercent == 255 ? null : s.BatteryLifePercent, s.ACLineStatus == 1);
    }

    /// <summary>
    /// 배터리 방전 전력(W)과 남은 시간. 충전 중이면 방전 전력은 null.
    /// Claw의 배터리 드라이버는 방전 속도·남은 시간을 "모름"으로 주므로, 남은 용량(mWh)이 줄어드는 속도로 직접 계산한다
    /// (20초 이상 지켜본 뒤부터 — 상태 표시줄이 10초마다 불러 표본이 쌓인다).
    /// </summary>
    public static (double? Watts, TimeSpan? Remaining) GetBatteryPower()
    {
        double? watts = null;
        int? capacity = null;
        try
        {
            // 빠른 설정이 열려 있는 동안 2초마다 — WMI 결과(COM 개체)는 바로 놓는다
            using var s = new ManagementObjectSearcher(@"root\WMI", "SELECT DischargeRate, RemainingCapacity FROM BatteryStatus");
            using var results = s.Get();
            foreach (ManagementObject o in results)
                using (o)
                {
                    if (Convert.ToInt32(o["DischargeRate"]) is > 0 and var mw) watts = mw / 1000.0;
                    if (Convert.ToInt32(o["RemainingCapacity"]) is > 0 and var cap) capacity = cap;
                }
        }
        catch (Exception) { /* 미지원 */ }

        var status = GetSystemPowerStatus(out var st);
        var charging = status && st.ACLineStatus == 1;
        var estimated = EstimateDischarge(capacity, charging);
        watts ??= estimated;
        TimeSpan? remaining = status && st.BatteryLifeTime > 0 ? TimeSpan.FromSeconds(st.BatteryLifeTime)
            : !charging && watts is > 0.5 && capacity is { } c ? TimeSpan.FromHours(c / 1000.0 / watts.Value) : null;
        if (charging) watts = null;
        return (watts, remaining);
    }

    private static readonly Queue<(long Ms, int MWh)> CapacitySamples = new();
    private static readonly object CapacityGate = new();
    private static double? _lastEstimate;
    private static long _lastEstimateAt = long.MinValue / 2;

    /// <summary>최근 90초 안에서 20초 이상 떨어진 두 표본의 용량 차이로 방전 전력을 잰다.</summary>
    private static double? EstimateDischarge(int? capacity, bool charging)
    {
        lock (CapacityGate)
        {
            if (charging || capacity is not { } now) { CapacitySamples.Clear(); _lastEstimate = null; return null; }
            var t = Environment.TickCount64;
            CapacitySamples.Enqueue((t, now));
            while (CapacitySamples.Count > 0 && t - CapacitySamples.Peek().Ms > 90_000) CapacitySamples.Dequeue();
            var (ms, old) = CapacitySamples.Peek();
            var hours = (t - ms) / 3_600_000.0;
            // 용량은 16mWh 단위로 몇 초마다 바뀌어 잠깐 같은 값일 수 있다 — 그동안은 직전 값을 쓴다 (1분까지)
            if (t - ms < 20_000 || old <= now) return t - _lastEstimateAt < 60_000 ? _lastEstimate : null;
            _lastEstimateAt = t;
            return _lastEstimate = (old - now) / 1000.0 / hours;
        }
    }

    public static void Sleep() => SetSuspendState(false, false, false);
    public static void Restart() => Process.Start(new ProcessStartInfo("shutdown", "/r /t 0") { CreateNoWindow = true });
    public static void Shutdown() => Process.Start(new ProcessStartInfo("shutdown", "/s /t 0") { CreateNoWindow = true });

    /// <summary>Windows 설정 앱의 특정 페이지 열기 (예: "network-wifi", "bluetooth").</summary>
    public static void OpenWindowsSettings(string page) =>
        Process.Start(new ProcessStartInfo($"ms-settings:{page}") { UseShellExecute = true });

    /// <summary>
    /// Windows 터치 키보드 토글 (한글 입력 가능). 문서화되지 않은 ITipInvocation이라 실패하면 osk로 대체.
    /// </summary>
    public static void ShowTouchKeyboard()
    {
        try
        {
            var type = Type.GetTypeFromCLSID(new Guid("4ce576fa-83dc-4F88-951c-9d0782b4e376"))!;
            var tip = (ITipInvocation)Activator.CreateInstance(type)!;
            tip.Toggle(GetDesktopWindow());
        }
        catch (Exception)
        {
            try { Process.Start(new ProcessStartInfo("osk.exe") { UseShellExecute = true }); }
            catch (Exception) { /* 키보드 없음 */ }
        }
    }

    [ComImport, Guid("37c994e7-432b-4834-a2f7-dce1f13b834b"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface ITipInvocation
    {
        void Toggle(IntPtr hwnd);
    }

    [DllImport("user32.dll")] private static extern IntPtr GetDesktopWindow();

    public static IntPtr ForegroundWindow() => GetForegroundWindow();

    /// <summary>앞에 떠 있는 창의 프로세스 ID (없으면 null).</summary>
    public static int? ForegroundProcessId()
    {
        var hwnd = GetForegroundWindow();
        if (hwnd == IntPtr.Zero) return null;
        GetWindowThreadProcessId(hwnd, out var pid);
        return pid == 0 ? null : (int)pid;
    }

    [DllImport("user32.dll")] private static extern uint GetWindowThreadProcessId(IntPtr hwnd, out uint pid);

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
