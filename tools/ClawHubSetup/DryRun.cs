using System.IO;
using System.Text;
using System.Windows;
using ClawHubSetup.Ops;

namespace ClawHubSetup;

/// <summary>
/// /dryrun: 시스템을 바꾸지 않고(읽기만) 설치 또는 제거 단계를 지금 상태 기준으로 출력한다.
/// 출력은 부모 콘솔(또는 리디렉션된 stdout)과 %TEMP%\ClawHubSetup-dryrun.log.
/// 마지막으로 설치 화면을 띄우지 않고 만들어 레이아웃만 계산해 XAML이 깨지지 않았는지 확인한다.
/// </summary>
internal static class DryRun
{
    public static int Run(bool uninstall)
    {
        Encoding.RegisterProvider(CodePagesEncodingProvider.Instance);
        Native.AttachParentConsoleIfNeeded();
        var path = Path.Combine(Path.GetTempPath(), "ClawHubSetup-dryrun.log");
        StreamWriter file;
        try { file = new StreamWriter(new FileStream(path, FileMode.Create, FileAccess.Write, FileShare.ReadWrite), new UTF8Encoding(true)); }
        catch (IOException) // 다른 드라이런이 쓰는 중
        {
            path = Path.Combine(Path.GetTempPath(), $"ClawHubSetup-dryrun-{Environment.ProcessId}.log");
            file = new StreamWriter(path, append: false, new UTF8Encoding(true));
        }
        using var _ = file;
        void W(string line)
        {
            file.WriteLine(line);
            // 콘솔은 OEM 코드 페이지(949)라 —가 ?로 깨진다
            try { Console.WriteLine(line.Replace('—', '-')); } catch (IOException) { /* 콘솔 없음 */ }
        }

        try
        {
            var user = UserContext.Detect();
            using var payload = Payload.Open();
            W($"ClawHub {(uninstall ? "제거" : "설치")} 드라이런 — 아무것도 바꾸지 않습니다");
            W($"설치 프로그램: {Environment.ProcessPath} (v{typeof(DryRun).Assembly.GetName().Version})");
            W($"페이로드: {payload?.Describe() ?? "없음 (dotnet build 결과물 — build.ps1로 만든 exe에만 들어 있음)"}");
            W($"설치된 버전: {InstalledDescription()}");
            W($"서비스: {ServiceOps.Describe(ServiceOps.Status())}, {user.Describe()}, 관리자 권한: {(UserContext.IsAdministrator() ? "예" : "아니요")}");
            W($"개발자 모드: {(PackageOps.DeveloperModeOn() ? "켜짐" : "꺼짐")}, MSI Center M: {MsiCenterOps.Enabled switch { null => "없음", true => "켜짐", false => "꺼짐" }}, OEMGameBarWidget: {GamingOps.OemWidget() ?? "(없음)"}");
            W($"설치 기록 HKLM\\{K.SetupKey}: {RecordDescription()}");
            W("");

            var options = SetupOptions.All();
            IReadOnlyList<SetupStep> steps;
            Installer? installer = null;
            if (uninstall) steps = new Uninstaller(options, user).BuildSteps();
            else steps = (installer = new Installer(payload, options, user)).BuildSteps();
            foreach (var line in StepRunner.Describe(steps)) W(line);
            installer?.Dispose();

            W("");
            W($"UI 확인: {CheckUi(uninstall)}");
            W($"로그: {path}");
            return 0;
        }
        catch (Exception ex)
        {
            W($"드라이런 실패: {ex}");
            return 1;
        }
    }

    internal static string InstalledDescription() => SetupState.InstalledVersion() is { } v
        ? v
        : File.Exists(K.ServiceExe) ? "있음 (스크립트 설치 — 앱 및 기능 항목 없음)" : "없음";

    private static string RecordDescription()
    {
        var parts = new List<string>();
        if (SetupState.GetString(SetupState.Version) is { } v) parts.Add($"버전 {v}");
        if (SetupState.GetString(SetupState.RunKeySid) is { } sid) parts.Add($"Run {sid}");
        foreach (var flag in new[] { SetupState.WidgetInstalled, SetupState.HomeRegistered, SetupState.OemWidgetSet, SetupState.MsiCenterDisabled })
            if (SetupState.GetFlag(flag)) parts.Add(flag);
        if (SetupState.GetString(SetupState.OemAppSlot) is { } slot) parts.Add($"슬롯 {slot}");
        if (SetupState.GetList(SetupState.ImportedCerts) is { Length: > 0 } certs) parts.Add($"인증서 {string.Join(",", certs)}");
        return parts.Count == 0 ? "없음" : string.Join(", ", parts);
    }

    /// <summary>창을 보이지 않은 채 만들고 레이아웃만 계산 (화면에 아무것도 안 뜸).</summary>
    private static string CheckUi(bool uninstall)
    {
        try
        {
            var w = new MainWindow(uninstall);
            var size = new Size(w.Width, w.Height);
            w.Measure(size);
            w.Arrange(new Rect(size));
            w.UpdateLayout();
            // Close()하지 않는다: 첫 창이 MainWindow가 되므로 닫으면 종료 코드가 덮인다. 보인 적 없는 창은 Shutdown 때 정리된다.
            return $"OK ({w.Summary()})";
        }
        catch (Exception ex)
        {
            return $"실패: {ex.GetType().Name}: {ex.Message}";
        }
    }
}
