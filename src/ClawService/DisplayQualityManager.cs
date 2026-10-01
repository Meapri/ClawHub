using ClawCore.Gpu;
using ClawCore.Ipc;

namespace ClawService;

/// <summary>
/// Intel 화면 품질 설정(레트로 스케일링·선명도·저지연·VRR)을 위젯/앱 대신 IGCL로 읽고 바꾼다.
/// IGCL 핸들은 열어 두고 재사용하며, 실패하면 닫았다가 30초 뒤 다시 연다 (드라이버 업데이트·TDR 대비).
/// </summary>
public sealed class DisplayQualityManager(ILogger<DisplayQualityManager> log) : IDisposable
{
    private readonly object _lock = new();
    private IgclDisplay? _igcl;
    private DateTime _retryAt = DateTime.MinValue;
    private string? _openError;
    private DisplayQualityState? _cached;
    private DateTime _cachedAt;

    /// <summary>현재 상태 (5초 캐시 — GetStatus가 자주 불린다).</summary>
    public DisplayQualityState State
    {
        get
        {
            lock (_lock)
            {
                if (_cached is not null && DateTime.UtcNow - _cachedAt < TimeSpan.FromSeconds(5)) return _cached;
                _cached = ReadLocked();
                _cachedAt = DateTime.UtcNow;
                return _cached;
            }
        }
    }

    public void Apply(DisplayQualityChange change)
    {
        if (change.SharpeningLevel is < 0 or > 100) throw new ArgumentException("선명도 강도는 0~100");
        if (change.RetroScalingMode is { } m && !Enum.IsDefined(m)) throw new ArgumentException("잘못된 스케일링 방식");
        if (change.LowLatency is { } l && !Enum.IsDefined(l)) throw new ArgumentException("잘못된 저지연 모드");
        lock (_lock)
        {
            var igcl = OpenLocked() ?? throw new InvalidOperationException($"Intel 그래픽 설정을 사용할 수 없습니다 ({_openError})");
            try
            {
                igcl.Apply(change);
                log.LogInformation("화면 품질 변경: 레트로 {Retro}/{Mode}, 선명도 {Sharp}/{Level}, 저지연 {Ll}",
                    change.RetroScaling, change.RetroScalingMode, change.Sharpening, change.SharpeningLevel, change.LowLatency);
            }
            catch (IgclException ex) when (ex.Code is 0x40000003 or 0x40000008 or 0x4000000c or 0x4000000d)
            {
                // 장치 분실·초기화 안 됨·잘못된 핸들 → 다음 호출 때 바로 다시 연다
                CloseLocked();
                _retryAt = DateTime.MinValue;
                throw;
            }
            finally
            {
                _cached = null;
            }
        }
    }

    private DisplayQualityState ReadLocked()
    {
        if (OpenLocked() is not { } igcl) return new DisplayQualityState { Available = false, Error = _openError };
        try
        {
            return igcl.Read();
        }
        catch (Exception ex) when (ex is IgclException or System.Runtime.InteropServices.SEHException)
        {
            log.LogWarning(ex, "IGCL 읽기 실패");
            CloseLocked();
            return new DisplayQualityState { Available = false, Error = ex.Message };
        }
    }

    private IgclDisplay? OpenLocked()
    {
        if (_igcl is not null) return _igcl;
        if (DateTime.UtcNow < _retryAt) return null;
        try
        {
            _igcl = IgclDisplay.TryOpen(out _openError);
        }
        catch (Exception ex) when (ex is InvalidOperationException or PlatformNotSupportedException or DllNotFoundException)
        {
            _openError = ex.Message;
        }
        if (_igcl is null)
        {
            _retryAt = DateTime.UtcNow + TimeSpan.FromSeconds(30);
            log.LogWarning("IGCL 열기 실패: {Error}", _openError);
        }
        return _igcl;
    }

    private void CloseLocked()
    {
        _igcl?.Dispose();
        _igcl = null;
        _retryAt = DateTime.UtcNow + TimeSpan.FromSeconds(30);
    }

    public void Dispose()
    {
        lock (_lock) _igcl?.Dispose();
    }
}
