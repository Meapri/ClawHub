using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Text;

namespace ClawCore.Games;

/// <summary>
/// 게임 감지용 프로세스 목록. 1.5~2초마다 불리므로
/// - Process 객체는 이름/ID만 뽑고 바로 Dispose,
/// - 실행 파일 경로(프로세스 핸들 필요)는 pid별로 캐시해 프로세스마다 한 번만 연다.
/// </summary>
public static class ProcessScanner
{
    public readonly record struct Proc(int Id, string Name);

    private static readonly object Lock = new();
    private static readonly Dictionary<int, (string Name, string? Path)> PathCache = [];

    /// <summary>현재 프로세스 목록 (이름은 확장자 제외). 사라진 pid의 경로 캐시도 정리한다.</summary>
    public static List<Proc> List()
    {
        var list = new List<Proc>();
        foreach (var p in Process.GetProcesses())
        {
            using (p)
            {
                try { list.Add(new Proc(p.Id, p.ProcessName)); }
                catch (InvalidOperationException) { /* 이미 종료 */ }
            }
        }

        lock (Lock)
        {
            if (PathCache.Count > 0)
            {
                var alive = list.Select(x => x.Id).ToHashSet();
                foreach (var pid in PathCache.Keys.Where(k => !alive.Contains(k)).ToList()) PathCache.Remove(pid);
            }
        }
        return list;
    }

    /// <summary>
    /// 프로세스 하나만 (없으면 빈 목록). 전체 목록(<see cref="List"/>, 프로세스 수백 개 + 스레드 정보 ≈ 3ms·0.5MB)을 만들지 않는다.
    /// 앞에 떠 있는 창의 프로세스만 볼 때 (2초마다 불림).
    /// </summary>
    public static List<Proc> Single(int pid)
    {
        try
        {
            using var p = Process.GetProcessById(pid);
            var proc = new Proc(p.Id, p.ProcessName);
            lock (Lock)
            {
                if (PathCache.Count > 512) PathCache.Clear(); // List()를 부르지 않는 동안에도 끝없이 늘지 않게
            }
            return [proc];
        }
        catch (Exception ex) when (ex is ArgumentException or InvalidOperationException)
        {
            return []; // 이미 종료
        }
    }

    /// <summary>실행 파일 전체 경로 (권한 없음/종료됨이면 null). pid 재사용은 이름이 바뀌었는지로 구분한다.</summary>
    public static string? PathOf(Proc p)
    {
        lock (Lock)
        {
            if (PathCache.TryGetValue(p.Id, out var c) && string.Equals(c.Name, p.Name, StringComparison.OrdinalIgnoreCase))
                return c.Path;
        }
        var path = QueryPath(p.Id);
        lock (Lock) PathCache[p.Id] = (p.Name, path);
        return path;
    }

    public static string? QueryPath(int pid)
    {
        var h = OpenProcess(0x1000 /* QUERY_LIMITED_INFORMATION */, false, pid);
        if (h == IntPtr.Zero) return null;
        try
        {
            var sb = new StringBuilder(1024);
            var size = sb.Capacity;
            return QueryFullProcessImageName(h, 0, sb, ref size) ? sb.ToString() : null;
        }
        finally { CloseHandle(h); }
    }

    // ── 런처·안티치트·도우미 프로세스 ─────────────────────────

    /// <summary>이름이 정확히 이것이면 게임 본체가 아님 (짧거나 흔한 단어라 접두어로 쓰면 게임까지 걸리는 것들).</summary>
    private static readonly HashSet<string> HelperNames = new(StringComparer.OrdinalIgnoreCase)
    {
        "steam", "steamservice", "steamerrorreporter", "steamerrorreporter64", "gameoverlayui", "gameoverlayui64",
        "vgc", "vgtray", "upc", "Origin", "Update", "Setup", "NGM", "NGM64", "Agent", "Purple", "x3", "BEService", "BEService_x64",
    };

    /// <summary>이름이 이것으로 시작하면 게임 본체가 아님 (설치 폴더 안에 있어도).</summary>
    private static readonly string[] HelperPrefixes =
    [
        "LeagueClient", "RiotClient", "Battle.net", "EpicGamesLauncher", "EpicWebHelper", "EpicOnlineServices",
        "NexonPlay", "BlackCipher", "EasyAntiCheat", "EAAntiCheat", "BattlEye", "xigncode", "GameGuard", "GameMon", "npggsvc",
        "nProtect", "AhnLab", "EADesktop", "EABackgroundService", "UbisoftConnect", "UplayWebCore", "GalaxyClient",
        "RockstarService", "Overwolf", "Discord", "CefSharp", "QtWebEngine", "UnrealCEFSubProcess",
        "dxsetup", "vcredist", "vc_redist", "UE4PrereqSetup", "UEPrereqSetup", "PnkBstr", "NCLauncher", "PlayNC",
    ];

    /// <summary>이름에 이것이 들어 있으면 게임 본체가 아님.</summary>
    private static readonly string[] HelperParts =
    [
        "Updater", "CrashHandler", "CrashReport", "CrashSender", "crashpad", "Launcher", "Installer", "Uninst", "unins",
        "AntiCheat", "BugReport", "ErrorReport", "Helper", "SubProcess", "Redist", "Prereq", "Patcher",
    ];

    /// <summary>
    /// 런처·안티치트·업데이터·크래시 리포터 등 게임과 같은 폴더에 있어도 "게임 실행 중"의 근거가 되면 안 되는 프로세스.
    /// (설치 폴더로 찾을 때만 적용 — 사용자가 이름으로 지정한 프로세스는 그대로 인정)
    /// </summary>
    public static bool IsHelper(string processName) =>
        HelperNames.Contains(processName)
        || HelperPrefixes.Any(p => processName.StartsWith(p, StringComparison.OrdinalIgnoreCase))
        || HelperParts.Any(p => processName.Contains(p, StringComparison.OrdinalIgnoreCase));

    // ── 설치 폴더 검증 ──────────────────────────────────────

    private static readonly Lazy<string> WindowsDir = new(() => Normalize(Environment.GetFolderPath(Environment.SpecialFolder.Windows)));

    private static readonly Lazy<string[]> ForbiddenDirs = new(() =>
    {
        var dirs = new[]
        {
            Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles),
            Environment.GetFolderPath(Environment.SpecialFolder.ProgramFilesX86),
            Environment.GetFolderPath(Environment.SpecialFolder.CommonProgramFiles),
            Environment.GetFolderPath(Environment.SpecialFolder.CommonProgramFilesX86),
            Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData),
            Environment.GetFolderPath(Environment.SpecialFolder.UserProfile),
            Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            Environment.GetFolderPath(Environment.SpecialFolder.Desktop),
            Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments),
            Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Programs"),
            Path.GetDirectoryName(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile)) ?? "", // C:\Users
        };
        return dirs.Where(d => d.Length > 0).Select(Normalize).Distinct(StringComparer.OrdinalIgnoreCase).ToArray();
    });

    private static string Normalize(string dir) => Path.GetFullPath(dir).TrimEnd('\\') + "\\";

    /// <summary>
    /// 게임 감지에 써도 되는 설치 폴더 ("…\" 로 끝나는 전체 경로). 드라이브 루트, Windows 폴더(하위 포함),
    /// Program Files·ProgramData·사용자 폴더 같은 공용 루트, Steam의 steamapps\common 자체는 null —
    /// 그 아래의 아무 프로세스나 "게임 실행 중"이 되는 것을 막는다.
    /// </summary>
    public static string? SafeInstallDir(string? installDir)
    {
        if (string.IsNullOrWhiteSpace(installDir)) return null;
        string dir;
        try { dir = Normalize(installDir); }
        catch (Exception) { return null; } // 잘못된 경로

        if (Path.GetPathRoot(dir) is { } root && string.Equals(Normalize(root), dir, StringComparison.OrdinalIgnoreCase)) return null;
        if (ForbiddenDirs.Value.Any(f => string.Equals(f, dir, StringComparison.OrdinalIgnoreCase))) return null;
        if (dir.StartsWith(WindowsDir.Value, StringComparison.OrdinalIgnoreCase)) return null; // Windows 폴더(하위 포함)
        if (dir.EndsWith(@"\steamapps\common\", StringComparison.OrdinalIgnoreCase)
            || dir.EndsWith(@"\steamapps\", StringComparison.OrdinalIgnoreCase)) return null;
        return dir;
    }

    /// <summary>
    /// 프로세스가 끝났는지 핸들로 지켜본다 (SYNCHRONIZE). 핸들을 들고 있는 동안은 pid가 다른 프로세스에 재사용되지 않으므로
    /// 목록을 다시 훑지 않고도 "그 프로세스가 아직 살아 있는지"를 정확히 알 수 있다.
    /// 열 수 없거나(보호된 프로세스) 이름이 다르면(그새 pid 재사용) null.
    /// </summary>
    public static WaitHandle? Watch(Proc p)
    {
        var h = OpenProcess(0x00100000 /* SYNCHRONIZE */ | 0x1000 /* QUERY_LIMITED_INFORMATION */, false, p.Id);
        if (h == IntPtr.Zero) return null;
        var handle = new ProcessWaitHandle(h);
        var sb = new StringBuilder(1024);
        var size = sb.Capacity;
        var name = QueryFullProcessImageName(h, 0, sb, ref size) ? Path.GetFileName(sb.ToString()) : null;
        if (name is not null && (string.Equals(name, p.Name, StringComparison.OrdinalIgnoreCase)
                                 || string.Equals(Path.GetFileNameWithoutExtension(name), p.Name, StringComparison.OrdinalIgnoreCase)))
            return handle;
        handle.Dispose();
        return null;
    }

    private sealed class ProcessWaitHandle : WaitHandle
    {
        public ProcessWaitHandle(IntPtr handle) => SafeWaitHandle = new Microsoft.Win32.SafeHandles.SafeWaitHandle(handle, ownsHandle: true);
    }

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern IntPtr OpenProcess(uint access, bool inherit, int pid);

    [DllImport("kernel32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
    private static extern bool QueryFullProcessImageName(IntPtr h, int flags, StringBuilder name, ref int size);

    [DllImport("kernel32.dll")]
    private static extern bool CloseHandle(IntPtr h);
}
