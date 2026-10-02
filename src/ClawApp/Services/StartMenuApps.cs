using System.IO;
using System.Runtime.InteropServices;
using System.Runtime.InteropServices.ComTypes;
using System.Text;

namespace ClawApp.Services;

/// <summary>시작 메뉴에 등록된 프로그램 하나 (바로가기 + 실제 실행 파일).</summary>
public sealed record StartMenuApp(string Name, string LinkPath, string TargetPath, string? Folder);

/// <summary>
/// 시작 메뉴(모든 사용자 + 현재 사용자)의 바로가기를 훑어 실행 가능한 프로그램 목록을 만든다.
/// 제거 프로그램·도움말·웹 링크·Windows 기본 도구는 뺀다. 스토어(UWP) 앱은 바로가기가 없어 나오지 않는다.
/// 느리므로(바로가기마다 COM 해석) UI 스레드에서 부르지 말 것.
/// </summary>
public static class StartMenuApps
{
    private static readonly string[] SkipNameWords =
    [
        "uninstall", "제거", "언인스톨", "삭제", "readme", "read me", "도움말", "help", "manual", "설명서", "license", "라이선스",
        "release notes", "website", "홈페이지", "web site", "documentation", "문서", "support", "고객 지원", "repair", "복구",
    ];

    private static readonly string[] SkipExeNames = ["unins000", "uninstall", "uninst", "setup", "msiexec", "updater", "update", "crashreporter"];

    public static List<StartMenuApp> Scan()
    {
        var roots = new[]
        {
            Environment.GetFolderPath(Environment.SpecialFolder.CommonPrograms),
            Environment.GetFolderPath(Environment.SpecialFolder.Programs),
        };
        var windows = Environment.GetFolderPath(Environment.SpecialFolder.Windows);
        var self = Environment.ProcessPath;
        var byTarget = new Dictionary<string, StartMenuApp>(StringComparer.OrdinalIgnoreCase);

        foreach (var root in roots.Where(Directory.Exists))
        {
            IEnumerable<string> links;
            try { links = Directory.EnumerateFiles(root, "*.lnk", SearchOption.AllDirectories).ToList(); }
            catch (Exception) { continue; } // 권한 없는 하위 폴더
            foreach (var link in links)
            {
                var name = Path.GetFileNameWithoutExtension(link);
                if (SkipNameWords.Any(w => name.Contains(w, StringComparison.OrdinalIgnoreCase))) continue;
                if (Resolve(link) is not { } target) continue;
                if (!target.EndsWith(".exe", StringComparison.OrdinalIgnoreCase) || !File.Exists(target)) continue;
                if (target.StartsWith(windows, StringComparison.OrdinalIgnoreCase)) continue; // 메모장·명령 프롬프트 등
                if (string.Equals(target, self, StringComparison.OrdinalIgnoreCase)) continue;
                var exe = Path.GetFileNameWithoutExtension(target);
                if (SkipExeNames.Any(s => exe.Equals(s, StringComparison.OrdinalIgnoreCase))) continue;

                var folder = Path.GetDirectoryName(Path.GetRelativePath(root, link));
                var app = new StartMenuApp(name, link, target, string.IsNullOrEmpty(folder) ? null : folder);
                // 같은 프로그램의 바로가기가 여러 개면 이름이 짧은 쪽 ("게임" vs "게임 (64비트)")
                if (!byTarget.TryGetValue(target, out var prev) || name.Length < prev.Name.Length) byTarget[target] = app;
            }
        }
        return [.. byTarget.Values.OrderBy(a => a.Name, StringComparer.CurrentCultureIgnoreCase)];
    }

    /// <summary>바로가기의 대상 경로. 해석 못 하면 null (MSI 광고 바로가기 등).</summary>
    public static string? Resolve(string linkPath)
    {
        object? obj = null;
        try
        {
            obj = new ShellLink();
            ((IPersistFile)obj).Load(linkPath, 0);
            var sb = new StringBuilder(1024);
            ((IShellLinkW)obj).GetPath(sb, sb.Capacity, IntPtr.Zero, 0);
            var path = Environment.ExpandEnvironmentVariables(sb.ToString());
            return path.Length > 0 ? path : null;
        }
        catch (Exception) { return null; }
        finally
        {
            if (obj is not null) Marshal.FinalReleaseComObject(obj);
        }
    }

    [ComImport, Guid("00021401-0000-0000-C000-000000000046")]
    private class ShellLink;

    [ComImport, InterfaceType(ComInterfaceType.InterfaceIsIUnknown), Guid("000214F9-0000-0000-C000-000000000046")]
    private interface IShellLinkW
    {
        void GetPath([Out, MarshalAs(UnmanagedType.LPWStr)] StringBuilder pszFile, int cch, IntPtr pfd, uint fFlags);
    }
}
