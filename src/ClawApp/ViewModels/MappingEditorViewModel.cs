using System.Collections.ObjectModel;
using ClawApp.Input;
using ClawCore.Controller;
using ClawCore.Mapping;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;

namespace ClawApp.ViewModels;

/// <summary>줄 안의 키캡 하나. 두 번째부터 앞에 "+".</summary>
public sealed record KeycapItem(string Label, bool ShowPlus)
{
    public static IReadOnlyList<KeycapItem> From(IEnumerable<string> labels) => [.. labels.Select((l, i) => new KeycapItem(l, i > 0))];
}

/// <summary>본체 버튼 한 줄: 무엇이 나가는지 키캡으로 보여 준다.</summary>
public sealed partial class ButtonRowViewModel(ClawButton button, string name, bool groupStart = false) : ObservableObject
{
    public ClawButton Button { get; } = button;
    public string Name { get; } = name;
    /// <summary>묶음(트리거·방향·스틱·뒷면)의 첫 줄 → 위에 간격.</summary>
    public bool GroupStart { get; } = groupStart;
    /// <summary>꼬리표용 짧은 이름 ("A", "↑", "M1").</summary>
    public string Short => Button switch
    {
        ClawButton.DpadUp => "↑", ClawButton.DpadDown => "↓", ClawButton.DpadLeft => "←", ClawButton.DpadRight => "→",
        _ => Button.ToString(),
    };

    [ObservableProperty] private IReadOnlyList<KeycapItem> _keyLabels = [];
    [ObservableProperty] private bool _isMapped;
    [ObservableProperty] private bool _isMacro;
    /// <summary>키캡 대신 쓰는 설명 ("게임패드 그대로", "매크로 · 3단계").</summary>
    [ObservableProperty] private string _summary = "";
    /// <summary>바뀔 때마다 키캡이 다시 미끄러져 들어온다 (Entrance.Key).</summary>
    [ObservableProperty] private int _version;
    /// <summary>테스트 중 이 버튼의 키가 눌려 있음.</summary>
    [ObservableProperty] private bool _isLive;
    /// <summary>화면의 첫 카드 — 이 페이지에 처음 오면 여기에 포커스.</summary>
    public bool IsFirst { get; set; }
    /// <summary>같은 키를 내는 다른 버튼이 있으면 "A와 같은 키" (없으면 null).</summary>
    [ObservableProperty] private string? _conflict;
    /// <summary>고급 동작 요약 (길게 Esc · 연사 …), 없으면 null.</summary>
    [ObservableProperty] private string? _advanced;
}

/// <summary>"함께 누를 키" 토글 (Ctrl·Shift·Alt·Win).</summary>
public sealed partial class ModifierViewModel(string id, string label) : ObservableObject
{
    public string Id { get; } = id;
    public string Label { get; } = label;
    [ObservableProperty] private bool _isOn;
}

/// <summary>키 선택판의 탭.</summary>
public sealed partial class PickerTabViewModel(string label, string icon) : ObservableObject
{
    public string Label { get; } = label;
    /// <summary>Lucide 글리프 (Themes/Icons.xaml의 Icon.&lt;이름&gt;).</summary>
    public string Icon { get; } = System.Windows.Application.Current?.TryFindResource("Icon." + icon) as string ?? "";
    [ObservableProperty] private bool _isSelected;
}

/// <summary>
/// 버튼 → 키 매핑 편집. 본체처럼 왼손·오른손 버튼을 양옆에, 가운데에 프로필 정보와 동작을 둔다.
/// 키 선택은 실제 키보드 모양 판에서 한 번 누르면 지정되고 바로 돌아온다 (보조 키는 위의 토글로 조합).
/// 변경은 즉시 저장된다.
/// </summary>
public sealed partial class MappingEditorViewModel : ObservableObject, INavPage
{
    // 순서 = 본체에서 위 → 아래 (사진 둘레 카드의 안내선이 서로 엇갈리지 않게): 왼쪽은 스틱이 방향 버튼보다 위
    private static readonly ButtonRowViewModel[] LeftLayout =
    [
        new(ClawButton.LT, "LT"), new(ClawButton.LB, "LB"),
        new(ClawButton.L3, "왼쪽 스틱 누름", true),
        new(ClawButton.DpadUp, "방향 ↑", true), new(ClawButton.DpadLeft, "방향 ←"), new(ClawButton.DpadRight, "방향 →"), new(ClawButton.DpadDown, "방향 ↓"),
        new(ClawButton.M1, "M1 · 뒷면", true),
    ];

    private static readonly ButtonRowViewModel[] RightLayout =
    [
        new(ClawButton.RT, "RT"), new(ClawButton.RB, "RB"),
        new(ClawButton.Y, "Y", true), new(ClawButton.X, "X"), new(ClawButton.B, "B"), new(ClawButton.A, "A"),
        new(ClawButton.R3, "오른쪽 스틱 누름", true), new(ClawButton.M2, "M2 · 뒷면", true),
    ];

    /// <summary>LB/RB로 넘기는 키 선택판 탭.</summary>
    public IReadOnlyList<PickerTabViewModel> Tabs { get; } =
    [
        new("키보드", "Keyboard"), new("마우스", "MousePointer"), new("조합·미디어", "Layers"), new("게임패드", "Gamepad2"),
    ];

    /// <summary>"함께 누를 키" 순서 = 바인딩에 들어가는 순서.</summary>
    public IReadOnlyList<ModifierViewModel> Modifiers { get; } =
        [new("KB_LCTRL", "Ctrl"), new("KB_LSHIFT", "Shift"), new("KB_LALT", "Alt"), new("KB_WIN", "Win")];

    private readonly ShellViewModel _shell;

    public MappingProfile Profile { get; }
    public IReadOnlyList<ButtonRowViewModel> LeftRows { get; }
    public IReadOnlyList<ButtonRowViewModel> RightRows { get; }
    private IEnumerable<ButtonRowViewModel> AllRows => LeftRows.Concat(RightRows);

    public List<List<KeySlotViewModel>> KeyboardRows { get; } = KeyboardLayout.Build();
    public List<KeySlotViewModel> MouseKeys { get; } = KeyboardLayout.Mouse();
    public List<KeySlotViewModel> MediaKeys { get; } = KeyboardLayout.Media();
    public List<KeySlotViewModel> GamepadKeys { get; } = KeyboardLayout.Gamepad();
    private IEnumerable<KeySlotViewModel> AllSlots =>
        KeyboardRows.SelectMany(r => r).Concat(MouseKeys).Concat(MediaKeys).Concat(GamepadKeys).Where(s => !s.IsSpacer);

    [ObservableProperty] private bool _isPickerOpen;
    [ObservableProperty] private ButtonRowViewModel? _editingRow;
    [ObservableProperty] private int _categoryIndex;
    [ObservableProperty] private string _comboText = "";
    /// <summary>여러 키 모드: 누를 때마다 넣고 빼며 선택판에 머문다 (최대 5개).</summary>
    [ObservableProperty] private bool _multiKey;

    public MappingEditorViewModel(ShellViewModel shell, MappingProfile profile)
    {
        _shell = shell;
        Profile = profile;
        // 줄 객체는 화면마다 새로 (정적 배열은 배치 정의로만 쓴다)
        LeftRows = [.. LeftLayout.Select(r => new ButtonRowViewModel(r.Button, r.Name, r.GroupStart))];
        RightRows = [.. RightLayout.Select(r => new ButtonRowViewModel(r.Button, r.Name, r.GroupStart))];
        LeftRows[0].IsFirst = true;
        Tabs[0].IsSelected = true;
        RefreshRows();
        MarkSticks();
    }

    public string Title => $"매핑 · {Profile.Name}";
    public IReadOnlyList<Hint> Hints => IsTesting
        ? [new("B", "테스트 끝내기")]
        : IsListening
            ? [] // 어떤 버튼이든 "찾을 버튼"으로 쓰이므로 힌트를 비운다 (가운데 안내 참고)
            : IsOptionsOpen || IsTuningOpen
                ? [new("A", "선택"), new("B", "닫기")]
            : IsPickerOpen
                ? [new("A", MultiKey ? "넣기/빼기" : "지정"), new("B", MultiKey ? "완료" : "취소"), new("X", "게임패드 그대로"),
                   new("Y", MultiKey ? "한 키씩" : "여러 키"), new("LB", "이전 탭"), new("RB", "다음 탭")]
                : [new("A", "키 지정"), new("X", "지우기"), new("Y", "15초 테스트"), new("RB", "눌러서 찾기"),
                   .. CanUndo ? new Hint[] { new("LB", "되돌리기") } : [], new("B", "뒤로")];

    /// <summary>TextBox에서 벗어날 때 저장된다.</summary>
    public string ProfileName
    {
        get => Profile.Name;
        set
        {
            if (Profile.IsSystem || string.IsNullOrWhiteSpace(value) || value == Profile.Name) return; // 기본·데스크톱은 이름 고정
            Profile.Name = value.Trim();
            Save();
            OnPropertyChanged();
            OnPropertyChanged(nameof(Title));
            _shell.OnPropertyChangedPageTitle();
        }
    }

    /// <summary>기본·데스크톱 프로필은 이름 고정 · 지울 수 없음.</summary>
    public bool CanEditName => !IsTesting && !Profile.IsSystem;
    public bool CanDelete => !Profile.IsSystem;
    public string ModeTitle => Profile.Id == MappingProfile.DesktopId ? "데스크톱에서 컨트롤러 모드" : "게임 중 컨트롤러 모드";

    public string ModeText => Profile.Mode == GamepadMode.Desktop ? "데스크톱 (스틱 = 마우스)" : "XInput (기본)";
    public string SummaryText
    {
        get
        {
            var mapped = AllRows.Count(r => r.IsMapped);
            return mapped == 0 ? "아직 지정한 버튼이 없어요" : $"버튼 {mapped}개를 키로 바꿔요 · 나머지는 게임패드 그대로";
        }
    }

    private List<string> Current(ClawButton b) => Profile.Bindings.TryGetValue(b, out var l) ? l : [];
    private static string Label(string id) => KeyCodes.Find(id)?.Label ?? id;

    private void RefreshRows()
    {
        foreach (var row in AllRows)
        {
            IReadOnlyList<KeycapItem> labels;
            if (Profile.Macros.TryGetValue(row.Button, out var macro) && macro.Count > 0)
            {
                row.IsMacro = true;
                row.IsMapped = true;
                row.Summary = $"매크로 · {macro.Count}단계";
                labels = [];
            }
            else
            {
                var ids = Current(row.Button);
                row.IsMacro = false;
                row.IsMapped = ids.Count > 0;
                row.Summary = ids.Count > 0 ? "" : "게임패드 그대로";
                labels = KeycapItem.From(ids.Select(Label));
            }
            if (!labels.SequenceEqual(row.KeyLabels))
            {
                row.KeyLabels = labels;
                row.Version++;
            }
        }
        MarkConflicts();
        MarkAdvanced();
        OnPropertyChanged(nameof(SummaryText));
    }

    private void Save()
    {
        _shell.Profiles.Upsert(Profile);
        RefreshRows();
    }

    // ── 키 선택판 ──────────────────────────────────────────

    [RelayCommand]
    private void EditRow(ButtonRowViewModel row)
    {
        StopListening();
        IsOptionsOpen = false;
        IsTuningOpen = false;
        EditingRow = row;
        FocusedButton = row.Button;
        EditingSlot = "press";
        RefreshSlots(); // 고급 입력: 원형 메뉴 버튼이면 첫 칸으로
        PrimeModifiers(SlotKeys(row.Button));
        MarkKeys();
        IsPickerOpen = true; // 포커스는 뷰가 옮긴다 (선택된 키 또는 첫 키)
        _shell.UpdateHints();
    }

    /// <summary>지금 키의 보조 키를 토글에 미리 켜 두고 맞는 탭을 연다 (Ctrl+Q → Ctrl 켜짐, Q만 다시 고르면 됨).</summary>
    private void PrimeModifiers(List<string> current)
    {
        var others = current.Where(id => Modifiers.All(m => m.Id != id)).ToList();
        foreach (var m in Modifiers) m.IsOn = others.Count > 0 && current.Contains(m.Id);
        MultiKey = current.Count > 2 || others.Count > 1; // 자유 조합이던 버튼은 그 모드로
        CategoryIndex = current.Select(KeyCodes.Find).FirstOrDefault(k => k is not null && Modifiers.All(m => m.Id != k.Id))?.Category switch
        {
            KeyCategory.Mouse => 1,
            KeyCategory.Media => 2,
            KeyCategory.Gamepad => 3,
            _ => 0,
        };
    }

    partial void OnCategoryIndexChanged(int value)
    {
        for (var i = 0; i < Tabs.Count; i++) Tabs[i].IsSelected = i == value;
    }

    partial void OnMultiKeyChanged(bool value) => _shell.UpdateHints();

    /// <summary>선택 표시 + "다른 버튼이 쓰는 키" 꼬리표 + 위쪽 미리보기.</summary>
    private void MarkKeys()
    {
        if (EditingRow is not { } row) return;
        var selected = SlotKeys(row.Button);
        var usedBy = new Dictionary<string, List<string>>();
        foreach (var other in AllRows.Where(r => r != row))
            foreach (var id in Current(other.Button))
                (usedBy.TryGetValue(id, out var l) ? l : usedBy[id] = []).Add(other.Short);

        foreach (var slot in AllSlots)
        {
            slot.IsSelected = selected.Contains(slot.Key!.Id);
            slot.UsedBy = usedBy.TryGetValue(slot.Key.Id, out var names) ? string.Join("·", names) : null;
        }
        foreach (var c in ComboPresets) c.IsSelected = selected.SequenceEqual(c.Combo.Keys);
        ComboText = selected.Count > 0 ? string.Join(" + ", selected.Select(Label)) : "게임패드 그대로";
        foreach (var s in Slots) s.Keys = s.Id == EditingSlot ? Describe(selected) : s.Keys;
        OnPropertyChanged(nameof(EditingKeyLabels));
        OnPropertyChanged(nameof(EditingHasKeys));
        OnPropertyChanged(nameof(EditingEmptyText));
    }

    /// <summary>선택판 위쪽 미리보기 키캡.</summary>
    public IReadOnlyList<KeycapItem> EditingKeyLabels => EditingRow is { } r ? KeycapItem.From(SlotKeys(r.Button).Select(Label)) : [];
    /// <summary>키가 없을 때: 누를 때 = 게임패드 그대로, 다른 칸 = 없음.</summary>
    public string EditingEmptyText => !AdvancedEnabled || EditingSlot == "press" ? "게임패드 그대로" : "없음";
    public bool EditingHasKeys => EditingKeyLabels.Count > 0;

    [RelayCommand]
    private void SelectTab(PickerTabViewModel tab) => CategoryIndex = Math.Max(0, Tabs.ToList().IndexOf(tab));

    [RelayCommand]
    private void ToggleModifier(ModifierViewModel mod)
    {
        if (EditingRow is not { } row) return;
        if (MultiKey)
        {
            // 여러 키 모드에서는 바로 넣고 뺀다
            mod.IsOn = Toggle(row.Button, mod.Id);
            return;
        }
        mod.IsOn = !mod.IsOn;
        // 이미 일반 키가 있으면 그 키와 다시 묶어서 바로 반영 (Q → Ctrl 켬 → Ctrl+Q)
        var main = SlotKeys(row.Button).Where(id => Modifiers.All(m => m.Id != id)).ToList();
        if (main.Count == 1) Assign(row, main[0], close: false);
    }

    [RelayCommand]
    private void PickKey(KeySlotViewModel slot)
    {
        if (EditingRow is not { } row || slot.Key is null) return;
        if (MultiKey) Toggle(row.Button, slot.Key.Id);
        else Assign(row, slot.Key.Id, close: !AdvancedEnabled || (EditingSlot == "press" && Adv(row.Button) is null)); // 고급 칸은 이어서 고르게 머문다
    }

    /// <summary>보조 키 토글 + 이 키로 바인딩을 바꾼다. close면 버튼 목록으로 돌아간다.</summary>
    private void Assign(ButtonRowViewModel row, string keyId, bool close)
    {
        var list = Modifiers.Where(m => m.IsOn && m.Id != keyId).Select(m => m.Id).ToList();
        list.Add(keyId);
        if (RejectPad(row.Button, list)) return;
        if (!SlotKeys(row.Button).SequenceEqual(list) || Profile.Macros.ContainsKey(row.Button)) Remember();
        SetSlotKeys(row.Button, list); // 누를 때 칸이면 매크로 대신 키
        Save();
        MarkKeys();
        if (!close) return;
        _shell.ShowToast($"{row.Name}{SlotShortName} → {string.Join(" + ", list.Select(Label))}");
        ClosePicker();
    }

    private bool Toggle(ClawButton button, string id)
    {
        var list = SlotKeys(button).ToList();
        if (!list.Remove(id))
        {
            if (list.Count >= ProfileLayout.MaxKeysPerBinding)
            {
                _shell.ShowToast($"한 버튼에 최대 {ProfileLayout.MaxKeysPerBinding}개까지 동시에 누를 수 있어요");
                return false;
            }
            if (RejectPad(button, [id])) return false;
            list.Add(id);
        }
        Remember();
        SetSlotKeys(button, list);
        Save();
        MarkKeys();
        return list.Contains(id);
    }

    [RelayCommand]
    private void ClearEditing()
    {
        if (EditingRow is not { } row) return;
        if (AdvancedEnabled && EditingSlot != "press")
        {
            // 고급 칸만 비운다 (누를 때 키는 그대로)
            if (SlotKeys(row.Button).Count > 0) Remember();
            SetSlotKeys(row.Button, []);
            Save();
            RefreshSlots();
            MarkKeys();
            return;
        }
        ResetRow(row.Button);
        _shell.ShowToast($"{row.Name} → 게임패드 그대로");
        ClosePicker();
    }

    public bool EditingIsMKey => EditingRow?.Button is ClawButton.M1 or ClawButton.M2;

    partial void OnEditingRowChanged(ButtonRowViewModel? value)
    {
        OnPropertyChanged(nameof(EditingIsMKey));
        OnPropertyChanged(nameof(EditingSlotCaption));
    }

    [RelayCommand]
    private void OpenMacro()
    {
        if (EditingRow is not { } row) return;
        IsPickerOpen = false;
        _shell.Navigate(new MacroEditorViewModel(_shell, Profile, row.Button));
    }

    public void OnShown() => RefreshRows(); // 매크로 편집에서 돌아왔을 때

    [RelayCommand]
    private void ClosePicker()
    {
        IsPickerOpen = false; // 뷰가 고르던 버튼 줄로 포커스를 돌려준다
        _shell.UpdateHints();
    }

    private void ResetRow(ClawButton button)
    {
        if (Profile.Bindings.ContainsKey(button) || Profile.Macros.ContainsKey(button)) Remember();
        Profile.Bindings.Remove(button);
        Profile.Macros.Remove(button);
        Save();
        MarkKeys();
    }

    // ── 빠른 채우기 ─────────────────────────────────────────

    [RelayCommand]
    private void FillDpad(string kind)
    {
        string[] ids = kind == "wasd" ? ["KB_W", "KB_S", "KB_A", "KB_D"] : ["KB_UP", "KB_DOWN", "KB_LEFT", "KB_RIGHT"];
        ClawButton[] pads = [ClawButton.DpadUp, ClawButton.DpadDown, ClawButton.DpadLeft, ClawButton.DpadRight];
        Remember();
        for (var i = 0; i < 4; i++) Profile.Bindings[pads[i]] = [ids[i]];
        Save();
        _shell.ShowToast(kind == "wasd" ? "방향 버튼 → W A S D" : "방향 버튼 → 방향키");
    }

    public IReadOnlyList<MappingProfile> Presets { get; } = MappingPresets.All;
    [RelayCommand(AllowConcurrentExecutions = true)]
    private async Task ApplyPreset(MappingProfile preset)
    {
        if (!await _shell.ConfirmAsync($"'{preset.Name}' 배치를 불러올까요?", "지금 프로필의 버튼 배치를 모두 덮어써요.", "불러오기", danger: true)) return;
        Remember();
        Profile.Bindings = preset.Bindings.ToDictionary(kv => kv.Key, kv => kv.Value.ToList());
        Profile.Mode = preset.Mode;
        Save();
        OnPropertyChanged(nameof(ModeText));
        _shell.ShowToast($"'{preset.Name}' 배치를 불러왔어요");
    }

    // ── 프로필 단위 ───────────────────────────────────────

    [RelayCommand]
    private void ToggleMode()
    {
        Remember();
        Profile.Mode = Profile.Mode == GamepadMode.XInput ? GamepadMode.Desktop : GamepadMode.XInput;
        Save();
        OnPropertyChanged(nameof(ModeText));
    }

    [RelayCommand]
    private void Duplicate()
    {
        var copy = Profile.Clone();
        _shell.Profiles.Upsert(copy);
        _shell.ShowToast($"'{copy.Name}' 프로필을 만들었어요");
        _shell.GoBack();
        _shell.Navigate(new MappingEditorViewModel(_shell, copy));
    }

    [RelayCommand(AllowConcurrentExecutions = true)]
    private async Task Delete()
    {
        if (Profile.IsSystem) return; // 기본·데스크톱 프로필은 지울 수 없다
        if (!await _shell.ConfirmAsync($"'{Profile.Name}' 프로필을 삭제할까요?", "이 프로필을 쓰던 게임은 기본 프로필로 실행돼요. 되돌릴 수 없어요.", "삭제", danger: true)) return;
        _shell.Profiles.Delete(Profile.Id);
        foreach (var g in _shell.Library.Games.Where(g => g.MappingProfileId == Profile.Id))
            _shell.Library.SetProfile(g, null);
        _shell.GoBack();
    }

    public bool Handle(NavAction action)
    {
        if (IsListening)
        {
            // 찾는 중에 화면 이동(스틱·키보드·힌트 바)이 오면 찾기를 그만둔다
            StopListening();
            return true;
        }
        if (IsTesting)
        {
            if (action == NavAction.Back) StopTest();
            return true; // 테스트 중에는 화면이 움직이지 않게
        }
        if (IsOptionsOpen || IsTuningOpen)
        {
            if (action == NavAction.Back) { IsOptionsOpen = IsTuningOpen = false; return true; }
            return action is NavAction.TabPrev or NavAction.TabNext or NavAction.Secondary or NavAction.Options; // 패널 안에서는 이동·선택만
        }

        if (IsPickerOpen)
        {
            switch (action)
            {
                case NavAction.Back: ClosePicker(); return true;
                case NavAction.TabPrev: CategoryIndex = (CategoryIndex + Tabs.Count - 1) % Tabs.Count; return true;
                case NavAction.TabNext: CategoryIndex = (CategoryIndex + 1) % Tabs.Count; return true;
                case NavAction.Secondary: ClearEditing(); return true;
                case NavAction.Options: MultiKey = !MultiKey; return true;
            }
            return false;
        }

        switch (action)
        {
            case NavAction.Secondary when System.Windows.Input.Keyboard.FocusedElement is System.Windows.FrameworkElement { DataContext: ButtonRowViewModel row }:
                ResetRow(row.Button);
                _shell.ShowToast($"{row.Name} → 게임패드 그대로");
                return true;
            case NavAction.Options:
                _ = Test();
                return true;
            case NavAction.TabPrev:
                Undo();
                return true;
            case NavAction.TabNext:
                ToggleListen();
                return true;
        }
        return false;
    }
}
