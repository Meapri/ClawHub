using System.Management;
using ClawCore.Gpu;
using ClawCore.Ipc;

namespace ClawService;

/// <summary>
/// Game Bar 위젯(샌드박스라 드라이버 DLL·WMI를 직접 못 씀)을 위해 서비스가 대신 해 주는 것들:
/// Intel 그래픽(FPS 제한·프레임 생성), 화면 밝기, 2초 간격 측정값 기록(그래프용), 런처 앱 상태 보관.
/// </summary>
public sealed class SystemManager(ILogger<SystemManager> log, EcManager ec, FrameMonitor frames) : IDisposable
{
    private const int HistoryLength = 60; // 2초 × 60 = 2분
    /// <summary>그래프 기간 (초). 느리게 재던 구간이 있어도 2분 창은 그대로.</summary>
    private const long HistorySeconds = 120;

    /// <summary>
    /// 측정 간격: 누가 보고 있으면(최근 30초 안에 GetTelemetry — 위젯 2초, 런처 상태 표시줄 10초) 2초,
    /// 아무도 안 보면(게임 중 등) 10초. 한 번 재는 데 EC WMI 3번 + 배터리 WMI 쿼리 2번(~25ms, WmiPrvSE 포함)이라
    /// 아무도 안 볼 때 2초마다 재면 그만큼 배터리를 쓴다. 다시 보기 시작하면 바로 재서 첫 값도 최신이다.
    /// </summary>
    private static readonly TimeSpan WatchedInterval = TimeSpan.FromSeconds(2);
    private static readonly TimeSpan IdleInterval = TimeSpan.FromSeconds(10);
    private static readonly TimeSpan WatchTimeout = TimeSpan.FromSeconds(30);

    private readonly object _lock = new();
    private readonly object _sampleLock = new();
    private readonly Queue<TelemetrySample> _history = new();
    private Telemetry _latest = new();
    private DateTime _sampledAt = DateTime.MinValue;
    private DateTime _lastWatched = DateTime.MinValue;
    private bool _watchedRate;
    private GraphicsState? _graphics;
    private DateTime _graphicsReadAt;
    private Timer? _timer;

    /// <summary>런처 앱이 SetAppState로 알려 준 상태. 앱이 끊기면 null. 게임 PID가 오면 FPS 측정 대상으로 넘긴다.</summary>
    public AppState? AppState
    {
        get => _appState;
        set
        {
            _appState = value;
            // 게임이 없으면 성능 오버레이가 최근(5초 안) 알려 준 앞 창을 계속 잰다
            var overlay = DateTime.UtcNow - _overlayPidAt < TimeSpan.FromSeconds(5) ? _overlayPid : null;
            frames.SetTarget(value?.RunningGamePid ?? overlay);
        }
    }
    private AppState? _appState;
    private int? _overlayPid;
    private DateTime _overlayPidAt = DateTime.MinValue;

    public void Start()
    {
        _timer = new Timer(_ => OnTimer(), null, TimeSpan.Zero, IdleInterval);
        frames.Start();
    }

    // ── 측정값 ──────────────────────────────────────────

    private void OnTimer()
    {
        Sample(onlyIfStale: false);
        bool nobodyWatching;
        lock (_lock) nobodyWatching = _watchedRate && DateTime.UtcNow - _lastWatched > WatchTimeout;
        if (nobodyWatching) SetRate(watched: false);
    }

    private void SetRate(bool watched)
    {
        lock (_lock)
        {
            if (_watchedRate == watched) return;
            _watchedRate = watched;
        }
        var period = watched ? WatchedInterval : IdleInterval;
        try { _timer?.Change(period, period); }
        catch (ObjectDisposedException) { /* 서비스 종료 중 */ }
    }

    /// <param name="onlyIfStale">true면 마지막 측정이 2초 주기보다 오래됐을 때만 (느리게 재다가 다시 보기 시작한 첫 조회).</param>
    private void Sample(bool onlyIfStale)
    {
        lock (_sampleLock) // 타이머와 조회가 동시에 재지 않게
        {
            if (onlyIfStale)
            {
                lock (_lock)
                {
                    if (DateTime.UtcNow - _sampledAt <= WatchedInterval + TimeSpan.FromSeconds(1)) return;
                }
            }
            try
            {
                var t = ec.ReadTelemetry();
                ReadBattery(t);
                var now = DateTimeOffset.UtcNow.ToUnixTimeSeconds();
                var sample = new TelemetrySample
                {
                    Time = now,
                    CpuTemp = t.CpuTemp,
                    CpuWatts = t.CpuWatts,
                    BatteryWatts = t.BatteryWatts,
                    Fps = frames.Current?.Fps,
                };
                lock (_lock)
                {
                    _latest = t;
                    _sampledAt = DateTime.UtcNow;
                    _history.Enqueue(sample);
                    while (_history.Count > HistoryLength || (_history.Count > 0 && _history.Peek().Time <= now - HistorySeconds))
                        _history.Dequeue();
                }
            }
            catch (Exception ex)
            {
                log.LogDebug(ex, "측정 실패");
            }
        }
    }

    /// <param name="framePid">성능 오버레이가 보는 앞 창 (0 = 없음, null = 오버레이 아님). 게임으로 알려 준 PID가 없을 때만 측정 대상으로.</param>
    public Telemetry GetTelemetry(int? framePid = null)
    {
        if (framePid is { } fp)
        {
            (_overlayPid, _overlayPidAt) = (fp, DateTime.UtcNow);
            if (_appState?.RunningGamePid is null) frames.SetTarget(fp);
        }
        frames.NotePoll(); // 누가 보고 있을 때만 PresentMon을 돌린다
        lock (_lock) _lastWatched = DateTime.UtcNow;
        Sample(onlyIfStale: true); // 느리게 재던 중이었으면 지금 값을 바로
        SetRate(watched: true);
        var fps = frames.Current;
        lock (_lock)
        {
            return new Telemetry
            {
                Fps = fps?.Fps,
                FrameTimeMs = fps?.FrameTimeMs,
                FpsLow1 = fps?.FpsLow1,
                FrameTimes = framePid is null ? null : frames.RecentFrames(),
                GpuTemp = _latest.GpuTemp,
                CpuTemp = _latest.CpuTemp,
                CpuWatts = _latest.CpuWatts,
                BatteryPercent = _latest.BatteryPercent,
                Charging = _latest.Charging,
                BatteryWatts = _latest.BatteryWatts,
                RemainingMinutes = _latest.RemainingMinutes,
                History = [.. _history],
            };
        }
    }

    private static void ReadBattery(Telemetry t)
    {
        try
        {
            // 측정마다 불리므로 WMI 결과(COM 개체)는 GC를 기다리지 않고 바로 놓는다
            using var s = new ManagementObjectSearcher(@"root\WMI", "SELECT Charging, PowerOnline, DischargeRate FROM BatteryStatus");
            using var results = s.Get();
            foreach (ManagementObject o in results)
            {
                using (o)
                {
                    t.Charging = o["Charging"] is true || o["PowerOnline"] is true;
                    if (!t.Charging && Convert.ToInt32(o["DischargeRate"]) is > 0 and var mw) t.BatteryWatts = Math.Round(mw / 1000.0, 1);
                }
            }
        }
        catch (Exception) { /* 미지원 */ }
        try
        {
            using var s = new ManagementObjectSearcher("SELECT EstimatedChargeRemaining, EstimatedRunTime FROM Win32_Battery");
            using var results = s.Get();
            foreach (ManagementObject o in results)
            {
                using (o)
                {
                    t.BatteryPercent = Convert.ToInt32(o["EstimatedChargeRemaining"]);
                    // 71582788 = 전원 연결(계산 안 함)
                    if (Convert.ToInt64(o["EstimatedRunTime"]) is > 0 and < 71582788 and var min && !t.Charging) t.RemainingMinutes = (int)min;
                }
            }
        }
        catch (Exception) { /* 미지원 */ }
    }

    // ── 그래픽 (Intel 드라이버) ──────────────────────────

    public GraphicsState Graphics
    {
        get
        {
            lock (_lock)
            {
                // Get 호출마다 드라이버 초기화(~25ms)가 있어 10초 캐시
                if (_graphics is null || DateTime.UtcNow - _graphicsReadAt > TimeSpan.FromSeconds(10))
                {
                    _graphics = new GraphicsState
                    {
                        Available = IntelGraphics.Available,
                        FpsLimit = IntelGraphics.GetFrameLimit() ?? 0,
                        FrameGeneration = IntelGraphics.GetFrameGeneration() ?? 0,
                    };
                    _graphicsReadAt = DateTime.UtcNow;
                }
                return _graphics;
            }
        }
    }

    public void SetGraphics(int? fps, int? frameGeneration)
    {
        if (!IntelGraphics.Available) throw new InvalidOperationException("Intel 그래픽 드라이버 설정을 사용할 수 없어요");
        lock (_lock)
        {
            if (fps is { } f && !IntelGraphics.SetFrameLimit(Math.Clamp(f, 0, 240)))
                throw new InvalidOperationException("FPS 제한을 바꾸지 못했어요");
            if (frameGeneration is { } g && !IntelGraphics.SetFrameGeneration(Math.Clamp(g, 0, 3)))
                throw new InvalidOperationException("프레임 생성을 바꾸지 못했어요 (드라이버 미지원)");
            _graphics = null; // 다음 조회 때 다시 읽기
        }
    }

    // ── 밝기 ────────────────────────────────────────────

    private int? _brightness;
    private DateTime _brightnessReadAt;

    /// <summary>위젯이 4초마다 상태를 물으므로 WMI 조회는 5초 캐시.</summary>
    public int? Brightness
    {
        get
        {
            if (DateTime.UtcNow - _brightnessReadAt < TimeSpan.FromSeconds(5)) return _brightness;
            try
            {
                using var s = new ManagementObjectSearcher(@"root\WMI", "SELECT CurrentBrightness FROM WmiMonitorBrightness");
                _brightness = s.Get().Cast<ManagementObject>().Select(o => (int?)Convert.ToInt32(o["CurrentBrightness"])).FirstOrDefault();
            }
            catch (Exception) { _brightness = null; }
            _brightnessReadAt = DateTime.UtcNow;
            return _brightness;
        }
    }

    public void SetBrightness(int percent)
    {
        _brightness = Math.Clamp(percent, 0, 100);
        _brightnessReadAt = DateTime.UtcNow;
        using var s = new ManagementObjectSearcher(@"root\WMI", "SELECT * FROM WmiMonitorBrightnessMethods");
        foreach (ManagementObject o in s.Get())
            o.InvokeMethod("WmiSetBrightness", [1u, (byte)Math.Clamp(percent, 0, 100)]);
    }

    public void Dispose() => _timer?.Dispose();
}
