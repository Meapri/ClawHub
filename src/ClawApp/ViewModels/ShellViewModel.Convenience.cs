using ClawApp.Services;
using CommunityToolkit.Mvvm.Input;

namespace ClawApp.ViewModels;

/// <summary>편의 기능: Game Bar 캡처, 데스크톱 모드 오른쪽 버튼.</summary>
public sealed partial class ShellViewModel
{
    /// <summary>런처를 숨긴 뒤 단축키를 보내기까지 기다리는 시간 (게임 창이 다시 앞으로 올 때까지).</summary>
    private static readonly TimeSpan CaptureFocusDelay = TimeSpan.FromMilliseconds(400);

    [RelayCommand] private Task Screenshot() => CaptureAsync(CaptureAction.Screenshot);
    [RelayCommand] private Task ToggleRecording() => CaptureAsync(CaptureAction.ToggleRecording);
    [RelayCommand] private Task SaveLast30s() => CaptureAsync(CaptureAction.SaveClip);

    /// <summary>
    /// Game Bar 캡처. 캡처는 앞에 있는 창을 대상으로 하므로 런처가 보이면 먼저 숨기고(게임으로 돌아가기)
    /// 게임 창이 앞으로 올 시간을 준 뒤 단축키를 보낸다. 위젯 명령(AppCommands.Screenshot 등)도 이 경로.
    /// </summary>
    public async Task CaptureAsync(CaptureAction action)
    {
        if (IsWindowVisible)
        {
            ReturnToBackground();
            await Task.Delay(CaptureFocusDelay);
        }
        if (!GameCapture.Send(action))
            ShowToast($"{GameCapture.Label(action)} 단축키를 보내지 못했습니다");
    }

    /// <summary>
    /// "데스크톱으로" 중(컨트롤러 Desktop 모드, 스틱 = 마우스)에 오른쪽 본체 버튼을 눌렀을 때.
    /// 설정이 터치 키보드면 키보드를 토글하고 true, 아니면 false(평소처럼 Game Bar).
    /// 게임이 실행 중이면 컨트롤러가 XInput으로 돌아가 있으므로 평소 동작.
    /// </summary>
    private bool TryDesktopRightButton()
    {
        if (!_inDesktop || IsWindowVisible || RunningGame is not null) return false;
        if (Settings.DesktopRightButton != DesktopRightButton.TouchKeyboard) return false;
        SystemControls.ShowTouchKeyboard();
        return true;
    }
}
