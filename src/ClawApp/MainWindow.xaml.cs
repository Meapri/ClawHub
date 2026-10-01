using System.ComponentModel;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Automation.Peers;
using System.Windows.Automation.Provider;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Threading;
using ClawApp.Input;
using ClawApp.ViewModels;
using ClawCore.Ec;

namespace ClawApp;

/// <summary>
/// 단일 전체 화면 창. 셸 화면/게임 위 오버레이를 모두 이 창이 담당한다.
/// 입력(패드·키보드)을 공통 내비게이션 액션으로 바꿔 포커스 이동/실행을 처리한다.
/// </summary>
public partial class MainWindow : Window
{
    private readonly ShellViewModel _vm;
    private readonly GamepadInput _pad = new();

    public MainWindow()
    {
        InitializeComponent();
        _vm = new ShellViewModel();
        DataContext = _vm;

        Left = 0;
        Top = 0;
        Width = SystemParameters.PrimaryScreenWidth;
        Height = SystemParameters.PrimaryScreenHeight;

        _vm.FocusRequested += () => Dispatcher.BeginInvoke(FocusCurrent, DispatcherPriority.ContextIdle);
        _vm.PropertyChanged += OnVmChanged;

        _pad.Action += a => { if (IsActive) Route(a); };
        _pad.Start();

        PreviewKeyDown += OnKey;
        Activated += (_, _) => _pad.Enabled = true;
        Deactivated += (_, _) => _pad.Enabled = false;

        ApplyWindowVisibility();
    }

    // ── 창 표시 ────────────────────────────────────────────

    private void OnVmChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(ShellViewModel.IsWindowVisible)) ApplyWindowVisibility();
        else if (e.PropertyName == nameof(ShellViewModel.IsShellVisible)) ApplyBackdrop();
    }

    // ── Windows 11 백드롭 ─────────────────────────────────

    private const int DwmwaUseImmersiveDarkMode = 20;
    private const int DwmwaSystemBackdropType = 38;
    private const int BackdropMica = 2, BackdropAcrylic = 3;

    protected override void OnSourceInitialized(EventArgs e)
    {
        base.OnSourceInitialized(e);
        var hwnd = new WindowInteropHelper(this).Handle;
        // WPF가 칠하는 배경을 투명하게 → DWM 백드롭이 보이게
        if (HwndSource.FromHwnd(hwnd)?.CompositionTarget is { } target) target.BackgroundColor = Colors.Transparent;
        var margins = new Margins { Left = -1, Right = -1, Top = -1, Bottom = -1 };
        DwmExtendFrameIntoClientArea(hwnd, ref margins);
        var dark = Services.SystemTheme.IsLight ? 0 : 1;
        DwmSetWindowAttribute(hwnd, DwmwaUseImmersiveDarkMode, ref dark, sizeof(int));
        ApplyBackdrop();
    }

    /// <summary>셸 화면 = Mica(바탕화면 색이 은은히 비침), 게임 위 오버레이 = Acrylic(뒤 화면을 흐림).</summary>
    private void ApplyBackdrop()
    {
        var hwnd = new WindowInteropHelper(this).Handle;
        if (hwnd == IntPtr.Zero) return;
        var type = _vm.IsShellVisible ? BackdropMica : BackdropAcrylic;
        var ok = DwmSetWindowAttribute(hwnd, DwmwaSystemBackdropType, ref type, sizeof(int)) == 0;
        // 백드롭을 못 쓰는 환경(원격 데스크톱 등)이면 단색 배경으로
        Background = ok ? Brushes.Transparent : (Brush)FindResource("Bg0");
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct Margins { public int Left, Right, Top, Bottom; }

    [DllImport("dwmapi.dll")] private static extern int DwmExtendFrameIntoClientArea(IntPtr hwnd, ref Margins margins);
    [DllImport("dwmapi.dll")] private static extern int DwmSetWindowAttribute(IntPtr hwnd, int attr, ref int value, int size);

    private void ApplyWindowVisibility()
    {
        if (_vm.IsWindowVisible)
        {
            Show();
            Topmost = true;
            ForceForeground();
            Dispatcher.BeginInvoke(FocusCurrent, DispatcherPriority.ContextIdle);
        }
        else
        {
            Hide();
        }
    }

    /// <summary>
    /// 서비스 이벤트(본체 버튼)로 창을 띄울 땐 이 프로세스에 입력 권한이 없어 SetForegroundWindow가 거부될 수 있다.
    /// 현재 포그라운드 스레드에 입력 큐를 잠깐 붙여서 넘겨받는다. (ALT 키 주입 방식은 게임에 키가 들어가므로 쓰지 않음)
    /// </summary>
    private void ForceForeground()
    {
        var hwnd = new WindowInteropHelper(this).EnsureHandle();
        var fg = GetForegroundWindow();
        var fgThread = GetWindowThreadProcessId(fg, IntPtr.Zero);
        var me = GetCurrentThreadId();
        var attached = fgThread != me && AttachThreadInput(me, fgThread, true);
        try
        {
            SetForegroundWindow(hwnd);
            BringWindowToTop(hwnd);
            Activate();
        }
        finally
        {
            if (attached) AttachThreadInput(me, fgThread, false);
        }
    }

    // ── 입력 ───────────────────────────────────────────────

    private void OnKey(object sender, KeyEventArgs e)
    {
        // 글자 입력 중에는 Enter/Esc(=입력 끝, 저장)와 위/아래 이동만 가로채고 나머지는 TextBox로
        if (Keyboard.FocusedElement is TextBox box)
        {
            if (e.Key is Key.Enter or Key.Escape or Key.Down or Key.Up)
            {
                e.Handled = true;
                box.MoveFocus(new TraversalRequest(e.Key == Key.Up ? FocusNavigationDirection.Up : FocusNavigationDirection.Down));
            }
            return;
        }

        NavAction? a = e.Key switch
        {
            Key.Up => NavAction.Up,
            Key.Down => NavAction.Down,
            Key.Left => NavAction.Left,
            Key.Right => NavAction.Right,
            Key.Enter or Key.Space => NavAction.Accept,
            Key.Escape or Key.Back or Key.BrowserBack => NavAction.Back,
            Key.PageUp => NavAction.TabPrev,
            Key.PageDown => NavAction.TabNext,
            Key.F1 => NavAction.MainMenu,
            Key.F2 => NavAction.QuickAccess,
            _ => null,
        };
        if (a is null) return;
        e.Handled = true;
        Route(a.Value);
    }

    private void Route(NavAction a)
    {
        if (_vm.BusyText is not null) return;

        // 글자 입력칸: A = 터치 키보드, B = 입력 끝(저장)
        if (Keyboard.FocusedElement is TextBox box)
        {
            if (a == NavAction.Accept)
            {
                box.SelectAll();
                Services.SystemControls.ShowTouchKeyboard();
                return;
            }
            if (a == NavAction.Back)
            {
                box.MoveFocus(new TraversalRequest(FocusNavigationDirection.Down));
                return;
            }
        }

        switch (a)
        {
            case NavAction.MainMenu:
                _vm.OnMsiButton(MsiButton.MainMenu);
                return;
            case NavAction.QuickAccess:
                _vm.OnMsiButton(MsiButton.QuickSettings);
                return;
            case NavAction.Back:
                if (_vm.IsAnyPanelOpen) _vm.ClosePanels();
                else if (!(_vm.CurrentPage?.Handle(a) ?? false) && !_vm.GoBack() && _vm.RunningGame is not null)
                    _vm.ReturnToBackground(); // 게임 중 홈에서 B → 게임으로
                return;
            case NavAction.Up or NavAction.Down or NavAction.Left or NavAction.Right:
                MoveFocus(a);
                return;
            case NavAction.Accept:
                Invoke(Keyboard.FocusedElement as UIElement);
                return;
            default:
                if (!_vm.IsAnyPanelOpen) _vm.CurrentPage?.Handle(a);
                return;
        }
    }

    private void MoveFocus(NavAction a)
    {
        if (Keyboard.FocusedElement is not UIElement focused || !focused.IsVisible)
        {
            FocusCurrent();
            return;
        }

        if (focused is Slider s && a is NavAction.Left or NavAction.Right)
        {
            s.Value = Math.Clamp(s.Value + (a == NavAction.Right ? s.SmallChange : -s.SmallChange), s.Minimum, s.Maximum);
            return;
        }

        var dir = a switch
        {
            NavAction.Up => FocusNavigationDirection.Up,
            NavAction.Down => FocusNavigationDirection.Down,
            NavAction.Left => FocusNavigationDirection.Left,
            _ => FocusNavigationDirection.Right,
        };
        focused.MoveFocus(new TraversalRequest(dir));
    }

    /// <summary>포커스된 요소를 "누른다". 버튼/토글/라디오를 자동화 패턴으로 실행한다.</summary>
    private static void Invoke(UIElement? el)
    {
        if (el is null) return;
        var peer = UIElementAutomationPeer.CreatePeerForElement(el);
        switch (el)
        {
            case RadioButton when peer?.GetPattern(PatternInterface.SelectionItem) is ISelectionItemProvider sel:
                sel.Select();
                break;
            case ToggleButton when peer?.GetPattern(PatternInterface.Toggle) is IToggleProvider toggle:
                toggle.Toggle();
                break;
            case ButtonBase when peer?.GetPattern(PatternInterface.Invoke) is IInvokeProvider invoke:
                invoke.Invoke();
                break;
        }
    }

    // ── 포커스 ─────────────────────────────────────────────

    private void FocusCurrent()
    {
        DependencyObject root = _vm.IsQuickAccessOpen ? QuickPanel : _vm.IsMainMenuOpen ? MainMenu : PageHost;
        var target = FindFocusable(root, initialOnly: true) ?? FindFocusable(root, initialOnly: false);
        if (target is not null) Keyboard.Focus(target);
    }

    private static IInputElement? FindFocusable(DependencyObject node, bool initialOnly)
    {
        if (node is UIElement { IsVisible: false }) return null;
        if (node is ButtonBase or Slider && node is UIElement { Focusable: true, IsEnabled: true } candidate &&
            (!initialOnly || Nav.GetInitialFocus(node)))
            return candidate;
        for (var i = 0; i < VisualTreeHelper.GetChildrenCount(node); i++)
            if (FindFocusable(VisualTreeHelper.GetChild(node, i), initialOnly) is { } hit) return hit;
        return null;
    }

    private void Scrim_MouseDown(object sender, MouseButtonEventArgs e) => _vm.ClosePanels();

    [DllImport("user32.dll")] private static extern IntPtr GetForegroundWindow();
    [DllImport("user32.dll")] private static extern uint GetWindowThreadProcessId(IntPtr hwnd, IntPtr pid);
    [DllImport("kernel32.dll")] private static extern uint GetCurrentThreadId();
    [DllImport("user32.dll")] private static extern bool AttachThreadInput(uint attach, uint attachTo, bool on);
    [DllImport("user32.dll")] private static extern bool SetForegroundWindow(IntPtr hwnd);
    [DllImport("user32.dll")] private static extern bool BringWindowToTop(IntPtr hwnd);
}
