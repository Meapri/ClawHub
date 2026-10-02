using System.IO;
using System.Windows.Media;
using ClawApp.Services;
using ClawCore;
using CommunityToolkit.Mvvm.Input;
using Microsoft.Win32;

namespace ClawApp.ViewModels;

/// <summary>
/// 설정 → 홈 화면: 배경(게임 아트·전체 화면 이미지·타일 그림·단색, 흐림·덮개·타일 투명도·사진 강조색)과 타일 크기.
/// </summary>
public sealed partial class SettingsViewModel
{
    private HomeSettings HomeSet => _shell.Settings.Home;

    private static readonly string[] BackdropProps =
    [
        nameof(BgGameArt), nameof(BgImage), nameof(BgTilePicture), nameof(BgPlain), nameof(BackdropModeText),
        nameof(BackdropPreview), nameof(HasBackdropPreview), nameof(UsesPicture), nameof(UsesFullScreenArt),
        nameof(TileTransparency), nameof(AccentFromImage), nameof(BackdropBlur), nameof(BackdropDim),
    ];

    private void ApplyHome(params string[] changed)
    {
        _shell.ApplyHomeSettings();
        foreach (var p in changed) OnPropertyChanged(p);
    }

    // ── 배경 ──────────────────────────────────────────────

    private static bool NeedsPicture(BackdropMode m) => m is BackdropMode.Image or BackdropMode.TilePicture;

    private void SetBackdropMode(BackdropMode mode)
    {
        if (NeedsPicture(mode) && HomeSet.BackdropImage is null)
        {
            // 아직 고른 사진이 없으면 바로 고르러 간다 (고르면 그때 이 방식으로 바뀜)
            _shell.Navigate(new BackdropPickerViewModel(_shell, mode));
            return;
        }
        // 타일 그림은 타일 색이 거의 비쳐야 사진이 보인다 — 처음 켤 때 투명도를 올려 둔다
        if (mode == BackdropMode.TilePicture && HomeSet.TileTransparency < 60) HomeSet.TileTransparency = 85;
        HomeSet.BackdropMode = mode;
        ApplyHome(BackdropProps);
    }

    public bool BgGameArt { get => HomeSet.BackdropMode == BackdropMode.GameArt; set { if (value) SetBackdropMode(BackdropMode.GameArt); } }
    public bool BgImage { get => HomeSet.BackdropMode == BackdropMode.Image; set { if (value) SetBackdropMode(BackdropMode.Image); } }
    public bool BgTilePicture { get => HomeSet.BackdropMode == BackdropMode.TilePicture; set { if (value) SetBackdropMode(BackdropMode.TilePicture); } }
    public bool BgPlain { get => HomeSet.BackdropMode == BackdropMode.Plain; set { if (value) SetBackdropMode(BackdropMode.Plain); } }

    public string BackdropModeText => HomeSet.BackdropMode switch
    {
        BackdropMode.Image => "고른 사진이 화면 전체에 깔리고, 타일은 투명도만큼 비쳐요",
        BackdropMode.TilePicture => "Windows 10 Mobile처럼 바탕은 단색이고, 사진은 타일 안에서만 보여요. 가로로 넘기면 사진이 천천히 따라와요",
        BackdropMode.Plain => "그림 없이 테마 색만 써요 (가장 깔끔하고 배터리에도 조금 유리해요)",
        _ => "포커스한 게임의 아트가 흐리게 배경에 깔리고, 게임을 옮겨 다니면 바뀌어요",
    };

    /// <summary>사진을 쓰는 방식 (전체 화면 이미지·타일 그림) — 사진 고르기·강조색 뽑기가 보인다.</summary>
    public bool UsesPicture => NeedsPicture(HomeSet.BackdropMode);
    /// <summary>흐림·덮개는 화면 전체에 그림이 깔릴 때만 의미가 있다.</summary>
    public bool UsesFullScreenArt => HomeSet.BackdropMode is BackdropMode.GameArt or BackdropMode.Image;

    /// <summary>고른 사진 미리보기.</summary>
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

    public int TileTransparency
    {
        get => HomeSet.TileTransparency;
        set { if (HomeSet.TileTransparency == value) return; HomeSet.TileTransparency = value; ApplyHome(nameof(TileTransparency)); }
    }

    public bool AccentFromImage
    {
        get => HomeSet.AccentFromImage;
        set
        {
            HomeSet.AccentFromImage = value;
            ApplyHome(nameof(AccentFromImage));
            if (value && !UsesPicture) _shell.ShowToast("전체 화면 이미지나 타일 그림을 고르면 그 사진에서 색을 뽑아요");
        }
    }

    /// <summary>게임 아트에서 고르기 — 지금 방식이 타일 그림이면 그대로, 아니면 전체 화면 이미지로.</summary>
    [RelayCommand]
    private void PickBackdropFromGames() => _shell.Navigate(new BackdropPickerViewModel(_shell, PictureMode));

    private BackdropMode PictureMode => HomeSet.BackdropMode == BackdropMode.TilePicture ? BackdropMode.TilePicture : BackdropMode.Image;

    /// <summary>사진 파일 (터치·마우스). 원본이 옮겨져도 남게 사용자 데이터 폴더에 복사한다.</summary>
    [RelayCommand]
    private void PickBackdropFile()
    {
        var dlg = new OpenFileDialog { Title = "배경으로 쓸 사진", Filter = "그림 (*.jpg;*.jpeg;*.png;*.bmp)|*.jpg;*.jpeg;*.png;*.bmp" };
        if (dlg.ShowDialog() != true) return;
        try
        {
            var dir = Directory.CreateDirectory(Path.Combine(JsonStore.UserDir, "backgrounds")).FullName;
            var dest = Path.Combine(dir, $"{DateTime.Now:yyyyMMdd-HHmmss}{Path.GetExtension(dlg.FileName)}");
            File.Copy(dlg.FileName, dest, overwrite: true);
            HomeSet.BackdropImage = dest;
            SetBackdropMode(PictureMode);
            _shell.ShowToast("배경을 바꿨어요");
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            _shell.ShowToast($"사진을 가져오지 못했어요: {ex.Message}");
        }
    }

    [RelayCommand]
    private void ResetBackdrop()
    {
        var d = new HomeSettings();
        HomeSet.BackdropMode = d.BackdropMode;
        HomeSet.BackdropBlur = d.BackdropBlur;
        HomeSet.BackdropDim = d.BackdropDim;
        HomeSet.TileTransparency = d.TileTransparency;
        HomeSet.AccentFromImage = d.AccentFromImage;
        ApplyHome(BackdropProps);
        _shell.ShowToast("배경을 기본값으로 돌렸어요");
    }

    /// <summary>배경 고르기에서 돌아왔을 때 다시 읽기.</summary>
    private void RefreshHomeSection()
    {
        foreach (var p in BackdropProps) OnPropertyChanged(p);
        OnPropertyChanged(nameof(StartActionItems));
    }

    // ── 시작 화면 ─────────────────────────────────────────

    public bool AutoPinNew
    {
        get => _shell.Settings.Start.AutoPinNew;
        set { _shell.Settings.Start.AutoPinNew = value; _shell.Settings.Save(); OnPropertyChanged(); }
    }

    /// <summary>뺐던 ClawHub 기능 타일을 다시 고정하는 목록.</summary>
    public IReadOnlyList<StartActionItem> StartActionItems =>
        [.. StartActions.All.Select(a => new StartActionItem(a, _shell.Start.IsActionPinned(a.Key)))];

    [RelayCommand]
    private void PinAction(StartActionItem item)
    {
        if (item.Pinned) { _shell.ShowToast($"{item.Info.Label} 타일은 이미 시작 화면에 있어요"); return; }
        _shell.Start.PinAction(item.Info.Key);
        OnPropertyChanged(nameof(StartActionItems));
        _shell.ShowToast($"{item.Info.Label} 타일을 시작 화면 끝에 붙였어요");
    }

    [RelayCommand(AllowConcurrentExecutions = true)]
    private async Task ResetStart()
    {
        if (!await _shell.ConfirmAsync("시작 화면을 처음 배치로 되돌릴까요?", "폴더·타일 크기·순서가 모두 지워져요.", "되돌리기", danger: true)) return;
        _shell.Start.ResetToDefault(_shell.Library);
        OnPropertyChanged(nameof(StartActionItems));
        _shell.ShowToast("시작 화면을 처음 배치로 되돌렸어요");
    }
}

public sealed record StartActionItem(StartActions.Info Info, bool Pinned)
{
    public string Glyph => System.Windows.Application.Current?.TryFindResource("Icon." + Info.Icon) as string ?? "";
}
