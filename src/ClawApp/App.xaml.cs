using System.Windows;
using System.Windows.Input;

namespace ClawApp;

public partial class App : Application
{
    private Mutex? _single;

    protected override void OnStartup(StartupEventArgs e)
    {
        // 오버레이 프로세스 (ClawOverlay.exe --overlay-host 파이프): 창만 그린다 — 한 번만 실행 검사·본 창 없음
        if (e.Args.Length >= 2 && e.Args[0] == Services.OverlayHost.HostArg)
        {
            Services.SystemTheme.Apply(this);
            Services.OverlayHost.RunHost(this, e.Args[1]);
            return;
        }

        _single = new Mutex(true, @"Local\ClawHub.App", out var first);
        if (!first)
        {
            // 이미 실행 중: 기존 창을 앞으로 (Xbox 모드 홈 버튼, clawhub: 링크, 바로가기 재실행)
            if (EventWaitHandle.TryOpenExisting(ShowEventName, out var show)) show.Set();
            Shutdown();
            return;
        }

        // 첫 창을 만드는 동안 라이브러리 검색·배경 그림 디코딩을 뒤에서 미리 (시작 시간 단축)
        Services.StartupPrewarm.Begin();

        // WPF 애니메이션 기본값은 60fps — 화면 주사율(Claw 8: 120Hz)에 맞춰 부드럽게
        var hz = Services.DisplayControls.Current()?.Hz is > 0 and var h ? h : 60;
        System.Windows.Media.Animation.Timeline.DesiredFrameRateProperty.OverrideMetadata(
            typeof(System.Windows.Media.Animation.Timeline), new FrameworkPropertyMetadata { DefaultValue = Math.Clamp(hz, 60, 240) });

        _show = new EventWaitHandle(false, EventResetMode.AutoReset, ShowEventName);
        new Thread(() =>
        {
            while (_show.WaitOne())
                Dispatcher.BeginInvoke(() => ViewModels.ShellViewModel.Instance?.ShowFromOutside());
        }) { IsBackground = true, Name = "ClawHub.Show" }.Start();

        Services.SystemTheme.Apply(this);
        Services.FrameMeter.StartIfRequested();

        // UI 이벤트 처리 중 예외로 앱이 죽지 않게 (죽으면 게임 매핑 정리가 늦어진다)
        DispatcherUnhandledException += (_, ev) =>
        {
            ev.Handled = true;
            LogError(ev.Exception);
            ViewModels.ShellViewModel.Instance?.ShowToast($"오류: {ev.Exception.Message}");
        };
        TaskScheduler.UnobservedTaskException += (_, ev) => ev.SetObserved();

        // 패드로 포커스가 이동하면 스크롤 영역 안에서 자동으로 보이게. GotKeyboardFocus는 위로 올라가는(bubbling) 이벤트라
        // 포커스를 받은 요소에서만 한다 — 예전에는 상위 요소마다 자기 전체를 보이게 하려 해서, 홈 편집 중(새 구역 칸까지 넓어진 판)
        // 포커스가 옮겨질 때마다 판 전체가 오른쪽 끝으로 24px씩 밀렸다.
        EventManager.RegisterClassHandler(typeof(FrameworkElement), UIElement.GotKeyboardFocusEvent,
            new KeyboardFocusChangedEventHandler((s, e) => { if (ReferenceEquals(s, e.NewFocus)) ((FrameworkElement)s).BringIntoView(); }));

        // 실행 옵션으로 시작 방식 강제: --shell / --companion (바로가기·테스트용)
        if (e.Args.Contains("--shell")) StartupOverride = Services.StartupMode.Shell;
        else if (e.Args.Contains("--companion")) StartupOverride = Services.StartupMode.Companion;

        base.OnStartup(e);
        _window = new MainWindow(); // 표시 여부는 창이 직접 결정 (Xbox 모드면 숨은 채로 시작)
        Input.AdvancedInputEngine.Instance.RadialView = new Services.RadialViewRouter(new Views.RadialMenuWindow());
        // 전체 화면 게임 위에 그리는 오버레이 프로세스 (설치본에만 있다) — 첫 화면을 띄운 뒤에
        Dispatcher.BeginInvoke(Services.OverlayHost.Start, System.Windows.Threading.DispatcherPriority.ApplicationIdle);
    }

    /// <summary>처리 못 한 UI 예외 기록 (%LocalAppData%\ClawHub\app-errors.log, 1MB 넘으면 새로).</summary>
    private static void LogError(Exception ex)
    {
        try
        {
            var path = Path.Combine(ClawCore.JsonStore.UserDir, "app-errors.log");
            if (File.Exists(path) && new FileInfo(path).Length > 1_000_000) File.Delete(path);
            File.AppendAllText(path, $"[{DateTime.Now:yyyy-MM-dd HH:mm:ss}] {ex}{Environment.NewLine}{Environment.NewLine}");
        }
        catch (Exception) { /* 기록 실패는 무시 */ }
    }

    private MainWindow? _window;
    private EventWaitHandle? _show;
    private const string ShowEventName = @"Local\ClawHub.Show";

    public static Services.StartupMode? StartupOverride { get; private set; }

    protected override void OnExit(ExitEventArgs e)
    {
        if (Services.OverlayHost.IsHost)
        {
            base.OnExit(e);
            return;
        }
        _single?.Dispose();
        Input.AdvancedInputEngine.Instance.Dispose(); // 누르고 있던 키를 떼고 훅을 푼다
        Input.VirtualPad.Stop(); // 실제 패드 숨김은 서비스가 앱 연결이 끊기면 되돌린다
        ViewModels.ShellViewModel.Instance?.Night?.Shutdown(); // 야간 모드 색을 원래대로
        base.OnExit(e);
    }
}
