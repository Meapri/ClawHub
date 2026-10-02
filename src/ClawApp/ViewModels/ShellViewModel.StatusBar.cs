using System.Windows;
using ClawApp.Services;
using ClawCore.Ec;
using ClawCore.Ipc;
using CommunityToolkit.Mvvm.ComponentModel;

namespace ClawApp.ViewModels;

/// <summary>
/// 상단 상태 표시줄: 성능 · CPU 온도 · 컨트롤러 · 네트워크 · 볼륨 · 배터리 · 시계.
/// 아이콘은 모두 Lucide 글리프 문자열(Themes/Icons.xaml)이라 크기·굵기가 한 가지로 맞는다.
/// 시계 타이머(10초)마다 갱신하고, 느린 조회(WLAN·오디오·서비스)는 UI 스레드 밖에서 한다.
/// </summary>
public sealed partial class ShellViewModel
{
    [ObservableProperty] private string _batteryIcon = "";
    [ObservableProperty] private bool _batteryLow;
    [ObservableProperty] private string _networkIcon = "";
    [ObservableProperty] private string? _networkText;
    [ObservableProperty] private string _volumeIcon = "";
    [ObservableProperty] private string? _volumeText;
    [ObservableProperty] private string? _cpuTempText;
    [ObservableProperty] private bool _cpuHot;

    private bool _statusBarBusy;

    private static string Icon(string key) => Application.Current?.TryFindResource($"Icon.{key}") as string ?? "";

    /// <summary>성능 모드 아이콘 (빠른 설정 타일과 같은 그림).</summary>
    public string PerfIcon => Icon(Status?.Performance?.Mode switch
    {
        PerformanceMode.Battery => "Leaf",
        PerformanceMode.Performance => "Rocket",
        PerformanceMode.Manual => "SlidersHorizontal",
        _ => "Scale",
    });

    public string? PerfText => Status?.Performance is { } p
        ? $"{(p.Mode switch { PerformanceMode.Battery => "절전", PerformanceMode.Performance => "성능", PerformanceMode.Manual => "수동", _ => "균형" })} {p.Pl1}W"
        : null;

    public bool ControllerConnected => Status?.ControllerConnected == true;

    partial void OnIsWindowVisibleChanged(bool value)
    {
        if (value) _ = RefreshStatusBarAsync(); // 게임에서 돌아오면 바로 최신 값
        _ = SyncFpsAsync(); // 런처가 보이는 동안은 드라이버 FPS 제한을 푼다
    }

    partial void OnServiceConnectedChanged(bool value) => _ = RefreshStatusBarAsync(); // 온도는 서비스가 붙자마자

    partial void OnStatusChanged(ServiceStatus? value)
    {
        OnPropertyChanged(nameof(PerfIcon));
        OnPropertyChanged(nameof(PerfText));
        OnPropertyChanged(nameof(ControllerConnected));
        OnPropertyChanged(nameof(ControllerStatusText));
        CheckServiceAppState(value);
    }

    private void UpdateBattery()
    {
        var b = SystemControls.GetBattery();
        BatteryText = b.Percent is { } p ? $"{p}%" : "";
        BatteryLow = b.Percent is < 15 && !b.Charging;
        BatteryIcon = b.Percent is not { } pct ? Icon("PlugZap")
            : b.Charging ? Icon("BatteryCharging")
            : pct >= 80 ? Icon("BatteryFull")
            : pct >= 40 ? Icon("BatteryMedium")
            : pct >= 15 ? Icon("BatteryLow")
            : Icon("BatteryWarning");
    }

    private async Task RefreshStatusBarAsync()
    {
        UpdateBattery();
        if (_statusBarBusy || !IsWindowVisible) return; // 게임 중엔 창이 숨어 있으니 조회하지 않는다
        _statusBarBusy = true;
        try
        {
            var (net, volume) = await Task.Run(() => (NetworkStatus.Get(), SystemControls.GetVolume()));
            (NetworkIcon, NetworkText) = net switch
            {
                NetworkStatus.Kind.Wifi => (Icon("Wifi"), "Wi-Fi 연결됨"),
                NetworkStatus.Kind.Ethernet => (Icon("EthernetPort"), "유선 연결됨"),
                _ => (Icon("WifiOff"), "네트워크 연결 안 됨"),
            };
            VolumeIcon = volume switch { null => "", 0 => Icon("VolumeX"), < 50 => Icon("Volume1"), _ => Icon("Volume2") };
            VolumeText = volume?.ToString();

            if (ServiceConnected)
            {
                try
                {
                    var resp = await Service.SendAsync(new Request { Op = Ops.GetTelemetry }, TimeSpan.FromSeconds(3));
                    CpuTempText = resp.Telemetry?.CpuTemp is { } c ? $"{c}°" : null;
                    CpuHot = resp.Telemetry?.CpuTemp >= 90;
                }
                catch (Exception) { CpuTempText = null; } // 서비스 응답 없음 — 다음 틱에
            }
            else
            {
                CpuTempText = null;
            }
        }
        finally
        {
            _statusBarBusy = false;
        }
    }
}
