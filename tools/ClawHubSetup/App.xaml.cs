using System.Diagnostics;
using System.IO;
using System.Windows;

namespace ClawHubSetup;

/// <summary>
/// 명령줄:
///   (없음)              설치/업데이트 화면
///   /uninstall          제거 화면 (앱 및 기능에서 호출. 설치 폴더에서 실행되면 %TEMP%로 복사해 다시 실행)
///   /dryrun             바꾸는 것 없이 지금 상태에서 할 일을 출력 (/dryrun /uninstall = 제거 계획). 관리자 아니어도 됨
/// </summary>
public partial class App : Application
{
    internal sealed record CommandLine(bool Uninstall, bool DryRun, bool FromTemp)
    {
        public static CommandLine Parse(IEnumerable<string> args)
        {
            var set = args.Select(a => a.TrimStart('/', '-').ToLowerInvariant()).ToHashSet();
            return new CommandLine(set.Contains("uninstall"), set.Contains("dryrun"), set.Contains("fromtemp"));
        }
    }

    internal static CommandLine Args { get; private set; } = new(false, false, false);

    protected override void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);
        Args = CommandLine.Parse(e.Args);

        if (Args.DryRun)
        {
            Shutdown(DryRun.Run(Args.Uninstall));
            return;
        }

        if (!UserContext.IsAdministrator())
        {
            MessageBox.Show("ClawHub 설치에는 관리자 권한이 필요해요.\nClawHubSetup.exe를 마우스 오른쪽 단추로 눌러 '관리자 권한으로 실행'하세요.",
                "ClawHub 설치", MessageBoxButton.OK, MessageBoxImage.Warning);
            Shutdown(1);
            return;
        }

        if (Args.Uninstall && !Args.FromTemp && RunningFromInstallDir())
        {
            // 설치 폴더 안의 exe는 자기 자신을 지울 수 없으므로 임시 폴더 복사본으로 제거한다
            var copy = Path.Combine(Path.GetTempPath(), $"ClawHubSetup-uninstall-{Guid.NewGuid():N}.exe");
            File.Copy(Environment.ProcessPath!, copy);
            Process.Start(new ProcessStartInfo(copy, "/uninstall /fromtemp") { UseShellExecute = false })?.Dispose();
            Shutdown(0);
            return;
        }
        if (Args.FromTemp) Native.DeleteAtReboot(Environment.ProcessPath!);

        MainWindow = new MainWindow(Args.Uninstall);
        MainWindow.Show();
    }

    private static bool RunningFromInstallDir() =>
        Path.GetFullPath(Environment.ProcessPath!).StartsWith(K.InstallDir + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase);
}
