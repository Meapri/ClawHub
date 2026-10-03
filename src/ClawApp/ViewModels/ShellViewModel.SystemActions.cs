using ClawApp.Services;
using ClawCore.Controller;

namespace ClawApp.ViewModels;

/// <summary>
/// 버튼에 넣은 시스템 동작 (SYS_ — 마이크 켜기/끄기, 스크린샷, 밝기 등). 고급 입력 엔진이 버튼을 받아 UI 스레드로 넘긴다.
/// 게임 중에는 화면 위 알림(OSD)으로, 허브가 보이면 토스트로 결과를 알려 준다.
/// </summary>
public sealed partial class ShellViewModel
{
    /// <summary>결과 알림: 허브가 앞이면 토스트, 아니면 게임 위 알림.</summary>
    public void Notify(string iconKey, string text, bool warn = false)
    {
        if (IsWindowVisible) ShowToast(text);
        else Views.OsdWindow.Show(iconKey, text, warn);
    }

    public void RunSystemAction(string id)
    {
        switch (id[SystemKeys.Prefix.Length..])
        {
            case "MIC":
                _ = Task.Run(SystemControls.ToggleMicrophone).ContinueWith(t => Ui(() =>
                {
                    if ((t.IsCompletedSuccessfully ? t.Result : null) is not { } on) Notify("Icon.MicOff", "마이크를 찾지 못했어요", warn: true);
                    else Notify(on ? "Icon.Mic" : "Icon.MicOff", on ? "마이크 켜짐" : "마이크 꺼짐", warn: !on);
                    QuickAccess.Refresh();
                }));
                break;
            case "KEYBOARD":
                SystemControls.ShowTouchKeyboard();
                break;
            case "HUB":
                ShowFromOutside();
                break;
            case "OVERLAY":
                CycleOverlayLevel();
                break;
            case "NIGHT":
                Night.ToggleCommand.Execute(null);
                Notify("Icon.Moon", Night.Active ? "야간 모드 켜짐" : "야간 모드 꺼짐");
                break;
            case "XBOXMODE":
                ToggleXboxModeCommand.Execute(null);
                break;
            case "QUICK":
                OnQuickSettingsButton();
                break;
            case "SCREENSHOT":
                _ = CaptureAsync(CaptureAction.Screenshot);
                break;
            case "RECORD":
                _ = CaptureAsync(CaptureAction.ToggleRecording);
                break;
            case "CLIP":
                _ = CaptureAsync(CaptureAction.SaveClip);
                break;
            case "PROFILE":
                if (RunningGame is null) { Notify("Icon.Gamepad2", "게임 중에만 프로필을 바꿀 수 있어요", warn: true); break; }
                CycleTargetProfile();
                Notify("Icon.Gamepad2", $"매핑 · {Profiles.LabelForGame(RunningGame.MappingProfileId)}");
                _ = PushAppStateAsync();
                _ = SyncControllerAsync();
                break;
            case "BRIGHT_UP":
            case "BRIGHT_DOWN":
                var up = id.EndsWith("UP", StringComparison.Ordinal);
                _ = Task.Run(() =>
                {
                    var now = SystemControls.GetBrightness() ?? 50;
                    var next = Math.Clamp((now + (up ? 10 : -10)) / 10 * 10, 0, 100); // 10 단위로 맞춘다
                    SystemControls.SetBrightness(next);
                    return next;
                }).ContinueWith(t => Ui(() => { if (t.IsCompletedSuccessfully) Notify(up ? "Icon.Sun" : "Icon.SunDim", $"밝기 {t.Result}%"); }));
                break;
        }
    }
}
