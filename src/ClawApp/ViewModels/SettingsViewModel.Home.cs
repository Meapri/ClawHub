using System.IO;
using System.Windows.Media;
using ClawApp.Services;
using ClawCore;
using CommunityToolkit.Mvvm.Input;
using Microsoft.Win32;

namespace ClawApp.ViewModels;

/// <summary>설정 → 홈 화면: 배경(게임 아트·고정 이미지·단색, 흐림·덮개)과 타일 크기.</summary>
public sealed partial class SettingsViewModel
{
    private HomeSettings HomeSet => _shell.Settings.Home;

    private void ApplyHome(params string[] changed)
    {
        _shell.ApplyHomeSettings();
        foreach (var p in changed) OnPropertyChanged(p);
    }

    // ── 배경 ──────────────────────────────────────────────

    private void SetBackdropMode(BackdropMode mode)
    {
        if (mode == BackdropMode.Image && HomeSet.BackdropImage is null)
        {
            // 아직 고른 그림이 없으면 바로 고르러 간다 (고르면 그때 고정 이미지로 바뀜)
            PickBackdropFromGames();
            return;
        }
        HomeSet.BackdropMode = mode;
        ApplyHome(nameof(BgGameArt), nameof(BgImage), nameof(BgPlain), nameof(BackdropModeText));
    }

    public bool BgGameArt { get => HomeSet.BackdropMode == BackdropMode.GameArt; set { if (value) SetBackdropMode(BackdropMode.GameArt); } }
    public bool BgImage { get => HomeSet.BackdropMode == BackdropMode.Image; set { if (value) SetBackdropMode(BackdropMode.Image); } }
    public bool BgPlain { get => HomeSet.BackdropMode == BackdropMode.Plain; set { if (value) SetBackdropMode(BackdropMode.Plain); } }

    public string BackdropModeText => HomeSet.BackdropMode switch
    {
        BackdropMode.Image => "고른 그림이 늘 배경에 깔려요",
        BackdropMode.Plain => "그림 없이 테마 색만 써요 (가장 깔끔하고 배터리에도 조금 유리해요)",
        _ => "포커스한 게임의 아트가 흐리게 배경에 깔리고, 게임을 옮겨 다니면 바뀌어요",
    };

    /// <summary>고정 이미지 미리보기.</summary>
    public ImageSource? BackdropPreview => ArtCache.LoadImage(HomeSet.BackdropImage);
    public bool HasBackdropPreview => BackdropPreview is not null;

    public int BackdropBlur
    {
        get => HomeSet.BackdropBlur;
        set { if (HomeSet.BackdropBlur == value) return; HomeSet.BackdropBlur = value; ApplyHome(nameof(BackdropBlur)); }
    }

    public int BackdropDim
    {
        get => HomeSet.BackdropDim;
        set { if (HomeSet.BackdropDim == value) return; HomeSet.BackdropDim = value; ApplyHome(nameof(BackdropDim)); }
    }

    [RelayCommand]
    private void PickBackdropFromGames() => _shell.Navigate(new BackdropPickerViewModel(_shell));

    /// <summary>사진 파일 (터치·마우스). 원본이 옮겨져도 남게 사용자 데이터 폴더에 복사한다.</summary>
    [RelayCommand]
    private void PickBackdropFile()
    {
        var dlg = new OpenFileDialog { Title = "배경으로 쓸 그림", Filter = "그림 (*.jpg;*.jpeg;*.png;*.bmp)|*.jpg;*.jpeg;*.png;*.bmp" };
        if (dlg.ShowDialog() != true) return;
        try
        {
            var dir = Directory.CreateDirectory(Path.Combine(JsonStore.UserDir, "backgrounds")).FullName;
            var dest = Path.Combine(dir, $"{DateTime.Now:yyyyMMdd-HHmmss}{Path.GetExtension(dlg.FileName)}");
            File.Copy(dlg.FileName, dest, overwrite: true);
            SetBackdropImage(dest);
            _shell.ShowToast("배경을 바꿨어요");
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            _shell.ShowToast($"그림을 가져오지 못했어요: {ex.Message}");
        }
    }

    /// <summary>고정 이미지로 지정 (배경 고르기 화면도 이걸 쓴다).</summary>
    public void SetBackdropImage(string path)
    {
        HomeSet.BackdropImage = path;
        HomeSet.BackdropMode = BackdropMode.Image;
        ApplyHome(nameof(BgGameArt), nameof(BgImage), nameof(BgPlain), nameof(BackdropModeText), nameof(BackdropPreview), nameof(HasBackdropPreview));
    }

    [RelayCommand]
    private void ResetBackdrop()
    {
        HomeSet.BackdropMode = BackdropMode.GameArt;
        HomeSet.BackdropBlur = new HomeSettings().BackdropBlur;
        HomeSet.BackdropDim = new HomeSettings().BackdropDim;
        ApplyHome(nameof(BgGameArt), nameof(BgImage), nameof(BgPlain), nameof(BackdropModeText), nameof(BackdropBlur), nameof(BackdropDim));
        _shell.ShowToast("배경을 기본값으로 돌렸어요");
    }

    // ── 타일 ──────────────────────────────────────────────

    private void SetHero(HeroTileSize size) { HomeSet.HeroSize = size; ApplyHome(nameof(HeroLarge), nameof(HeroMedium), nameof(HeroHidden)); }
    public bool HeroLarge { get => HomeSet.HeroSize == HeroTileSize.Large; set { if (value) SetHero(HeroTileSize.Large); } }
    public bool HeroMedium { get => HomeSet.HeroSize == HeroTileSize.Medium; set { if (value) SetHero(HeroTileSize.Medium); } }
    public bool HeroHidden { get => HomeSet.HeroSize == HeroTileSize.Hidden; set { if (value) SetHero(HeroTileSize.Hidden); } }

    private void SetTileDefault(TileSize size) { HomeSet.RecentTileSize = size; ApplyHome(nameof(TileSmall), nameof(TileMedium), nameof(TileLarge)); }
    public bool TileSmall { get => HomeSet.RecentTileSize == TileSize.Small; set { if (value) SetTileDefault(TileSize.Small); } }
    public bool TileMedium { get => HomeSet.RecentTileSize == TileSize.Medium; set { if (value) SetTileDefault(TileSize.Medium); } }
    public bool TileLarge { get => HomeSet.RecentTileSize == TileSize.Large; set { if (value) SetTileDefault(TileSize.Large); } }

    private void SetCount(int n) { HomeSet.RecentCount = n; ApplyHome(nameof(Count6), nameof(Count12), nameof(Count18)); }
    public bool Count6 { get => HomeSet.RecentCount == 6; set { if (value) SetCount(6); } }
    public bool Count12 { get => HomeSet.RecentCount == 12; set { if (value) SetCount(12); } }
    public bool Count18 { get => HomeSet.RecentCount == 18; set { if (value) SetCount(18); } }

    public string TileOverrideText => HomeSet.TileSizes.Count == 0
        ? "홈에서 게임 타일에 놓고 X를 누르면 그 타일만 작게·보통·크게로 바뀌어요"
        : $"따로 크기를 바꾼 타일 {HomeSet.TileSizes.Count}개 — 홈에서 X로 바꾸거나 아래에서 모두 기본 크기로 돌릴 수 있어요";

    [RelayCommand]
    private void ResetTileSizes()
    {
        HomeSet.TileSizes.Clear();
        ApplyHome(nameof(TileOverrideText));
        _shell.ShowToast("모든 타일을 기본 크기로 돌렸어요");
    }
}
