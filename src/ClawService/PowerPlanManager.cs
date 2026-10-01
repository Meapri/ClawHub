using ClawCore.Ipc;
using ClawCore.Power;

namespace ClawService;

/// <summary>
/// Windows 전원 구성표 설정 (화면 끄기, 절전, 최대 절전, 전원 단추, 절약 모드, 밝기 자동 조절).
/// 활성 구성표의 AC/DC 값을 읽고(10초 캐시) 검증한 뒤 써서 바로 적용한다. Game Bar 위젯은 powrprof를 직접 못 쓰므로 서비스 경유.
/// Windows 설정 앱처럼 "절전"이 "화면 끄기"보다 짧아지지 않게 둘을 맞춘다.
/// </summary>
public sealed class PowerPlanManager(ILogger<PowerPlanManager> log, PowerManager power)
{
    private readonly object _lock = new();
    private PowerPlanState? _state;
    private DateTime _readAt;

    /// <summary>캐시된 상태 (10초 지나면 다시 읽음 — 읽기는 수십 ms).</summary>
    public PowerPlanState State
    {
        get
        {
            lock (_lock)
            {
                if (_state is null || DateTime.UtcNow - _readAt > TimeSpan.FromSeconds(10)) ReadLocked();
                return _state!;
            }
        }
    }

    /// <summary>지금 다시 읽는다 (GetPowerPlan, 쓰기 직후).</summary>
    public PowerPlanState Refresh()
    {
        lock (_lock)
        {
            ReadLocked();
            return _state!;
        }
    }

    private void ReadLocked()
    {
        _state = PowerPlanReader.Read();
        _readAt = DateTime.UtcNow;
        if (_state.Error is { } e) log.LogDebug("전원 구성표 읽기: {Error}", e);
    }

    /// <summary>값 하나를 활성 구성표에 쓰고 적용. <paramref name="source"/>가 null이면 지금 전원 상태의 칸.</summary>
    public void Set(PowerPlanSetting setting, PowerSourceKind? source, int value)
    {
        lock (_lock)
        {
            ReadLocked();
            var state = _state!;
            var def = PowerPlanReader.Find(setting);
            var src = source ?? (def.AcApplies ? (power.OnBattery ? PowerSourceKind.Battery : PowerSourceKind.Ac) : PowerSourceKind.Battery);
            PowerPlanReader.Validate(state, setting, src, value);
            if (state.SchemeGuid is null || !Guid.TryParse(state.SchemeGuid, out var scheme))
                throw new InvalidOperationException("활성 전원 구성표를 읽지 못했어요");

            var ac = src == PowerSourceKind.Ac;
            PowerPlanNative.Write(scheme, def.Sub, def.Guid, ac, (uint)value);
            LinkDisplayAndSleep(state, scheme, setting, ac, value);
            PowerPlanNative.Apply(scheme);
            log.LogInformation("전원 설정 변경: {Setting} {Source} = {Value}", setting, src, value);
            ReadLocked();
        }
    }

    /// <summary>
    /// Windows 설정 앱과 같은 규칙: 절전을 화면 끄기보다 짧게 하면 화면 끄기도 같이 줄이고,
    /// 화면 끄기를 절전보다 길게(또는 사용 안 함으로) 하면 절전도 같이 늘린다.
    /// </summary>
    private void LinkDisplayAndSleep(PowerPlanState state, Guid scheme, PowerPlanSetting setting, bool ac, int value)
    {
        static int Eff(int v) => v == 0 ? int.MaxValue : v; // 0 = 사용 안 함 = 무한대
        var display = state.Settings.FirstOrDefault(s => s.PowerSetting == PowerPlanSetting.DisplayOff);
        var sleep = state.Settings.FirstOrDefault(s => s.PowerSetting == PowerPlanSetting.Sleep);
        if (display is null || sleep is null) return;

        if (setting == PowerPlanSetting.Sleep)
        {
            var cur = ac ? display.Ac : display.Dc;
            if (Eff(value) < Eff(cur))
            {
                var d = PowerPlanReader.Find(PowerPlanSetting.DisplayOff);
                PowerPlanNative.Write(scheme, d.Sub, d.Guid, ac, (uint)value);
                log.LogInformation("절전보다 길던 화면 끄기도 {Value}초로 맞춤", value);
            }
        }
        else if (setting == PowerPlanSetting.DisplayOff)
        {
            var cur = ac ? sleep.Ac : sleep.Dc;
            if (Eff(value) > Eff(cur))
            {
                var s = PowerPlanReader.Find(PowerPlanSetting.Sleep);
                PowerPlanNative.Write(scheme, s.Sub, s.Guid, ac, (uint)value);
                log.LogInformation("화면 끄기보다 짧던 절전도 {Value}초로 맞춤", value);
            }
        }
    }
}
