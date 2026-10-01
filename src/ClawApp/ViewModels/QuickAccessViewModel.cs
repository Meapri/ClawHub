using System.Windows.Threading;
using ClawApp.Services;
using ClawCore.Ec;
using ClawCore.Ipc;
using CommunityToolkit.Mvvm.ComponentModel;

namespace ClawApp.ViewModels;

/// <summary>퀵 설정 패널. 슬라이더 변경은 짧게 모아서(디바운스) 보낸다.</summary>
public sealed partial class QuickAccessViewModel : ObservableObject
{
    private readonly ShellViewModel _shell;
    private readonly DispatcherTimer _perfDebounce;
    private readonly DispatcherTimer _brightDebounce;
    private bool _loading;

    [ObservableProperty] private PerformanceMode _mode = PerformanceMode.Balanced;
    [ObservableProperty] private int _pl1 = 17;
    [ObservableProperty] private int _pl2 = 37;
    [ObservableProperty] private bool _coolerBoost;
    [ObservableProperty] private int _brightness = 50;
    [ObservableProperty] private int _volume = 50;
    [ObservableProperty] private ChargeLimit? _chargeLimit;
    [ObservableProperty] private string _batteryText = "";

    public QuickAccessViewModel(ShellViewModel shell)
    {
        _shell = shell;
        _perfDebounce = Debounce(TimeSpan.FromMilliseconds(500), async () => await SendPerformanceAsync());
        _brightDebounce = Debounce(TimeSpan.FromMilliseconds(120), () => SystemControls.SetBrightness(Brightness));
    }

    public bool PerfAvailable => _shell.Status?.EcWritable == true;
    public string PerfUnavailableText => _shell.ServiceConnected
        ? (_shell.Status?.Warnings.FirstOrDefault() ?? "EC를 사용할 수 없습니다")
        : "ClawHub 서비스가 실행되지 않아 성능 설정을 바꿀 수 없습니다";
    public bool IsManual => Mode == PerformanceMode.Manual;

    public string? PerfNote => _shell.Status?.TemporaryPerformance is { } t
        ? $"지금은 게임별 설정({t.Mode switch { PerformanceMode.Battery => "절전", PerformanceMode.Performance => "성능", _ => "균형" }})이 적용 중 — 여기서 바꾸면 기본값이 바뀝니다"
        : null;

    [CommunityToolkit.Mvvm.Input.RelayCommand]
    private void OpenAllSettings() => _shell.OpenSettingsEverywhere();
    public string TdpText => $"{Pl1}W / 최대 {Pl2}W";
    public bool ChargeLimitAvailable => ChargeLimit is not null;

    // 칩 바인딩용 bool 래퍼
    public bool IsBattery { get => Mode == PerformanceMode.Battery; set { if (value) Mode = PerformanceMode.Battery; } }
    public bool IsBalanced { get => Mode == PerformanceMode.Balanced; set { if (value) Mode = PerformanceMode.Balanced; } }
    public bool IsPerformance { get => Mode == PerformanceMode.Performance; set { if (value) Mode = PerformanceMode.Performance; } }
    public bool IsManualMode { get => Mode == PerformanceMode.Manual; set { if (value) Mode = PerformanceMode.Manual; } }
    public bool Limit100 { get => ChargeLimit == ClawCore.Ec.ChargeLimit.Full; set { if (value) _ = SetChargeLimitAsync(ClawCore.Ec.ChargeLimit.Full); } }
    public bool Limit80 { get => ChargeLimit == ClawCore.Ec.ChargeLimit.Balanced; set { if (value) _ = SetChargeLimitAsync(ClawCore.Ec.ChargeLimit.Balanced); } }
    public bool Limit60 { get => ChargeLimit == ClawCore.Ec.ChargeLimit.Battery; set { if (value) _ = SetChargeLimitAsync(ClawCore.Ec.ChargeLimit.Battery); } }

    /// <summary>매핑 대상: 실행 중 게임, 없으면 오버레이 직전에 앞에 있던 앱.</summary>
    public string? TargetTitle => _shell.RunningGame?.Title ?? _shell.ForegroundApp?.Name;
    public string ProfileText => _shell.RunningGame is { } g
        ? _shell.Profiles.Find(g.MappingProfileId)?.Name ?? "사용 안 함 (게임패드 그대로)"
        : "사용 안 함 — A로 프로필 지정";

    [CommunityToolkit.Mvvm.Input.RelayCommand]
    private void CycleProfile() => _shell.CycleTargetProfile();

    public void Refresh()
    {
        _loading = true;
        try
        {
            if (_shell.Status?.Performance is { } p)
            {
                Mode = p.Mode;
                Pl1 = p.Pl1;
                Pl2 = p.Pl2;
                CoolerBoost = p.Fan == FanMode.CoolerBoost;
            }
            ChargeLimit = _shell.Status?.ChargeLimit;
            var b = SystemControls.GetBattery();
            BatteryText = b.Percent is { } pct ? $"배터리 {pct}%{(b.Charging ? " · 충전 중" : "")}" : "";
        }
        finally
        {
            _loading = false;
        }
        OnPropertyChanged(string.Empty); // 파생 속성 전체 갱신
        _ = LoadSystemValuesAsync();
    }

    /// <summary>밝기(WMI)·볼륨 조회는 수백 ms가 걸려서 패널을 먼저 열고 뒤에서 채운다.</summary>
    private async Task LoadSystemValuesAsync()
    {
        var (brightness, volume) = await Task.Run(() => (SystemControls.GetBrightness(), SystemControls.GetVolume()));
        _loading = true;
        try
        {
            if (brightness is { } br) Brightness = br;
            if (volume is { } v) Volume = v;
        }
        finally
        {
            _loading = false;
        }
    }

    partial void OnModeChanged(PerformanceMode value)
    {
        if (!_loading)
        {
            // 프리셋을 고르면 슬라이더도 그 값으로 보여 준다
            if (value != PerformanceMode.Manual) (Pl1, Pl2) = (PerformanceManager.Preset(value).Pl1, PerformanceManager.Preset(value).Pl2);
            _perfDebounce.Restart();
        }
        OnPropertyChanged(nameof(IsManual));
        OnPropertyChanged(nameof(IsBattery));
        OnPropertyChanged(nameof(IsBalanced));
        OnPropertyChanged(nameof(IsPerformance));
        OnPropertyChanged(nameof(IsManualMode));
    }

    partial void OnPl1Changed(int value)
    {
        if (value > Pl2) Pl2 = value;
        OnPropertyChanged(nameof(TdpText));
        if (!_loading && IsManual) _perfDebounce.Restart();
    }

    partial void OnPl2Changed(int value)
    {
        if (value < Pl1) Pl1 = value;
        OnPropertyChanged(nameof(TdpText));
        if (!_loading && IsManual) _perfDebounce.Restart();
    }

    partial void OnCoolerBoostChanged(bool value)
    {
        if (!_loading) _perfDebounce.Restart();
    }

    partial void OnBrightnessChanged(int value)
    {
        if (!_loading) _brightDebounce.Restart();
    }

    partial void OnVolumeChanged(int value)
    {
        if (!_loading) SystemControls.SetVolume(value);
    }

    partial void OnChargeLimitChanged(ChargeLimit? value)
    {
        OnPropertyChanged(nameof(Limit100));
        OnPropertyChanged(nameof(Limit80));
        OnPropertyChanged(nameof(Limit60));
        OnPropertyChanged(nameof(ChargeLimitAvailable));
    }

    private async Task SendPerformanceAsync()
    {
        var state = new PerformanceState(Mode, Pl1, Pl2, CoolerBoost ? FanMode.CoolerBoost : FanMode.Auto);
        await _shell.SendAsync(new Request { Op = Ops.SetPerformance, Performance = state });
    }

    private async Task SetChargeLimitAsync(ChargeLimit limit)
    {
        if (_loading || limit == ChargeLimit) return;
        if (await _shell.SendAsync(new Request { Op = Ops.SetChargeLimit, ChargeLimit = limit }))
            ChargeLimit = limit;
        else
            OnChargeLimitChanged(ChargeLimit); // 칩 선택 되돌리기
    }

    private static DispatcherTimer Debounce(TimeSpan delay, Action action)
    {
        var t = new DispatcherTimer { Interval = delay };
        t.Tick += (_, _) => { t.Stop(); action(); };
        return t;
    }
}

internal static class DispatcherTimerExtensions
{
    public static void Restart(this DispatcherTimer t)
    {
        t.Stop();
        t.Start();
    }
}
