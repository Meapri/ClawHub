using System.Collections.Concurrent;
using System.IO.Pipes;
using System.Text.Json;
using System.Threading.Channels;
using ClawCore;
using ClawCore.Ipc;

namespace ClawService;

/// <summary>
/// 줄 단위 JSON 파이프 서버. 로컬의 대화형 사용자만 접속 가능.
/// 하드웨어를 바꾸는 요청과 본체 버튼 이벤트는 지금 화면(콘솔)을 가진 세션의 클라이언트에게만 허용한다
/// (빠른 사용자 전환 시 뒤에 남은 다른 사용자의 앱은 상태 조회만 가능).
/// </summary>
public sealed class PipeServer(ILogger<PipeServer> log, RequestHandler handler)
{
    /// <summary>다른 세션의 클라이언트도 쓸 수 있는 읽기 전용 요청.</summary>
    private static readonly HashSet<string> ReadOnlyOps = [Ops.GetStatus, Ops.GetTelemetry, Ops.Subscribe];

    private sealed class Client(StreamWriter writer, SemaphoreSlim writeLock, uint? session)
    {
        public StreamWriter Writer { get; } = writer;
        public SemaphoreSlim WriteLock { get; } = writeLock;
        public uint? Session { get; } = session;
        public bool? IsAdmin { get; set; }

        /// <summary>구독 이벤트를 순서대로 내보내는 큐 (구독자마다 하나, 쓰기 작업도 하나).</summary>
        public Channel<string> Events { get; } = Channel.CreateBounded<string>(
            new BoundedChannelOptions(256) { FullMode = BoundedChannelFullMode.DropOldest, SingleReader = true });

        public bool IsActiveConsole => Session is { } s && s == PipeAccess.ActiveConsoleSession;
    }

    private readonly ConcurrentDictionary<Guid, Client> _subscribers = new();

    public async Task RunAsync(CancellationToken ct)
    {
        while (!ct.IsCancellationRequested)
        {
            NamedPipeServerStream pipe;
            try
            {
                pipe = PipeAccess.Create(Ops.PipeName);
            }
            catch (Exception ex) when (ex is UnauthorizedAccessException or IOException or System.ComponentModel.Win32Exception)
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
            if (!PipeAccess.IsAllowedClient(pipe, out var who))
            {
                log.LogWarning("허용되지 않은 클라이언트 거부: {Who}", who);
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

    /// <summary>
    /// 구독자에게 이벤트를 보낸다. 구독자마다 순서가 보장된다 (하나의 큐 + 하나의 쓰기 작업).
    /// <paramref name="activeConsoleOnly"/>: 본체 버튼·위젯 명령처럼 지금 화면을 가진 사용자에게만 의미 있는 이벤트.
    /// </summary>
    public void Broadcast(Response evt, bool activeConsoleOnly = false)
    {
        var line = JsonSerializer.Serialize(evt, JsonStore.Options).ReplaceLineEndings("");
        foreach (var (_, sub) in _subscribers)
        {
            if (activeConsoleOnly && !sub.IsActiveConsole) continue;
            sub.Events.Writer.TryWrite(line); // 큐가 가득 차면(앱이 안 읽음) 가장 오래된 것을 버린다
        }
    }

    private async Task PumpEventsAsync(Client sub)
    {
        try
        {
            await foreach (var line in sub.Events.Reader.ReadAllAsync())
            {
                await sub.WriteLock.WaitAsync();
                try { await sub.Writer.WriteLineAsync(line); }
                finally { sub.WriteLock.Release(); }
            }
        }
        catch (Exception)
        {
            // 연결 끊김 — 구독 해제는 읽기 루프의 finally가 한다
            sub.Events.Writer.TryComplete();
        }
    }

    private async Task HandleClientAsync(NamedPipeServerStream pipe, CancellationToken ct)
    {
        var id = Guid.NewGuid();
        await using var pipeOwner = pipe;
        using var reader = new StreamReader(pipe);
        var writer = new StreamWriter(pipe) { AutoFlush = true };
        var writeLock = new SemaphoreSlim(1, 1);
        var client = new Client(writer, writeLock, PipeAccess.ClientSession(pipe));

        try
        {
            while (!ct.IsCancellationRequested && await reader.ReadLineAsync(ct) is { } line)
            {
                if (line.Length > MaxLineLength) break; // 비정상 요청 → 연결 종료
                Request? req;
                Response resp;
                try
                {
                    req = JsonSerializer.Deserialize<Request>(line, JsonStore.Options);
                }
                catch (JsonException ex)
                {
                    // 잘못된 요청도 반드시 응답한다 (응답이 없으면 클라이언트가 무한 대기)
                    resp = new Response { Id = TryReadId(line), Ok = false, Error = $"잘못된 요청: {ex.Message}" };
                    await WriteAsync(writer, writeLock, resp, ct);
                    continue;
                }
                if (req is null) continue;

                if (req.Op == Ops.Subscribe && _subscribers.TryAdd(id, client))
                    _ = Task.Run(() => PumpEventsAsync(client), CancellationToken.None);

                if (!ReadOnlyOps.Contains(req.Op) && !MayControl(pipe, client, req.Op))
                {
                    log.LogWarning("다른 세션(세션 {Session})의 {Op} 거부", client.Session, req.Op);
                    resp = new Response
                    {
                        Ok = false,
                        Error = "다른 사용자가 이 기기를 쓰고 있어 상태 조회만 할 수 있어요",
                        Status = await Task.Run(handler.BuildStatus, ct),
                    };
                }
                else
                {
                    resp = await Task.Run(() => handler.Handle(req), ct);
                }
                resp.Id = req.Id;
                await WriteAsync(writer, writeLock, resp, ct);
            }
        }
        catch (Exception ex) when (ex is IOException or OperationCanceledException or ObjectDisposedException)
        {
            // 클라이언트 종료
        }
        finally
        {
            client.Events.Writer.TryComplete();
            if (_subscribers.TryRemove(id, out _) && _subscribers.IsEmpty)
            {
                log.LogInformation("마지막 앱 연결 종료 → 매핑·게임별 성능 복원");
                try { LastSubscriberGone?.Invoke(); }
                catch (Exception ex) { log.LogError(ex, "복원 실패"); }
            }
        }
    }

    /// <summary>
    /// 하드웨어 상태를 바꿀 수 있는지: 지금 콘솔 세션의 클라이언트만.
    /// MSI Center 켜기/끄기(시스템 서비스·작업 변경)는 다른 세션이라도 승격된 관리자면 허용.
    /// </summary>
    private static bool MayControl(NamedPipeServerStream pipe, Client client, string op)
    {
        if (client.IsActiveConsole) return true;
        if (op != Ops.SetMsiCenter) return false;
        client.IsAdmin ??= PipeAccess.IsClientAdmin(pipe); // 요청을 읽은 뒤라 가장 가능
        return client.IsAdmin == true;
    }

    private static int TryReadId(string line)
    {
        try
        {
            using var doc = JsonDocument.Parse(line);
            return doc.RootElement.TryGetProperty("Id", out var id) && id.TryGetInt32(out var v) ? v : 0;
        }
        catch (JsonException) { return 0; }
    }

    private static async Task WriteAsync(StreamWriter writer, SemaphoreSlim writeLock, Response resp, CancellationToken ct)
    {
        var line = JsonSerializer.Serialize(resp, JsonStore.Options).ReplaceLineEndings("");
        await writeLock.WaitAsync(ct);
        try { await writer.WriteLineAsync(line); }
        finally { writeLock.Release(); }
    }
}
