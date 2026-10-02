using System.Diagnostics;

namespace ClawCore.Games;

/// <summary>
/// 게임 실행과 종료 감지. 런처(Steam/넥슨) 경유 실행도 있으므로 직접 띄운 프로세스가 아니라
/// 설치 폴더/프로세스 이름으로 "게임이 돌고 있는지"를 판단한다.
/// 설치 폴더로 찾을 때는 런처·안티치트·업데이터 같은 도우미 프로세스는 세지 않는다 (<see cref="ProcessScanner.IsHelper"/>).
/// </summary>
public sealed class GameSession : IDisposable
{
    private static readonly TimeSpan PollInterval = TimeSpan.FromSeconds(1.5);
    /// <summary>실행 후 이 시간 안에 게임 프로세스가 안 보이면 실행 실패로 본다 (런처 로그인 대기 고려).</summary>
    private static readonly TimeSpan StartGrace = TimeSpan.FromMinutes(3);

    private readonly Timer _timer;
    private readonly object _pollLock = new();
    /// <summary>이 세션에서 설치 폴더로 찾은 게임 본체 프로세스 이름들 — 종료 대상.</summary>
    private readonly HashSet<string> _seenNames = new(StringComparer.OrdinalIgnoreCase);
    private DateTime _launchedAt;
    private bool _seenRunning;
    private bool _disposed;

    public GameEntry Game { get; }
    public bool IsRunning { get; private set; }
    /// <summary>ClawHub가 직접 실행했는지 (false면 Xbox 앱/런처 등 외부에서 실행된 것을 감지해 붙은 세션).</summary>
    public bool LaunchedByUs { get; private init; } = true;

    public event Action<GameSession>? Started;
    public event Action<GameSession>? Ended;

    private GameSession(GameEntry game)
    {
        Game = game;
        _timer = new Timer(_ => Poll(), null, Timeout.Infinite, Timeout.Infinite);
    }

    public static GameSession Launch(GameEntry game)
    {
        var session = new GameSession(game);
        try
        {
            using var _ = Process.Start(new ProcessStartInfo(game.LaunchTarget, game.LaunchArgs ?? "")
            {
                UseShellExecute = true,
                WorkingDirectory = File.Exists(game.LaunchTarget) ? Path.GetDirectoryName(game.LaunchTarget)! : "",
            });
        }
        catch
        {
            session.Dispose();
            throw;
        }
        session._launchedAt = DateTime.UtcNow;
        session.Schedule();
        return session;
    }

    /// <summary>이미 돌고 있는 게임(외부 실행)에 붙는다. 종료 감지만 한다.</summary>
    public static GameSession Attach(GameEntry game)
    {
        var session = new GameSession(game) { LaunchedByUs = false, _seenRunning = true, IsRunning = true };
        session._launchedAt = DateTime.UtcNow;
        session.Schedule();
        return session;
    }

    /// <summary>
    /// 후보 게임 중 지금 실행 중인 첫 번째를 찾는다. 프로세스 목록은 한 번만 훑는다.
    /// <paramref name="onlyPid"/>를 주면 그 프로세스만 본다 (앞에 떠 있는 창 — 트레이에 상주하는 메신저 등이 "실행 중 게임"이 되지 않게).
    /// </summary>
    public static GameEntry? FindRunning(IReadOnlyCollection<GameEntry> candidates, int? onlyPid = null)
    {
        if (candidates.Count == 0) return null;
        var byName = new Dictionary<string, GameEntry>(StringComparer.OrdinalIgnoreCase);
        foreach (var g in candidates)
            foreach (var n in g.ProcessNames) byName.TryAdd(n, g);
        var dirs = candidates.Select(g => (Dir: ProcessScanner.SafeInstallDir(g.InstallDir), Game: g))
            .Where(x => x.Dir is not null).ToList();

        // 한 프로세스만 볼 때는 전체 목록을 만들지 않는다 (2초마다 불림)
        foreach (var p in onlyPid is { } pid ? ProcessScanner.Single(pid) : ProcessScanner.List())
        {
            if (onlyPid is { } only && p.Id != only) continue;
            if (byName.TryGetValue(p.Name, out var hit)) return hit;
            if (dirs.Count == 0 || ProcessScanner.IsHelper(p.Name) || ProcessScanner.PathOf(p) is not { } path) continue;
            foreach (var (dir, game) in dirs)
                if (path.StartsWith(dir!, StringComparison.OrdinalIgnoreCase)) return game;
        }
        return null;
    }

    /// <summary>
    /// 게임 프로세스를 종료한다 (퀵 설정의 "게임 종료"). 폴더 전체가 아니라 게임 자신의 실행 파일만:
    /// 지정된 프로세스 이름, 실행 대상/아이콘 exe, 이 세션에서 설치 폴더로 찾은 게임 본체.
    /// 이름만 같은 다른 폴더의 프로세스는 건드리지 않는다. 프로세스 목록을 훑으므로 UI 스레드에서 부르지 말 것.
    /// </summary>
    public void Kill()
    {
        var explicitNames = new HashSet<string>(Game.ProcessNames, StringComparer.OrdinalIgnoreCase);
        var exeNames = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var exe in new[] { Game.LaunchTarget, Game.IconSource })
            if (exe is not null && exe.EndsWith(".exe", StringComparison.OrdinalIgnoreCase)
                && Path.GetFileNameWithoutExtension(exe) is { Length: > 0 } n && !ProcessScanner.IsHelper(n))
                exeNames.Add(n);
        foreach (var p in FindGameProcesses()) exeNames.Add(p.Name); // 지금 도는 게임 본체 (도우미 제외)
        lock (_pollLock) exeNames.UnionWith(_seenNames);

        // 지정 이름이 아닌 것은 게임 폴더 안의 같은 이름일 때만 (흔한 이름 "Game" 등이 다른 곳에서 죽지 않게)
        var dir = ProcessScanner.SafeInstallDir(Game.InstallDir)
            ?? (File.Exists(Game.LaunchTarget) ? ProcessScanner.SafeInstallDir(Path.GetDirectoryName(Game.LaunchTarget)) : null);
        foreach (var p in ProcessScanner.List())
        {
            if (p.Id == Environment.ProcessId) continue;
            var kill = explicitNames.Contains(p.Name)
                || (dir is not null && exeNames.Contains(p.Name)
                    && ProcessScanner.PathOf(p)?.StartsWith(dir, StringComparison.OrdinalIgnoreCase) == true);
            if (!kill) continue;
            try
            {
                using var proc = Process.GetProcessById(p.Id);
                proc.Kill(entireProcessTree: true);
            }
            catch (Exception) { /* 권한 없음/이미 종료 */ }
        }
    }

    /// <summary>게임 창 (게임으로 돌아가기용). 없으면 0. 프로세스 목록을 훑으므로 UI 스레드에서 부르지 말 것.</summary>
    public IntPtr FindMainWindow()
    {
        foreach (var p in FindGameProcesses())
        {
            try
            {
                using var proc = Process.GetProcessById(p.Id);
                if (proc.MainWindowHandle != IntPtr.Zero) return proc.MainWindowHandle;
            }
            catch (Exception) { /* 이미 종료 */ }
        }
        return IntPtr.Zero;
    }

    /// <summary>지금 도는 게임 프로세스 ID (도우미 제외). 프로세스 목록을 훑으므로 UI 스레드에서 부르지 말 것.</summary>
    public List<int> FindProcessIds() => [.. FindGameProcesses().Select(p => p.Id)];

    private void Schedule()
    {
        // 한 번씩만 예약하고 Poll이 끝난 뒤 다시 예약 — 목록 훑기가 느려도 Poll이 겹치지 않는다
        lock (_pollLock)
        {
            if (!_disposed) _timer.Change(PollInterval, Timeout.InfiniteTimeSpan);
        }
    }

    /// <summary>
    /// 마지막으로 찾은 게임 프로세스들의 핸들. 하나라도 살아 있으면 "실행 중"이 확실하므로 1.5초마다 전체 프로세스 목록
    /// (수백 개 + 스레드 정보, ≈3ms·0.5MB)을 다시 만들지 않는다 — 게임하는 동안 런처가 거의 아무 일도 하지 않게.
    /// 모두 끝났거나(또는 열 수 없는 프로세스뿐이면) 예전처럼 목록을 훑는다. _pollLock 안에서만 만진다.
    /// </summary>
    private readonly List<WaitHandle> _watched = [];

    private bool AnyWatchedAliveLocked()
    {
        foreach (var w in _watched)
        {
            try
            {
                if (!w.WaitOne(0)) return true; // 신호 없음 = 아직 실행 중
            }
            catch (Exception ex) when (ex is ObjectDisposedException or AbandonedMutexException) { /* 끝난 것으로 본다 */ }
        }
        return false;
    }

    private void ReplaceWatchedLocked(List<WaitHandle> next)
    {
        foreach (var w in _watched) w.Dispose();
        _watched.Clear();
        _watched.AddRange(next);
    }

    private void Poll()
    {
        try
        {
            bool running;
            lock (_pollLock)
            {
                if (_disposed) return;
                running = _seenRunning && AnyWatchedAliveLocked();
            }
            if (!running)
            {
                var found = FindGameProcesses();
                running = found.Count > 0;
                var watch = new List<WaitHandle>();
                foreach (var p in found)
                    if (ProcessScanner.Watch(p) is { } h) watch.Add(h);
                lock (_pollLock)
                {
                    if (_disposed) { foreach (var h in watch) h.Dispose(); return; }
                    ReplaceWatchedLocked(watch);
                }
            }

            if (running && !_seenRunning)
            {
                _seenRunning = IsRunning = true;
                Started?.Invoke(this);
            }
            else if (!running && (_seenRunning || DateTime.UtcNow - _launchedAt > StartGrace))
            {
                IsRunning = false;
                Ended?.Invoke(this);
                return; // 끝난 세션은 다시 예약하지 않는다
            }
        }
        catch (Exception)
        {
            // 타이머 스레드의 예외는 앱 전체를 죽인다 — 다음 주기에 다시 본다
        }
        Schedule();
    }

    /// <summary>게임 프로세스: 지정된 이름, 또는 설치 폴더 아래의 도우미가 아닌 프로세스.</summary>
    private List<ProcessScanner.Proc> FindGameProcesses()
    {
        var names = new HashSet<string>(Game.ProcessNames, StringComparer.OrdinalIgnoreCase);
        var dir = ProcessScanner.SafeInstallDir(Game.InstallDir);
        var found = new List<ProcessScanner.Proc>();

        foreach (var p in ProcessScanner.List())
        {
            if (names.Contains(p.Name)) { found.Add(p); continue; }
            if (dir is not null && !ProcessScanner.IsHelper(p.Name)
                && ProcessScanner.PathOf(p) is { } path && path.StartsWith(dir, StringComparison.OrdinalIgnoreCase))
            {
                found.Add(p);
                lock (_pollLock) _seenNames.Add(p.Name);
            }
        }
        return found;
    }

    /// <summary>프로세스의 실행 파일 경로 (권한 없음/종료됨이면 null).</summary>
    public static string? GetPath(Process p) => ProcessScanner.QueryPath(p.Id);

    public void Dispose()
    {
        lock (_pollLock)
        {
            _disposed = true;
            _timer.Dispose();
            ReplaceWatchedLocked([]);
        }
    }
}
