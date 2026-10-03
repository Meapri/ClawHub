using ClawApp.Services;

namespace ClawApp.ViewModels;

/// <summary>
/// 게임별 화면 주사율: 게임이 시작되면 그 게임의 주사율로 바꾸고, 끝나면 바꾸기 전 주사율로 되돌린다
/// (SteamOS의 게임별 화면 주사율). 해상도는 그대로 둔다.
/// </summary>
public sealed partial class ShellViewModel
{
    /// <summary>게임 때문에 바꾸기 전 주사율 (바꾸지 않았으면 null).</summary>
    private int? _hzBeforeGame;

    /// <summary>지금 게임의 주사율을 적용하거나(게임 중), 원래대로 되돌린다(게임 끝).</summary>
    public void ApplyGameRefresh()
    {
        if (DisplayControls.Current() is not { } now) return;
        if (RunningGame?.RefreshHz is { } want)
        {
            if (now.Hz == want) return;
            _hzBeforeGame ??= now.Hz;
            if (DisplayControls.Apply(now with { Hz = want }) is { } err) Notify("Icon.Monitor", err, warn: true);
        }
        else if (_hzBeforeGame is { } before)
        {
            _hzBeforeGame = null;
            if (now.Hz != before) DisplayControls.Apply(now with { Hz = before });
        }
    }
}
