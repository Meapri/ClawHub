using ClawCore.Controller;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;

namespace ClawApp.ViewModels;

public sealed partial class KeyComboViewModel(KeyCombo combo) : ObservableObject
{
    public KeyCombo Combo { get; } = combo;
    public string Label => Combo.Label;
    [ObservableProperty] private bool _isSelected;
}

/// <summary>키 선택 패널의 조합키 프리셋 (Game Bar 녹화 등). UI는 Views/Sections/ComboPresetsSection.xaml.</summary>
public sealed partial class MappingEditorViewModel
{
    private IReadOnlyList<KeyComboViewModel>? _comboPresets;

    /// <summary>키 선택 패널에 함께 보여 줄 조합키 프리셋. A = 이 버튼을 그 조합으로 (다시 누르면 해제).</summary>
    public IReadOnlyList<KeyComboViewModel> ComboPresets => _comboPresets ??= [.. KeyCombos.All.Select(c => new KeyComboViewModel(c))];

    private void RefreshComboSelection()
    {
        IReadOnlyList<string> current = EditingRow is null ? [] : Current(EditingRow.Button);
        foreach (var c in ComboPresets) c.IsSelected = current.SequenceEqual(c.Combo.Keys);
    }

    [RelayCommand]
    private void ApplyCombo(KeyComboViewModel combo)
    {
        if (EditingRow is null) return;
        var button = EditingRow.Button;
        if (Current(button).SequenceEqual(combo.Combo.Keys)) Profile.Bindings.Remove(button); // 같은 조합 → 해제
        else Profile.Bindings[button] = [.. combo.Combo.Keys];
        Save();
        LoadKeys(); // 개별 키 선택 표시 + ComboText 갱신
        RefreshComboSelection();
    }

    // 개별 키를 켜고 끌 때도 ComboText가 바뀌므로 여기서 프리셋 선택 표시를 맞춘다
    partial void OnComboTextChanged(string value) => RefreshComboSelection();
}
