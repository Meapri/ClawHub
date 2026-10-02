using System.Collections.ObjectModel;
using ClawApp.Services;
using CommunityToolkit.Mvvm.ComponentModel;

namespace ClawApp.ViewModels;

/// <summary>시작 화면의 구역 하나: 제목 + 타일 판.</summary>
public sealed partial class StartGroupViewModel(StartGroupDef def) : ObservableObject
{
    public StartGroupDef Def { get; } = def;
    public ObservableCollection<StartTileViewModel> Tiles { get; } = [];

    public string Title => Def.Title;
    public bool HasTitle => Def.Title.Length > 0;

    [ObservableProperty] private bool _isEditing;

    /// <summary>편집 중 제목 자리: 이름이 없으면 "이름 붙이기".</summary>
    public string EditTitle => HasTitle ? Def.Title : "이름 붙이기";

    /// <summary>평소 제목 줄을 보일지 (제목이 있거나 편집 중이면).</summary>
    public bool ShowHeader => HasTitle || IsEditing;

    partial void OnIsEditingChanged(bool value) => OnPropertyChanged(nameof(ShowHeader));

    public void Refresh()
    {
        OnPropertyChanged(nameof(Title));
        OnPropertyChanged(nameof(HasTitle));
        OnPropertyChanged(nameof(EditTitle));
        OnPropertyChanged(nameof(ShowHeader));
    }
}
