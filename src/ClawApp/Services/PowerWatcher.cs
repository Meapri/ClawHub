using ClawCore.Power;
using Microsoft.Win32;

namespace ClawApp.Services;

/// <summary>
/// 전원 연결 ↔ 배터리 전환 감지 (앱 쪽). SystemEvents.PowerModeChanged(StatusChange/Resume)에 반응하고,
/// 이벤트를 놓쳐도 되도록 5초마다 한 번 더 확인한다. StatusChange는 배터리 % 변화에도 오므로 실제 변화만 알린다.
/// </summary>
public static class PowerWatcher
{
    private static readonly object Lock = new();
    private static Timer? _poll;
    private static bool _onBattery = PowerSource.OnBattery;

    public static bool OnBattery
    {
        get { lock (Lock) return _onBattery; }
    }

    /// <summary>바뀌면 (인수: 이제 배터리인지). UI 스레드가 아닌 곳에서 호출될 수 있다.</summary>
    public static event Action<bool>? Changed;

    public static void Start()
    {
        if (_poll is not null) return;
        SystemEvents.PowerModeChanged += (_, e) =>
        {
            if (e.Mode is PowerModes.StatusChange or PowerModes.Resume) Check();
        };
        _poll = new Timer(_ => Check(), null, TimeSpan.FromSeconds(5), TimeSpan.FromSeconds(5));
    }

    public static void Check()
    {
        var now = PowerSource.OnBattery;
        lock (Lock)
        {
            if (now == _onBattery) return;
            _onBattery = now;
        }
        Changed?.Invoke(now);
    }
}
