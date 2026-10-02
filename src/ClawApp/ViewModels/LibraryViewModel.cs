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
        // 게임 탭들은 앱(게임이 아닌 프로그램)을 빼고, 앱은 마지막 탭에 따로
        new("게임", g => !g.IsApp),
        new("최근", g => !g.IsApp && g.LastPlayed is not null),
        new("Steam", g => !g.IsApp && g.Source == GameSource.Steam),
        new("Epic", g => !g.IsApp && g.Source == GameSource.Epic),
        new("기타", g => !g.IsApp && g.Source is GameSource.Installed or GameSource.Custom),
        new("앱", g => g.IsApp),
    ];

    [ObservableProperty] private int _tabIndex;
    [ObservableProperty] private ObservableCollection<GameTileViewModel> _tiles = [];

    public LibraryViewModel(ShellViewModel shell)
    {
        _shell = shell;
        Apply();
    }

    public string Title => "라이브러리";
    public IReadOnlyList<Hint> Hints => [new("A", "열기"), new("Y", "프로그램 추가"), new("B", "뒤로"), new("LB", "이전 탭"), new("RB", "다음 탭")];
    public bool IsEmpty => Tiles.Count == 0;

    partial void OnTabIndexChanged(int value) => Apply();

    public void Refresh() => Apply();

    /// <summary>프로그램 추가 화면에서 돌아오면 새로 넣은 것이 보이게.</summary>
    public void OnShown() => Apply();

    [CommunityToolkit.Mvvm.Input.RelayCommand]
    private void AddProgram() => _shell.OpenAppPickerCommand.Execute(null);

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
            case NavAction.Options: AddProgram(); return true;
            default: return false;
        }
        _shell.RequestFocus();
        return true;
    }
}
