using System.Collections.ObjectModel;
using ClawApp.Input;
using ClawCore.Games;
using CommunityToolkit.Mvvm.ComponentModel;

namespace ClawApp.ViewModels;

public sealed partial class LibraryViewModel : ObservableObject, INavPage
{
    public sealed record Tab(string Label, Func<GameEntry, bool> Filter);

    private readonly ShellViewModel _shell;

    public IReadOnlyList<Tab> Tabs { get; } =
    [
        new("전체", _ => true),
        new("최근", g => g.LastPlayed is not null),
        new("Steam", g => g.Source == GameSource.Steam),
        new("Epic", g => g.Source == GameSource.Epic),
        new("기타", g => g.Source is GameSource.Installed or GameSource.Custom),
    ];

    [ObservableProperty] private int _tabIndex;
    [ObservableProperty] private ObservableCollection<GameTileViewModel> _tiles = [];

    public LibraryViewModel(ShellViewModel shell)
    {
        _shell = shell;
        Apply();
    }

    public string Title => "라이브러리";
    public IReadOnlyList<Hint> Hints => [new("A", "열기"), new("B", "뒤로"), new("LB", "이전 탭"), new("RB", "다음 탭")];
    public bool IsEmpty => Tiles.Count == 0;

    partial void OnTabIndexChanged(int value) => Apply();

    private void Apply()
    {
        var filter = Tabs[TabIndex].Filter;
        Tiles = new ObservableCollection<GameTileViewModel>(
            _shell.Library.Visible.Where(filter).Select(g => new GameTileViewModel(_shell, g)));
        OnPropertyChanged(nameof(IsEmpty));
    }

    public bool Handle(NavAction action)
    {
        switch (action)
        {
            case NavAction.TabPrev: TabIndex = (TabIndex + Tabs.Count - 1) % Tabs.Count; break;
            case NavAction.TabNext: TabIndex = (TabIndex + 1) % Tabs.Count; break;
            default: return false;
        }
        _shell.RequestFocus();
        return true;
    }
}
