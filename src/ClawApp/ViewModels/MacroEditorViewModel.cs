using System.Collections.ObjectModel;
using ClawApp.Input;
using ClawCore.Controller;
using ClawCore.Mapping;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;

namespace ClawApp.ViewModels;

public sealed partial class MacroStepViewModel(MappingProfile.MacroStepDef def, int index) : ObservableObject
{
    public MappingProfile.MacroStepDef Def { get; } = def;
    public int Index { get; set; } = index;
    public string Number => $"{Index + 1}";
    public string KeyLabel => KeyCodes.Find(Def.Key)?.Label ?? Def.Key;
    [ObservableProperty] private bool _isSelected;

    public int HoldMs
    {
        get => Def.HoldMs;
        set { Def.HoldMs = value; OnPropertyChanged(); OnPropertyChanged(nameof(Summary)); }
    }

    public int GapMs
    {
        get => Def.GapMs;
        set { Def.GapMs = value; OnPropertyChanged(); OnPropertyChanged(nameof(Summary)); }
    }

    public string Summary => $"{HoldMs}ms 누름 · {GapMs}ms 쉼";

    public void Renumber(int i)
    {
        Index = i;
        OnPropertyChanged(nameof(Number));
    }
}

/// <summary>M1/M2 시간 매크로 편집: 단계 목록, 단계별 누름/쉼 시간, 키 선택.</summary>
public sealed partial class MacroEditorViewModel : ObservableObject, INavPage
{
    private readonly ShellViewModel _shell;
    private readonly MappingProfile _profile;
    private readonly ClawButton _button;

    public ObservableCollection<MacroStepViewModel> Steps { get; }
    /// <summary>매핑 편집과 같은 키보드 모양 판 + 마우스·미디어 타일.</summary>
    public List<List<KeySlotViewModel>> KeyboardRows { get; } = KeyboardLayout.Build();
    public List<KeySlotViewModel> ExtraKeys { get; } = [.. KeyboardLayout.Mouse(), .. KeyboardLayout.Media()];

    [ObservableProperty] private MacroStepViewModel? _selected;
    [ObservableProperty] private bool _isPickerOpen;

    public MacroEditorViewModel(ShellViewModel shell, MappingProfile profile, ClawButton button)
    {
        _shell = shell;
        _profile = profile;
        _button = button;
        var list = profile.Macros.TryGetValue(button, out var l) ? l : [];
        Steps = new(list.Select((s, i) => new MacroStepViewModel(s, i)));
        Selected = Steps.FirstOrDefault();
        if (Selected is not null) Selected.IsSelected = true;
    }

    public string Title => $"매크로 · {(_button == ClawButton.M1 ? "M1" : "M2")}";
    public IReadOnlyList<Hint> Hints => IsPickerOpen
        ? [new("A", "이 키로"), new("B", "취소")]
        : [new("A", "선택"), new("B", "뒤로"), new("X", "단계 삭제"), new("Y", "15초 테스트")];

    public bool IsEmpty => Steps.Count == 0;
    public bool CanAdd => Steps.Count < MacroLayout.MaxSteps;
    public string TotalText => Steps.Count == 0 ? "단계 없음"
        : $"{Steps.Count}단계 · 총 {Steps.Sum(s => s.HoldMs + s.GapMs)}ms";

    partial void OnSelectedChanged(MacroStepViewModel? oldValue, MacroStepViewModel? newValue)
    {
        if (oldValue is not null) oldValue.IsSelected = false;
        if (newValue is not null) newValue.IsSelected = true;
    }

    [RelayCommand] private void Select(MacroStepViewModel step) => Selected = step;

    [RelayCommand]
    private void AddStep()
    {
        if (!CanAdd) return;
        Selected = null; // 새 단계 추가 모드
        OpenPicker();
    }

    [RelayCommand]
    private void ChangeKey()
    {
        if (Selected is not null) OpenPicker();
    }

    private void OpenPicker()
    {
        // 바꾸는 단계의 지금 키를 표시 (새 단계면 표시 없음)
        foreach (var slot in KeyboardRows.SelectMany(r => r).Concat(ExtraKeys))
            slot.IsSelected = slot.Key is not null && Selected?.Def.Key == slot.Key.Id;
        IsPickerOpen = true; // 포커스는 뷰가 옮긴다
        _shell.UpdateHints();
    }

    [RelayCommand]
    private void PickKey(KeySlotViewModel slot)
    {
        if (slot.Key is not { } key) return;
        if (Selected is { } s)
        {
            s.Def.Key = key.Id;
            Steps[s.Index] = new MacroStepViewModel(s.Def, s.Index) { IsSelected = true };
            Selected = Steps[s.Index];
        }
        else
        {
            var step = new MacroStepViewModel(new MappingProfile.MacroStepDef { Key = key.Id }, Steps.Count);
            Steps.Add(step);
            Selected = step;
        }
        ClosePicker();
        Save();
    }

    private void ClosePicker()
    {
        IsPickerOpen = false;
        _shell.UpdateHints();
    }

    private void DeleteSelected()
    {
        if (Selected is not { } s) return;
        Steps.Remove(s);
        for (var i = 0; i < Steps.Count; i++) Steps[i].Renumber(i);
        Selected = Steps.ElementAtOrDefault(Math.Min(s.Index, Steps.Count - 1));
        Save();
    }

    [RelayCommand]
    private void Clear()
    {
        Steps.Clear();
        Selected = null;
        Save();
    }

    /// <summary>슬라이더 변경 등 단계 값이 바뀔 때도 저장.</summary>
    public void Save()
    {
        if (Steps.Count == 0) _profile.Macros.Remove(_button);
        else _profile.Macros[_button] = Steps.Select(s => s.Def).ToList();
        _shell.Profiles.Upsert(_profile);
        OnPropertyChanged(nameof(IsEmpty));
        OnPropertyChanged(nameof(CanAdd));
        OnPropertyChanged(nameof(TotalText));
    }

    public void OnHidden() => Save();

    public bool Handle(NavAction action)
    {
        if (IsPickerOpen)
        {
            if (action != NavAction.Back) return false;
            ClosePicker();
            return true;
        }
        switch (action)
        {
            case NavAction.Secondary: DeleteSelected(); return true;
            case NavAction.Options:
                _shell.ShowToast("15초 동안 테스트 중 — 메모장 등에서 눌러 보세요");
                _ = _shell.TestMappingAsync(_profile, TimeSpan.FromSeconds(15));
                return true;
        }
        return false;
    }
}
