using System.Diagnostics;
using ClawApp.Services;
using ClawApp.Views;
using ClawCore.Ipc;

namespace ClawApp.ViewModels;

/// <summary>
/// 성능 오버레이 (SteamOS처럼 4단계: 끔 · FPS · 한 줄 · 자세히). 빠른 설정·위젯·버튼 매핑(SYS_OVERLAY)으로 단계를 바꾼다.
/// 켜져 있는 동안만 1초(자세히는 0.5초)마다 값을 모은다: FPS·프레임 시간·온도·CPU 전력은 서비스(PresentMon·EC),
/// CPU·GPU 사용률·클럭·메모리는 앱(성능 카운터), 배터리는 앱. 허브 화면 전체가 보일 때는 가린다.
/// </summary>
public sealed partial class ShellViewModel
{
    public static readonly string[] OverlayLevelNames = ["끔", "FPS", "한 줄", "자세히"];

    private PerfOverlayWindow? _overlayWindow;
    private CancellationTokenSource? _overlayCts;
    private TaskCompletionSource? _overlayKick;

    public int OverlayLevel => Math.Clamp(Settings.PerfOverlayLevel, 0, 3);

    /// <summary>단계가 바뀌었다 (빠른 설정 칩 표시 맞추기).</summary>
    public event Action? OverlayLevelChanged;

    public void SetOverlayLevel(int level, bool notify = false)
    {
        level = Math.Clamp(level, 0, 3);
        if (level != Settings.PerfOverlayLevel)
        {
            Settings.PerfOverlayLevel = level;
            Settings.Save();
            OverlayLevelChanged?.Invoke();
            _ = PushAppStateAsync(); // 위젯이 고른 단계를 보여 준다
        }
        if (notify) Notify("Icon.Gauge", level == 0 ? "성능 오버레이 꺼짐" : $"성능 오버레이 · {OverlayLevelNames[level]}");
        if (level > 0) { StartOverlay(); _overlayKick?.TrySetResult(); } // 이미 돌고 있으면 기다리지 않고 바로 다시 그린다
        else StopOverlay();
    }

    public void CycleOverlayLevel() => SetOverlayLevel((OverlayLevel + 1) % 4, notify: true);

    /// <summary>앱 시작 때: 지난번에 켜 두었으면 다시 켠다.</summary>
    private void InitOverlay()
    {
        if (OverlayLevel > 0) StartOverlay();
    }

    private void StartOverlay()
    {
        if (_overlayCts is not null) return;
        _overlayCts = new CancellationTokenSource();
        _ = OverlayLoopAsync(_overlayCts.Token);
    }

    private void StopOverlay()
    {
        _overlayCts?.Cancel();
        _overlayCts = null;
        _overlayWindow?.Hide();
    }

    private async Task OverlayLoopAsync(CancellationToken ct)
    {
        using var metrics = await Task.Run(() => new SystemMetrics());
        var window = _overlayWindow ??= new PerfOverlayWindow();
        (double? Watts, TimeSpan? Remaining) battery = (null, null);
        var batteryAt = DateTime.MinValue;
        while (!ct.IsCancellationRequested)
        {
            var level = OverlayLevel;
            if (level == 0) break;
            if (IsWindowVisible && !IsQuickAccessOpen)
            {
                window.Hide(); // 허브 화면이 앞에 있으면 가린다 (빠른 설정만 열렸을 때는 단계 바꾸는 걸 보이게 둔다)
                metrics.Sample(gpu: false); // 카운터 기준값은 계속 (다시 보일 때 첫 값이 바로 나오게)
            }
            else
            {
                var sample = await Task.Run(() => metrics.Sample(gpu: level >= 2), ct);
                var pid = await Task.Run(OverlayTargetPid, ct);
                Telemetry? t = null;
                if (ServiceConnected)
                {
                    try { t = (await Service.SendAsync(new Request { Op = Ops.GetTelemetry, FramePid = pid ?? 0 }, TimeSpan.FromSeconds(2))).Telemetry; }
                    catch (Exception) { /* 서비스 응답 없음 — 다음에 */ }
                }
                // 배터리 방전 전력은 WMI라 2초에 한 번만
                if (level >= 2 && DateTime.UtcNow - batteryAt > TimeSpan.FromSeconds(2))
                {
                    battery = await Task.Run(SystemControls.GetBatteryPower, ct);
                    batteryAt = DateTime.UtcNow;
                }
                if (ct.IsCancellationRequested) break;
                var b = SystemControls.GetBattery();
                window.Update(level, new PerfOverlayData(
                    t?.Fps, t?.FpsLow1, t?.FrameTimeMs, t?.FrameTimes,
                    sample.CpuPercent, sample.CpuMhz, t?.CpuTemp, sample.GpuPercent,
                    t?.CpuWatts, b.Charging ? null : battery.Watts, b.Percent, b.Charging, b.Charging ? null : battery.Remaining,
                    sample.RamUsedGb, sample.RamTotalGb, CurrentFps));
            }
            _overlayKick = new TaskCompletionSource();
            try { await Task.WhenAny(Task.Delay(level >= 3 ? 500 : 1000, ct), _overlayKick.Task); }
            catch (OperationCanceledException) { break; }
        }
        if (_overlayCts is null || _overlayCts.Token == ct) window.Hide();
    }

    private static readonly HashSet<string> ShellProcesses = new(StringComparer.OrdinalIgnoreCase)
    {
        "explorer", "ShellExperienceHost", "StartMenuExperienceHost", "SearchHost", "TextInputHost", "GameBar",
        "XboxGameBarWidgets", "ClawWidget", "LockApp", "ApplicationFrameHost", "dwm",
    };

    private int? _overlayPid;

    /// <summary>
    /// FPS를 잴 앞 창 프로세스: 우리 창·작업 표시줄·Game Bar 같은 셸 창이 앞이면 직전 대상을 그대로 (빠른 설정을 열어도 게임 FPS가 이어지게).
    /// 직전 대상이 끝났으면 null.
    /// </summary>
    private int? OverlayTargetPid()
    {
        if (SystemControls.ForegroundProcessId() is { } pid && pid != Environment.ProcessId && pid != _overlayPid)
        {
            try
            {
                using var p = Process.GetProcessById(pid);
                if (!ShellProcesses.Contains(p.ProcessName)) _overlayPid = pid;
            }
            catch (ArgumentException) { /* 그사이 끝남 */ }
        }
        if (_overlayPid is { } last)
        {
            try
            {
                using var p = Process.GetProcessById(last);
                if (p.HasExited) _overlayPid = null;
            }
            catch (Exception ex) when (ex is ArgumentException or InvalidOperationException) { _overlayPid = null; }
        }
        return _overlayPid;
    }
}
