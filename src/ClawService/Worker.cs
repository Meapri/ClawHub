using ClawCore.Ec;
using ClawCore.Ipc;

namespace ClawService;

public sealed class Worker(ILogger<Worker> log, PipeServer pipe, RequestHandler handler, ControllerManager controller, EcManager ec)
    : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken ct)
    {
        if (File.Exists(Path.Combine(ClawCore.JsonStore.ServiceDir, "ec-state.json"))) ec.Reapply("서비스 시작");

        controller.StatusChanged += () => pipe.Broadcast(new Response { Event = Ops.EventStatus, Status = handler.BuildStatus() });

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

        try
        {
            await pipe.RunAsync(ct);
        }
        finally
        {
            buttons?.Dispose();
        }
    }
}
