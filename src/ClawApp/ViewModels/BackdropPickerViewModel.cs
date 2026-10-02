using System.Windows.Media;
using ClawApp.Services;
using CommunityToolkit.Mvvm.Input;

namespace ClawApp.ViewModels;

/// <summary>
/// 배경 고르기: 라이브러리 게임의 배경·커버 아트 중에서 패드로 고른다 (파일은 설정의 "사진 파일에서 고르기").
/// 고르면 <paramref name="mode"/>(전체 화면 이미지 또는 타일 그림)로 바뀐다.
/// </summary>
public sealed partial class BackdropPickerViewModel(ShellViewModel shell, BackdropMode mode) : INavPage
{
    public sealed record Choice(string Title, string Path, ImageSource Image, bool IsCurrent);

    public IReadOnlyList<Choice> Choices { get; } = [.. shell.Library.Visible
        .SelectMany(g => new[] { (g.Title, g.HeroImage), (g.Title + " · 커버", g.CoverImage) })
        .Where(x => x.Item2 is not null)
        .Select(x => (x.Item1, Path: x.Item2!, Image: ArtCache.LoadImage(x.Item2)))
        .Where(x => x.Image is not null)
        .Select(x => new Choice(x.Item1, x.Path, x.Image!, x.Path == shell.Settings.Home.BackdropImage))];

    public bool IsEmpty => Choices.Count == 0;

    public string Title => "배경 고르기";
    public IReadOnlyList<Hint> Hints => [new("A", "배경으로"), new("B", "뒤로")];

    /// <summary>고르는 동안은 포커스한 그림이 실제 배경에 깔려 미리 보이게 (고정·단색 모드여도).</summary>
    public void OnShown() => Motion.Backdrop.FollowFocus = true;

    public void OnHidden()
    {
        Motion.Backdrop.FollowFocus = shell.Settings.Home.BackdropMode == BackdropMode.GameArt;
        Motion.Backdrop.ShowDefault();
    }

    [RelayCommand]
    private void Choose(Choice choice)
    {
        shell.Settings.Home.BackdropImage = choice.Path;
        if (mode == BackdropMode.TilePicture && shell.Settings.Home.TileTransparency < 60) shell.Settings.Home.TileTransparency = 85;
        shell.Settings.Home.BackdropMode = mode;
        shell.ApplyHomeSettings();
        shell.ShowToast($"배경: {choice.Title}");
        shell.GoBack(); // 설정 화면은 OnShown에서 새 값을 읽는다
    }
}
