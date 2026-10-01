using ClawApp.Services;

namespace ClawApp.ViewModels;

/// <summary>편의 기능 설정: 데스크톱 모드 오른쪽 버튼, SteamGridDB 키. UI는 Views/Sections/DesktopModeSection.xaml.</summary>
public sealed partial class SettingsViewModel
{
    public bool DesktopRightKeyboard
    {
        get => _shell.Settings.DesktopRightButton == DesktopRightButton.TouchKeyboard;
        set { if (value) SetDesktopRight(DesktopRightButton.TouchKeyboard); }
    }

    public bool DesktopRightGameBar
    {
        get => _shell.Settings.DesktopRightButton == DesktopRightButton.GameBar;
        set { if (value) SetDesktopRight(DesktopRightButton.GameBar); }
    }

    private void SetDesktopRight(DesktopRightButton choice)
    {
        _shell.Settings.DesktopRightButton = choice;
        _shell.Settings.Save();
        OnPropertyChanged(nameof(DesktopRightKeyboard));
        OnPropertyChanged(nameof(DesktopRightGameBar));
    }

    /// <summary>TextBox에서 벗어날 때 저장된다. 빈 값 = 사용 안 함.</summary>
    public string SteamGridDbKey
    {
        get => _shell.Settings.SteamGridDbKey ?? "";
        set
        {
            var key = string.IsNullOrWhiteSpace(value) ? null : value.Trim();
            if (key == _shell.Settings.SteamGridDbKey) return;
            _shell.Settings.SteamGridDbKey = key;
            _shell.Settings.Save();
            OnPropertyChanged();
        }
    }
}
