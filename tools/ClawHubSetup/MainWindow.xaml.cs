using System.ComponentModel;
using System.Diagnostics;
using System.IO;
using System.Windows;
using ClawHubSetup.Ops;

namespace ClawHubSetup;

public partial class MainWindow : Window
{
    private enum Page { Install, Uninstall, Progress }

    private readonly Payload? _payload;
    private readonly UserContext _user;
    private bool _running;
    private bool _done;
    private bool _installed;

    internal MainWindow(bool uninstall)
    {
        InitializeComponent();
        _payload = Payload.Open();
        _user = UserContext.Detect();
        Closed += (_, _) => _payload?.Dispose();
        Activated += (_, _) => { if (!_running && !_done) RefreshDevMode(); };

        PrepareInstallPage();
        PrepareUninstallPage();
        ShowPage(uninstall ? Page.Uninstall : Page.Install);
    }

    /// <summary>드라이런 UI 확인용 요약.</summary>
    internal string Summary() =>
        $"{HeaderText.Text} / {InstallStatusText.Text} / 위젯 {(WidgetCheck.IsEnabled ? "가능" : "불가")}, 홈 {(DevModeNotice.Visibility == Visibility.Visible ? "개발자 모드 꺼짐" : "가능")}";

    // ── 화면 준비 ──────────────────────────────────────────

    private void PrepareInstallPage()
    {
        var installed = SetupState.InstalledVersion();
        _installed = installed is not null || File.Exists(K.ServiceExe);
        var version = _payload?.Version;

        if (_payload is null)
        {
            InstallStatusText.Text = "이 설치 파일에는 설치할 ClawHub가 들어 있지 않아요.";
            InstallDetailText.Text = "tools\\ClawHubSetup\\build.ps1로 만든 ClawHubSetup.exe를 쓰세요. 제거는 할 수 있어요.";
        }
        else if (!_installed)
        {
            InstallStatusText.Text = $"ClawHub {version}을(를) 새로 설치해요.";
            InstallDetailText.Text = $"설치 위치: {K.InstallDir}";
        }
        else
        {
            InstallStatusText.Text = installed is null
                ? $"스크립트로 설치한 ClawHub를 {version}(으)로 업데이트해요."
                : installed == version ? $"ClawHub {version}을(를) 다시 설치(복구)해요." : $"ClawHub {installed}을(를) {version}(으)로 업데이트해요.";
            InstallDetailText.Text = "서비스를 잠시 멈추고(컨트롤러 매핑은 원래대로) 파일을 바꾼 뒤 다시 시작해요. 사용자 설정은 그대로예요.";
        }

        // 기존 설치에서 자동 실행을 꺼 두었으면 그 선택을 존중
        AutoStartCheck.IsChecked = !_installed || _user.GetRunValue() is not null;

        var widgetAvailable = _payload?.WidgetIdentity is not null && _user.SameUser;
        WidgetCheck.IsEnabled = widgetAvailable;
        WidgetCheck.IsChecked = widgetAvailable;
        if (_payload is not null && _payload.WidgetIdentity is null) WidgetCaption.Text = "이 설치 파일에는 위젯이 들어 있지 않아요.";

        var homeAvailable = _payload?.HomeIdentity is not null && _user.SameUser;
        HomeCheck.IsEnabled = homeAvailable;
        HomeCheck.IsChecked = homeAvailable;
        if (_payload is not null && _payload.HomeIdentity is null) HomeCaption.Text = "이 설치 파일에는 홈 신원 패키지가 들어 있지 않아요.";

        OemWidgetCheck.IsChecked = GamingOps.OemWidget() == K.WidgetFamily;
        OemWidgetCheck.IsEnabled = _user.SameUser && (widgetAvailable || PackageOps.InstalledVersion(K.WidgetName, K.Publisher) is not null);
        WidgetCheck.Checked += (_, _) => OemWidgetCheck.IsEnabled = _user.SameUser;

        var msi = MsiCenterOps.Enabled;
        MsiCenterCheck.IsEnabled = msi == true;
        MsiCenterCheck.IsChecked = false;
        if (msi is null) MsiCenterCaption.Text = "MSI Center M이 설치돼 있지 않아요.";
        else if (msi == false) MsiCenterCaption.Text = "MSI Center M은 이미 꺼져 있어요.";

        if (!_user.SameUser)
        {
            UserNotice.Visibility = Visibility.Visible;
            UserNoticeText.Text = $"관리자 계정({_user.ElevatedName})이 로그인한 사용자({_user.TargetName})와 달라요. " +
                                  $"자동 실행은 {_user.TargetName}에게 설정하지만, 위젯·홈 앱 같은 사용자별 패키지는 다른 사용자에게 설치할 수 없어 건너뛰어요. " +
                                  $"필요하면 {_user.TargetName}이(가) 관리자 계정일 때 직접 실행하세요.";
        }

        GoUninstallButton.Visibility = _installed ? Visibility.Visible : Visibility.Collapsed;
        RefreshDevMode();
    }

    private void RefreshDevMode()
    {
        var off = HomeCheck.IsEnabled && !PackageOps.DeveloperModeOn();
        DevModeNotice.Visibility = off ? Visibility.Visible : Visibility.Collapsed;
    }

    private void PrepareUninstallPage()
    {
        var lines = new List<string> { "• ClawHub 서비스 중지·삭제 (컨트롤러 매핑 원래대로)", "• ClawHub 앱, 설치 폴더, 로그인 시 자동 실행" };
        if (PackageOps.InstalledVersion(K.WidgetName, K.Publisher) is not null) lines.Add("• Game Bar 위젯");
        if (PackageOps.InstalledVersion(K.HomeName, K.Publisher) is not null) lines.Add("• Xbox 모드 홈 앱 등록, Xbox 앱 '내 앱' 항목");
        if (GamingOps.HasBackup) lines.Add("• Game Bar OEM 위젯 → 원래 값으로");
        if (SetupState.GetFlag(SetupState.MsiCenterDisabled)) lines.Add("• MSI Center M 다시 켜기");
        if (SetupState.GetList(SetupState.ImportedCerts).Length > 0) lines.Add("• 설치할 때 추가한 서명 인증서 신뢰");
        UninstallPlanText.Text = string.Join("\n", lines);

        string userData;
        try { userData = _user.UserDataDir; }
        catch (SetupException) { userData = "%LocalAppData%\\ClawHub"; }
        PurgeCaption.Text = $"게임 라이브러리, 매핑 프로필, 설정 ({userData})과 서비스 데이터({K.DataDir})를 지워요. 체크하지 않으면 남겨 두었다가 다시 설치할 때 그대로 써요.";
    }

    private Page _page;

    private void ShowPage(Page page)
    {
        _page = page;
        InstallPage.Visibility = page == Page.Install ? Visibility.Visible : Visibility.Collapsed;
        UninstallPage.Visibility = page == Page.Uninstall ? Visibility.Visible : Visibility.Collapsed;
        ProgressPage.Visibility = page == Page.Progress ? Visibility.Visible : Visibility.Collapsed;

        switch (page)
        {
            case Page.Install:
                Title = HeaderText.Text = "ClawHub 설치";
                SubtitleText.Text = "MSI Claw 게임 런처 · 하드웨어 제어";
                PrimaryButton.Content = _installed ? "업데이트" : "설치";
                PrimaryButton.IsEnabled = _payload is not null;
                SecondaryButton.Content = "취소";
                break;
            case Page.Uninstall:
                Title = HeaderText.Text = "ClawHub 제거";
                SubtitleText.Text = $"설치된 버전: {DryRun.InstalledDescription()}";
                PrimaryButton.Content = "제거";
                PrimaryButton.IsEnabled = true;
                SecondaryButton.Content = "취소";
                break;
        }
    }

    // ── 실행 ─────────────────────────────────────────────

    private async void PrimaryButton_Click(object sender, RoutedEventArgs e)
    {
        if (_done) { Close(); return; }
        if (_page == Page.Install) await RunInstallAsync();
        else if (_page == Page.Uninstall) await RunUninstallAsync();
    }

    private async Task RunInstallAsync()
    {
        var options = new SetupOptions
        {
            AutoStart = AutoStartCheck.IsChecked == true,
            Widget = WidgetCheck.IsEnabled && WidgetCheck.IsChecked == true,
            Home = HomeCheck.IsEnabled && HomeCheck.IsChecked == true,
            OemWidget = OemWidgetCheck.IsEnabled && OemWidgetCheck.IsChecked == true,
            DisableMsiCenter = MsiCenterCheck.IsEnabled && MsiCenterCheck.IsChecked == true,
        };
        using var installer = new Installer(_payload, options, _user);
        var result = await RunStepsAsync("install", installer.BuildSteps(), installer.OnAbort);
        if (result != RunResult.Failed && _user.SameUser && File.Exists(K.AppExe))
            LaunchButton.Visibility = Visibility.Visible;
    }

    private async Task RunUninstallAsync()
    {
        var options = new SetupOptions { PurgeUserData = PurgeCheck.IsChecked == true };
        await RunStepsAsync("uninstall", new Uninstaller(options, _user).BuildSteps(), null);
    }

    private async Task<RunResult> RunStepsAsync(string kind, IReadOnlyList<SetupStep> steps, Action<SetupLog>? onAbort)
    {
        var install = kind == "install";
        _running = true;
        ShowPage(Page.Progress);
        PrimaryButton.IsEnabled = SecondaryButton.IsEnabled = false;
        LogBox.Clear();

        using var log = new SetupLog(SetupLog.NewFilePath(kind));
        LogPathText.Text = log.FilePath is null ? "" : $"로그: {log.FilePath}";
        LogPathText.ToolTip = log.FilePath;
        log.Line += line => Dispatcher.BeginInvoke(() =>
        {
            LogBox.AppendText(line + Environment.NewLine);
            LogBox.ScrollToEnd();
        });
        log.Info($"ClawHubSetup {typeof(MainWindow).Assembly.GetName().Version} · {_user.Describe()}");

        var result = await Task.Run(() => StepRunner.Run(steps, log, (i, n, title) => Dispatcher.BeginInvoke(() =>
        {
            Progress.Value = n == 0 ? 1 : (double)i / n;
            StepText.Text = i < n ? $"{title} ({i + 1}/{n})" : title;
        }), onAbort));

        (HeaderText.Text, StepText.Text) = (install, result) switch
        {
            (true, RunResult.Success) => ("설치 완료", "ClawHub가 설치됐어요."),
            (true, RunResult.Warnings) => ("설치 완료 (경고 있음)", "기본 기능은 설치됐지만 일부 선택 항목은 실패했어요. 로그를 확인하세요."),
            (true, _) => ("설치 실패", "설치를 끝내지 못했어요. 로그를 확인하세요."),
            (false, RunResult.Success) => ("제거 완료", "ClawHub를 제거했어요."),
            (false, RunResult.Warnings) => ("제거 완료 (경고 있음)", "제거했지만 일부 항목은 되돌리지 못했어요. 로그를 확인하세요."),
            _ => ("제거 실패", "제거를 끝내지 못했어요. 로그를 확인하세요."),
        };
        if (result != RunResult.Failed) Progress.Value = 1;
        SubtitleText.Text = log.FilePath ?? "";
        Title = HeaderText.Text;

        _running = false;
        _done = true;
        PrimaryButton.Content = "닫기";
        PrimaryButton.IsEnabled = true;
        SecondaryButton.Visibility = Visibility.Collapsed;
        return result;
    }

    // ── 단추 ─────────────────────────────────────────────

    private void SecondaryButton_Click(object sender, RoutedEventArgs e) => Close();

    private void GoUninstallButton_Click(object sender, RoutedEventArgs e) => ShowPage(Page.Uninstall);

    private void DevModeButton_Click(object sender, RoutedEventArgs e) =>
        Process.Start(new ProcessStartInfo("ms-settings:developers") { UseShellExecute = true })?.Dispose();

    /// <summary>
    /// 관리자 권한을 물려주지 않도록 탐색기(이미 실행 중인 셸, 일반 권한)를 통해 실행한다 —
    /// ClawApp이 관리자로 뜨면 거기서 실행한 게임도 관리자 권한을 상속한다.
    /// </summary>
    private void LaunchButton_Click(object sender, RoutedEventArgs e)
    {
        Process.Start(new ProcessStartInfo("explorer.exe", $"\"{K.AppExe}\"") { UseShellExecute = false })?.Dispose();
        LaunchButton.IsEnabled = false;
    }

    protected override void OnClosing(CancelEventArgs e)
    {
        if (_running) e.Cancel = true; // 진행 중에는 닫지 않는다 (서비스가 멈춘 채 남지 않게)
        base.OnClosing(e);
    }
}
