using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Text;

namespace ClawCore.Games;

/// <summary>
/// 게임 실행과 종료 감지. 런처(Steam/넥슨) 경유 실행도 있으므로 직접 띄운 프로세스가 아니라
/// 설치 폴더/프로세스 이름으로 "게임이 돌고 있는지"를 판단한다.
/// </summary>
public sealed class GameSession : IDisposable
{
    private static readonly TimeSpan PollInterval = TimeSpan.FromSeconds(1.5);
    /// <summary>실행 후 이 시간 안에 게임 프로세스가 안 보이면 실행 실패로 본다 (런처 로그인 대기 고려).</summary>
    private static readonly TimeSpan StartGrace = TimeSpan.FromMinutes(3);

    private readonly Timer _timer;
    private DateTime _launchedAt;
    private bool _seenRunning;

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
        Process.Start(new ProcessStartInfo(game.LaunchTarget, game.LaunchArgs ?? "")
        {
            UseShellExecute = true,
            WorkingDirectory = File.Exists(game.LaunchTarget) ? Path.GetDirectoryName(game.LaunchTarget)! : "",
        });
        session._launchedAt = DateTime.UtcNow;
        session._timer.Change(PollInterval, PollInterval);
        return session;
    }

    /// <summary>이미 돌고 있는 게임(외부 실행)에 붙는다. 종료 감지만 한다.</summary>
    public static GameSession Attach(GameEntry game)
    {
        var session = new GameSession(game) { LaunchedByUs = false, _seenRunning = true, IsRunning = true };
        session._launchedAt = DateTime.UtcNow;
        session._timer.Change(PollInterval, PollInterval);
        return session;
    }

    /// <summary>
    /// 후보 게임 중 지금 실행 중인 첫 번째를 찾는다. 프로세스 목록은 한 번만 훑는다.
    /// </summary>
    public static GameEntry? FindRunning(IReadOnlyCollection<GameEntry> candidates)
    {
        if (candidates.Count == 0) return null;
        var byName = new Dictionary<string, GameEntry>(StringComparer.OrdinalIgnoreCase);
        foreach (var g in candidates)
            foreach (var n in g.ProcessNames) byName.TryAdd(n, g);
        var dirs = candidates.Where(g => g.InstallDir is { Length: > 0 })
            .Select(g => (Dir: Path.GetFullPath(g.InstallDir!).TrimEnd('\\') + "\\", Game: g)).ToList();

        foreach (var p in Process.GetProcesses())
        {
            if (byName.TryGetValue(p.ProcessName, out var hit)) return hit;
            if (dirs.Count == 0 || GetPath(p) is not { } path) continue;
            foreach (var (dir, game) in dirs)
                if (path.StartsWith(dir, StringComparison.OrdinalIgnoreCase)) return game;
        }
        return null;
    }

    /// <summary>게임 프로세스를 종료한다 (퀵 설정의 "게임 종료").</summary>
    public void Kill()
    {
        foreach (var p in FindProcesses())
        {
            try { p.Kill(entireProcessTree: true); } catch (Exception) { /* 권한 없음/이미 종료 */ }
        }
    }

    private void Poll()
    {
        var running = FindProcesses().Any();
        if (running && !_seenRunning)
        {
            _seenRunning = IsRunning = true;
            Started?.Invoke(this);
        }
        else if (!running && (_seenRunning || DateTime.UtcNow - _launchedAt > StartGrace))
        {
            IsRunning = false;
            _timer.Change(Timeout.Infinite, Timeout.Infinite);
            Ended?.Invoke(this);
        }
    }

    public IEnumerable<Process> FindProcesses()
    {
        var names = new HashSet<string>(Game.ProcessNames, StringComparer.OrdinalIgnoreCase);
        var dir = Game.InstallDir is { Length: > 0 } d ? Path.GetFullPath(d).TrimEnd('\\') + "\\" : null;

        foreach (var p in Process.GetProcesses())
        {
            if (names.Contains(p.ProcessName)) { yield return p; continue; }
            if (dir is not null && GetPath(p) is { } path && path.StartsWith(dir, StringComparison.OrdinalIgnoreCase))
                yield return p;
        }
    }

    public static string? GetPath(Process p)
    {
        var h = OpenProcess(0x1000 /* QUERY_LIMITED_INFORMATION */, false, p.Id);
        if (h == IntPtr.Zero) return null;
        try
        {
            var sb = new StringBuilder(1024);
            var size = sb.Capacity;
            return QueryFullProcessImageName(h, 0, sb, ref size) ? sb.ToString() : null;
        }
        finally { CloseHandle(h); }
    }

    public void Dispose() => _timer.Dispose();

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern IntPtr OpenProcess(uint access, bool inherit, int pid);

    [DllImport("kernel32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
    private static extern bool QueryFullProcessImageName(IntPtr h, int flags, StringBuilder name, ref int size);

    [DllImport("kernel32.dll")]
    private static extern bool CloseHandle(IntPtr h);
}
