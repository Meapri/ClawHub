using ClawApp.Services;
using ClawCore.Games;

namespace ClawApp.ViewModels;

/// <summary>
/// 게임 중 화면 꺼짐·절전 방지 (<see cref="AppSettings.KeepAwakeWhileGaming"/>).
/// 게임이 실행 중이고 런처가 숨어 있을 때(=게임 화면)만 켠다. 런처로 돌아오면 "일시 정지"로 보고 Windows 설정대로 꺼지게 둔다.
/// </summary>
public sealed partial class ShellViewModel
{
    private readonly KeepAwake _keepAwake = new();

    /// <summary>지금 화면 꺼짐·절전을 막고 있는지.</summary>
    public bool KeepingAwake => _keepAwake.Requested;

    // 생성기가 만드는 (old, new) 버전을 쓴다 — (value) 버전은 ShellViewModel.cs가 이미 구현
    partial void OnRunningGameChanged(GameEntry? oldValue, GameEntry? newValue) => UpdateKeepAwake();
    partial void OnIsWindowVisibleChanged(bool oldValue, bool newValue) => UpdateKeepAwake();

    public void UpdateKeepAwake()
    {
        var on = Settings.KeepAwakeWhileGaming && RunningGame is not null && !IsWindowVisible;
        if (on == _keepAwake.Requested) return;
        _keepAwake.Set(on);
        OnPropertyChanged(nameof(KeepingAwake));
    }
}
