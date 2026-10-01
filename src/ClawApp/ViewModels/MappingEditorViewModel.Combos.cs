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

/// <summary>키 선택판 "조합·미디어" 탭의 조합키 프리셋 (Game Bar 녹화 등).</summary>
public sealed partial class MappingEditorViewModel
{
    private IReadOnlyList<KeyComboViewModel>? _comboPresets;

    public IReadOnlyList<KeyComboViewModel> ComboPresets => _comboPresets ??= [.. KeyCombos.All.Select(c => new KeyComboViewModel(c))];

    /// <summary>이 버튼을 그 조합으로 지정하고 목록으로 돌아간다. 같은 조합을 다시 고르면 해제.</summary>
    [RelayCommand]
    private void ApplyCombo(KeyComboViewModel combo)
    {
        if (EditingRow is not { } row) return;
        if (Current(row.Button).SequenceEqual(combo.Combo.Keys))
        {
            ClearEditing();
            return;
        }
        Profile.Bindings[row.Button] = [.. combo.Combo.Keys];
        Profile.Macros.Remove(row.Button);
        Save();
        _shell.ShowToast($"{row.Name} → {combo.Label}");
        ClosePicker();
    }
}
