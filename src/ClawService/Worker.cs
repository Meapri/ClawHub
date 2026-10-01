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
        handler.RelayAppCommand = cmd => pipe.Broadcast(new Response { Event = Ops.EventAppCommand, Command = cmd });
        pipe.LastSubscriberGone += () =>
        {
            system.AppState = null; // 런처 앱이 없으면 위젯은 게임·매핑 정보를 숨긴다
            controller.RestoreBaseline();
            ec.RestoreDefault();
        };

        MsiButtonWatcher? buttons = null;
        try
        {
            buttons = new MsiButtonWatcher();
            buttons.Pressed += b =>
            {
                log.LogInformation("버튼 {Button}", b);
                pipe.Broadcast(new Response { Event = Ops.EventButton, Button = b });
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
}
