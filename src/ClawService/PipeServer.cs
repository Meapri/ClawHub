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
            catch (Exception ex) when (ex is IOException or ObjectDisposedException)
            {
                // 연결 직후 끊은 클라이언트(ERROR_NO_DATA 등) — 서비스는 계속
                await pipe.DisposeAsync();
                continue;
            }
            _ = Task.Run(() => HandleClientAsync(pipe, ct), ct);
        }
    }

    /// <summary>
    /// 구독 중인 앱이 하나도 남지 않았을 때 (앱 종료·크래시·로그오프).
    /// 서비스는 이때 게임 매핑과 게임별 성능을 원래대로 돌린다 — 앱 없이 매핑이 남는 일을 막는다.
    /// </summary>
    public event Action? LastSubscriberGone;

    private const int MaxLineLength = 64 * 1024;

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
                if (line.Length > MaxLineLength) break; // 비정상 요청 → 연결 종료
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
            if (_subscribers.TryRemove(id, out var _) && _subscribers.IsEmpty)
            {
                log.LogInformation("마지막 앱 연결 종료 → 매핑·게임별 성능 복원");
                try { LastSubscriberGone?.Invoke(); }
                catch (Exception ex) { log.LogError(ex, "복원 실패"); }
            }
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
