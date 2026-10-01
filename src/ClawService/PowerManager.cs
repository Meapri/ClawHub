using System.Management;
using ClawCore.Ipc;
using ClawCore.Power;

namespace ClawService;

/// <summary>
/// 전원 상태 감시 (전원 연결 ↔ 배터리 전환, 절전 복귀)와 배터리 수명 정보(60초 캐시).
/// Win32_PowerManagementEvent: EventType 7 = 절전 복귀, 10 = 전원 상태 변경(배터리 % 변화에도 오므로 실제 AC/DC 변화만 알림).
/// 이벤트를 놓쳐도 되도록 5초마다 한 번 더 확인한다.
/// </summary>
public sealed class PowerManager : IDisposable
{
    private readonly ILogger _log;
    private readonly ManagementEventWatcher? _watcher;
    private readonly Timer _poll;
    private readonly object _lock = new();
    private bool _onBattery;

    private BatteryHealth? _health;
    private DateTime _healthReadAt;
    private int _healthRefreshing;

    /// <summary>전원 연결 ↔ 배터리가 바뀜 (인수: 이제 배터리인지). 스레드 풀에서 호출된다.</summary>
    public event Action<bool>? PowerSourceChanged;
    /// <summary>절전에서 깨어남 (3초 뒤, 하드웨어가 안정된 다음). 스레드 풀에서 호출된다.</summary>
    public event Action? Resumed;

    public PowerManager(ILogger<PowerManager> log)
    {
        _log = log;
        _onBattery = PowerSource.OnBattery;
        try
        {
            _watcher = new ManagementEventWatcher(new WqlEventQuery(
                "SELECT * FROM Win32_PowerManagementEvent WHERE EventType = 7 OR EventType = 10"));
            _watcher.EventArrived += (_, e) =>
            {
                var type = Convert.ToInt32(e.NewEvent["EventType"]);
                if (type == 7) Task.Delay(3000).ContinueWith(_ => { Check(); Raise(Resumed, "Resumed"); });
                else Check();
            };
            _watcher.Start();
        }
        catch (Exception ex)
        {
            _log.LogWarning(ex, "전원 이벤트 감시 실패 — 5초 간격 확인만 사용");
        }
        _poll = new Timer(_ => Check(), null, TimeSpan.FromSeconds(5), TimeSpan.FromSeconds(5));
        _ = Task.Run(RefreshHealth); // 첫 GetStatus 전에 미리 읽어 둔다
    }

    /// <summary>지금 값 (매번 GetSystemPowerStatus — 이벤트보다 앱 요청이 먼저 와도 맞는 값을 쓰도록).</summary>
    public bool OnBattery => PowerSource.OnBattery;

    private void Check()
    {
        var now = PowerSource.OnBattery;
        lock (_lock)
        {
            if (now == _onBattery) return;
            _onBattery = now;
        }
        _log.LogInformation("전원 상태 변경: {Source}", now ? "배터리" : "전원 연결");
        var handlers = PowerSourceChanged;
        if (handlers is null) return;
        foreach (var h in handlers.GetInvocationList().Cast<Action<bool>>())
        {
            try { h(now); }
            catch (Exception ex) { _log.LogError(ex, "전원 상태 변경 처리 실패"); }
        }
    }

    private void Raise(Action? evt, string name)
    {
        if (evt is null) return;
        foreach (var h in evt.GetInvocationList().Cast<Action>())
        {
            try { h(); }
            catch (Exception ex) { _log.LogError(ex, "{Event} 처리 실패", name); }
        }
    }

    // ── 배터리 수명 ─────────────────────────────────────

    /// <summary>캐시된 값 (60초 지나면 뒤에서 다시 읽고, 그동안은 이전 값). 첫 읽기 전이면 null.</summary>
    public BatteryHealth? BatteryHealth
    {
        get
        {
            lock (_lock)
            {
                if (DateTime.UtcNow - _healthReadAt > TimeSpan.FromSeconds(60) && Interlocked.Exchange(ref _healthRefreshing, 1) == 0)
                    _ = Task.Run(RefreshHealth);
                return _health;
            }
        }
    }

    private void RefreshHealth()
    {
        try
        {
            var h = BatteryHealthReader.Read();
            lock (_lock)
            {
                _health = h;
                _healthReadAt = DateTime.UtcNow;
            }
        }
        catch (Exception ex)
        {
            _log.LogDebug(ex, "배터리 수명 읽기 실패");
            lock (_lock) _healthReadAt = DateTime.UtcNow; // 60초 뒤 재시도
        }
        finally
        {
            Interlocked.Exchange(ref _healthRefreshing, 0);
        }
    }

    public void Dispose()
    {
        _poll.Dispose();
        _watcher?.Stop();
        _watcher?.Dispose();
    }
}

/// <summary>
/// 전원 관련 시작 작업: 저장된 Endurance Gaming 재적용(시작·절전 복귀), 전원이 바뀌면 상태를 구독자(앱·위젯)에 푸시.
/// Worker를 건드리지 않도록 별도 호스티드 서비스로 둔다.
/// </summary>
public sealed class PowerHostedService(PowerManager power, EnduranceManager endurance, PipeServer pipe, RequestHandler handler)
    : IHostedService
{
    public Task StartAsync(CancellationToken ct)
    {
        _ = Task.Run(() => endurance.Reapply("서비스 시작"), ct);
        power.Resumed += () => endurance.Reapply("절전 복귀");
        // EcManager가 먼저 성능을 바꾼 뒤(같은 이벤트, 먼저 구독) 바뀐 상태를 알린다
        power.PowerSourceChanged += _ => pipe.Broadcast(new Response { Event = Ops.EventStatus, Status = handler.BuildStatus() });
        return Task.CompletedTask;
    }

    public Task StopAsync(CancellationToken ct) => Task.CompletedTask;
}
