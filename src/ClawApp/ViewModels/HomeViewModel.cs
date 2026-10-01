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
    /// <summary>게임 중이면 B(또는 왼쪽 버튼 한 번 더)로 게임에 돌아간다. "…" = 오른쪽 버튼 → Game Bar 빠른 설정.</summary>
    public IReadOnlyList<Hint> Hints => _shell.IsGameRunning
        ? [new("A", "선택"), new("B", "게임으로"), new("…", "빠른 설정")]
        : [new("A", "선택"), new("…", "빠른 설정")];
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

    /// <summary>홈에서 B: 게임 중이면 게임으로 돌아가고, 아니면 아무것도 하지 않음.</summary>
    public bool Handle(NavAction action)
    {
        if (action != NavAction.Back) return false;
        if (_shell.IsGameRunning) _shell.ReturnToBackground();
        return true;
    }
}
