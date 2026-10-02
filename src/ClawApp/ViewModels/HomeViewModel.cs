using System.Collections.ObjectModel;
using System.Windows;
using ClawApp.Input;
using ClawApp.Services;
using CommunityToolkit.Mvvm.ComponentModel;

namespace ClawApp.ViewModels;

public sealed partial class HomeViewModel : ObservableObject, INavPage
{
    private readonly ShellViewModel _shell;

    [ObservableProperty] private GameTileViewModel? _hero;
    [ObservableProperty] private ObservableCollection<GameTileViewModel> _recent = [];
    /// <summary>앱(게임이 아닌 프로그램) 타일. A = 바로 실행.</summary>
    [ObservableProperty] private ObservableCollection<GameTileViewModel> _apps = [];
    public bool HasApps => Apps.Count > 0;

    public HomeViewModel(ShellViewModel shell)
    {
        _shell = shell;
        Reload();
    }

    private HomeSettings Settings => _shell.Settings.Home;

    public string Title => "홈";
    /// <summary>게임 중이면 B(또는 왼쪽 버튼 한 번 더)로 게임에 돌아간다. "…" = 오른쪽 버튼 → Game Bar 빠른 설정.</summary>
    public IReadOnlyList<Hint> Hints => _shell.IsGameRunning
        ? [new("A", "선택"), new("X", "타일 크기"), new("B", "게임으로"), new("…", "빠른 설정")]
        : [new("A", "선택"), new("X", "타일 크기"), new("…", "빠른 설정")];
    public bool IsEmpty => Hero is null;

    // ── "이어서 플레이" 타일 (설정 → 홈 화면, 또는 그 타일에서 X) ──
    public bool HeroVisible => !IsEmpty && Settings.HeroSize != HeroTileSize.Hidden;
    public double HeroWidth => Settings.HeroSize == HeroTileSize.Medium ? 344 : 520;

    /// <summary>히어로 = 가장 최근 게임 (없으면 첫 게임), 옆 그룹 = 최근 + 나머지 순. 타일 크기는 설정을 따른다.</summary>
    public void Reload()
    {
        var lib = _shell.Library;
        var ordered = lib.Recent.Concat(lib.Visible.Where(g => g.LastPlayed is null))
            .Where(g => !g.IsApp) // 앱은 아래 "앱" 그룹으로
            .Take(Math.Clamp(Settings.RecentCount, 3, 30))
            .Select(g => new GameTileViewModel(_shell, g) { Size = Settings.TileSizes.GetValueOrDefault(g.Id, Settings.RecentTileSize) })
            .ToList();
        Hero = ordered.FirstOrDefault();
        Recent = new ObservableCollection<GameTileViewModel>(ordered);
        // 앱: 최근에 쓴 순, 안 쓴 건 이름 순 (최대 8개 = 2열)
        Apps = new ObservableCollection<GameTileViewModel>(lib.Visible.Where(g => g.IsApp)
            .OrderByDescending(g => g.LastPlayed ?? DateTime.MinValue).ThenBy(g => g.Title)
            .Take(8).Select(g => new GameTileViewModel(_shell, g)));
        OnPropertyChanged(nameof(HasApps));
        OnPropertyChanged(nameof(IsEmpty));
        OnPropertyChanged(nameof(HeroVisible));
        OnPropertyChanged(nameof(HeroWidth));
    }

    public void OnShown() => Reload();

    /// <summary>B: 게임 중이면 게임으로. X: 포커스한 타일 크기 바꾸기 (최근 게임 = 작게→보통→크게, 이어서 플레이 = 크게↔보통).</summary>
    public bool Handle(NavAction action)
    {
        switch (action)
        {
            case NavAction.Back:
                if (_shell.IsGameRunning) _shell.ReturnToBackground();
                return true;
            case NavAction.Secondary:
                ResizeFocused();
                return true;
        }
        return false;
    }

    private void ResizeFocused()
    {
        switch (System.Windows.Input.Keyboard.FocusedElement)
        {
            case FrameworkElement { DataContext: GameTileViewModel tile } when Recent.Contains(tile):
                tile.Size = tile.Size switch { TileSize.Small => TileSize.Medium, TileSize.Medium => TileSize.Large, _ => TileSize.Small };
                Settings.TileSizes[tile.Game.Id] = tile.Size;
                _shell.Settings.Save();
                _shell.ShowToast($"{tile.Title} 타일: {SizeLabel(tile.Size)}");
                break;
            case FrameworkElement { Tag: "Hero" }:
                Settings.HeroSize = Settings.HeroSize == HeroTileSize.Large ? HeroTileSize.Medium : HeroTileSize.Large;
                _shell.Settings.Save();
                OnPropertyChanged(nameof(HeroWidth));
                _shell.ShowToast($"이어서 플레이 타일: {(Settings.HeroSize == HeroTileSize.Large ? "크게" : "보통")}");
                break;
            default:
                _shell.ShowToast("게임 타일에서 X를 누르면 크기가 바뀌어요. 배경과 기본 크기는 설정 → 홈 화면에서 바꿀 수 있어요.");
                break;
        }
    }

    public static string SizeLabel(TileSize s) => s switch { TileSize.Small => "작게", TileSize.Large => "크게", _ => "보통" };
}
