using ClawApp.Services;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;

namespace ClawApp.ViewModels;

/// <summary>
/// 상단 상태 표시줄 항목을 누르면 열리는 작은 패널 (성능·시스템·컨트롤러·네트워크·밝기와 소리·배터리·날짜).
/// 값과 동작은 빠른 설정(QuickAccess)과 같은 것을 쓴다. 바깥을 누르거나 B로 닫힌다.
/// </summary>
public sealed partial class ShellViewModel
{
    /// <summary>열린 패널 키 (null = 닫힘): service · perf · system · controller · network · sound · battery · clock.</summary>
    [ObservableProperty] private string? _statusPanel;

    partial void OnStatusPanelChanged(string? value)
    {
        if (value is not null) StatusPanelShown = value; // 닫히는 동안에도 그 칸을 보여 준다 (ShellViewModel.Shown.cs)
        OnPropertyChanged(nameof(IsAnyPanelOpen));
        // 날짜·시간 패널이 열려 있는 동안만 초 단위로 갱신 (평소 시계는 10초 주기)
        if (value == "clock")
        {
            UpdateClockPanel();
            _secondsTimer ??= NewSecondsTimer();
            _secondsTimer.Start();
        }
        else _secondsTimer?.Stop();
    }

    private System.Windows.Threading.DispatcherTimer? _secondsTimer;

    private System.Windows.Threading.DispatcherTimer NewSecondsTimer()
    {
        var t = new System.Windows.Threading.DispatcherTimer { Interval = TimeSpan.FromMilliseconds(250) };
        t.Tick += (_, _) => UpdateClockPanel();
        return t;
    }

    /// <summary>날짜·시간 패널의 큰 시계 (초까지).</summary>
    [ObservableProperty] private string _panelClock = "";
    [ObservableProperty] private string _panelSeconds = "";

    private void UpdateClockPanel()
    {
        var now = DateTime.Now;
        PanelClock = now.ToString("tt h:mm");
        PanelSeconds = now.ToString(":ss");
        if (Clock != PanelClock) Clock = PanelClock; // 분이 바뀌면 상태 표시줄 시계도 바로
    }

    [RelayCommand]
    private void OpenStatus(string key)
    {
        if (StatusPanel == key)
        {
            ClosePanels();
            return;
        }
        IsQuickAccessOpen = false;
        QuickAccess.Refresh(); // 성능·밝기·볼륨·충전 한도 값을 읽고 실시간 상태를 2초마다 갱신
        StatusPanel = key;
        OnPropertyChanged(nameof(DateText));
        OnPropertyChanged(nameof(UptimeText));
        AfterPanelChange();
    }

    public string DateText => DateTime.Now.ToString("yyyy년 M월 d일 dddd");
    public string UptimeText
    {
        get
        {
            var up = TimeSpan.FromMilliseconds(Environment.TickCount64);
            return up.TotalDays >= 1 ? $"켠 지 {(int)up.TotalDays}일 {up.Hours}시간" : $"켠 지 {up.Hours}시간 {up.Minutes}분";
        }
    }

    public string ControllerStatusText => ControllerConnected
        ? Status?.ControllerMode is { } m ? $"연결됨 · {(m.ToString() == "Desktop" ? "데스크톱 모드 (스틱 = 마우스)" : "XInput 모드")}" : "연결됨"
        : "연결 안 됨";

    /// <summary>Windows 설정 화면 열기 (스틱이 마우스로, 왼쪽 버튼으로 돌아온다).</summary>
    [RelayCommand]
    private void OpenWindowsPage(string page)
    {
        ClosePanels();
        SystemControls.OpenWindowsSettings(page);
        ShowOtherWindow();
        ShowToast("왼쪽 MSI 버튼을 누르면 ClawHub로 돌아와요");
    }

    /// <summary>패널에서 다른 화면으로 갈 때 (닫고 이동).</summary>
    [RelayCommand]
    private void OpenSettingsAt(string category)
    {
        ClosePanels();
        var settings = new SettingsViewModel(this);
        if (settings.Categories.FirstOrDefault(c => c.Key == category) is { } c) settings.Selected = c;
        IsShellVisible = true;
        Navigate(settings);
    }
}
