using ClawCore;
using ClawCore.Gpu;
using ClawCore.Ipc;

namespace ClawService;

/// <summary>
/// Intel Endurance Gaming (Auto TDP). 사용자가 ClawHub에서 정한 값을 endurance.json에 저장하고
/// 서비스 시작·절전 복귀 때 다시 적용한다. 한 번도 정하지 않았으면 드라이버 값을 건드리지 않는다.
/// </summary>
public sealed class EnduranceManager(ILogger<EnduranceManager> log)
{
    private sealed class Saved
    {
        public EnduranceControl? Control { get; set; }
        public EnduranceMode? Mode { get; set; }
    }

    private static string StatePath => Path.Combine(JsonStore.ServiceDir, "endurance.json");

    private readonly object _lock = new();
    private readonly Saved _saved = JsonStore.Load<Saved>(StatePath);
    private EnduranceState? _state;
    private DateTime _readAt;

    private EnduranceSetting? SavedSetting => _saved is { Control: { } c, Mode: { } m } ? new EnduranceSetting(c, m) : null;

    /// <summary>현재 상태 (드라이버 읽기는 ~25ms라 10초 캐시).</summary>
    public EnduranceState State
    {
        get
        {
            lock (_lock)
            {
                if (_state is null || DateTime.UtcNow - _readAt > TimeSpan.FromSeconds(10))
                {
                    _state = Read();
                    _readAt = DateTime.UtcNow;
                }
                return _state;
            }
        }
    }

    private EnduranceState Read()
    {
        var s = new EnduranceState { Available = EnduranceGaming.Available, Managed = SavedSetting is not null };
        if (!s.Available) return s;
        // getEGmode는 On이나 일부 조합을 표현하지 못한다(255) → 그때는 우리가 마지막으로 정한 값
        if (EnduranceGaming.Read() is { } d) (s.Control, s.Mode, s.Source) = (d.Control, d.Mode, "Driver");
        else if (SavedSetting is { } v) (s.Control, s.Mode, s.Source) = (v.Control, v.Mode, "Saved");
        return s;
    }

    public void Set(EnduranceControl control, EnduranceMode? mode)
    {
        if (!EnduranceGaming.Available) throw new InvalidOperationException("Intel 그래픽 드라이버 설정을 사용할 수 없습니다");
        lock (_lock)
        {
            var m = mode ?? _saved.Mode ?? State.Mode ?? EnduranceMode.Balanced;
            if (!EnduranceGaming.Set(new EnduranceSetting(control, m)))
                throw new InvalidOperationException("Endurance Gaming을 바꾸지 못했습니다 (드라이버 미지원)");
            _saved.Control = control;
            _saved.Mode = m;
            JsonStore.Save(StatePath, _saved);
            _state = null; // 다음 조회 때 다시 읽기
        }
        log.LogInformation("Endurance Gaming: {Control} / {Mode}", control, _saved.Mode);
    }

    public void Reapply(string reason)
    {
        if (SavedSetting is not { } v || !EnduranceGaming.Available) return;
        lock (_lock)
        {
            try
            {
                if (EnduranceGaming.Set(v)) log.LogInformation("Endurance Gaming 재적용 ({Reason}): {Setting}", reason, v);
                else log.LogWarning("Endurance Gaming 재적용 실패 ({Reason})", reason);
            }
            catch (Exception ex) { log.LogError(ex, "Endurance Gaming 재적용 실패"); }
            _state = null;
        }
    }
}
