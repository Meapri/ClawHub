using System.Management;
using ClawCore;
using ClawCore.Ec;
using ClawCore.Ipc;

namespace ClawService;

/// <summary>EC 성능/배터리 상태 소유자. 마지막 설정을 저장해 두고 부팅·절전 복귀 시 다시 적용한다.</summary>
public sealed class EcManager : IDisposable
{
    private sealed class SavedState
    {
        public PerformanceState Performance { get; set; } = new(PerformanceMode.Balanced, 17, 37, FanMode.Auto);
        /// <summary>배터리로 돌 때만 쓸 기본값 (null = 배터리여도 Performance).</summary>
        public PerformanceState? PerformanceOnBattery { get; set; }
    }

    private static string StatePath => Path.Combine(JsonStore.ServiceDir, "ec-state.json");

    private readonly ILogger _log;
    private readonly SavedState _state = JsonStore.Load<SavedState>(StatePath);
    private readonly MsiWmi? _wmi;
    private readonly PerformanceManager? _perf;
    private readonly ManagementEventWatcher? _resumeWatcher;

    public string? Error { get; }

    private readonly PowerManager _power;

    public EcManager(ILogger<EcManager> log, PowerManager power)
    {
        _log = log;
        _power = power;
        power.PowerSourceChanged += OnPowerSourceChanged;
        try
        {
            _wmi = MsiWmi.Open();
            _perf = new PerformanceManager(_wmi);
            _log.LogInformation("EC 연결: WMI v{Major}.{Minor}, {Ec}", _wmi.WmiMajor, _wmi.WmiMinor, _wmi.EcVersion);
            if (!_wmi.WritesEnabled) Error = $"WMI v{_wmi.WmiMajor}는 검증되지 않아 쓰기를 막았습니다";

            // Win32_PowerManagementEvent EventType 7 = 절전 복귀
            _resumeWatcher = new ManagementEventWatcher(new WqlEventQuery("SELECT * FROM Win32_PowerManagementEvent WHERE EventType = 7"));
            _resumeWatcher.EventArrived += (_, _) => Task.Delay(3000).ContinueWith(_ => Reapply("절전 복귀"));
            _resumeWatcher.Start();
        }
        catch (Exception ex)
        {
            Error = ex.Message;
            _log.LogError(ex, "EC 초기화 실패");
        }
    }

    public bool Available => _wmi is not null;
    public bool Writable => _wmi?.WritesEnabled == true;
    public string? EcVersion => _wmi?.EcVersion;
    /// <summary>지금 적용되는 사용자 기본값: 배터리 사용 중이고 배터리 전용 값이 있으면 그것, 아니면 전원 연결 값.</summary>
    public PerformanceState Performance => Effective(_power.OnBattery);
    public PerformanceState PerformanceOnAc => _state.Performance;
    public PerformanceState? PerformanceOnBattery => _state.PerformanceOnBattery;
    public bool OnBattery => _power.OnBattery;

    private PerformanceState Effective(bool onBattery) =>
        onBattery && _state.PerformanceOnBattery is { } b ? b : _state.Performance;

    public ChargeLimit? ChargeLimit
    {
        get
        {
            try { return _perf?.GetChargeLimit(); }
            catch (Exception) { return null; }
        }
    }

    /// <summary>
    /// 모든 EC 쓰기를 직렬화한다. 요청(여러 클라이언트)과 절전 복귀 재적용이 동시에 오면
    /// PL1→8W→PL2→PL1 순서가 섞여 PL1 > PL2가 될 수 있다.
    /// </summary>
    private readonly object _ecLock = new();

    public void Reapply(string reason)
    {
        if (!Writable) return;
        lock (_ecLock)
        {
            try
            {
                var state = Temporary ?? Performance;
                _perf!.Apply(state);
                _log.LogInformation("성능 설정 재적용 ({Reason}): {State}", reason, state);
            }
            catch (Exception ex) { _log.LogError(ex, "성능 설정 재적용 실패"); }
        }
    }

    /// <summary>게임별 성능 모드가 적용 중이면 그 값 (사용자 기본값은 <see cref="Performance"/>).</summary>
    public PerformanceState? Temporary { get; private set; }

    /// <summary>
    /// 사용자 기본값 저장. <paramref name="slot"/>이 null이면 지금 적용 중인 칸(배터리 + 배터리 전용 값 있음 → 배터리, 아니면 전원 연결).
    /// 바꾼 칸이 지금 적용되는 칸이면 바로 적용하고 게임별 임시 값은 해제한다 (예전 동작과 같음).
    /// </summary>
    public void SetPerformance(PerformanceState state, PowerSourceKind? slot = null)
    {
        if (!Writable) throw new InvalidOperationException(Error ?? "EC 쓰기 불가");
        lock (_ecLock)
        {
            var onBattery = _power.OnBattery;
            var target = slot ?? (onBattery && _state.PerformanceOnBattery is not null ? PowerSourceKind.Battery : PowerSourceKind.Ac);
            var active = target == PowerSourceKind.Battery ? onBattery : !(onBattery && _state.PerformanceOnBattery is not null);
            if (active)
            {
                _perf!.Apply(state);
                Temporary = null;
            }
            if (target == PowerSourceKind.Battery) _state.PerformanceOnBattery = state;
            else _state.Performance = state;
            JsonStore.Save(StatePath, _state);
        }
    }

    /// <summary>배터리 전용 기본값을 지운다 → 배터리여도 전원 연결 값.</summary>
    public void ClearPerformanceOnBattery()
    {
        lock (_ecLock)
        {
            if (_state.PerformanceOnBattery is null) return;
            _state.PerformanceOnBattery = null;
            JsonStore.Save(StatePath, _state);
            if (Writable && Temporary is null && _power.OnBattery) _perf!.Apply(_state.Performance);
        }
    }

    /// <summary>전원이 바뀌면 배터리 전용 기본값으로(또는 그 반대로). 게임별 임시 값이 있으면 앱이 다시 맞춘다.</summary>
    private void OnPowerSourceChanged(bool onBattery)
    {
        if (!Writable || _state.PerformanceOnBattery is null) return;
        lock (_ecLock)
        {
            if (Temporary is not null) return;
            try
            {
                var state = Effective(onBattery);
                _perf!.Apply(state);
                _log.LogInformation("전원 변경으로 성능 기본값 적용 ({Source}): {State}", onBattery ? "배터리" : "전원 연결", state);
            }
            catch (Exception ex) { _log.LogError(ex, "전원 변경 성능 적용 실패"); }
        }
    }

    public void ApplyTemporary(PerformanceState state)
    {
        if (!Writable) throw new InvalidOperationException(Error ?? "EC 쓰기 불가");
        lock (_ecLock)
        {
            _perf!.Apply(state);
            Temporary = state;
        }
    }

    public void RestoreDefault()
    {
        lock (_ecLock)
        {
            if (Temporary is null) return;
            Temporary = null;
            if (Writable) _perf!.Apply(Performance);
        }
    }

    public Telemetry ReadTelemetry()
    {
        var t = new Telemetry();
        if (_wmi is null) return t;
        try
        {
            var temp = _wmi.GetTemperature(0);
            t.CpuTemp = temp[0] is > 0 and < 120 ? temp[0] : null;
            t.GpuTemp = temp[1] is > 0 and < 120 ? temp[1] : null;
        }
        catch (Exception) { /* 미지원 */ }
        try { t.CpuWatts = _wmi.GetData(0xDD); } catch (Exception) { }
        try { t.FanRaw = _wmi.GetFan(0); } catch (Exception) { }
        return t;
    }

    public void SetChargeLimit(ChargeLimit limit)
    {
        if (!Writable) throw new InvalidOperationException(Error ?? "EC 쓰기 불가");
        lock (_ecLock) _perf!.SetChargeLimit(limit);
    }

    public void Dispose()
    {
        _resumeWatcher?.Stop();
        _resumeWatcher?.Dispose();
    }
}
