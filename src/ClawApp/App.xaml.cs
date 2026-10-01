using System.Windows;
using System.Windows.Input;

namespace ClawApp;

public partial class App : Application
{
    private Mutex? _single;

    protected override void OnStartup(StartupEventArgs e)
    {
        _single = new Mutex(true, @"Local\ClawHub.App", out var first);
        if (!first)
        {
            Shutdown();
            return;
        }

        Services.SystemTheme.Apply(this);

        // UI 이벤트 처리 중 예외로 앱이 죽지 않게 (죽으면 게임 매핑 정리가 늦어진다)
        DispatcherUnhandledException += (_, ev) =>
        {
            ev.Handled = true;
            ViewModels.ShellViewModel.Instance?.ShowToast($"오류: {ev.Exception.Message}");
        };
        TaskScheduler.UnobservedTaskException += (_, ev) => ev.SetObserved();

        // 패드로 포커스가 이동하면 스크롤 영역 안에서 자동으로 보이게
        EventManager.RegisterClassHandler(typeof(FrameworkElement), UIElement.GotKeyboardFocusEvent,
            new KeyboardFocusChangedEventHandler((s, _) => ((FrameworkElement)s).BringIntoView()));

        // 실행 옵션으로 시작 방식 강제: --shell / --companion (바로가기·테스트용)
        if (e.Args.Contains("--shell")) StartupOverride = Services.StartupMode.Shell;
        else if (e.Args.Contains("--companion")) StartupOverride = Services.StartupMode.Companion;

        base.OnStartup(e);
        _window = new MainWindow(); // 표시 여부는 창이 직접 결정 (Xbox 모드면 숨은 채로 시작)
    }

    private MainWindow? _window;

    public static Services.StartupMode? StartupOverride { get; private set; }

    protected override void OnExit(ExitEventArgs e)
    {
        _single?.Dispose();
        base.OnExit(e);
    }
}
