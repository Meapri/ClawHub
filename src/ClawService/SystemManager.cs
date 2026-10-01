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
    private readonly object _lock = new();
    private readonly Queue<TelemetrySample> _history = new();
    private Telemetry _latest = new();
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
            frames.SetTarget(value?.RunningGamePid);
        }
    }
    private AppState? _appState;

    public void Start()
    {
        _timer = new Timer(_ => Sample(), null, TimeSpan.Zero, TimeSpan.FromSeconds(2));
        frames.Start();
    }

    // ── 측정값 ──────────────────────────────────────────

    private void Sample()
    {
        try
        {
            var t = ec.ReadTelemetry();
            ReadBattery(t);
            var sample = new TelemetrySample
            {
                Time = DateTimeOffset.UtcNow.ToUnixTimeSeconds(),
                CpuTemp = t.CpuTemp,
                CpuWatts = t.CpuWatts,
                BatteryWatts = t.BatteryWatts,
                Fps = frames.Current?.Fps,
            };
            lock (_lock)
            {
                _latest = t;
                _history.Enqueue(sample);
                while (_history.Count > HistoryLength) _history.Dequeue();
            }
        }
        catch (Exception ex)
        {
            log.LogDebug(ex, "측정 실패");
        }
    }

    public Telemetry GetTelemetry()
    {
        frames.NotePoll(); // 누가 보고 있을 때만 PresentMon을 돌린다
        var fps = frames.Current;
        lock (_lock)
        {
            return new Telemetry
            {
                Fps = fps?.Fps,
                FrameTimeMs = fps?.FrameTimeMs,
                FpsLow1 = fps?.FpsLow1,
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
            using var s = new ManagementObjectSearcher(@"root\WMI", "SELECT Charging, PowerOnline, DischargeRate FROM BatteryStatus");
            foreach (ManagementObject o in s.Get())
            {
                t.Charging = o["Charging"] is true || o["PowerOnline"] is true;
                if (!t.Charging && Convert.ToInt32(o["DischargeRate"]) is > 0 and var mw) t.BatteryWatts = Math.Round(mw / 1000.0, 1);
            }
        }
        catch (Exception) { /* 미지원 */ }
        try
        {
            using var s = new ManagementObjectSearcher("SELECT EstimatedChargeRemaining, EstimatedRunTime FROM Win32_Battery");
            foreach (ManagementObject o in s.Get())
            {
                t.BatteryPercent = Convert.ToInt32(o["EstimatedChargeRemaining"]);
                // 71582788 = 전원 연결(계산 안 함)
                if (Convert.ToInt64(o["EstimatedRunTime"]) is > 0 and < 71582788 and var min && !t.Charging) t.RemainingMinutes = (int)min;
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
        if (!IntelGraphics.Available) throw new InvalidOperationException("Intel 그래픽 드라이버 설정을 사용할 수 없습니다");
        lock (_lock)
        {
            if (fps is { } f && !IntelGraphics.SetFrameLimit(Math.Clamp(f, 0, 240)))
                throw new InvalidOperationException("FPS 제한을 바꾸지 못했습니다");
            if (frameGeneration is { } g && !IntelGraphics.SetFrameGeneration(Math.Clamp(g, 0, 3)))
                throw new InvalidOperationException("프레임 생성을 바꾸지 못했습니다 (드라이버 미지원)");
            _graphics = null; // 다음 조회 때 다시 읽기
        }
    }

    // ── 밝기 ────────────────────────────────────────────

    public int? Brightness
    {
        get
        {
            try
            {
                using var s = new ManagementObjectSearcher(@"root\WMI", "SELECT CurrentBrightness FROM WmiMonitorBrightness");
                return s.Get().Cast<ManagementObject>().Select(o => (int?)Convert.ToInt32(o["CurrentBrightness"])).FirstOrDefault();
            }
            catch (Exception) { return null; }
        }
    }

    public void SetBrightness(int percent)
    {
        using var s = new ManagementObjectSearcher(@"root\WMI", "SELECT * FROM WmiMonitorBrightnessMethods");
        foreach (ManagementObject o in s.Get())
            o.InvokeMethod("WmiSetBrightness", [1u, (byte)Math.Clamp(percent, 0, 100)]);
    }

    public void Dispose() => _timer?.Dispose();
}
