using System.ComponentModel;
using System.Diagnostics;
using System.Runtime.InteropServices;
using ClawCore.Gpu;

namespace ClawService;

/// <summary>
/// 실행 중 게임의 실제 FPS·프레임 시간을 PresentMon(ETW, 주입 없음)으로 잰다.
/// ETW 실시간 세션은 관리자/Performance Log Users 권한이 필요하므로 서비스(LocalSystem)에서만 돌린다.
/// PresentMon은 (1) 게임 PID를 알고 (2) 최근 10초 안에 누가 측정값을 조회했을 때만 켜 둔다.
/// 게임이 끝나거나 아무도 안 보면 끈다. 서비스가 죽어도 남지 않게 Job 객체에 넣는다.
/// </summary>
public sealed class FrameMonitor(ILogger<FrameMonitor> log) : IDisposable
{
    /// <summary>MSI Center M에 들어 있는 PresentMon 1.x 콘솔판 (MSI 서명).</summary>
    private static readonly string[] Candidates =
    [
        Path.Combine(AppContext.BaseDirectory, "PresentMon", "PresentMon.exe"), // 직접 넣어 둔 경우 (2.x 콘솔)
        @"C:\Program Files (x86)\MSI\MSI Center M\Resources\OSDInfo\PresentMon-dev-x64.exe",
    ];

    private const string SessionName = "ClawHub_PresentMon";
    private static readonly TimeSpan PollTimeout = TimeSpan.FromSeconds(10);
    /// <summary>
    /// 이 시간 동안 새 프레임 줄이 없으면 측정값을 비운다. PresentMon의 표준 출력은 파이프에서 4KB 단위로
    /// 버퍼링되므로(줄당 ~120바이트 → 30fps면 ~1초 지연) 넉넉히 둔다. FPS 값 자체는 프레임 시간으로 계산해 영향 없음.
    /// </summary>
    private static readonly TimeSpan Stale = TimeSpan.FromSeconds(5);

    private readonly object _lock = new();
    private readonly FrameStats _stats = new();
    private Timer? _timer;
    private int? _targetPid;
    private DateTime _lastPoll = DateTime.MinValue;
    private Process? _proc;
    private int _procTarget;
    private DateTime _startedAt;
    private DateTime _retryAt = DateTime.MinValue;
    private IntPtr _job;
    private bool _warnedMissing;

    /// <summary>PresentMon 실행 파일 (없으면 null → FPS 측정 안 함).</summary>
    public static string? ExePath => Candidates.FirstOrDefault(File.Exists);

    /// <summary>마지막 실패 이유 (진단용).</summary>
    public string? LastError { get; private set; }

    public void Start() => _timer ??= new Timer(_ => Supervise(), null, TimeSpan.FromSeconds(1), TimeSpan.FromSeconds(1));

    /// <summary>앱이 알려 준 게임 프로세스 (SetAppState). null이면 게임 없음.</summary>
    public void SetTarget(int? pid)
    {
        lock (_lock)
        {
            if (_targetPid == pid) return;
            _targetPid = pid is > 4 ? pid : null; // 0 = Idle, 4 = System
            _retryAt = DateTime.MinValue;
        }
        ThreadPool.QueueUserWorkItem(_ => Supervise()); // 요청 스레드를 PresentMon 종료·정리로 막지 않는다
    }

    /// <summary>누군가 측정값을 보고 있다 (GetTelemetry). 10초간 조회가 없으면 PresentMon을 끈다.</summary>
    public void NotePoll()
    {
        bool startNow;
        lock (_lock)
        {
            startNow = DateTime.UtcNow - _lastPoll > PollTimeout; // 다시 보기 시작 → 바로 켠다
            _lastPoll = DateTime.UtcNow;
        }
        if (startNow) ThreadPool.QueueUserWorkItem(_ => Supervise());
    }

    /// <summary>현재 측정값. 측정 중이 아니거나 최근 프레임이 없으면 null.</summary>
    public FrameSnapshot? Current => _running ? _stats.Snapshot(Stale) : null; // 락 없이 (정리 중에도 조회가 막히지 않게)

    private volatile bool _running;

    private void Supervise()
    {
        lock (_lock)
        {
            try
            {
                var target = _targetPid;
                var wanted = target is { } pid && DateTime.UtcNow - _lastPoll < PollTimeout && IsAlive(pid);
                if (!wanted)
                {
                    StopLocked("대상 없음/조회 없음");
                    return;
                }

                if (_proc is { } p)
                {
                    if (_procTarget == target && !HasExited(p)) return; // 정상 측정 중
                    if (HasExited(p) && DateTime.UtcNow - _startedAt < TimeSpan.FromSeconds(5))
                    {
                        // 곧바로 꺼짐 = ETW 세션 실패 등 → 30초 뒤 재시도
                        _retryAt = DateTime.UtcNow + TimeSpan.FromSeconds(30);
                        LastError ??= $"PresentMon이 바로 종료됨 (코드 {SafeExitCode(p)})";
                        log.LogWarning("PresentMon 시작 실패: {Error}", LastError);
                    }
                    StopLocked("대상 변경/종료");
                }

                if (DateTime.UtcNow < _retryAt) return;
                StartLocked(target!.Value);
            }
            catch (Exception ex)
            {
                log.LogError(ex, "FPS 측정 관리 실패");
                _retryAt = DateTime.UtcNow + TimeSpan.FromSeconds(30);
            }
        }
    }

    private void StartLocked(int pid)
    {
        if (ExePath is not { } exe)
        {
            if (!_warnedMissing) log.LogInformation("PresentMon이 없어 FPS를 측정하지 않습니다");
            _warnedMissing = true;
            LastError = "PresentMon 없음";
            _retryAt = DateTime.UtcNow + TimeSpan.FromMinutes(1);
            return;
        }

        var v2 = FileVersionInfo.GetVersionInfo(exe).FileMajorPart >= 2;
        // 1.x: 대시 하나, 2.x: 대시 둘. 표준 출력 CSV, 대상 PID 하나, 대상이 끝나면 스스로 종료.
        var d = v2 ? "--" : "-";
        var args = string.Join(' ',
            $"{d}process_id {pid}", $"{d}output_stdout", v2 ? "--no_console_stats" : "-no_top",
            $"{d}session_name {SessionName}", $"{d}stop_existing_session", $"{d}terminate_on_proc_exit",
            v2 ? "" : "-no_track_display");

        var psi = new ProcessStartInfo(exe, args)
        {
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            RedirectStandardInput = true, // 콘솔 입력 대기(키 입력으로 종료 등) 방지
            WorkingDirectory = Path.GetDirectoryName(exe)!,
        };
        _stats.Reset();
        var p = new Process { StartInfo = psi, EnableRaisingEvents = true };
        p.OutputDataReceived += (_, e) => _stats.AddLine(e.Data);
        p.ErrorDataReceived += (_, e) =>
        {
            if (string.IsNullOrWhiteSpace(e.Data) || e.Data.StartsWith("warning", StringComparison.OrdinalIgnoreCase)) return;
            if (e.Data.Contains("error", StringComparison.OrdinalIgnoreCase)) LastError = e.Data.Trim();
            log.LogDebug("PresentMon: {Line}", e.Data);
        };
        try
        {
            p.Start();
        }
        catch (Win32Exception ex)
        {
            p.Dispose();
            LastError = ex.Message;
            _retryAt = DateTime.UtcNow + TimeSpan.FromMinutes(1);
            log.LogWarning(ex, "PresentMon 실행 실패");
            return;
        }
        AddToJob(p);
        p.BeginOutputReadLine();
        p.BeginErrorReadLine();
        _proc = p;
        _running = true;
        _procTarget = pid;
        _startedAt = DateTime.UtcNow;
        LastError = null;
        log.LogInformation("FPS 측정 시작 (PID {Pid}, {Exe})", pid, Path.GetFileName(exe));
    }

    private void StopLocked(string why)
    {
        if (_proc is not { } p) return;
        _proc = null;
        _running = false;
        try
        {
            if (!p.HasExited)
            {
                // 입력을 닫아도 안 끝나므로 바로 종료. ETW 세션은 다음 시작 때 stop_existing_session으로 정리된다.
                p.Kill();
                p.WaitForExit(2000);
            }
        }
        catch (Exception ex) when (ex is InvalidOperationException or Win32Exception) { /* 이미 종료 */ }
        var exe = p.StartInfo.FileName;
        var v2 = p.StartInfo.Arguments.StartsWith("--", StringComparison.Ordinal);
        p.Dispose();
        _stats.Reset();
        log.LogInformation("FPS 측정 중지 ({Why})", why);

        // 강제 종료하면 ETW 실시간 세션이 남으므로 이름으로 정리한다 (게임 종료로 스스로 끝났으면 이미 없음).
        // 락 안에서 동기로 — 곧이어 같은 이름으로 새 세션을 시작할 수 있으므로.
        try
        {
            var d = v2 ? "--" : "-";
            using var t = Process.Start(new ProcessStartInfo(exe, $"{d}terminate_existing {d}session_name {SessionName}")
            {
                UseShellExecute = false,
                CreateNoWindow = true,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
            });
            if (t is not null && !t.WaitForExit(5000)) t.Kill();
        }
        catch (Exception ex) when (ex is Win32Exception or InvalidOperationException)
        {
            log.LogDebug(ex, "PresentMon 세션 정리 실패");
        }
    }

    private static bool IsAlive(int pid)
    {
        try
        {
            using var p = Process.GetProcessById(pid);
            return !p.HasExited;
        }
        catch (Exception ex) when (ex is ArgumentException or InvalidOperationException or Win32Exception)
        {
            return false;
        }
    }

    private static bool HasExited(Process p)
    {
        try { return p.HasExited; }
        catch (InvalidOperationException) { return true; }
    }

    private static string SafeExitCode(Process p)
    {
        try { return p.ExitCode.ToString(); }
        catch (InvalidOperationException) { return "?"; }
    }

    // ── Job 객체: 서비스 프로세스가 끝나면 PresentMon도 같이 끝난다 ──

    private void AddToJob(Process p)
    {
        try
        {
            if (_job == IntPtr.Zero)
            {
                _job = CreateJobObject(IntPtr.Zero, null);
                var info = new JobExtendedLimitInformation { BasicLimitInformation = { LimitFlags = 0x2000 /* KILL_ON_JOB_CLOSE */ } };
                var size = Marshal.SizeOf<JobExtendedLimitInformation>();
                var buf = Marshal.AllocHGlobal(size);
                try
                {
                    Marshal.StructureToPtr(info, buf, false);
                    SetInformationJobObject(_job, 9 /* ExtendedLimitInformation */, buf, (uint)size);
                }
                finally { Marshal.FreeHGlobal(buf); }
            }
            AssignProcessToJobObject(_job, p.Handle);
        }
        catch (Exception ex) when (ex is Win32Exception or InvalidOperationException)
        {
            log.LogDebug(ex, "Job 객체 등록 실패");
        }
    }

    public void Dispose()
    {
        _timer?.Dispose();
        lock (_lock) StopLocked("서비스 종료");
        if (_job != IntPtr.Zero) CloseHandle(_job);
        _job = IntPtr.Zero;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct JobBasicLimitInformation
    {
        public long PerProcessUserTimeLimit;
        public long PerJobUserTimeLimit;
        public uint LimitFlags;
        public UIntPtr MinimumWorkingSetSize;
        public UIntPtr MaximumWorkingSetSize;
        public uint ActiveProcessLimit;
        public UIntPtr Affinity;
        public uint PriorityClass;
        public uint SchedulingClass;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct JobExtendedLimitInformation
    {
        public JobBasicLimitInformation BasicLimitInformation;
        public ulong ReadOperationCount, WriteOperationCount, OtherOperationCount;
        public ulong ReadTransferCount, WriteTransferCount, OtherTransferCount;
        public UIntPtr ProcessMemoryLimit, JobMemoryLimit, PeakProcessMemoryUsed, PeakJobMemoryUsed;
    }

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode)]
    private static extern IntPtr CreateJobObject(IntPtr attributes, string? name);

    [DllImport("kernel32.dll")]
    private static extern bool SetInformationJobObject(IntPtr job, int infoClass, IntPtr info, uint length);

    [DllImport("kernel32.dll")]
    private static extern bool AssignProcessToJobObject(IntPtr job, IntPtr process);

    [DllImport("kernel32.dll")]
    private static extern bool CloseHandle(IntPtr handle);
}
