using ClawApp.Input;
using ClawCore.Ipc;
using ClawCore.Mapping;

namespace ClawApp.ViewModels;

/// <summary>
/// 3단계 가상 패드: 게임이 도는 동안 그 게임 프로필이 가상 패드를 쓰면 가상 Xbox 패드를 만들고 실제 패드를 숨긴다.
/// 허브를 열 때마다 패드를 다시 시작하지 않도록 게임이 끝날 때까지 유지하고, 허브가 앞에 있는 동안은 일시정지만 한다
/// (게임에는 손을 뗀 패드로 보인다). 앱이 꺼지거나 죽으면 서비스가 실제 패드를 되돌린다.
/// </summary>
public sealed partial class ShellViewModel
{
    private bool _padHidden;

    /// <summary>SyncControllerAsync 안에서 (동기화 잠금을 쥔 채) 부른다. <paramref name="applied"/> = 지금 적용한 게임 매핑.</summary>
    private async Task SyncVirtualPadAsync(MappingProfile? applied)
    {
        var game = RunningGame is not null ? Profiles.ForGame(RunningGame.MappingProfileId) : null;
        var want = game?.Advanced is { UsesVirtualPad: true } adv && Status?.VirtualPadDrivers == true ? adv : null;

        if (want is not null)
        {
            try
            {
                VirtualPad.Start(want); // 먼저 가상 패드 — 숨기는 동안 게임에 패드가 없는 순간이 없게
            }
            catch (Exception ex)
            {
                ShowToast($"가상 패드를 만들지 못했어요: {ex.Message}");
                want = null;
            }
        }

        if (want is not null && !_padHidden)
        {
            var resp = await Service.SendAsync(new Request { Op = Ops.SetPadHidden, Enabled = true });
            if (!resp.Ok)
            {
                ShowToast($"실제 패드를 숨기지 못했어요: {resp.Error}");
                VirtualPad.Stop();
                return;
            }
            _padHidden = true;
        }
        else if (want is null && _padHidden)
        {
            // 먼저 실제 패드를 되돌리고 나서 가상 패드를 뗀다
            try { await Service.SendAsync(new Request { Op = Ops.SetPadHidden, Enabled = false }); }
            catch (Exception) { /* 연결이 끊겼으면 서비스가 알아서 되돌린다 */ }
            _padHidden = false;
        }

        if (want is null) VirtualPad.Stop();
        else if (VirtualPad.Active is { } pad) pad.Paused = applied is null;

        // 가상 패드 없이 스틱 → 마우스·키 (XInput 모드 프로필, 게임이 앞에 있는 동안만)
        StickConverter.Use(want is null && applied is not null && game is { Mode: ClawCore.Controller.GamepadMode.XInput, Advanced: { ConvertsSticks: true } sticks }
            ? sticks : null);
    }

    /// <summary>앱을 끌 때: 실제 패드를 되돌리고 가상 패드를 뗀다.</summary>
    private async Task ShutdownVirtualPadAsync()
    {
        if (_padHidden)
        {
            try { await Service.SendAsync(new Request { Op = Ops.SetPadHidden, Enabled = false }, TimeSpan.FromSeconds(5)); }
            catch (Exception) { /* 서비스가 연결 끊김을 보고 되돌린다 */ }
            _padHidden = false;
        }
        VirtualPad.Stop();
        StickConverter.Use(null);
    }
}
