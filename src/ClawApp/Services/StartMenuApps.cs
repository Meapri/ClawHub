using System.IO;
using System.Runtime.InteropServices;
using System.Runtime.InteropServices.ComTypes;
using System.Text;

namespace ClawApp.Services;

/// <summary>
/// 시작 메뉴에 등록된 프로그램 하나. 바로가기 앱은 LinkPath = 바로가기, TargetPath = 실행 파일.
/// 패키지(MSIX·스토어) 앱은 LinkPath = shell:AppsFolder\앱 ID (실행용), TargetPath = 로고 이미지 (아이콘용).
/// </summary>
public sealed record StartMenuApp(string Name, string LinkPath, string TargetPath, string? Folder);

/// <summary>
/// 시작 메뉴(모든 사용자 + 현재 사용자)의 바로가기와 현재 사용자에게 설치된 패키지 앱(Claude·ChatGPT 같은 MSIX, 스토어 앱)을
/// 훑어 실행 가능한 프로그램 목록을 만든다. 제거 프로그램·도움말·웹 링크·Windows 기본 도구는 뺀다.
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
        foreach (var app in PackagedApps()) byTarget.TryAdd(app.LinkPath, app);
        return [.. byTarget.Values.OrderBy(a => a.Name, StringComparer.CurrentCultureIgnoreCase)];
    }

    /// <summary>ClawHub 자신의 패키지 (Xbox 모드 홈 신원·Game Bar 위젯).</summary>
    private static readonly string[] OwnPackagePrefixes = ["ClawHub_", "ClawHub.Widget_"];

    /// <summary>
    /// 현재 사용자의 패키지 앱 중 시작 메뉴에 보이는 것. Windows 구성 요소(시스템 서명)·프레임워크·리소스 패키지는 뺀다.
    /// 앱 하나에 진입점이 여럿이면 각각 나온다 (시작 메뉴와 같다).
    /// </summary>
    private static IEnumerable<StartMenuApp> PackagedApps()
    {
        var result = new List<StartMenuApp>();
        try
        {
            var manager = new Windows.Management.Deployment.PackageManager();
            foreach (var package in manager.FindPackagesForUser(string.Empty))
            {
                try
                {
                    if (package.IsFramework || package.IsResourcePackage || package.IsBundle) continue;
                    if (package.SignatureKind == Windows.ApplicationModel.PackageSignatureKind.System) continue;
                    var family = package.Id.FamilyName;
                    if (OwnPackagePrefixes.Any(p => family.StartsWith(p, StringComparison.OrdinalIgnoreCase))) continue;
                    var logo = LogoPath(package);
                    foreach (var entry in package.GetAppListEntries())
                    {
                        var name = entry.DisplayInfo.DisplayName;
                        if (string.IsNullOrWhiteSpace(name)) continue;
                        result.Add(new StartMenuApp(name, @"shell:AppsFolder\" + entry.AppUserModelId, logo ?? "", null));
                    }
                }
                catch (Exception) { /* 상태가 깨진 패키지 하나는 건너뛴다 */ }
            }
        }
        catch (Exception) { /* 패키지 API를 못 쓰는 환경 */ }
        return result;
    }

    /// <summary>패키지 로고 파일 (Package.Logo는 배율이 붙은 실제 파일로 풀린 경로). 없으면 null.</summary>
    private static string? LogoPath(Windows.ApplicationModel.Package package)
    {
        try
        {
            var path = package.Logo?.LocalPath;
            return path is not null && File.Exists(path) ? path : null;
        }
        catch (Exception) { return null; }
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
