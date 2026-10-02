using System.ComponentModel;
using System.Windows.Media;
using ClawApp.Services;

namespace ClawApp.ViewModels;

/// <summary>전체 화면 배경 (설정 → 홈 화면 → 배경). MainWindow의 배경 층이 바인딩한다.</summary>
public sealed partial class ShellViewModel
{
    private ImageSource? _customBackdrop;
    private string? _customBackdropPath;

    /// <summary>기본 배경 그림: 게임 아트면 홈의 "이어서 플레이" 게임, 고정 이미지면 그 그림, 단색이면 없음.</summary>
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
        Motion.Backdrop.FollowFocus = Settings.Home.BackdropMode == BackdropMode.GameArt;
    }

    /// <summary>홈·배경 설정을 바꾼 뒤: 저장하고 화면에 바로 반영.</summary>
    public void ApplyHomeSettings()
    {
        Settings.Save();
        Motion.Backdrop.FollowFocus = Settings.Home.BackdropMode == BackdropMode.GameArt;
        OnPropertyChanged(nameof(BackdropDefault));
        OnPropertyChanged(nameof(BackdropBlur));
        OnPropertyChanged(nameof(BackdropDim));
        Motion.Backdrop.ShowDefault();
        Home.Reload();
    }
}
