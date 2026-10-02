using System.Collections.ObjectModel;
using ClawCore.Controller;
using ClawCore.Mapping;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;

namespace ClawApp.ViewModels;

/// <summary>키 선택판 위의 "언제" 칩 (누를 때 · 길게 · 두 번 · 레이어 중 · 원형 메뉴 칸).</summary>
public sealed partial class SlotChoiceViewModel(string id, string label) : ObservableObject
{
    public string Id { get; } = id;
    public string Label { get; } = label;
    [ObservableProperty] private bool _isSelected;
    /// <summary>지금 이 칸에 지정된 키 (없으면 "없음").</summary>
    [ObservableProperty] private string _keys = "";
}

/// <summary>
/// 고급 입력 (2단계, Steam Input식): 버튼마다 길게·두 번 누르기, 연사, 토글, 레이어, 원형 메뉴.
/// 키 선택판은 그대로 쓰고, 위의 칩으로 "언제 낼 키"를 고른다. 프로필에서 켜야 보인다 (안티치트 안내 후).
/// </summary>
public sealed partial class MappingEditorViewModel
{
    private static readonly string[] RadialArrows = ["↑", "↗", "→", "↘", "↓", "↙", "←", "↖"];
    private static readonly int[] TurboSteps = [0, 60, 100, 200];

    public bool AdvancedEnabled => Profile.Advanced?.Enabled == true;
    public string AdvancedText => AdvancedEnabled ? "켜짐 · 길게·두 번·연사·토글·레이어·원형 메뉴" : "꺼짐";

    /// <summary>선택판이 지금 키를 넣는 곳: press · long · double · layer · r0–r7.</summary>
    [ObservableProperty] private string _editingSlot = "press";

    public ObservableCollection<SlotChoiceViewModel> Slots { get; } = [];

    private AdvancedButton? Adv(ClawButton b) => Profile.Advanced?.Buttons.GetValueOrDefault(b);

    private AdvancedButton AdvFor(ClawButton b)
    {
        Profile.Advanced ??= new AdvancedInput { Enabled = true };
        return Profile.Advanced.Buttons.TryGetValue(b, out var a) ? a : Profile.Advanced.Buttons[b] = new AdvancedButton();
    }

    /// <summary>아무 고급 동작도 없는 버튼은 지운다 (펌웨어가 그대로 처리 — 지연·안티치트 영향 없음).</summary>
    private void Prune(ClawButton b)
    {
        if (Profile.Advanced?.Buttons.TryGetValue(b, out var a) == true && !a.NeedsEngine) Profile.Advanced.Buttons.Remove(b);
    }

    /// <summary>엔진이 이 버튼을 맡는지 (그러면 키보드·마우스만 낼 수 있다).</summary>
    private bool EngineHandles(ClawButton b) => Profile.EngineButtons().Contains(b);

    private static int RadialIndex(string slot) => slot.Length > 1 && slot[0] == 'r' && int.TryParse(slot[1..], out var i) ? i : -1;

    /// <summary>선택판이 보여 주고 바꾸는 키 목록 (지금 고른 칸).</summary>
    private List<string> SlotKeys(ClawButton b)
    {
        if (!AdvancedEnabled || EditingSlot == "press") return Current(b);
        var a = Adv(b);
        return EditingSlot switch
        {
            "long" => a?.Long ?? [],
            "double" => a?.Double ?? [],
            "layer" => a?.Layer ?? [],
            _ when RadialIndex(EditingSlot) is var i and >= 0 && a?.Radial is { } sl && i < sl.Count => sl[i].Keys,
            _ => [],
        };
    }

    private void SetSlotKeys(ClawButton b, List<string> list)
    {
        if (!AdvancedEnabled || EditingSlot == "press")
        {
            if (list.Count == 0) Profile.Bindings.Remove(b);
            else Profile.Bindings[b] = list;
            Profile.Macros.Remove(b);
            return;
        }
        var a = AdvFor(b);
        switch (EditingSlot)
        {
            case "long": a.Long = list; break;
            case "double": a.Double = list; break;
            case "layer": a.Layer = list; break;
            default:
                var i = RadialIndex(EditingSlot);
                a.Radial ??= NewRadial();
                if (i >= 0 && i < a.Radial.Count) a.Radial[i] = new RadialSlice { Keys = list };
                break;
        }
        Prune(b);
    }

    /// <summary>
    /// 고급 동작에는 게임패드 버튼을 낼 수 없다 (소프트웨어로 패드 입력을 내려면 3단계 가상 패드가 필요).
    /// 막았으면 true.
    /// </summary>
    private bool RejectPad(ClawButton b, IEnumerable<string> ids)
    {
        if (!ids.Any(id => id.StartsWith("PAD_", StringComparison.Ordinal))) return false;
        if (!AdvancedEnabled || (EditingSlot == "press" && !EngineHandles(b))) return false;
        if (VirtualPadOn)
        {
            // 가상 패드에 없는 버튼 (M1·M2·Camera)
            if (ids.All(id => !id.StartsWith("PAD_", StringComparison.Ordinal) || Input.VirtualPad.CanSend(id))) return false;
            _shell.ShowToast("M1·M2·Camera는 Xbox 패드에 없는 버튼이라 고급 동작에서 낼 수 없어요");
            return true;
        }
        _shell.ShowToast("고급 동작이 있는 버튼은 키보드·마우스 키만 낼 수 있어요 — 옵션에서 '가상 패드'를 켜면 게임패드 버튼도 돼요");
        return true;
    }

    private static List<RadialSlice> NewRadial() => [.. RadialArrows.Select(_ => new RadialSlice())];

    // ── 칩 ─────────────────────────────────────────────────

    /// <summary>선택판을 열 때·동작을 바꿀 때 칩 목록을 다시 만든다.</summary>
    private void RefreshSlots()
    {
        Slots.Clear();
        if (!AdvancedEnabled || EditingRow is not { } row) return;
        var a = Adv(row.Button);
        if (a?.Radial is { } radial)
        {
            for (var i = 0; i < RadialArrows.Length; i++)
                Slots.Add(new SlotChoiceViewModel($"r{i}", RadialArrows[i]) { Keys = Describe(i < radial.Count ? radial[i].Keys : []) });
            if (RadialIndex(EditingSlot) < 0) EditingSlot = "r0";
        }
        else if (a?.LayerShift == true)
        {
            EditingSlot = "press"; // 레이어 버튼 자체는 키를 내지 않는다
        }
        else
        {
            Slots.Add(new SlotChoiceViewModel("press", "누를 때") { Keys = Describe(Current(row.Button)) });
            Slots.Add(new SlotChoiceViewModel("long", "길게") { Keys = Describe(a?.Long ?? []) });
            Slots.Add(new SlotChoiceViewModel("double", "두 번") { Keys = Describe(a?.Double ?? []) });
            Slots.Add(new SlotChoiceViewModel("layer", "레이어 중") { Keys = Describe(a?.Layer ?? []) });
            if (RadialIndex(EditingSlot) >= 0) EditingSlot = "press";
        }
        foreach (var s in Slots) s.IsSelected = s.Id == EditingSlot;
        NotifyBehavior();
    }

    private static string Describe(IReadOnlyList<string> ids) => ids.Count == 0 ? "없음" : string.Join(" + ", ids.Select(Label));

    [RelayCommand]
    private void SelectSlot(SlotChoiceViewModel slot) => EditingSlot = slot.Id;

    partial void OnEditingSlotChanged(string value)
    {
        foreach (var s in Slots) s.IsSelected = s.Id == value;
        if (EditingRow is not { } row) return;
        PrimeModifiers(SlotKeys(row.Button));
        MarkKeys();
        OnPropertyChanged(nameof(EditingSlotCaption));
    }

    /// <summary>선택판 머리: "A 버튼을 길게 누르면".</summary>
    public string EditingSlotCaption
    {
        get
        {
            var name = EditingRow?.Name ?? "";
            if (!AdvancedEnabled) return $"{name} 버튼을 누르면";
            return EditingSlot switch
            {
                "long" => $"{name} 버튼을 길게 누르면",
                "double" => $"{name} 버튼을 두 번 누르면",
                "layer" => $"레이어 버튼을 누른 채 {name}을(를) 누르면",
                _ when RadialIndex(EditingSlot) is var i and >= 0 => $"원형 메뉴에서 {RadialArrows[i]} 칸을 고르면",
                _ when Adv(EditingRow?.Button ?? default)?.LayerShift == true => $"{name} = 레이어 버튼 (누르는 동안 다른 버튼이 '레이어 중' 키)",
                _ => $"{name} 버튼을 누르면",
            };
        }
    }

    /// <summary>알림용 짧은 이름: " 길게", " 원형 ↑" (누를 때는 빈칸).</summary>
    private string SlotShortName => !AdvancedEnabled ? "" : EditingSlot switch
    {
        "long" => " 길게",
        "double" => " 두 번",
        "layer" => " 레이어 중",
        _ when RadialIndex(EditingSlot) is var i and >= 0 => $" 원형 {RadialArrows[i]}",
        _ => "",
    };

    // ── 동작 스위치 (연사 · 토글 · 레이어 버튼 · 원형 메뉴) ──────────────

    public bool EditingTurbo => EditingRow is { } r && Adv(r.Button)?.TurboMs > 0;
    public string TurboText => EditingRow is { } r && Adv(r.Button)?.TurboMs is > 0 and var ms
        ? ms <= 60 ? "연사 · 빠르게" : ms <= 100 ? "연사 · 보통" : "연사 · 느리게"
        : "연사";
    public bool EditingToggle => EditingRow is { } r && Adv(r.Button)?.Toggle == true;
    public bool EditingLayerShift => EditingRow is { } r && Adv(r.Button)?.LayerShift == true;
    public bool EditingRadial => EditingRow is { } r && Adv(r.Button)?.Radial is not null;

    private void NotifyBehavior()
    {
        OnPropertyChanged(nameof(EditingTurbo));
        OnPropertyChanged(nameof(TurboText));
        OnPropertyChanged(nameof(EditingToggle));
        OnPropertyChanged(nameof(EditingLayerShift));
        OnPropertyChanged(nameof(EditingRadial));
        OnPropertyChanged(nameof(EditingSlotCaption));
    }

    /// <summary>바꾼 뒤 공통: 저장 · 칩 · 선택판 표시.</summary>
    private void AfterBehavior(ClawButton b)
    {
        Prune(b);
        Save();
        RefreshSlots();
        MarkKeys();
        WarnPadPress(b);
    }

    /// <summary>"누를 때"가 게임패드 버튼인데 엔진이 맡게 됐으면 알려 준다.</summary>
    private void WarnPadPress(ClawButton b)
    {
        if (EngineHandles(b) && !VirtualPadOn && Current(b).Any(id => id.StartsWith("PAD_", StringComparison.Ordinal)))
            _shell.ShowToast("'누를 때'가 게임패드 버튼이라 나오지 않아요 — 키보드·마우스 키로 바꿔 주세요");
    }

    [RelayCommand]
    private void CycleTurbo()
    {
        if (EditingRow is not { } row) return;
        Remember();
        var a = AdvFor(row.Button);
        var i = Array.IndexOf(TurboSteps, a.TurboMs);
        a.TurboMs = TurboSteps[(i + 1) % TurboSteps.Length];
        if (a.TurboMs > 0) a.Toggle = false; // 연사와 토글은 함께 쓰지 않는다
        AfterBehavior(row.Button);
        if (a.TurboMs > 0 && (a.Long.Count > 0 || a.Double.Count > 0))
            _shell.ShowToast("길게·두 번 누르기가 있으면 연사는 쓰이지 않아요");
    }

    [RelayCommand]
    private void ToggleToggle()
    {
        if (EditingRow is not { } row) return;
        Remember();
        var a = AdvFor(row.Button);
        a.Toggle = !a.Toggle;
        if (a.Toggle) a.TurboMs = 0;
        AfterBehavior(row.Button);
        if (a.Toggle) _shell.ShowToast($"{row.Name}: 한 번 누르면 계속 눌린 채, 다시 누르면 떼요");
    }

    [RelayCommand]
    private void ToggleLayerShift()
    {
        if (EditingRow is not { } row) return;
        Remember();
        var a = AdvFor(row.Button);
        a.LayerShift = !a.LayerShift;
        if (a.LayerShift)
        {
            a.Radial = null;
            EditingSlot = "press";
            _shell.ShowToast($"{row.Name}을(를) 누르고 있는 동안 다른 버튼이 '레이어 중' 키를 내요");
        }
        AfterBehavior(row.Button);
    }

    [RelayCommand]
    private void ToggleRadial()
    {
        if (EditingRow is not { } row) return;
        Remember();
        var a = AdvFor(row.Button);
        if (a.Radial is null)
        {
            a.Radial = NewRadial();
            a.LayerShift = false;
            EditingSlot = "r0";
            _shell.ShowToast("누르고 있으면 메뉴가 뜨고, 오른쪽 스틱으로 고른 뒤 떼면 그 칸의 키를 눌러요");
        }
        else
        {
            a.Radial = null;
            EditingSlot = "press";
        }
        AfterBehavior(row.Button);
    }

    // ── 프로필 단위 켜기 ───────────────────────────────────

    [RelayCommand(AllowConcurrentExecutions = true)]
    private async Task ToggleAdvanced()
    {
        if (!AdvancedEnabled && !await _shell.ConfirmAsync("고급 입력을 켤까요?",
                "길게·두 번 누르기, 연사, 토글, 레이어, 원형 메뉴를 ClawHub가 소프트웨어로 처리해요. " +
                "이런 동작을 넣은 버튼은 키보드·마우스 입력만 낼 수 있고, 일부 온라인 게임의 안티치트는 소프트웨어 입력을 막거나 문제 삼을 수 있어요. " +
                "동작을 넣지 않은 버튼은 지금처럼 펌웨어가 처리해요.",
                "켜기"))
            return;
        Remember();
        Profile.Advanced ??= new AdvancedInput();
        Profile.Advanced.Enabled = !Profile.Advanced.Enabled;
        Save();
        RefreshAdvanced();
        _shell.ShowToast(AdvancedEnabled ? "고급 입력을 켰어요 — 버튼을 고르면 위에 '언제' 칩이 생겨요" : "고급 입력을 껐어요 (설정은 남아 있어요)");
    }

    // ── 3단계 가상 패드 ────────────────────────────────────

    public bool VirtualPadOn => Profile.Advanced?.UsesVirtualPad == true;
    public bool VirtualPadDrivers => _shell.Status?.VirtualPadDrivers == true;
    public string VirtualPadText => !VirtualPadDrivers ? "드라이버가 설치돼 있지 않아요" : VirtualPadOn ? "켜짐 · 게임에는 ClawHub 가상 패드" : "꺼짐";

    public IReadOnlyList<TuningChoice<StickOutput>> LeftStickChoices { get; } = StickChoices();
    public IReadOnlyList<TuningChoice<StickOutput>> RightStickChoices { get; } = StickChoices();

    private static IReadOnlyList<TuningChoice<StickOutput>> StickChoices() =>
        [new("스틱 그대로", StickOutput.Stick), new("WASD", StickOutput.Wasd), new("방향키", StickOutput.Arrows), new("마우스", StickOutput.Mouse)];

    public int MouseSpeed
    {
        get => Profile.Advanced?.MouseSpeed ?? 40;
        set
        {
            if (Profile.Advanced is not { } a || a.MouseSpeed == value) return;
            RememberOnce("mouse-speed");
            a.MouseSpeed = Math.Clamp(value, 1, 100);
            Save();
            OnPropertyChanged();
        }
    }

    public bool UsesStickMouse => Profile.Advanced is { UsesVirtualPad: true } a && (a.LeftStick == StickOutput.Mouse || a.RightStick == StickOutput.Mouse);

    [RelayCommand(AllowConcurrentExecutions = true)]
    private async Task ToggleVirtualPad()
    {
        if (!VirtualPadOn)
        {
            if (!VirtualPadDrivers)
            {
                _shell.ShowToast("ViGEmBus·HidHide 드라이버를 먼저 설치해 주세요");
                return;
            }
            if (!await _shell.ConfirmAsync("가상 패드를 쓸까요?",
                    "이 프로필로 게임을 하는 동안 실제 패드를 게임에서 숨기고 ClawHub가 만든 가상 Xbox 패드를 대신 보여 줘요. " +
                    "고급 동작에서 게임패드 버튼을 낼 수 있고 스틱을 키·마우스로 바꿀 수 있어요. " +
                    "게임을 시작할 때 패드가 1–2초 다시 연결되고, 일부 온라인 게임 안티치트는 가상 패드를 막을 수 있어요.",
                    "켜기"))
                return;
        }
        Remember();
        Profile.Advanced ??= new AdvancedInput();
        if (!VirtualPadOn) { Profile.Advanced.Enabled = true; Profile.Advanced.VirtualPad = true; }
        else Profile.Advanced.VirtualPad = false;
        Save();
        RefreshAdvanced();
        _shell.ShowToast(VirtualPadOn ? "가상 패드를 켰어요 — 다음에 이 프로필로 게임을 시작할 때부터 적용돼요" : "가상 패드를 껐어요");
        if (_shell.RunningGame is not null) await _shell.SyncControllerAsync();
    }

    [RelayCommand]
    private void PickLeftStick(TuningChoice<StickOutput> c) => PickStick(c, left: true);

    [RelayCommand]
    private void PickRightStick(TuningChoice<StickOutput> c) => PickStick(c, left: false);

    private void PickStick(TuningChoice<StickOutput> c, bool left)
    {
        if (Profile.Advanced is not { } a) return;
        Remember();
        if (left) a.LeftStick = c.Value;
        else a.RightStick = c.Value;
        Save();
        RefreshAdvanced();
    }

    private void MarkSticks()
    {
        var a = Profile.Advanced;
        foreach (var c in LeftStickChoices) c.IsSelected = c.Value == (a?.LeftStick ?? StickOutput.Stick);
        foreach (var c in RightStickChoices) c.IsSelected = c.Value == (a?.RightStick ?? StickOutput.Stick);
        OnPropertyChanged(nameof(UsesStickMouse));
        OnPropertyChanged(nameof(MouseSpeed));
    }

    private void RefreshAdvanced()
    {
        OnPropertyChanged(nameof(VirtualPadOn));
        OnPropertyChanged(nameof(VirtualPadText));
        MarkSticks();
        OnPropertyChanged(nameof(AdvancedEnabled));
        OnPropertyChanged(nameof(AdvancedText));
        if (!AdvancedEnabled) EditingSlot = "press";
        RefreshSlots();
        RefreshRows();
        NotifyBehavior();
    }

    /// <summary>버튼 카드의 고급 동작 꼬리표 + 짧게 눌러도 아무것도 안 나오는 경우 경고.</summary>
    private void MarkAdvanced()
    {
        foreach (var row in AllRows)
        {
            if (!AdvancedEnabled || Adv(row.Button) is not { } a || !EngineHandles(row.Button))
            {
                row.Advanced = null;
                continue;
            }
            var parts = new List<string>();
            if (a.LayerShift) parts.Add("레이어 버튼");
            if (a.Radial is { } sl) parts.Add($"원형 메뉴 {sl.Count(s => s.Keys.Count > 0)}칸");
            if (a.Long.Count > 0) parts.Add("길게 " + Describe(a.Long));
            if (a.Double.Count > 0) parts.Add("두 번 " + Describe(a.Double));
            if (a.TurboMs > 0) parts.Add("연사");
            if (a.Toggle) parts.Add("토글");
            if (a.Layer.Count > 0) parts.Add("레이어 " + Describe(a.Layer));
            row.Advanced = string.Join(" · ", parts);
            row.IsMapped = true;

            if (row.Conflict is not null) continue;
            var press = Current(row.Button);
            if (!a.LayerShift && a.Radial is null && press.Count == 0)
                row.Conflict = "짧게 누르면 아무것도 안 나와요 — '누를 때' 키를 정해 주세요";
            else if (!VirtualPadOn && press.Any(id => id.StartsWith("PAD_", StringComparison.Ordinal)))
                row.Conflict = "'누를 때' 게임패드 버튼은 고급 동작 중 나오지 않아요";
        }
    }
}
