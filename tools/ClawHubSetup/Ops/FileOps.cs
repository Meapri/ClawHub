using System.IO;
using System.Security.AccessControl;

namespace ClawHubSetup.Ops;

internal static class FileOps
{
    /// <summary>
    /// service\, app\ 를 새 내용으로 바꾼다. 먼저 같은 볼륨의 임시 폴더에 다 풀어 두고 나서 기존 폴더를 바꿔 끼우므로
    /// 압축 풀기가 실패해도 기존 설치는 그대로 남는다.
    /// </summary>
    public static void InstallFiles(Payload payload, SetupLog log)
    {
        Directory.CreateDirectory(K.InstallDir);
        var staging = Path.Combine(K.InstallDir, $".staging-{Guid.NewGuid():N}");
        try
        {
            var svc = payload.ExtractDir("service/", Path.Combine(staging, "service"));
            var app = payload.ExtractDir("app/", Path.Combine(staging, "app"));
            if (!File.Exists(Path.Combine(staging, "service", "ClawService.exe")) || !File.Exists(Path.Combine(staging, "app", "ClawApp.exe")))
                throw new SetupException("페이로드에 ClawService.exe 또는 ClawApp.exe가 없어요.");
            // 외부 위치(sparse) 신원 패키지가 앱 폴더의 *.sccd와 Public 폴더를 찾는다 (register-home.ps1과 같음)
            Directory.CreateDirectory(Path.Combine(staging, "app", "Public"));
            log.Info($"압축 풀기: 서비스 {svc}개, 앱 {app}개 파일");

            ReplaceDir(Path.Combine(staging, "service"), K.ServiceDir, log);
            ReplaceDir(Path.Combine(staging, "app"), K.AppDir, log);
        }
        finally
        {
            DeleteDir(staging, log, quiet: true);
        }

        if (payload.Has("ClawHub.ico")) payload.ExtractFile("ClawHub.ico", K.InstallDir);

        // 제거·복구용으로 설치 프로그램 자신을 설치 폴더에 둔다 (앱 및 기능의 UninstallString)
        var self = Environment.ProcessPath!;
        if (!string.Equals(Path.GetFullPath(self), Path.GetFullPath(K.SetupExe), StringComparison.OrdinalIgnoreCase))
            File.Copy(self, K.SetupExe, overwrite: true);
        log.Ok($"설치 위치: {K.InstallDir}");
    }

    private static void ReplaceDir(string source, string target, SetupLog log)
    {
        if (Directory.Exists(target) && !TryDelete(target))
        {
            // 잠긴 파일이 남아 있으면 옆으로 치우고 재부팅 때 지운다
            var old = $"{target}.old-{DateTime.Now:yyyyMMddHHmmss}";
            Directory.Move(target, old);
            ScheduleDelete(old);
            log.Warn($"{Path.GetFileName(target)} 폴더 일부가 사용 중이라 {Path.GetFileName(old)}로 옮기고 재부팅 때 지웁니다.");
        }
        Directory.Move(source, target);
    }

    private static bool TryDelete(string dir)
    {
        for (var i = 0; i < 6; i++)
        {
            try
            {
                if (Directory.Exists(dir)) Directory.Delete(dir, recursive: true);
                return true;
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                Thread.Sleep(500);
            }
        }
        return false;
    }

    /// <summary>폴더를 지운다. 사용 중인 파일이 있으면 재부팅 때 지우도록 예약.</summary>
    public static void DeleteDir(string dir, SetupLog log, bool quiet = false)
    {
        if (!Directory.Exists(dir)) return;
        if (TryDelete(dir))
        {
            if (!quiet) log.Ok($"삭제: {dir}");
            return;
        }
        ScheduleDelete(dir);
        log.Warn($"{dir} 일부가 사용 중이라 재부팅 때 지웁니다.");
    }

    private static void ScheduleDelete(string dir)
    {
        if (!Directory.Exists(dir)) return;
        foreach (var f in Directory.EnumerateFiles(dir, "*", SearchOption.AllDirectories)) Native.DeleteAtReboot(f);
        foreach (var d in Directory.EnumerateDirectories(dir, "*", SearchOption.AllDirectories).OrderByDescending(d => d.Length))
            Native.DeleteAtReboot(d);
        Native.DeleteAtReboot(dir);
    }

    /// <summary>C:\ProgramData\ClawHub: 상속 끊기 + SYSTEM/Administrators 모든 권한 + Users 읽기/실행 (install.ps1 icacls와 같음).</summary>
    public static void SecureDataDir(SetupLog log)
    {
        var dir = Directory.CreateDirectory(K.DataDir);
        var security = new DirectorySecurity();
        security.SetSecurityDescriptorSddlForm(K.DataDirSddl, AccessControlSections.Access);
        dir.SetAccessControl(security);
        var applied = dir.GetAccessControl(AccessControlSections.Access).GetSecurityDescriptorSddlForm(AccessControlSections.Access);
        log.Ok($"{K.DataDir} 권한: {applied}");
    }

    public static long DirectorySizeKb(string dir) => !Directory.Exists(dir) ? 0
        : Directory.EnumerateFiles(dir, "*", SearchOption.AllDirectories).Sum(f => new FileInfo(f).Length) / 1024;
}
