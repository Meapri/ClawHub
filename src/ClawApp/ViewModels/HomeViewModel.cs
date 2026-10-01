using System.Collections.ObjectModel;
using ClawApp.Input;
using CommunityToolkit.Mvvm.ComponentModel;

namespace ClawApp.ViewModels;

public sealed partial class HomeViewModel : ObservableObject, INavPage
{
    private readonly ShellViewModel _shell;

    [ObservableProperty] private GameTileViewModel? _hero;
    [ObservableProperty] private ObservableCollection<GameTileViewModel> _recent = [];

    public HomeViewModel(ShellViewModel shell)
    {
        _shell = shell;
        Reload();
    }

    public string Title => "홈";
    public IReadOnlyList<Hint> Hints => [new("A", "선택"), new("☰", "메뉴"), new("…", "퀵 설정")];
    public bool IsEmpty => Hero is null;

    /// <summary>히어로 = 가장 최근 게임 (없으면 첫 게임), 아래 줄 = 최근 + 나머지 순.</summary>
    public void Reload()
    {
        var lib = _shell.Library;
        var ordered = lib.Recent.Concat(lib.Visible.Where(g => g.LastPlayed is null)).Take(12)
            .Select(g => new GameTileViewModel(_shell, g)).ToList();
        Hero = ordered.FirstOrDefault();
        Recent = new ObservableCollection<GameTileViewModel>(ordered);
        OnPropertyChanged(nameof(IsEmpty));
    }

    public void OnShown() => Reload();

    public bool Handle(NavAction action) => action == NavAction.Back; // 홈에서 B는 아무것도 하지 않음
}
