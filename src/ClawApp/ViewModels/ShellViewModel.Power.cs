using ClawApp.Services;

namespace ClawApp.ViewModels;

/// <summary>
/// 전원 연결/배터리별 설정. 우선순위 (FPS·성능 모드 모두):
/// 게임의 배터리 전용 값(배터리일 때) → 게임 값 → 기본값의 배터리 전용 값(배터리일 때) → 기본값.
/// 성능 기본값(전원 연결/배터리)은 서비스가 전원 상태를 보고 직접 고른다 — 앱은 게임별 임시 값만 정한다.
/// </summary>
public sealed partial class ShellViewModel
{
    /// <summary>배터리로 돌고 있는지 (앱이 본 값).</summary>
    public bool OnBattery => PowerWatcher.OnBattery;

    private void InitPower()
    {
        PowerWatcher.Changed += _ => Ui(OnPowerSourceChangedAsync);
        PowerWatcher.Start();
    }

    private async Task OnPowerSourceChangedAsync()
    {
        OnPropertyChanged(nameof(OnBattery));
        await SyncControllerAsync(); // 게임별 배터리 성능·FPS 다시 계산
        await RefreshStatusAsync();  // 서비스가 바꾼 기본 성능 반영
        await PushAppStateAsync();
        if (IsQuickAccessOpen) QuickAccess.Refresh();
        else QuickAccess.Power.Refresh();
    }

    /// <summary>지금 적용할 FPS 제한 (null = ClawHub가 관리 안 함 → 시작 때 드라이버 값).</summary>
    private int? EffectiveFps()
    {
        var battery = OnBattery;
        return RunningGame?.EffectiveFpsLimit(battery)
            ?? (battery && Settings.FpsLimitOnBattery is { } b ? b : Settings.FpsLimit);
    }

    /// <summary>배터리 전용 기본 FPS 제한 저장 (null = 따로 안 씀) 후 바로 적용.</summary>
    public async Task SetDefaultFpsOnBatteryAsync(int? fps)
    {
        Settings.FpsLimitOnBattery = fps;
        Settings.Save();
        await SyncControllerAsync();
    }
}
