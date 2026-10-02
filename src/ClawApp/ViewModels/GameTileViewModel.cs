using System.Windows.Media;
using ClawApp.Services;
using ClawCore.Games;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;

namespace ClawApp.ViewModels;

public sealed partial class GameTileViewModel(ShellViewModel shell, GameEntry game) : ObservableObject
{
    public GameEntry Game { get; } = game;
    public string Title => Game.Title;
    public ImageSource? Cover { get; private set; } = ArtCache.LoadImage(game.CoverImage);
    public ImageSource? Hero { get; private set; } = ArtCache.LoadImage(game.HeroImage) ?? ArtCache.LoadImage(game.CoverImage);
    public ImageSource? Logo { get; private set; } = ArtCache.LoadImage(game.LogoImage);
    public bool HasLogo => Logo is not null;
    public bool HasHero => Hero is not null;
    public ImageSource? Icon { get; } = ArtCache.LoadIcon(game.IconSource ?? (File.Exists(game.LaunchTarget) ? game.LaunchTarget : null));
    public Brush Placeholder { get; } = ArtCache.PlaceholderBrush(game.Title);
    public bool HasCover => Cover is not null;

    /// <summary>홈 "최근 게임"에서의 크기 (2:3 커버). 한 열(높이 528)에 작게 3 · 보통 2 · 크게 1개.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(TileWidth), nameof(TileHeight), nameof(IsSmall))]
    private TileSize _size = TileSize.Medium;

    public double TileWidth => Size switch { TileSize.Small => 112, TileSize.Large => 344, _ => 168 };
    public double TileHeight => Size switch { TileSize.Small => 168, TileSize.Large => 520, _ => 256 };
    /// <summary>작은 타일은 제목 글자를 줄인다.</summary>
    public bool IsSmall => Size == TileSize.Small;

    /// <summary>아트를 바꾼 뒤(아트 고르기) 다시 읽는다.</summary>
    public void ReloadArt()
    {
        Cover = ArtCache.LoadImage(Game.CoverImage);
        Hero = ArtCache.LoadImage(Game.HeroImage) ?? Cover;
        Logo = ArtCache.LoadImage(Game.LogoImage);
        foreach (var p in new[] { nameof(Cover), nameof(Hero), nameof(Logo), nameof(HasCover), nameof(HasHero), nameof(HasLogo) })
            OnPropertyChanged(p);
    }

    public string SourceLabel => Game.Source switch
    {
        GameSource.Steam => "Steam",
        GameSource.Epic => "Epic Games",
        GameSource.Installed => "설치된 게임",
        _ => "직접 추가",
    };

    public string ProfileLabel => shell.Profiles.Find(Game.MappingProfileId)?.Name is { } n ? $"🎮 {n}" : "";

    public string LastPlayedLabel => Game.LastPlayed is { } t
        ? (DateTime.Now - t).TotalDays < 1 ? "오늘 플레이" : $"{(int)(DateTime.Now - t).TotalDays}일 전 플레이"
        : "플레이 기록 없음";

    [RelayCommand] private void Open() => shell.Navigate(new GameViewModel(shell, this));
    [RelayCommand] private Task Play() => shell.LaunchAsync(Game);
}
