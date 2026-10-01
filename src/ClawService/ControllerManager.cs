using ClawCore;
using ClawCore.Controller;
using HidSharp;

namespace ClawService;

/// <summary>
/// 컨트롤러 소유자. 컨트롤러 RAM 상태 = baseline(원래 프로필) + 사용자 설정 + (게임 중이면) 매핑.
/// 플래시에는 쓰지 않으므로 재연결·재부팅마다 다시 적용한다.
/// </summary>
public sealed class ControllerManager : IDisposable
{
    private static string BaselinePath => Path.Combine(JsonStore.ServiceDir, "controller-baseline.bin");
    private static string SettingsPath => Path.Combine(JsonStore.ServiceDir, "controller-settings.json");
    /// <summary>이 파일이 있으면 RAM이 baseline과 다른 상태 → 서비스 재시작 시 현재 RAM을 baseline으로 착각하지 않도록 저장본 사용.</summary>
    private static string ModifiedMarkerPath => Path.Combine(JsonStore.ServiceDir, "mapping-active");

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
        Settings = JsonStore.Load<ControllerSettings>(SettingsPath);
        DeviceList.Local.Changed += (_, _) => Task.Run(Reconnect);
        Reconnect();
    }

    public ControllerSettings Settings { get; private set; }
    public ControllerSettings? Defaults => _baseline is null ? null : SettingsLayout.ReadDefaults(_baseline);
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
                WriteTarget(); // 설정(+진행 중인 매핑) 재적용
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
        if (_baseline is not null) return; // 재연결: 이미 알고 있음

        var saved = File.Exists(BaselinePath) ? File.ReadAllBytes(BaselinePath) : null;
        if (saved is { Length: ProfileLayout.ImageLength } && File.Exists(ModifiedMarkerPath))
        {
            _log.LogInformation("RAM이 수정된 상태로 재시작됨 → 저장된 baseline 사용");
            _baseline = saved;
            return;
        }

        // 아무것도 덮어쓰지 않은 상태의 현재 프로필이 곧 사용자의 원래 설정 (MSI에서 바꾼 M1/M2 포함)
        _baseline = (byte[])_current!.Clone();
        File.WriteAllBytes(BaselinePath, _baseline);
    }

    public void ApplyMapping(IReadOnlyDictionary<ClawButton, byte[]> bindings)
    {
        lock (_lock)
        {
            _activeBindings = bindings;
            WriteTarget();
        }
        StatusChanged?.Invoke();
    }

    public void RestoreBaseline()
    {
        lock (_lock)
        {
            _activeBindings = null;
            WriteTarget();
        }
        StatusChanged?.Invoke();
    }

    public void SetSettings(ControllerSettings settings)
    {
        lock (_lock)
        {
            Settings = settings;
            JsonStore.Save(SettingsPath, settings);
            WriteTarget();
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

    private void WriteTarget()
    {
        if (_ctl is null || _baseline is null || _current is null) return;

        var target = _activeBindings is null ? (byte[])_baseline.Clone() : ProfileLayout.Apply(_baseline, _activeBindings);
        SettingsLayout.Apply(target, Settings);

        if (_ctl.WriteProfileRam(_current, target) is { } range && SettingsLayout.TouchesLight(range.First, range.Last))
            _ctl.SyncRgb();
        _current = target;

        if (target.AsSpan().SequenceEqual(_baseline)) File.Delete(ModifiedMarkerPath);
        else File.WriteAllText(ModifiedMarkerPath, DateTime.Now.ToString("O"));
    }

    public void Dispose()
    {
        // 서비스 종료 시 매핑만 풀고 사용자 설정은 유지
        try { RestoreBaseline(); } catch (Exception) { /* 종료 중 */ }
        _ctl?.Dispose();
    }
}
