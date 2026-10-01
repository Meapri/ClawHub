using System.Management;
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

    /// <summary>앱이 마지막으로 요청한 모드. 절전 복귀·재연결로 컨트롤러가 기본 모드로 돌아가면 다시 맞춘다.</summary>
    private GamepadMode? _desiredMode;
    private readonly ManagementEventWatcher? _resumeWatcher;

    public ControllerManager(ILogger<ControllerManager> log)
    {
        _log = log;
        Settings = JsonStore.Load<ControllerSettings>(SettingsPath);
        DeviceList.Local.Changed += (_, _) => Task.Run(Reconnect);
        Reconnect();

        // 절전 복귀 때 USB 재열거 없이 컨트롤러 RAM만 초기화될 수 있다 → DeviceList 이벤트가 없어도 다시 맞춘다
        try
        {
            _resumeWatcher = new ManagementEventWatcher(new WqlEventQuery("SELECT * FROM Win32_PowerManagementEvent WHERE EventType = 7"));
            _resumeWatcher.EventArrived += (_, _) => Task.Delay(3000).ContinueWith(_ => Reconnect());
            _resumeWatcher.Start();
        }
        catch (Exception ex)
        {
            _log.LogWarning(ex, "절전 복귀 감시 실패 — 복귀 후 컨트롤러 설정은 재연결 때만 다시 적용됩니다");
        }
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

    /// <summary>
    /// 장치 목록 변경·절전 복귀·시작 때 호출. 연결이 살아 있으면 RAM이 기대한 상태인지 확인해 다르면 다시 쓰고
    /// (절전 복귀 시 재열거 없이 RAM이 초기화되는 경우), 끊겼으면 다시 열어 설정/매핑을 재적용한다.
    /// </summary>
    public void Reconnect()
    {
        lock (_lock)
        {
            if (_disposed) return;
            ReconnectLocked();
        }
        StatusChanged?.Invoke();
    }

    private bool _disposed;

    private void ReconnectLocked()
    {
        if (_ctl is not null)
        {
            var alive = false;
            try { _ctl.ReadMode(); alive = true; }
            catch (Exception) { _ctl.Dispose(); _ctl = null; }

            if (alive)
            {
                try { WriteTarget(); } // 쓰기 전에 RAM을 다시 읽으므로, 그대로면 아무것도 쓰지 않는다
                catch (Exception ex) { _log.LogError(ex, "컨트롤러 상태 재확인 실패"); }
                RestoreMode();
                return;
            }
        }

        _ctl = ClawController.TryOpen();
        if (_ctl is null) { _log.LogWarning("컨트롤러를 찾지 못함"); return; }

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
            return;
        }
        RestoreMode();
    }

    /// <summary>앱이 고른 모드와 실제 모드가 다르면 (절전 복귀·재연결로 초기화) 다시 맞춘다. _lock 안에서 호출.</summary>
    private void RestoreMode()
    {
        if (_ctl is null || _desiredMode is not { } want) return;
        try
        {
            var (mode, mk) = _ctl.ReadMode();
            if (mode == want) return;
            _log.LogInformation("컨트롤러 모드가 {Actual}(으)로 바뀌어 있음 → {Want} 재적용", mode, want);
            _ctl.SwitchModeVerified(want, mk);
        }
        catch (Exception ex) { _log.LogError(ex, "컨트롤러 모드 재적용 실패"); }
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

    public void ApplyMapping(IReadOnlyDictionary<ClawButton, byte[]> bindings, IReadOnlyDictionary<ClawButton, MacroStep[]>? macros = null)
    {
        if (bindings.Count > Enum.GetValues<ClawButton>().Length || bindings.Keys.Any(b => !Enum.IsDefined(b)))
            throw new ArgumentException("알 수 없는 버튼");
        if (macros?.Keys.Any(b => b is not (ClawButton.M1 or ClawButton.M2)) == true)
            throw new ArgumentException("매크로는 M1/M2만 가능");
        lock (_lock)
        {
            // 검증(이미지 생성)이 성공한 뒤에만 상태를 바꾼다
            WriteTarget(bindings, macros, Settings);
            _activeBindings = bindings;
            _activeMacros = macros;
        }
        StatusChanged?.Invoke();
    }

    private IReadOnlyDictionary<ClawButton, MacroStep[]>? _activeMacros;

    public void RestoreBaseline()
    {
        lock (_lock)
        {
            _activeBindings = null;
            _activeMacros = null;
            WriteTarget(null, null, Settings);
        }
        StatusChanged?.Invoke();
    }

    public void SetSettings(ControllerSettings settings)
    {
        if (settings.LightColor is not { Length: 3 }) throw new ArgumentException("LightColor는 RGB 3바이트");
        if (!Enum.IsDefined(settings.Light)) throw new ArgumentException("알 수 없는 조명 모드");
        lock (_lock)
        {
            WriteTarget(_activeBindings, _activeMacros, settings);
            Settings = settings;
            JsonStore.Save(SettingsPath, settings);
        }
        StatusChanged?.Invoke();
    }

    public void SetMode(GamepadMode mode)
    {
        Exception? error = null;
        lock (_lock)
        {
            var ctl = _ctl ?? throw new InvalidOperationException("컨트롤러 미연결");
            var (_, mk) = ctl.ReadMode();
            try
            {
                ctl.SwitchModeVerified(mode, mk); // 0x24는 응답이 없으므로 0x26으로 다시 읽어 확인
                _desiredMode = mode;
            }
            catch (Exception ex)
            {
                error = ex;
            }
        }
        StatusChanged?.Invoke(); // 실패해도 실제 모드를 알린다 (앱의 모드 장부 무효화)
        if (error is not null) System.Runtime.ExceptionServices.ExceptionDispatchInfo.Throw(error);
    }

    private void WriteTarget() => WriteTarget(_activeBindings, _activeMacros, Settings);

    /// <summary>
    /// baseline + 설정 + 매핑으로 목표 이미지를 만들어 RAM에 쓴다.
    /// 이미지 생성(=입력 검증)은 쓰기 전에 끝나고, 표시 파일은 쓰기 전에 남긴다(중간에 죽어도 다음 시작 때 저장된 baseline 사용).
    /// 쓰기가 실패하면 실제 RAM 상태를 다시 읽어서 다음 복원이 헛돌지 않게 한다.
    /// </summary>
    private void WriteTarget(IReadOnlyDictionary<ClawButton, byte[]>? bindings, IReadOnlyDictionary<ClawButton, MacroStep[]>? macros,
        ControllerSettings settings)
    {
        if (_ctl is null || _baseline is null || _current is null) return;

        var target = bindings is null ? (byte[])_baseline.Clone() : ProfileLayout.Apply(_baseline, bindings);
        foreach (var (button, steps) in macros ?? new Dictionary<ClawButton, MacroStep[]>())
            MacroLayout.Apply(target, button, steps);
        SettingsLayout.Apply(target, settings);

        // 마지막으로 쓴 상태를 믿지 않고 실제 RAM을 다시 읽어 차이를 계산한다 (27회 읽기 ≈ 0.1~0.2초).
        // 절전 복귀로 RAM이 초기화됐거나 다른 프로그램이 바꿨으면 전체 차이를 다시 쓴다.
        var actual = _ctl.ReadProfile();
        if (!actual.AsSpan().SequenceEqual(_current))
            _log.LogWarning("컨트롤러 RAM이 마지막으로 쓴 상태와 다릅니다 (절전 복귀 또는 외부 변경) → 다시 적용");
        _current = actual;

        var modified = !target.AsSpan().SequenceEqual(_baseline);
        if (modified) File.WriteAllText(ModifiedMarkerPath, DateTime.Now.ToString("O"));

        try
        {
            if (_ctl.WriteProfileRam(_current, target) is { } range)
            {
                _log.LogDebug("RAM 쓰기 0x{First:X}..0x{Last:X}, ack 0x{Ack:X2}", range.First, range.Last, _ctl.LastWriteAck);
                if (SettingsLayout.TouchesLight(range.First, range.Last)) _ctl.SyncRgb();
            }
            _current = target;
        }
        catch (Exception)
        {
            try { _current = _ctl.ReadProfile(); }
            catch (Exception) { /* 장치가 사라짐 → Reconnect가 처리 */ }
            throw;
        }

        if (!modified) File.Delete(ModifiedMarkerPath);
    }

    public void Dispose()
    {
        // 서비스 종료 시 매핑만 풀고 사용자 설정은 유지
        try { _resumeWatcher?.Stop(); _resumeWatcher?.Dispose(); } catch (Exception) { /* 종료 중 */ }
        try { RestoreBaseline(); } catch (Exception) { /* 종료 중 */ }
        lock (_lock)
        {
            _disposed = true;
            _ctl?.Dispose();
            _ctl = null;
        }
    }
}
