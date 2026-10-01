using System.Collections.Concurrent;
using System.IO.Pipes;
using System.Text.Json;

namespace ClawCore.Ipc;

/// <summary>
/// ClawService와의 단일 지속 연결. 끊기면 자동 재접속하고, 푸시 이벤트(버튼/상태)를 전달한다.
/// </summary>
public sealed class ServiceClient : IAsyncDisposable
{
    private readonly CancellationTokenSource _cts = new();
    private readonly ConcurrentDictionary<int, TaskCompletionSource<Response>> _pending = new();
    private readonly SemaphoreSlim _writeLock = new(1, 1);
    private StreamWriter? _writer;
    private int _nextId;

    public event Action<Response>? EventReceived;
    public event Action<bool>? ConnectionChanged;
    public bool IsConnected => _writer is not null;

    public void Start() => _ = Task.Run(() => RunAsync(_cts.Token));

    public async Task<Response> SendAsync(Request request, TimeSpan? timeout = null)
    {
        var writer = _writer ?? throw new InvalidOperationException("ClawService에 연결되지 않았어요");
        request.Id = Interlocked.Increment(ref _nextId);
        var tcs = new TaskCompletionSource<Response>(TaskCreationOptions.RunContinuationsAsynchronously);
        _pending[request.Id] = tcs;
        try
        {
            await _writeLock.WaitAsync();
            try { await writer.WriteLineAsync(JsonSerializer.Serialize(request, JsonStore.Options).ReplaceLineEndings("")); }
            finally { _writeLock.Release(); }
            return await tcs.Task.WaitAsync(timeout ?? TimeSpan.FromSeconds(10));
        }
        finally
        {
            _pending.TryRemove(request.Id, out _);
        }
    }

    private async Task RunAsync(CancellationToken ct)
    {
        while (!ct.IsCancellationRequested)
        {
            try
            {
                await using var pipe = new NamedPipeClientStream(".", Ops.PipeName, PipeDirection.InOut, PipeOptions.Asynchronous);
                await pipe.ConnectAsync(2000, ct);
                using var reader = new StreamReader(pipe);
                _writer = new StreamWriter(pipe) { AutoFlush = true };
                ConnectionChanged?.Invoke(true);
                _ = SendAsync(new Request { Op = Ops.Subscribe });

                while (!ct.IsCancellationRequested && await reader.ReadLineAsync(ct) is { } line)
                {
                    var msg = JsonSerializer.Deserialize<Response>(line, JsonStore.Options);
                    if (msg is null) continue;
                    if (msg.Id != 0 && _pending.TryRemove(msg.Id, out var tcs)) tcs.TrySetResult(msg);
                    else if (msg.Event is not null) EventReceived?.Invoke(msg);
                }
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested) { return; }
            catch (Exception) { /* 서비스 미실행 또는 연결 끊김 → 재시도 */ }

            if (_writer is not null)
            {
                _writer = null;
                foreach (var p in _pending.Values) p.TrySetException(new IOException("서비스 연결 끊김"));
                ConnectionChanged?.Invoke(false);
            }
            await Task.Delay(2000, ct).ContinueWith(_ => { });
        }
    }

    public async ValueTask DisposeAsync()
    {
        await _cts.CancelAsync();
        _cts.Dispose();
    }
}
