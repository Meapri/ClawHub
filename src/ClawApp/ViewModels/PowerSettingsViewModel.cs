using System.ComponentModel;
using ClawCore.Ec;
using ClawCore.Gpu;
using ClawCore.Ipc;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;

namespace ClawApp.ViewModels;

/// <summary>
/// 전원 관련 설정: Intel Endurance Gaming(Auto TDP), 배터리 전용 기본값(성능·FPS), 배터리 수명(읽기 전용).
/// 설정 화면(<see cref="SettingsViewModel.Power"/>)과 앱 안 빠른 설정(<see cref="QuickAccessViewModel.Power"/>)이 같은 인스턴스를 쓴다.
/// 하드웨어 쓰기는 전부 서비스 경유 (Game Bar 위젯도 같은 요청을 쓴다).
/// </summary>
public sealed partial class PowerSettingsViewModel : ObservableObject
{
    private readonly ShellViewModel _shell;

    public PowerSettingsViewModel(ShellViewModel shell)
    {
        _shell = shell;
        BatteryFpsChoices =
        [
            new ChoiceItem("따로 안 씀", -1),
            new ChoiceItem("끔", 0),
            .. IntelGraphics.FpsPresets.Select(f => new ChoiceItem($"{f}", f)),
        ];
        shell.PropertyChanged += OnShellChanged;
        MarkBatteryFps();
    }

    private void OnShellChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName is nameof(ShellViewModel.Status) or nameof(ShellViewModel.ServiceConnected) or nameof(ShellViewModel.OnBattery))
            Refresh();
    }

    /// <summary>서비스 상태가 바뀌거나 화면이 열릴 때. 칩 선택 상태를 서비스 값으로 되돌린다.</summary>
    public void Refresh()
    {
        MarkBatteryFps();
        OnPropertyChanged(string.Empty);
    }

    private ServiceStatus? Status => _shell.Status;

    public string PowerSourceText => _shell.OnBattery ? "지금: 배터리 사용 중" : "지금: 전원 연결됨";

    // ── Intel Endurance Gaming ───────────────────────────

    private EnduranceState? Endurance => Status?.Graphics?.Endurance;

    /// <summary>예전 서비스는 Endurance를 보내지 않는다 → 사용 불가로 표시.</summary>
    public bool EnduranceAvailable => _shell.ServiceConnected && Endurance?.Available == true;

    public string EnduranceNote => !_shell.ServiceConnected ? "ClawHub 서비스가 실행되지 않아 바꿀 수 없습니다"
        : Endurance is null ? "서비스를 업데이트해야 쓸 수 있습니다"
        : !Endurance.Available ? "Intel 그래픽 드라이버 설정(MSI Center M의 intelGEDll.dll)을 찾을 수 없습니다"
        : Endurance.Source is null ? "현재 값을 드라이버에서 읽지 못했습니다"
        : Endurance.Source == "Saved" ? "드라이버에서 읽을 수 없는 조합이라 마지막으로 정한 값을 표시합니다"
        : "자동이면 배터리로 돌 때만 드라이버가 프레임·전력을 줄입니다";

    public bool EgOff { get => Endurance?.Control == EnduranceControl.Off; set { if (value) _ = SetEnduranceAsync(EnduranceControl.Off, null); } }
    public bool EgOn { get => Endurance?.Control == EnduranceControl.On; set { if (value) _ = SetEnduranceAsync(EnduranceControl.On, null); } }
    public bool EgAuto { get => Endurance?.Control == EnduranceControl.Auto; set { if (value) _ = SetEnduranceAsync(EnduranceControl.Auto, null); } }

    /// <summary>강도는 켜짐/자동일 때만 의미가 있다.</summary>
    public bool EgModeVisible => Endurance?.Control is EnduranceControl.On or EnduranceControl.Auto;

    public bool EgPerformance { get => Endurance?.Mode == EnduranceMode.Performance; set { if (value) _ = SetEnduranceModeAsync(EnduranceMode.Performance); } }
    public bool EgBalanced { get => Endurance?.Mode == EnduranceMode.Balanced; set { if (value) _ = SetEnduranceModeAsync(EnduranceMode.Balanced); } }
    public bool EgMaxBattery { get => Endurance?.Mode == EnduranceMode.MaxBattery; set { if (value) _ = SetEnduranceModeAsync(EnduranceMode.MaxBattery); } }

    private Task SetEnduranceModeAsync(EnduranceMode mode) => SetEnduranceAsync(Endurance?.Control ?? EnduranceControl.Auto, mode);

    private async Task SetEnduranceAsync(EnduranceControl control, EnduranceMode? mode)
    {
        if (!EnduranceAvailable) { Refresh(); return; }
        if (Endurance is { } cur && cur.Control == control && (mode is null || cur.Mode == mode)) return;
        await _shell.SendAsync(new Request { Op = Ops.SetEnduranceGaming, EnduranceControl = control, EnduranceMode = mode });
        Refresh(); // 실패하면 칩을 서비스 값으로 되돌림
    }

    // ── 배터리 전용 기본 성능 ─────────────────────────────

    /// <summary>예전 서비스는 PowerSource를 모르므로(기본값을 덮어씀) PerformanceOnAc가 올 때만 허용.</summary>
    public bool BatteryPerfAvailable => Status is { EcWritable: true, PerformanceOnAc: not null };

    private PerformanceState? BatteryPerf => Status?.PerformanceOnBattery;

    public bool BatPerfNone { get => BatteryPerf is null; set { if (value) _ = SetBatteryPerfAsync(null); } }
    public bool BatPerfBattery { get => BatteryPerf?.Mode == PerformanceMode.Battery; set { if (value) _ = SetBatteryPerfAsync(PerformanceMode.Battery); } }
    public bool BatPerfBalanced { get => BatteryPerf?.Mode == PerformanceMode.Balanced; set { if (value) _ = SetBatteryPerfAsync(PerformanceMode.Balanced); } }
    public bool BatPerfPerformance { get => BatteryPerf?.Mode == PerformanceMode.Performance; set { if (value) _ = SetBatteryPerfAsync(PerformanceMode.Performance); } }

    public string BatteryPerfText => BatteryPerf switch
    {
        null => "배터리여도 기본 성능 모드를 그대로 씁니다",
        { Mode: PerformanceMode.Manual } p => $"배터리일 때: 수동 {p.Pl1}W / 최대 {p.Pl2}W",
        _ => "배터리로 돌 때 이 모드로 바꾸고, 전원을 연결하면 기본 성능 모드로 돌아옵니다",
    };

    private async Task SetBatteryPerfAsync(PerformanceMode? mode)
    {
        if (!BatteryPerfAvailable) { Refresh(); return; }
        if (mode == BatteryPerf?.Mode) return;
        PerformanceState? state = mode is { } m
            ? new PerformanceState(m, PerformanceManager.Preset(m).Pl1, PerformanceManager.Preset(m).Pl2,
                Status?.PerformanceOnAc?.Fan ?? FanMode.Auto)
            : null; // null + PowerSource Battery = 배터리 전용 값 지우기
        await _shell.SendAsync(new Request { Op = Ops.SetPerformance, Performance = state, PowerSource = PowerSourceKind.Battery });
        Refresh();
        _shell.QuickAccess.Refresh(); // 지금 배터리면 퀵 설정의 모드 표시도 바뀐다
    }

    // ── 배터리 전용 기본 FPS 제한 (드라이버 설정, 앱이 적용) ──────

    public bool GpuAvailable => IntelGraphics.Available;
    public IReadOnlyList<ChoiceItem> BatteryFpsChoices { get; }

    public string BatteryFpsText => _shell.Settings.FpsLimitOnBattery switch
    {
        null => "배터리여도 기본 FPS 제한을 그대로 씁니다",
        _ => "게임별 값이 있으면 그게 우선입니다 (게임 페이지에서 게임별 배터리 값도 정할 수 있어요)",
    };

    [RelayCommand]
    private async Task PickBatteryFps(ChoiceItem item)
    {
        var v = (int)item.Value;
        await _shell.SetDefaultFpsOnBatteryAsync(v < 0 ? null : v);
        MarkBatteryFps();
        OnPropertyChanged(nameof(BatteryFpsText));
    }

    private void MarkBatteryFps()
    {
        var cur = _shell.Settings.FpsLimitOnBattery ?? -1;
        foreach (var c in BatteryFpsChoices) c.IsSelected = (int)c.Value == cur;
    }

    // ── 배터리 수명 (읽기 전용) ───────────────────────────

    private BatteryHealth? Health => Status?.BatteryHealth;

    public bool HasHealth => Health is { } h && (h.HealthPercent ?? h.FullChargeCapacityMWh ?? h.DesignedCapacityMWh) is not null;
    public bool NoHealth => !HasHealth;

    public string HealthUnavailableText => _shell.ServiceConnected
        ? "배터리 정보를 읽는 중이거나 이 기기에서 읽을 수 없습니다"
        : "ClawHub 서비스가 실행되지 않아 배터리 정보를 읽을 수 없습니다";

    public string HealthPercentText => Health?.HealthPercent is { } p ? $"{p}%" : "–";

    public string HealthSummary => Health?.HealthPercent switch
    {
        null => "설계 용량을 알 수 없어 수명을 계산하지 못했습니다",
        >= 90 => "좋음 — 거의 새 배터리 수준이에요",
        >= 75 => "보통 — 정상적으로 닳은 상태예요",
        >= 60 => "다소 닳음 — 사용 시간이 눈에 띄게 줄었을 수 있어요",
        _ => "많이 닳음 — 교체를 고려해 보세요",
    };

    public string DesignedText => Wh(Health?.DesignedCapacityMWh);
    public string FullChargeText => Wh(Health?.FullChargeCapacityMWh);
    public string CycleText => Health?.CycleCount is { } c ? $"{c}회" : "알 수 없음";
    public string ManufacturerText => Health?.Manufacturer ?? "–";

    private static string Wh(int? mWh) => mWh is { } v ? $"{v / 1000.0:0.0} Wh" : "–";
}
