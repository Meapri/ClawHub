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
/// 단일 전체 화면 창 (ClawHub 런처). 게임 위 오버레이는 띄우지 않는다 — 빠른 설정은 Game Bar 위젯,
/// 홈 버튼은 이 창(또는 Xbox 앱)으로. 입력(패드·키보드)을 공통 내비게이션 액션으로 바꿔 포커스 이동/실행을 처리한다.
/// Menu/F1 = 왼쪽 본체 버튼(홈), View/F2 = 오른쪽 본체 버튼(빠른 설정)과 같은 경로.
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
        PreviewKeyUp += OnKeyUp;
        // 포커스가 어디로 가든(패드 이동·페이지 전환·선택판) 화면 밖이면 스크롤해서 보여 준다
        AddHandler(Keyboard.GotKeyboardFocusEvent, new KeyboardFocusChangedEventHandler((_, _) => RevealFocused()), true);
        PreviewMouseDown += OnMouseButton;
        PreviewMouseUp += OnMouseButton;
        PreviewMouseWheel += OnMouseWheelCapture;
        Activated += (_, _) => _pad.Enabled = true;
        Deactivated += (_, _) => _pad.Enabled = false;

        ApplyWindowVisibility();
    }

    // ── 창 표시 ────────────────────────────────────────────

    private void OnVmChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(ShellViewModel.IsWindowVisible)) ApplyWindowVisibility();
        else if (e.PropertyName == nameof(ShellViewModel.IsShellVisible)) ApplyBackdrop();
        else if (e.PropertyName == nameof(ShellViewModel.CurrentPage)) PlayPageEntrance();
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

    /// <summary>셸 화면 = Mica(바탕화면 색이 은은히 비침). 창이 보이면 항상 셸이므로 Acrylic은 예비용.</summary>
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

    /// <summary>매핑 테스트 중이면 키·마우스 입력을 화면 이동 대신 그 페이지에 넘긴다.</summary>
    private IKeyCapture? Capture => _vm.CurrentPage is IKeyCapture { IsCapturing: true } c && !_vm.IsAnyPanelOpen ? c : null;

    private void OnKeyUp(object sender, KeyEventArgs e)
    {
        if (Capture is not { } cap) return;
        if (Input.KeyIds.From(e) is { } id) cap.OnCapturedKey(id, false);
        e.Handled = true;
    }

    private void OnMouseButton(object sender, MouseButtonEventArgs e)
    {
        if (Capture is not { } cap || Input.KeyIds.From(e.ChangedButton) is not { } id) return;
        // 터치·마우스로 "테스트 끝내기"를 누르는 건 입력 기록이 아니라 클릭으로
        for (var d = e.OriginalSource as DependencyObject; d is not null; d = d is Visual ? VisualTreeHelper.GetParent(d) : LogicalTreeHelper.GetParent(d))
            if (d is UIElement { Uid: "CaptureBypass" }) return;
        cap.OnCapturedKey(id, e.ButtonState == MouseButtonState.Pressed);
        e.Handled = true;
    }

    private void OnMouseWheelCapture(object sender, MouseWheelEventArgs e)
    {
        if (Capture is not { } cap) return;
        var id = e.Delta > 0 ? "MS_WHEELUP" : "MS_WHEELDOWN";
        cap.OnCapturedKey(id, true);
        // 휠은 떼는 신호가 없다 — 잠깐 밝혔다가 끈다
        var off = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(250) };
        off.Tick += (_, _) => { off.Stop(); cap.OnCapturedKey(id, false); };
        off.Start();
        e.Handled = true;
    }

    private void OnKey(object sender, KeyEventArgs e)
    {
        if (Capture is { } cap)
        {
            if (Input.KeyIds.From(e) is { } id) cap.OnCapturedKey(id, true);
            e.Handled = true;
            return;
        }

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
        if (Capture is not null)
        {
            _vm.CurrentPage?.Handle(a); // 테스트 중: B만 의미 있음 (끝내기)
            return;
        }

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
                    _vm.ReturnToBackground(); // 게임 중 첫 화면에서 B → 게임으로 (홈은 HomeViewModel.Handle이 처리)
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

        // 아래(위)에 더 고를 것이 없으면 읽기 전용 내용(배터리 수명 등)이 보이게 그냥 스크롤한다
        if (Keyboard.FocusedElement == focused && a is NavAction.Up or NavAction.Down && FindScroller(focused) is { } sv)
            sv.ScrollToVerticalOffset(sv.VerticalOffset + (a == NavAction.Down ? 160 : -160));
    }

    private static ScrollViewer? FindScroller(DependencyObject from)
    {
        for (var d = VisualTreeHelper.GetParent(from); d is not null; d = VisualTreeHelper.GetParent(d))
            if (d is ScrollViewer { ScrollableHeight: > 0 } sv) return sv;
        return null;
    }

    /// <summary>
    /// 포커스가 옮겨 간 요소를 스크롤해서 보여 준다 (위아래 여유 포함).
    /// WPF는 "마지막 입력이 키보드"일 때만 스스로 스크롤하므로 패드 입력에서는 직접 해 줘야 한다.
    /// </summary>
    private void RevealFocused() => Dispatcher.BeginInvoke(() =>
    {
        if (Keyboard.FocusedElement is FrameworkElement { IsVisible: true } fe)
            fe.BringIntoView(new Rect(-16, -48, fe.ActualWidth + 32, fe.ActualHeight + 96));
    }, DispatcherPriority.Loaded);

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

    /// <summary>페이지별 마지막 포커스 위치(포커스 가능한 요소 중 몇 번째인지). 뒤로 돌아오면 그 자리로.</summary>
    private readonly Dictionary<INavPage, int> _focusMemory = [];

    private void FocusCurrent()
    {
        // 앱 안 빠른 설정 패널(위젯이 없을 때의 대체 경로)
        DependencyObject root = _vm.IsQuickAccessOpen ? QuickPanel : PageHost;

        if (root == PageHost && _vm.CurrentPage is { } page && _focusMemory.TryGetValue(page, out var index))
        {
            var all = new List<UIElement>();
            CollectFocusable(PageHost, all);
            if (index < all.Count && Keyboard.Focus(all[index]) == all[index]) return;
        }

        var target = FindFocusable(root, initialOnly: true) ?? FindFocusable(root, initialOnly: false);
        if (target is not null) Keyboard.Focus(target);
    }

    private void PageHost_GotKeyboardFocus(object sender, KeyboardFocusChangedEventArgs e)
    {
        if (_vm.CurrentPage is not { } page || e.NewFocus is not UIElement el) return;
        var all = new List<UIElement>();
        CollectFocusable(PageHost, all);
        var i = all.IndexOf(el);
        if (i >= 0) _focusMemory[page] = i;
    }

    private static void CollectFocusable(DependencyObject node, List<UIElement> into)
    {
        if (node is UIElement { IsVisible: false }) return;
        if (node is ButtonBase or Slider or TextBox && node is UIElement { Focusable: true, IsEnabled: true } el) into.Add(el);
        for (var i = 0; i < VisualTreeHelper.GetChildrenCount(node); i++) CollectFocusable(VisualTreeHelper.GetChild(node, i), into);
    }

    /// <summary>메트로 페이지 전환: 앞으로 가면 오른쪽에서, 뒤로 오면 왼쪽에서 (피벗 제목이 한 박자 늦게 따라온다).</summary>
    private void PlayPageEntrance() => Motion.PageTransition.Play(PageHost, PivotHeader, _vm.CurrentPage);

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
