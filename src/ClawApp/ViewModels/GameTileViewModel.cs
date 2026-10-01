using System.Windows.Media;
using ClawApp.Services;
using ClawCore.Games;
using CommunityToolkit.Mvvm.Input;

namespace ClawApp.ViewModels;

public sealed partial class GameTileViewModel(ShellViewModel shell, GameEntry game)
{
    public GameEntry Game { get; } = game;
    public string Title => Game.Title;
    public ImageSource? Cover { get; } = ArtCache.LoadImage(game.CoverImage);
    public ImageSource? Hero { get; } = ArtCache.LoadImage(game.HeroImage) ?? ArtCache.LoadImage(game.CoverImage);
    public ImageSource? Icon { get; } = ArtCache.LoadIcon(game.IconSource ?? (File.Exists(game.LaunchTarget) ? game.LaunchTarget : null));
    public Brush Placeholder { get; } = ArtCache.PlaceholderBrush(game.Title);
    public bool HasCover => Cover is not null;

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
