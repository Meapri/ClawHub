using ClawCore;
using ClawCore.Controller;
using HidSharp;

namespace ClawService;

/// <summary>
/// 컨트롤러 소유자. baseline(원래 프로필)을 보관하고, 게임 매핑을 RAM에만 덮어쓰고, 언제든 되돌린다.
/// </summary>
public sealed class ControllerManager : IDisposable
{
    private static string BaselinePath => Path.Combine(JsonStore.ServiceDir, "controller-baseline.bin");
    /// <summary>이 파일이 있으면 매핑이 적용된 상태로 서비스가 죽었다는 뜻 → 시작 시 baseline으로 복원.</summary>
    private static string ActiveMarkerPath => Path.Combine(JsonStore.ServiceDir, "mapping-active");

    private readonly ILogger _log;
    private readonly object _lock = new();
    private ClawController? _ctl;
    private byte[]? _baseline;
    private byte[]? _current;
    private IReadOnlyDictionary<ClawButton, byte[]>? _activeBindings;

    public event Action? StatusChanged;

    public ControllerManager(ILogger<ControllerManager> log)
    {
        _log = log;
        DeviceList.Local.Changed += (_, _) => Task.Run(Reconnect);
        Reconnect();
    }

    public bool Connected => _ctl is not null;
    public string? Firmware => _ctl?.FirmwareVersion;
    public bool MappingActive => _activeBindings is not null;

    public GamepadMode? Mode
    {
        get
        {
            lock (_lock)
            {
                try { return _ctl?.ReadMode().Mode; }
                catch (Exception) { return null; }
            }
        }
    }

    public void Reconnect()
    {
        lock (_lock)
        {
            if (_ctl is not null)
            {
                try { _ctl.ReadMode(); return; } // 아직 살아 있음
                catch (Exception) { _ctl.Dispose(); _ctl = null; }
            }

            _ctl = ClawController.TryOpen();
            if (_ctl is null) { _log.LogWarning("컨트롤러를 찾지 못함"); StatusChanged?.Invoke(); return; }

            try
            {
                _current = _ctl.ReadProfile();
                LoadOrCaptureBaseline();
                // 재연결(모드 전환 재열거 등) 후 적용 중이던 매핑을 다시 쓴다
                if (_activeBindings is not null) WriteBindings(_activeBindings);
                _log.LogInformation("컨트롤러 연결: 펌웨어 {Fw}", _ctl.FirmwareVersion);
            }
            catch (Exception ex)
            {
                _log.LogError(ex, "컨트롤러 초기화 실패");
                _ctl.Dispose();
                _ctl = null;
            }
        }
        StatusChanged?.Invoke();
    }

    private void LoadOrCaptureBaseline()
    {
        var saved = File.Exists(BaselinePath) ? File.ReadAllBytes(BaselinePath) : null;
        if (saved is { Length: ProfileLayout.ImageLength } && File.Exists(ActiveMarkerPath))
        {
            _log.LogWarning("이전 세션의 매핑이 남아 있음 → baseline으로 복원");
            _baseline = saved;
            _ctl!.WriteProfileRam(_current!, _baseline);
            _current = (byte[])_baseline.Clone();
            File.Delete(ActiveMarkerPath);
            return;
        }

        // 매핑이 적용되지 않은 상태의 현재 프로필이 곧 사용자의 원래 설정이다 (MSI에서 바꾼 M1/M2 포함)
        _baseline = (byte[])_current!.Clone();
        File.WriteAllBytes(BaselinePath, _baseline);
    }

    public void ApplyMapping(IReadOnlyDictionary<ClawButton, byte[]> bindings)
    {
        lock (_lock)
        {
            WriteBindings(bindings);
            _activeBindings = bindings;
            File.WriteAllText(ActiveMarkerPath, DateTime.Now.ToString("O"));
        }
        StatusChanged?.Invoke();
    }

    public void RestoreBaseline()
    {
        lock (_lock)
        {
            _activeBindings = null;
            if (_ctl is not null && _baseline is not null && _current is not null)
            {
                _ctl.WriteProfileRam(_current, _baseline);
                _current = (byte[])_baseline.Clone();
            }
            File.Delete(ActiveMarkerPath);
        }
        StatusChanged?.Invoke();
    }

    public void SetMode(GamepadMode mode)
    {
        lock (_lock)
        {
            var ctl = _ctl ?? throw new InvalidOperationException("컨트롤러 미연결");
            var (_, mk) = ctl.ReadMode();
            ctl.SwitchMode(mode, mk);
        }
        StatusChanged?.Invoke();
    }

    private void WriteBindings(IReadOnlyDictionary<ClawButton, byte[]> bindings)
    {
        var ctl = _ctl ?? throw new InvalidOperationException("컨트롤러 미연결");
        var target = ProfileLayout.Apply(_baseline!, bindings);
        ctl.WriteProfileRam(_current!, target);
        _current = target;
    }

    public void Dispose()
    {
        try { RestoreBaseline(); } catch (Exception) { /* 종료 중 */ }
        _ctl?.Dispose();
    }
}
