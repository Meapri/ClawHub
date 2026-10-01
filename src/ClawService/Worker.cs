using ClawCore.Ec;
using ClawCore.Ipc;

namespace ClawService;

public sealed class Worker(ILogger<Worker> log, PipeServer pipe, RequestHandler handler, ControllerManager controller, EcManager ec,
    SystemManager system) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken ct)
    {
        if (File.Exists(Path.Combine(ClawCore.JsonStore.ServiceDir, "ec-state.json"))) ec.Reapply("서비스 시작");
        system.Start();

        controller.StatusChanged += () => pipe.Broadcast(new Response { Event = Ops.EventStatus, Status = handler.BuildStatus() });
        // Game Bar 위젯의 명령(프로필 변경·게임 종료·ClawHub 열기)을 구독 중인 런처 앱에 전달
        // 위젯 명령은 위젯과 같은 (지금 화면을 가진) 세션의 런처에게만
        handler.RelayAppCommand = cmd => pipe.Broadcast(new Response { Event = Ops.EventAppCommand, Command = cmd }, activeConsoleOnly: true);
        pipe.LastSubscriberGone += () =>
        {
            system.AppState = null; // 런처 앱이 없으면 위젯은 게임·매핑 정보를 숨긴다
            controller.RestoreBaseline();
            ec.RestoreDefault();
        };
        _ = Task.Run(() => WatchConsoleSessionAsync(ct), ct);

        MsiButtonWatcher? buttons = null;
        try
        {
            buttons = new MsiButtonWatcher();
            buttons.Pressed += b =>
            {
                log.LogInformation("버튼 {Button}", b);
                // 본체 버튼은 지금 화면 앞의 사용자 것 — 빠른 사용자 전환으로 뒤에 남은 세션의 앱은 받지 않는다
                pipe.Broadcast(new Response { Event = Ops.EventButton, Button = b }, activeConsoleOnly: true);
            };
        }
        catch (Exception ex)
        {
            log.LogError(ex, "MSI_Event 감시 실패 — 전용 버튼을 사용할 수 없습니다");
        }

        // 파이프 루프가 예기치 않게 죽어도 서비스(EC 재적용 등)는 유지하고 재시작한다
        while (!ct.IsCancellationRequested)
        {
            try
            {
                await pipe.RunAsync(ct);
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested)
            {
                break;
            }
            catch (Exception ex)
            {
                log.LogError(ex, "파이프 서버 오류, 2초 후 재시작");
                await Task.Delay(2000, ct).ContinueWith(_ => { });
            }
        }
        buttons?.Dispose();
    }

    /// <summary>
    /// 빠른 사용자 전환: 화면(콘솔)을 다른 세션이 가져가면 이전 사용자의 게임 매핑·게임별 성능·위젯 상태를 풀어
    /// 새 사용자가 남의 키 매핑으로 조작하는 일이 없게 한다. 돌아오면 그 세션의 앱이 상태 이벤트를 보고 다시 적용한다.
    /// </summary>
    private async Task WatchConsoleSessionAsync(CancellationToken ct)
    {
        var last = PipeAccess.ActiveConsoleSession;
        while (!ct.IsCancellationRequested)
        {
            try { await Task.Delay(2000, ct); }
            catch (OperationCanceledException) { return; }

            var now = PipeAccess.ActiveConsoleSession;
            if (now == last || now == uint.MaxValue) continue; // 전환 중(0xFFFFFFFF)은 무시
            log.LogInformation("콘솔 세션 전환 {From} → {To}: 매핑·게임별 성능 복원", last, now);
            last = now;
            try
            {
                system.AppState = null;
                controller.RestoreBaseline();
                ec.RestoreDefault();
            }
            catch (Exception ex) { log.LogError(ex, "세션 전환 복원 실패"); }
            pipe.Broadcast(new Response { Event = Ops.EventStatus, Status = handler.BuildStatus() });
        }
    }
}
