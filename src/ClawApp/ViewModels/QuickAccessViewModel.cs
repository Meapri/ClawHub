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
        Power = new PowerSettingsViewModel(shell);
        _perfDebounce =Debounce(TimeSpan.FromMilliseconds(500), async () => await SendPerformanceAsync());
        _brightDebounce = Debounce(TimeSpan.FromMilliseconds(120), () => SystemControls.SetBrightness(Brightness));

        FpsChoices = [new ChoiceItem("끔", 0), .. ClawCore.Gpu.IntelGraphics.FpsPresets.Select(f => new ChoiceItem($"{f}", f))];
        FrameGenChoices = [new ChoiceItem("끔", 0), new ChoiceItem("2배", 1), new ChoiceItem("3배", 2), new ChoiceItem("4배", 3)];

        // 패널이 열려 있는 동안만 2초마다 온도·전력 갱신
        _telemetryTimer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(2) };
        _telemetryTimer.Tick += async (_, _) =>
        {
            if (!_shell.IsQuickAccessOpen) { _telemetryTimer.Stop(); return; }
            await RefreshTelemetryAsync();
        };
    }

    // ── 실시간 상태 / FPS / 프레임 생성 ─────────────────────

    private readonly DispatcherTimer _telemetryTimer;

    [ObservableProperty] private string _cpuTempText = "–";
    [ObservableProperty] private string _gpuTempText = "–";
    [ObservableProperty] private string _powerText = "–";
    [ObservableProperty] private string _remainingText = "–";

    public bool GpuAvailable => ClawCore.Gpu.IntelGraphics.Available;
    public IReadOnlyList<ChoiceItem> FpsChoices { get; }
    public IReadOnlyList<ChoiceItem> FrameGenChoices { get; }

    /// <summary>Endurance Gaming, 배터리 전용 기본값, 배터리 수명 (설정 화면과 공유).</summary>
    public PowerSettingsViewModel Power { get; }

    public string FpsNote => _shell.RunningGame?.EffectiveFpsLimit(_shell.OnBattery) is { } f
        ? $"지금은 이 게임 전용 값({(f == 0 ? "끔" : f + " FPS")})이 적용 중 — 게임 페이지에서 바꿀 수 있어요"
        : _shell.OnBattery && _shell.Settings.FpsLimitOnBattery is not null
            ? "배터리 사용 중 기본값 (전원을 연결하면 원래 기본값)"
            : "모든 게임에 적용되는 기본값";

    private async Task RefreshTelemetryAsync()
    {
        var (watts, remaining) = await Task.Run(SystemControls.GetBatteryPower);
        RemainingText = remaining is { } r ? (r.TotalHours >= 1 ? $"{(int)r.TotalHours}시간 {r.Minutes}분" : $"{r.Minutes}분") : "충전 중";

        if (!_shell.ServiceConnected) return;
        try
        {
            var resp = await _shell.Service.SendAsync(new ClawCore.Ipc.Request { Op = ClawCore.Ipc.Ops.GetTelemetry }, TimeSpan.FromSeconds(3));
            var t = resp.Telemetry;
            CpuTempText = t?.CpuTemp is { } c ? $"{c}°" : "–";
            // 이 칩(내장 GPU)은 EC가 GPU 온도를 따로 주지 않음 → CPU 패키지 전력 표시 (실기 확인)
            GpuTempText = t?.CpuWatts is { } cw ? $"{cw}W" : "–";
            PowerText = watts is { } w ? $"{w:0.0}W" : "충전 중";
        }
        catch (Exception) { /* 서비스 응답 없음 — 다음 틱에 */ }
    }

    [CommunityToolkit.Mvvm.Input.RelayCommand]
    private async Task PickFps(ChoiceItem item)
    {
        var fps = (int)item.Value;
        if (_shell.OnBattery && _shell.RunningGame is { FpsLimitOnBattery: not null } gb)
        {
            _shell.Library.SetFpsLimitOnBattery(gb, fps); // 배터리 사용 중이면 이 게임의 배터리 전용 값
            await _shell.SyncControllerAsync();
        }
        else if (_shell.RunningGame is { FpsLimit: not null } g)
        {
            _shell.Library.SetFpsLimit(g, fps); // 이 게임 전용 값이 있으면 그걸 바꾼다
            await _shell.SyncControllerAsync();
        }
        else
        {
            await _shell.SetDefaultFpsAsync(fps);
        }
        MarkFps();
    }

    [CommunityToolkit.Mvvm.Input.RelayCommand]
    private void PickFrameGen(ChoiceItem item)
    {
        if (ClawCore.Gpu.IntelGraphics.SetFrameGeneration((int)item.Value))
            foreach (var c in FrameGenChoices) c.IsSelected = c == item;
        else
            _shell.ShowToast("프레임 생성을 바꾸지 못했어요 (드라이버 미지원)");
    }

    private void MarkFps()
    {
        var cur = _shell.CurrentFps;
        foreach (var c in FpsChoices) c.IsSelected = (int)c.Value == cur;
        OnPropertyChanged(nameof(FpsNote));
    }

    public bool PerfAvailable => _shell.Status?.EcWritable == true;
    public string PerfUnavailableText => _shell.ServiceConnected
        ? (_shell.Status?.Warnings.FirstOrDefault() ?? "EC를 사용할 수 없어요")
        : "ClawHub 서비스가 실행되지 않아 성능 설정을 바꿀 수 없어요";
    public bool IsManual => Mode == PerformanceMode.Manual;

    public string? PerfNote => _shell.Status?.TemporaryPerformance is { } t
        ? $"지금은 게임별 설정({t.Mode switch { PerformanceMode.Battery => "절전", PerformanceMode.Performance => "성능", _ => "균형" }})이 적용 중 — 여기서 바꾸면 기본값이 바뀌어요"
        : _shell.Status is { OnBattery: true, PerformanceOnBattery: not null }
            ? "배터리 사용 중 기본값 — 여기서 바꾸면 배터리용 값이 바뀌고, 전원을 연결하면 원래 기본값으로 돌아가요"
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

    /// <summary>매핑 대상: 실행 중 게임, 없으면 본체 버튼으로 런처를 띄우기 직전에 앞에 있던 앱.</summary>
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

        MarkFps();
        var fg = ClawCore.Gpu.IntelGraphics.GetFrameGeneration() ?? 0;
        foreach (var c in FrameGenChoices) c.IsSelected = (int)c.Value == fg;
        _ = RefreshTelemetryAsync();
        _telemetryTimer.Start();
        Power.Refresh();
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
