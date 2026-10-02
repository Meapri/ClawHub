using System.Windows;
using System.Windows.Media;
using ClawApp.Services;

namespace ClawApp.ViewModels;

/// <summary>
/// 전체 화면 배경과 타일 꾸미기 (설정 → 홈 화면). MainWindow의 배경 층과 타일 템플릿이 쓴다.
/// 타일 그림·타일 투명도·사진 강조색은 앱 리소스를 덮어써서 모든 화면에 한 번에 반영한다.
/// </summary>
public sealed partial class ShellViewModel
{
    private ImageSource? _customBackdrop;
    private string? _customBackdropPath;

    /// <summary>강조색 뽑기로 앱 리소스에 덮어쓴 키 (끄면 지워서 시스템 강조색으로 돌아간다).</summary>
    private static readonly string[] AccentKeys = ["Accent", "AccentHover", "TileAccent"];

    /// <summary>
    /// 기본 배경 그림: 게임 아트면 홈의 "이어서 플레이" 게임, 전체 화면 이미지면 그 그림,
    /// 단색·타일 그림이면 없음 (타일 그림은 사진이 타일 안에만 보인다).
    /// </summary>
    public ImageSource? BackdropDefault => Settings.Home.BackdropMode switch
    {
        BackdropMode.GameArt => Home?.Hero?.Hero,
        BackdropMode.Image => CustomBackdrop(),
        _ => null,
    };

    public double BackdropBlur => Math.Clamp(Settings.Home.BackdropBlur, 0, 80);
    public double BackdropDim => Math.Clamp(Settings.Home.BackdropDim, 0, 100) / 100.0;

    private ImageSource? CustomBackdrop()
    {
        if (_customBackdropPath != Settings.Home.BackdropImage)
        {
            _customBackdropPath = Settings.Home.BackdropImage;
            _customBackdrop = ArtCache.LoadImage(_customBackdropPath);
        }
        return _customBackdrop;
    }

    private void InitBackdrop()
    {
        Home.PropertyChanged += (_, e) =>
        {
            if (e.PropertyName == nameof(HomeViewModel.Hero)) OnPropertyChanged(nameof(BackdropDefault));
        };
        ApplyTileLook();
    }

    /// <summary>홈·배경 설정을 바꾼 뒤: 저장하고 화면에 바로 반영.</summary>
    public void ApplyHomeSettings()
    {
        Settings.Save();
        ApplyTileLook();
        OnPropertyChanged(nameof(BackdropDefault));
        OnPropertyChanged(nameof(BackdropBlur));
        OnPropertyChanged(nameof(BackdropDim));
        Motion.Backdrop.ShowDefault();
        Home.Reload();
    }

    private void ApplyTileLook()
    {
        var home = Settings.Home;
        var tilePicture = home.BackdropMode == BackdropMode.TilePicture;
        Motion.Backdrop.FollowFocus = home.BackdropMode == BackdropMode.GameArt;
        Motion.TilePicture.SetSource(tilePicture ? CustomBackdrop() : null);

        var res = Application.Current.Resources;
        res["TileBackgroundOpacity"] = 1 - Math.Clamp(home.TileTransparency, 0, 100) / 100.0;
        // 타일 그림 위에서는 유리 타일 글자도 흰색 (W10M처럼)
        if (tilePicture) res["TileText"] = Frozen(Colors.White);
        else res.Remove("TileText");

        var palette = home.AccentFromImage && home.BackdropMode is BackdropMode.Image or BackdropMode.TilePicture
            ? AccentFromImage.FromFile(home.BackdropImage, SystemTheme.IsLight)
            : null;
        if (palette is { } p)
        {
            res["Accent"] = Frozen(p.Accent);
            res["AccentHover"] = Frozen(p.AccentHover);
            res["TileAccent"] = Frozen(p.TileAccent);
        }
        else
        {
            foreach (var k in AccentKeys) res.Remove(k);
        }
    }

    private static SolidColorBrush Frozen(Color c)
    {
        var b = new SolidColorBrush(c);
        b.Freeze();
        return b;
    }
}
