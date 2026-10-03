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

    private bool? _customBackdropFull;

    /// <summary>
    /// 사용자 배경 그림. 선명하게 보일 때(흐림 약함 · 타일 그림)만 화면 해상도로 읽고, 많이 흐리게 깔 때는
    /// 타일 크기(600px)로 읽는다 — 흐림이 세부를 지워 보이는 결과는 같고, 메모리 ~9MB와 디코딩 시간을 아낀다.
    /// </summary>
    private ImageSource? CustomBackdrop()
    {
        var home = Settings.Home;
        var full = home.BackdropMode == BackdropMode.TilePicture || home.BackdropBlur < 24;
        if (_customBackdropPath != home.BackdropImage || _customBackdropFull != full)
        {
            _customBackdropPath = home.BackdropImage;
            _customBackdropFull = full;
            _customBackdrop = full ? ArtCache.LoadFullScreen(_customBackdropPath) : ArtCache.LoadImage(_customBackdropPath);
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
        Motion.Backdrop.Sharp = home.BackdropBlur < 24; // 흐림이 약하면 그림을 화면 해상도로
        Motion.TilePicture.SetSource(tilePicture ? CustomBackdrop() : null);

        // 앱 리소스를 쓰거나 지울 때마다 WPF는 열린 창의 요소 트리 전체를 훑어 DynamicResource를 다시 찾는다.
        // 설정 슬라이더를 움직일 때마다 여기로 오므로 값이 실제로 바뀐 것만 쓴다 (보이는 결과는 같다)
        var res = Application.Current.Resources;
        SetIfChanged(res, "TileBackgroundOpacity", 1 - Math.Clamp(home.TileTransparency, 0, 100) / 100.0);
        // 타일 그림 위에서는 유리 타일 글자도 흰색 (W10M처럼)
        if (tilePicture) SetBrushIfChanged(res, "TileText", Colors.White);
        else RemoveIfPresent(res, "TileText");

        var palette = home.AccentFromImage && home.BackdropMode is BackdropMode.Image or BackdropMode.TilePicture
            ? AccentFromImage.FromFile(home.BackdropImage, SystemTheme.IsLight)
            : null;
        if (palette is { } p)
        {
            SetBrushIfChanged(res, "Accent", p.Accent);
            SetBrushIfChanged(res, "AccentHover", p.AccentHover);
            SetBrushIfChanged(res, "TileAccent", p.TileAccent);
        }
        else
        {
            foreach (var k in AccentKeys) RemoveIfPresent(res, k);
        }
    }

    /// <summary>여기서 앱 리소스 최상위에 덮어쓴 키 (병합된 테마 사전의 기본값과 구분 — Contains/인덱서는 병합 사전까지 찾는다).</summary>
    private static readonly HashSet<string> Overridden = [];

    private static void SetIfChanged(ResourceDictionary res, string key, double value)
    {
        if (res[key] is double old && old.Equals(value)) return; // 지금 보이는 값과 같음
        res[key] = value;
        Overridden.Add(key);
    }

    private static void SetBrushIfChanged(ResourceDictionary res, string key, Color color)
    {
        if (res[key] is SolidColorBrush old && old.Color == color) return;
        res[key] = Frozen(color);
        Overridden.Add(key);
    }

    /// <summary>덮어쓴 적이 있을 때만 지운다 → 테마 사전의 기본값으로 돌아간다.</summary>
    private static void RemoveIfPresent(ResourceDictionary res, string key)
    {
        if (Overridden.Remove(key)) res.Remove(key);
    }

    private static SolidColorBrush Frozen(Color c)
    {
        var b = new SolidColorBrush(c);
        b.Freeze();
        return b;
    }
}
