using System.Collections.ObjectModel;
using System.ComponentModel;
using ClawCore.Ipc;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;

namespace ClawApp.ViewModels;

/// <summary>
/// Windows 전원 구성표 설정 (화면 끄기, 절전, 최대 절전, 전원 단추, 절약 모드, 밝기 자동 조절)과
/// "게임 중 화면 꺼짐·절전 방지". 설정 화면(<see cref="SettingsViewModel.PowerPlan"/>)과
/// 앱 안 빠른 설정(<see cref="QuickAccessViewModel.PowerPlan"/>)이 같은 인스턴스를 쓴다.
/// 값은 서비스(Status.PowerPlan)가 주고, 쓰기도 서비스 경유 (Game Bar 위젯도 같은 요청).
/// </summary>
public sealed partial class PowerPlanViewModel : ObservableObject
{
    private readonly ShellViewModel _shell;
    private bool _busy;

    public PowerPlanViewModel(ShellViewModel shell)
    {
        _shell = shell;
        DisplayOff = new(this, PowerPlanSetting.DisplayOff, "화면 끄기", "입력이 없을 때 화면을 끄는 시간");
        Sleep = new(this, PowerPlanSetting.Sleep, "절전 전환", "입력이 없을 때 절전 모드로 들어가는 시간 (화면 끄기보다 짧게 하면 화면 끄기도 같이 줄어요)");
        Hibernate = new(this, PowerPlanSetting.Hibernate, "최대 절전 전환", "절전한 뒤 이 시간이 지나면 최대 절전으로 — 배터리를 거의 쓰지 않아요");
        PowerButton = new(this, PowerPlanSetting.PowerButton, "전원 버튼을 누르면", "본체 전원 버튼을 짧게 눌렀을 때 할 일");
        EnergySaverThreshold = new(this, PowerPlanSetting.EnergySaverThreshold, "절약 모드 자동 켜짐", "배터리가 이만큼 남으면 Windows 절약 모드를 켜요");
        EnergySaverBrightness = new(this, PowerPlanSetting.EnergySaverBrightness, "절약 모드 화면 밝기", "절약 모드일 때 평소 밝기의 몇 %로 낮출지");
        AdaptiveBrightness = new(this, PowerPlanSetting.AdaptiveBrightness, "밝기 자동 조절", "주변 밝기에 맞춰 화면 밝기를 바꿔요");
        StandbyNetwork = new(this, PowerPlanSetting.StandbyNetwork, "대기 중 네트워크",
            "화면이 꺼져 대기하는 동안 Wi-Fi를 유지할지 — 끊으면 대기 중 배터리를 훨씬 덜 쓰고, 유지하면 메시지·다운로드가 계속돼요");
        WakeTimers = new(this, PowerPlanSetting.WakeTimers, "절전 해제 타이머", "예약 작업(업데이트 등)이 절전 중인 기기를 깨울 수 있게 할지");
        Rows = [DisplayOff, Sleep, Hibernate, PowerButton, EnergySaverThreshold, EnergySaverBrightness, AdaptiveBrightness, StandbyNetwork, WakeTimers];
        shell.PropertyChanged += OnShellChanged;
        Refresh();
    }

    private void OnShellChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName is nameof(ShellViewModel.Status) or nameof(ShellViewModel.ServiceConnected) or nameof(ShellViewModel.OnBattery))
            Refresh();
        else if (e.PropertyName == nameof(ShellViewModel.KeepingAwake))
            OnPropertyChanged(nameof(KeepAwakeText));
    }

    public PowerPlanRow DisplayOff { get; }
    public PowerPlanRow Sleep { get; }
    public PowerPlanRow Hibernate { get; }
    public PowerPlanRow PowerButton { get; }
    public PowerPlanRow EnergySaverThreshold { get; }
    public PowerPlanRow EnergySaverBrightness { get; }
    public PowerPlanRow AdaptiveBrightness { get; }
    public PowerPlanRow StandbyNetwork { get; }
    public PowerPlanRow WakeTimers { get; }

    // ── 기기 전체 기능 (최대 절전 모드 · 빠른 시작) ──────────────

    public bool HibernateFeature
    {
        get => Plan?.HibernateEnabled == true;
        set => _ = SetFeatureAsync(PowerFeature.Hibernate, value);
    }

    public bool FastStartup
    {
        get => Plan?.FastStartupEnabled == true;
        set => _ = SetFeatureAsync(PowerFeature.FastStartup, value);
    }

    public bool CanFastStartup => Available && Plan?.HibernateEnabled == true;

    private async Task SetFeatureAsync(PowerFeature feature, bool on)
    {
        if (_busy || !Available) { OnPropertyChanged(string.Empty); return; }
        _busy = true;
        try
        {
            await _shell.SendAsync(new Request { Op = Ops.SetPowerFeature, PowerFeature = feature, Enabled = on });
        }
        finally
        {
            _busy = false;
            Refresh(); // 실패하면 서비스 값으로 되돌림
        }
    }
    private IReadOnlyList<PowerPlanRow> Rows { get; }

    private PowerPlanState? Plan => _shell.Status?.PowerPlan;
    internal bool OnBattery => _shell.OnBattery;

    /// <summary>예전 서비스는 PowerPlan을 보내지 않는다 → 사용 불가로 표시.</summary>
    public bool Available => _shell.ServiceConnected && Plan?.Available == true;
    public bool NotAvailable => !Available;

    public string Note => !_shell.ServiceConnected ? "ClawHub 서비스가 실행되지 않아 바꿀 수 없어요"
        : Plan is null ? "서비스를 업데이트해야 쓸 수 있어요"
        : !Plan.Available ? Plan.Error ?? "전원 설정을 읽지 못했어요"
        : $"Windows 전원 구성표 '{Plan.SchemeName ?? "알 수 없음"}'{(Plan.ModernStandby ? " · 모던 스탠바이 기기" : "")} — Windows 설정과 같은 값이에요";

    public string CurrentSourceText => OnBattery ? "지금: 배터리 사용 중" : "지금: 충전 중";
    /// <summary>빠른 설정용: 지금 전원 상태 칸을 바꾼다는 안내.</summary>
    public string QuickSourceText => OnBattery ? "배터리 사용 중 값" : "충전 중 값";

    // ── 게임 중 화면 꺼짐·절전 방지 (앱이 직접) ─────────────

    public bool KeepAwakeWhileGaming
    {
        get => _shell.Settings.KeepAwakeWhileGaming;
        set
        {
            if (_shell.Settings.KeepAwakeWhileGaming == value) return;
            _shell.Settings.KeepAwakeWhileGaming = value;
            _shell.Settings.Save();
            _shell.UpdateKeepAwake();
            OnPropertyChanged();
            OnPropertyChanged(nameof(KeepAwakeText));
        }
    }

    public string KeepAwakeText => _shell.KeepingAwake
        ? "지금 게임 중이라 화면이 꺼지지 않게 막고 있어요"
        : "게임 화면에 있는 동안 화면이 꺼지거나 절전되지 않게 해요 (ClawHub로 돌아오면 원래대로)";

    // ── 서비스 값 반영 / 쓰기 ─────────────────────────────

    /// <summary>서비스 상태가 바뀔 때. 칩은 값이 같으면 그대로 두고 선택만 바꾼다 (패드 포커스 유지).</summary>
    public void Refresh()
    {
        var plan = Plan;
        var enabled = Available && !_busy;
        foreach (var row in Rows)
        {
            var s = plan?.Settings.FirstOrDefault(x => x.PowerSetting == row.Setting);
            var rowEnabled = enabled && !(row.Setting == PowerPlanSetting.Hibernate && plan?.HibernateEnabled == false);
            row.Update(s, rowEnabled);
        }
        Hibernate.ExtraNote = plan is { HibernateEnabled: false }
            ? "최대 절전 모드가 꺼져 있어요 (관리자 권한으로 powercfg /h on 을 실행하면 켤 수 있어요)"
            : null;
        OnPropertyChanged(string.Empty);
    }

    /// <summary>화면이 열릴 때 서비스에 캐시 없이 다시 읽게 한다 (Windows 설정에서 바꾼 값 반영). 실패는 조용히 무시.</summary>
    public async Task ReloadAsync()
    {
        if (!_shell.ServiceConnected) return;
        try
        {
            var resp = await _shell.Service.SendAsync(new Request { Op = Ops.GetPowerPlan }, TimeSpan.FromSeconds(3));
            if (resp.Status is { } s) _shell.Status = s;
        }
        catch (Exception) { /* 연결 끊김 — 다음 상태 때 */ }
    }

    /// <summary>한 줄의 한 칸만 펼친다 (다른 줄은 접는다). null이면 접기.</summary>
    internal void Expand(PowerPlanRow row, PowerSourceKind? source)
    {
        foreach (var r in Rows)
            if (r != row) r.Expanded = null;
        row.Expanded = source;
    }

    internal async Task SetAsync(PowerPlanRow row, PowerPlanChip chip)
    {
        if (_busy || !row.IsEnabled || chip.Value == row.CurrentValue(chip.Source))
        {
            row.Remark(); // 이미 선택된 칩을 눌러 꺼진 모양이 되면 되돌린다
            row.Expanded = null; // 지금 값을 다시 고른 것 = 확인
            return;
        }
        row.Expanded = null; // 고르면 바로 접는다 (값 버튼에 새 값이 보인다)
        _busy = true;
        try
        {
            await _shell.SendAsync(new Request
            {
                Op = Ops.SetPowerPlanSetting,
                PowerSetting = row.Setting,
                PowerSource = chip.Source,
                Value = chip.Value,
            });
        }
        finally
        {
            _busy = false;
            Refresh(); // 실패하면 서비스 값으로 되돌림
        }
    }
}

/// <summary>전원 설정 한 줄. 충전 중(AC)·배터리(DC) 칩 목록을 따로 가진다.</summary>
public sealed partial class PowerPlanRow(PowerPlanViewModel owner, PowerPlanSetting setting, string title, string description) : ObservableObject
{
    private PowerPlanSettingState? _state;

    public PowerPlanSetting Setting { get; } = setting;
    public string Title { get; } = title;
    public string Description { get; } = description;

    /// <summary>이 기기에 있는 설정인지 (없으면 줄을 숨긴다).</summary>
    public bool IsAvailable => _state is not null;
    public bool IsEnabled { get; private set; }
    /// <summary>false면 배터리 칸만 (절약 모드 항목) — 충전 중 칸에는 안내 문구.</summary>
    public bool AcApplies => _state?.AcApplies ?? true;
    public bool AcNotApplicable => !AcApplies;
    public string AcText => _state?.AcText ?? "–";
    public string DcText => _state?.DcText ?? "–";
    public string? ExtraNote { get; set; }
    public bool HasExtraNote => ExtraNote is not null;

    public ObservableCollection<PowerPlanChip> AcChoices { get; } = [];
    public ObservableCollection<PowerPlanChip> DcChoices { get; } = [];

    /// <summary>빠른 설정용: 지금 전원 상태 칸의 칩.</summary>
    public ObservableCollection<PowerPlanChip> CurrentChoices => owner.OnBattery || !AcApplies ? DcChoices : AcChoices;
    public string CurrentText => owner.OnBattery || !AcApplies ? DcText : AcText;

    // ── 설정 화면: 값 버튼 두 개(충전 중·배터리) + 누른 칸의 칩만 펼침 ──

    /// <summary>칩을 펼친 칸 (null = 접힘).</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsExpanded), nameof(ExpandedChoices), nameof(AcExpanded), nameof(DcExpanded))]
    private PowerSourceKind? _expanded;

    public bool IsExpanded => Expanded is not null;
    public bool AcExpanded => Expanded == PowerSourceKind.Ac;
    public bool DcExpanded => Expanded == PowerSourceKind.Battery;
    public IEnumerable<PowerPlanChip> ExpandedChoices => Expanded switch
    {
        PowerSourceKind.Ac => AcChoices,
        PowerSourceKind.Battery => DcChoices,
        _ => [],
    };

    /// <summary>지금 적용되는 칸 ("지금" 표시).</summary>
    public bool AcIsCurrent => !owner.OnBattery && AcApplies;
    public bool DcIsCurrent => owner.OnBattery || !AcApplies;

    [RelayCommand]
    private void Expand(PowerSourceKind source) => owner.Expand(this, Expanded == source ? null : source);

    internal int? CurrentValue(PowerSourceKind source) => source == PowerSourceKind.Ac ? _state?.Ac : _state?.Dc;

    internal void Update(PowerPlanSettingState? state, bool enabled)
    {
        _state = state;
        IsEnabled = enabled && state is not null;
        if (state is not null)
        {
            Sync(AcChoices, state.Choices, PowerSourceKind.Ac, state.Ac);
            Sync(DcChoices, state.Choices, PowerSourceKind.Battery, state.Dc);
        }
        OnPropertyChanged(string.Empty);
    }

    /// <summary>선택 표시를 서비스 값으로 다시 맞춘다.</summary>
    internal void Remark()
    {
        if (_state is null) return;
        foreach (var c in AcChoices) c.Mark(c.Value == _state.Ac);
        foreach (var c in DcChoices) c.Mark(c.Value == _state.Dc);
    }

    private static void Sync(ObservableCollection<PowerPlanChip> chips, List<PowerPlanChoice> choices, PowerSourceKind source, int current)
    {
        var same = chips.Count == choices.Count && chips.Zip(choices).All(p => p.First.Value == p.Second.Value && p.First.Label == p.Second.Label);
        if (!same)
        {
            chips.Clear();
            foreach (var c in choices) chips.Add(new PowerPlanChip(c.Label, c.Value, source));
        }
        foreach (var c in chips) c.Mark(c.Value == current);
    }

    [RelayCommand]
    private Task Pick(PowerPlanChip chip) => owner.SetAsync(this, chip);
}

/// <summary>칩 하나. IsSelected는 값이 같아도 항상 알린다 — 눌러서 꺼진 모양이 된 ToggleButton을 되돌리기 위해.</summary>
public sealed class PowerPlanChip(string label, int value, PowerSourceKind source) : ObservableObject
{
    private bool _isSelected;

    public string Label { get; } = label;
    public int Value { get; } = value;
    public PowerSourceKind Source { get; } = source;
    public bool IsSelected => _isSelected;

    internal void Mark(bool selected)
    {
        _isSelected = selected;
        OnPropertyChanged(nameof(IsSelected));
    }
}
