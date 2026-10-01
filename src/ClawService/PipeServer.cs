using System.Collections.Concurrent;
using System.IO.Pipes;
using System.Security.AccessControl;
using System.Security.Principal;
using System.Text.Json;
using ClawCore;
using ClawCore.Ipc;

namespace ClawService;

/// <summary>줄 단위 JSON 파이프 서버. 로컬의 대화형 사용자만 접속 가능.</summary>
public sealed class PipeServer(ILogger<PipeServer> log, RequestHandler handler)
{
    private readonly ConcurrentDictionary<Guid, (StreamWriter Writer, SemaphoreSlim Lock)> _subscribers = new();

    public async Task RunAsync(CancellationToken ct)
    {
        while (!ct.IsCancellationRequested)
        {
            NamedPipeServerStream pipe;
            try
            {
                pipe = NamedPipeServerStreamAcl.Create(Ops.PipeName, PipeDirection.InOut,
                    NamedPipeServerStream.MaxAllowedServerInstances, PipeTransmissionMode.Byte, PipeOptions.Asynchronous,
                    0, 0, CreateSecurity());
            }
            catch (Exception ex) when (ex is UnauthorizedAccessException or IOException)
            {
                // 파이프 하나가 실패해도 서비스 전체(EC 재적용, 버튼 감시)는 계속 돌아야 한다
                log.LogError(ex, "파이프 생성 실패, 재시도");
                await Task.Delay(1000, ct);
                continue;
            }

            try
            {
                await pipe.WaitForConnectionAsync(ct);
            }
            catch (OperationCanceledException)
            {
                await pipe.DisposeAsync();
                return;
            }
            _ = Task.Run(() => HandleClientAsync(pipe, ct), ct);
        }
    }

    public void Broadcast(Response evt)
    {
        var line = JsonSerializer.Serialize(evt, JsonStore.Options).ReplaceLineEndings("");
        foreach (var (id, sub) in _subscribers)
        {
            _ = Task.Run(async () =>
            {
                await sub.Lock.WaitAsync();
                try { await sub.Writer.WriteLineAsync(line); }
                catch (Exception) { _subscribers.TryRemove(id, out _); }
                finally { sub.Lock.Release(); }
            });
        }
    }

    private async Task HandleClientAsync(NamedPipeServerStream pipe, CancellationToken ct)
    {
        var id = Guid.NewGuid();
        await using var _ = pipe;
        using var reader = new StreamReader(pipe);
        var writer = new StreamWriter(pipe) { AutoFlush = true };
        var writeLock = new SemaphoreSlim(1, 1);

        try
        {
            while (!ct.IsCancellationRequested && await reader.ReadLineAsync(ct) is { } line)
            {
                Request? req;
                try { req = JsonSerializer.Deserialize<Request>(line, JsonStore.Options); }
                catch (JsonException) { continue; }
                if (req is null) continue;

                if (req.Op == Ops.Subscribe) _subscribers[id] = (writer, writeLock);

                var resp = await Task.Run(() => handler.Handle(req), ct);
                resp.Id = req.Id;
                await writeLock.WaitAsync(ct);
                try { await writer.WriteLineAsync(JsonSerializer.Serialize(resp, JsonStore.Options).ReplaceLineEndings("")); }
                finally { writeLock.Release(); }
            }
        }
        catch (Exception ex) when (ex is IOException or OperationCanceledException)
        {
            // 클라이언트 종료
        }
        finally
        {
            _subscribers.TryRemove(id, out var _);
            log.LogDebug("클라이언트 연결 종료 {Id}", id);
        }
    }

    private static PipeSecurity CreateSecurity()
    {
        var sec = new PipeSecurity();
        sec.AddAccessRule(new PipeAccessRule(new SecurityIdentifier(WellKnownSidType.LocalSystemSid, null), PipeAccessRights.FullControl, AccessControlType.Allow));
        sec.AddAccessRule(new PipeAccessRule(new SecurityIdentifier(WellKnownSidType.BuiltinAdministratorsSid, null), PipeAccessRights.FullControl, AccessControlType.Allow));
        // 파이프를 만드는 계정 자신 (서비스면 SYSTEM, 개발용 콘솔 실행이면 현재 사용자) — 추가 인스턴스 생성 권한 필요
        if (WindowsIdentity.GetCurrent().User is { } self)
            sec.AddAccessRule(new PipeAccessRule(self, PipeAccessRights.FullControl, AccessControlType.Allow));
        sec.AddAccessRule(new PipeAccessRule(new SecurityIdentifier(WellKnownSidType.InteractiveSid, null), PipeAccessRights.ReadWrite, AccessControlType.Allow));
        sec.AddAccessRule(new PipeAccessRule(new SecurityIdentifier(WellKnownSidType.NetworkSid, null), PipeAccessRights.FullControl, AccessControlType.Deny));
        return sec;
    }
}
