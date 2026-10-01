using System.Collections.ObjectModel;
using ClawApp.Input;
using ClawCore.Controller;
using ClawCore.Mapping;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;

namespace ClawApp.ViewModels;

public sealed partial class ButtonRowViewModel(ClawButton button, string name) : ObservableObject
{
    public ClawButton Button { get; } = button;
    public string Name { get; } = name;
    [ObservableProperty] private string _bindingText = "";
    [ObservableProperty] private bool _isMapped;
}

public sealed partial class KeyItemViewModel(KeyCode key) : ObservableObject
{
    public KeyCode Key { get; } = key;
    public string Label => Key.Label;
    [ObservableProperty] private bool _isSelected;
}

/// <summary>버튼 → 키 매핑 편집. 변경은 즉시 저장된다.</summary>
public sealed partial class MappingEditorViewModel : ObservableObject, INavPage
{
    private static readonly (ClawButton Button, string Name)[] ButtonNames =
    [
        (ClawButton.A, "A"), (ClawButton.B, "B"), (ClawButton.X, "X"), (ClawButton.Y, "Y"),
        (ClawButton.DpadUp, "D-pad ↑"), (ClawButton.DpadDown, "D-pad ↓"), (ClawButton.DpadLeft, "D-pad ←"), (ClawButton.DpadRight, "D-pad →"),
        (ClawButton.LB, "LB"), (ClawButton.RB, "RB"), (ClawButton.LT, "LT"), (ClawButton.RT, "RT"),
        (ClawButton.L3, "L3"), (ClawButton.R3, "R3"),
        (ClawButton.M1, "M1 뒷면"), (ClawButton.M2, "M2 뒷면"),
    ];

    public static readonly (KeyCategory Category, string Label)[] Categories =
        [(KeyCategory.Keyboard, "키보드"), (KeyCategory.Mouse, "마우스"), (KeyCategory.Media, "미디어"), (KeyCategory.Gamepad, "게임패드")];

    private readonly ShellViewModel _shell;

    public MappingProfile Profile { get; }
    public ObservableCollection<ButtonRowViewModel> Rows { get; }

    [ObservableProperty] private bool _isPickerOpen;
    [ObservableProperty] private ButtonRowViewModel? _editingRow;
    [ObservableProperty] private int _categoryIndex;
    [ObservableProperty] private ObservableCollection<KeyItemViewModel> _keys = [];
    [ObservableProperty] private string _comboText = "";
    [ObservableProperty] private bool _isTesting;

    public MappingEditorViewModel(ShellViewModel shell, MappingProfile profile)
    {
        _shell = shell;
        Profile = profile;
        Rows = new ObservableCollection<ButtonRowViewModel>(ButtonNames.Select(b => new ButtonRowViewModel(b.Button, b.Name)));
        RefreshRows();
    }

    public string Title => $"매핑 · {Profile.Name}";
    public IReadOnlyList<Hint> Hints => IsPickerOpen
        ? [new("A", "키 추가/빼기"), new("B", "완료"), new("LB", "이전 분류"), new("RB", "다음 분류"), new("X", "모두 지우기")]
        : [new("A", "키 지정"), new("B", "뒤로"), new("X", "원래대로"), new("Y", "15초 테스트")];

    /// <summary>TextBox에서 벗어날 때 저장된다.</summary>
    public string ProfileName
    {
        get => Profile.Name;
        set
        {
            if (string.IsNullOrWhiteSpace(value) || value == Profile.Name) return;
            Profile.Name = value.Trim();
            Save();
            OnPropertyChanged();
            OnPropertyChanged(nameof(Title));
            _shell.OnPropertyChangedPageTitle();
        }
    }

    public string ModeText => Profile.Mode == GamepadMode.Desktop ? "데스크톱 모드" : "XInput 모드 (기본)";
    public string CategoryLabel => Categories[CategoryIndex].Label;

    private List<string> Current(ClawButton b) => Profile.Bindings.TryGetValue(b, out var l) ? l : [];

    private void RefreshRows()
    {
        foreach (var row in Rows)
        {
            if (Profile.Macros.TryGetValue(row.Button, out var macro) && macro.Count > 0)
            {
                row.IsMapped = true;
                row.BindingText = $"매크로 · {macro.Count}단계";
                continue;
            }
            var ids = Current(row.Button);
            row.IsMapped = ids.Count > 0;
            row.BindingText = ids.Count > 0
                ? string.Join(" + ", ids.Select(id => KeyCodes.Find(id)?.Label ?? id))
                : "기본 (게임패드)";
        }
    }

    private void Save()
    {
        _shell.Profiles.Upsert(Profile);
        RefreshRows();
    }

    // ── 키 선택 패널 ──────────────────────────────────────

    [RelayCommand]
    private void EditRow(ButtonRowViewModel row)
    {
        EditingRow = row;
        IsPickerOpen = true;
        LoadKeys();
        _shell.UpdateHints();
        _shell.RequestFocus();
    }

    partial void OnCategoryIndexChanged(int value)
    {
        OnPropertyChanged(nameof(CategoryLabel));
        LoadKeys();
    }

    private void LoadKeys()
    {
        if (EditingRow is null) return;
        var selected = Current(EditingRow.Button);
        var cat = Categories[CategoryIndex].Category;
        Keys = new ObservableCollection<KeyItemViewModel>(KeyCodes.All.Where(k => k.Category == cat)
            .Select(k => new KeyItemViewModel(k) { IsSelected = selected.Contains(k.Id) }));
        ComboText = selected.Count > 0 ? string.Join(" + ", selected.Select(id => KeyCodes.Find(id)?.Label ?? id)) : "선택한 키 없음";
    }

    [RelayCommand]
    private void ToggleKey(KeyItemViewModel key)
    {
        if (EditingRow is null) return;
        var list = Current(EditingRow.Button).ToList();
        if (list.Remove(key.Key.Id)) { }
        else if (list.Count >= ProfileLayout.MaxKeysPerBinding)
        {
            _shell.ShowToast($"한 버튼에 최대 {ProfileLayout.MaxKeysPerBinding}개까지 동시에 누를 수 있어요");
            key.IsSelected = list.Contains(key.Key.Id);
            return;
        }
        else list.Add(key.Key.Id);

        if (list.Count == 0) Profile.Bindings.Remove(EditingRow.Button);
        else Profile.Bindings[EditingRow.Button] = list;
        Save();
        key.IsSelected = list.Contains(key.Key.Id);
        ComboText = list.Count > 0 ? string.Join(" + ", list.Select(id => KeyCodes.Find(id)?.Label ?? id)) : "선택한 키 없음";
    }

    public bool EditingIsMKey => EditingRow?.Button is ClawButton.M1 or ClawButton.M2;

    partial void OnEditingRowChanged(ButtonRowViewModel? value) => OnPropertyChanged(nameof(EditingIsMKey));

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
        IsPickerOpen = false;
        _shell.UpdateHints();
        _shell.RequestFocus();
    }

    private void ResetRow(ClawButton button)
    {
        Profile.Bindings.Remove(button);
        Save();
        LoadKeys();
    }

    // ── 프로필 단위 ───────────────────────────────────────

    [RelayCommand]
    private void ToggleMode()
    {
        Profile.Mode = Profile.Mode == GamepadMode.XInput ? GamepadMode.Desktop : GamepadMode.XInput;
        Save();
        OnPropertyChanged(nameof(ModeText));
    }

    [RelayCommand]
    private async Task Test()
    {
        if (IsTesting) return;
        IsTesting = true;
        try { await _shell.TestMappingAsync(Profile, TimeSpan.FromSeconds(15)); }
        finally { IsTesting = false; }
    }

    [RelayCommand]
    private void Duplicate()
    {
        var copy = Profile.Clone();
        _shell.Profiles.Upsert(copy);
        _shell.ShowToast($"'{copy.Name}' 프로필을 만들었습니다");
        _shell.GoBack();
        _shell.Navigate(new MappingEditorViewModel(_shell, copy));
    }

    private DateTime _deleteArmedAt;

    [RelayCommand]
    private void Delete()
    {
        if (DateTime.UtcNow - _deleteArmedAt > TimeSpan.FromSeconds(3))
        {
            _deleteArmedAt = DateTime.UtcNow;
            _shell.ShowToast("한 번 더 누르면 이 프로필을 삭제합니다");
            return;
        }
        _shell.Profiles.Delete(Profile.Id);
        foreach (var g in _shell.Library.Games.Where(g => g.MappingProfileId == Profile.Id))
            _shell.Library.SetProfile(g, null);
        _shell.GoBack();
    }

    public bool Handle(NavAction action)
    {
        if (IsPickerOpen)
        {
            switch (action)
            {
                case NavAction.Back: ClosePicker(); return true;
                case NavAction.TabPrev: CategoryIndex = (CategoryIndex + Categories.Length - 1) % Categories.Length; _shell.RequestFocus(); return true;
                case NavAction.TabNext: CategoryIndex = (CategoryIndex + 1) % Categories.Length; _shell.RequestFocus(); return true;
                case NavAction.Secondary when EditingRow is not null: ResetRow(EditingRow.Button); ComboText = "선택한 키 없음"; return true;
            }
            return false;
        }

        switch (action)
        {
            case NavAction.Secondary when System.Windows.Input.Keyboard.FocusedElement is System.Windows.FrameworkElement { DataContext: ButtonRowViewModel row }:
                ResetRow(row.Button);
                return true;
            case NavAction.Options:
                _ = Test();
                return true;
        }
        return false;
    }
}
