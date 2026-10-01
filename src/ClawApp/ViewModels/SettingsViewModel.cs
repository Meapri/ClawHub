using ClawApp.Services;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Microsoft.Win32;

namespace ClawApp.ViewModels;

public sealed partial class SettingsViewModel(ShellViewModel shell) : ObservableObject, INavPage
{
    public string Title => "설정";
    public IReadOnlyList<Hint> Hints => [new("A", "선택"), new("B", "뒤로")];

    public ShellViewModel Shell => shell;

    public bool StartWithWindows
    {
        get => AppSettings.StartWithWindows;
        set { AppSettings.StartWithWindows = value; OnPropertyChanged(); }
    }

    public bool AutoControllerMode
    {
        get => shell.Settings.AutoControllerMode;
        set { shell.Settings.AutoControllerMode = value; shell.Settings.Save(); OnPropertyChanged(); }
    }

    public bool StartupAuto { get => shell.Settings.StartupMode == StartupMode.Auto; set { if (value) SetStartup(StartupMode.Auto); } }
    public bool StartupShell { get => shell.Settings.StartupMode == StartupMode.Shell; set { if (value) SetStartup(StartupMode.Shell); } }
    public bool StartupCompanion { get => shell.Settings.StartupMode == StartupMode.Companion; set { if (value) SetStartup(StartupMode.Companion); } }

    private void SetStartup(StartupMode mode)
    {
        shell.Settings.StartupMode = mode;
        shell.Settings.Save();
        shell.IsCompanion = mode == StartupMode.Companion || (mode == StartupMode.Auto && shell.Settings.ResolveCompanion());
        OnPropertyChanged(nameof(StartupAuto));
        OnPropertyChanged(nameof(StartupShell));
        OnPropertyChanged(nameof(StartupCompanion));
        OnPropertyChanged(nameof(CompanionText));
    }

    public string CompanionText => shell.IsCompanion
        ? "지금: Xbox 모드 동반 — 홈 화면은 Xbox 앱, ClawHub는 버튼·자동 매핑 담당"
        : "지금: ClawHub 전체 화면";

    public string ServiceText => shell.ServiceConnected ? "실행 중" : "연결 안 됨 — install.ps1로 서비스를 설치하세요";

    public string ControllerText => shell.Status is { ControllerConnected: true } s
        ? $"펌웨어 {s.ControllerFirmware} · {s.ControllerMode} 모드{(s.MappingActive ? " · 게임 매핑 적용 중" : "")}"
        : "연결 안 됨";

    public string EcText => shell.Status is { EcAvailable: true } s
        ? $"{s.EcVersion} · {(s.EcWritable ? "제어 가능" : "읽기 전용")}"
        : "사용 불가";

    public string Warnings => shell.Status?.Warnings is { Count: > 0 } w ? string.Join("\n", w) : "";

    public void OnShown()
    {
        _ = shell.RefreshStatusAsync().ContinueWith(_ => OnPropertyChanged(string.Empty),
            TaskScheduler.FromCurrentSynchronizationContext());
    }

    [RelayCommand]
    private void AddGame()
    {
        var dlg = new OpenFileDialog
        {
            Title = "라이브러리에 추가할 게임 실행 파일",
            Filter = "실행 파일 (*.exe;*.lnk;*.url)|*.exe;*.lnk;*.url",
        };
        if (dlg.ShowDialog() != true) return;
        var game = shell.Library.AddCustom(Path.GetFileNameWithoutExtension(dlg.FileName), dlg.FileName);
        shell.ShowToast($"{game.Title}을(를) 추가했습니다");
        shell.Home.Reload();
    }

    [RelayCommand]
    private void Rescan()
    {
        shell.Library.Refresh();
        shell.Home.Reload();
        shell.ShowToast($"게임 {shell.Library.Visible.Count()}개를 찾았습니다");
    }

    [RelayCommand]
    private void UnhideAll()
    {
        var n = shell.Library.UnhideAll();
        shell.Home.Reload();
        shell.ShowToast(n > 0 ? $"숨긴 게임 {n}개를 다시 표시합니다" : "숨긴 게임이 없습니다");
    }
}
