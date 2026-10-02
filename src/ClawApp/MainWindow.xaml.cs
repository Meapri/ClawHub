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
        // 창이 활성일 때만 폴링한다 (Activated에서 켜짐). 숨은 채로 시작하는 Xbox 모드에서도 타이머가 돌지 않게
        _pad.Enabled = false;
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
        else if (e.PropertyName == nameof(ShellViewModel.IsConfirmOpen)) OnConfirmChanged();
        else if (e.PropertyName == nameof(ShellViewModel.Toast) && _vm.Toast is not null && Motion.MotionCore.Enabled)
            Motion.MotionCore.SlideIn(ToastCard, 0, 20, TimeSpan.Zero, Motion.MotionCore.Ms(360), fadeRatio: 0.5); // 아래에서 살짝 떠오른다
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
        FitToScreen();
        // 해상도·배율이 실행 중에 바뀌어도 화면 전체를 덮게 (배율을 바꾸면 창이 예전 논리 크기로 남아 구석에 작게 붙는다)
        Microsoft.Win32.SystemEvents.DisplaySettingsChanged += (_, _) => Dispatcher.BeginInvoke(FitToScreen);
#if DEBUG
        // 테스트: CLAWHUB_TESTSCALE=1.0 등 → 다른 배율의 논리 화면 크기를 흉내 낸다 (윈도우 설정은 건드리지 않음)
        if (double.TryParse(Environment.GetEnvironmentVariable("CLAWHUB_TESTSCALE"), System.Globalization.NumberStyles.Float,
                System.Globalization.CultureInfo.InvariantCulture, out var testScale) && testScale > 0
            && Content is FrameworkElement root && PresentationSource.FromVisual(this)?.CompositionTarget is { } ct)
        {
            var k = testScale / ct.TransformToDevice.M11;
            root.LayoutTransform = new ScaleTransform(k, k);
        }
        // 테스트: CLAWHUB_TESTDPICHANGE=96 → 8초 뒤 실행 중 배율 변경(WM_DPICHANGED)을 흉내 낸다
        if (int.TryParse(Environment.GetEnvironmentVariable("CLAWHUB_TESTDPICHANGE"), out var testDpi) && testDpi > 0)
        {
            var t = new DispatcherTimer { Interval = TimeSpan.FromSeconds(8) };
            t.Tick += (_, _) =>
            {
                t.Stop();
                var scale = testDpi / 96.0;
                var rect = new int[] { 0, 0, (int)(ActualWidth * scale), (int)(ActualHeight * scale) };
                var mem = Marshal.AllocHGlobal(16);
                Marshal.Copy(rect, 0, mem, 4);
                SendMessage(hwnd, 0x02E0, (IntPtr)((testDpi << 16) | testDpi), mem);
                Marshal.FreeHGlobal(mem);
            };
            t.Start();
        }
#endif
    }

    protected override void OnDpiChanged(DpiScale oldDpi, DpiScale newDpi)
    {
        base.OnDpiChanged(oldDpi, newDpi);
        Dispatcher.BeginInvoke(FitToScreen, DispatcherPriority.Loaded); // 시스템이 제안한 크기로 줄어든 뒤에 다시 맞춘다
    }

    /// <summary>창이 있는 모니터 전체(실제 픽셀)를 지금 배율의 논리 크기로 덮는다.</summary>
    private void FitToScreen()
    {
        var hwnd = new WindowInteropHelper(this).Handle;
        if (hwnd == IntPtr.Zero || PresentationSource.FromVisual(this)?.CompositionTarget is not { } ct) return;
        var info = new MonitorInfo { Size = Marshal.SizeOf<MonitorInfo>() };
        if (!GetMonitorInfo(MonitorFromWindow(hwnd, 2 /* NEAREST */), ref info)) return;
        var m = ct.TransformFromDevice;
        var topLeft = m.Transform(new Point(info.Left, info.Top));
        var bottomRight = m.Transform(new Point(info.Right, info.Bottom));
        Left = topLeft.X;
        Top = topLeft.Y;
        Width = bottomRight.X - topLeft.X;
        Height = bottomRight.Y - topLeft.Y;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct MonitorInfo
    {
        public int Size;
        public int Left, Top, Right, Bottom;
        public int WorkLeft, WorkTop, WorkRight, WorkBottom;
        public int Flags;
    }

    [DllImport("user32.dll")] private static extern IntPtr MonitorFromWindow(IntPtr hwnd, int flags);
    [DllImport("user32.dll")] private static extern bool GetMonitorInfo(IntPtr monitor, ref MonitorInfo info);

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
    private IKeyCapture? Capture => _vm.CurrentPage is IKeyCapture { IsCapturing: true } c && !_vm.IsAnyPanelOpen && !_vm.IsConfirmOpen ? c : null;

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
            Key.F3 => NavAction.Secondary, // 패드 X
            Key.F4 => NavAction.Options,   // 패드 Y
            _ => null,
        };
        if (a is null) return;
        e.Handled = true;
        Route(a.Value);
    }

    private void Route(NavAction a)
    {
        if (_vm.BusyText is not null) return;
        // 확인 대화 상자가 떠 있으면 그것만 입력을 받는다 (본체 버튼 Menu/View는 대화 상자를 취소하고 원래대로)
        if (_vm.IsConfirmOpen && a is not (NavAction.MainMenu or NavAction.QuickAccess))
        {
            RouteConfirm(a);
            return;
        }
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
            // 페이지가 먼저 받을 기회 (시작 화면 편집: 잡은 타일 옮기기·A로 잡기/놓기)
            case NavAction.Up or NavAction.Down or NavAction.Left or NavAction.Right:
                if (!_vm.IsAnyPanelOpen && _vm.CurrentPage?.Handle(a) == true) return;
                MoveFocus(a);
                return;
            case NavAction.Accept:
                if (Keyboard.FocusedElement is not TextBox && !_vm.IsAnyPanelOpen && _vm.CurrentPage?.Handle(a) == true) return;
                Invoke(Keyboard.FocusedElement as UIElement);
                return;
            default:
                if (!_vm.IsAnyPanelOpen) _vm.CurrentPage?.Handle(a);
                return;
        }
    }

    /// <summary>확인 대화 상자 입력: A = 포커스된 버튼, B = 취소, 방향 = 버튼 사이 이동. 아래 페이지는 아무것도 받지 않는다.</summary>
    private void RouteConfirm(NavAction a)
    {
        var inside = Keyboard.FocusedElement is Visual f && ConfirmLayer.IsAncestorOf(f);
        switch (a)
        {
            case NavAction.Back:
                _vm.CancelConfirm();
                return;
            case NavAction.Accept when inside:
                Invoke(Keyboard.FocusedElement as UIElement);
                return;
            case NavAction.Up or NavAction.Down or NavAction.Left or NavAction.Right when inside:
                MoveFocus(a);
                return;
            default:
                if (!inside) FocusCurrent(); // 포커스가 바깥에 있으면 대화 상자로 데려온다
                return;
        }
    }

    // ── 확인 대화 상자 ─────────────────────────────────────

    /// <summary>대화 상자를 열기 전 포커스 (닫히면 그 자리로 돌아간다).</summary>
    private IInputElement? _focusBeforeConfirm;

    private void OnConfirmChanged()
    {
        if (_vm.IsConfirmOpen)
        {
            if (Keyboard.FocusedElement is not Visual f || !ConfirmLayer.IsAncestorOf(f)) _focusBeforeConfirm = Keyboard.FocusedElement;
            Dispatcher.BeginInvoke(FocusCurrent, DispatcherPriority.ContextIdle); // 레이아웃 뒤 기본 버튼으로
            return;
        }
        var back = _focusBeforeConfirm;
        _focusBeforeConfirm = null;
        if (back is UIElement { IsVisible: true, IsEnabled: true, Focusable: true } el && Keyboard.Focus(el) == el) return;
        Dispatcher.BeginInvoke(FocusCurrent, DispatcherPriority.ContextIdle);
    }

    /// <summary>대화 상자 바깥을 누르면 취소. 그 아래 힌트(A 선택·B 취소)를 누른 거면 그 동작으로.</summary>
    private void ConfirmScrim_MouseDown(object sender, MouseButtonEventArgs e)
    {
        e.Handled = true;
        if (HintActionAt(e) is { } hint) Route(hint);
        else _vm.CancelConfirm();
    }

    // ── 터치: 뒤로 버튼·힌트 바 ─────────────────────────────

    /// <summary>제목 옆 뒤로 버튼 = 패드 B (페이지가 먼저 받고, 처리하지 않으면 이전 페이지로).</summary>
    private void BackButton_Click(object sender, RoutedEventArgs e) => Route(NavAction.Back);

    /// <summary>힌트를 누르면 그 패드 버튼을 누른 것과 같다 (A는 지금 포커스된 요소를 실행).</summary>
    private void HintButton_Click(object sender, RoutedEventArgs e)
    {
        if (sender is FrameworkElement { DataContext: Hint h } && ActionFor(h.Glyph) is { } a) Route(a);
    }

    private static NavAction? ActionFor(string glyph) => glyph switch
    {
        "A" => NavAction.Accept,
        "B" => NavAction.Back,
        "X" => NavAction.Secondary,
        "Y" => NavAction.Options,
        "LB" => NavAction.TabPrev,
        "RB" => NavAction.TabNext,
        "☰" or "Menu" => NavAction.MainMenu,
        "…" or "View" => NavAction.QuickAccess,
        _ => null,
    };

    /// <summary>덮개(스크림) 아래 힌트 바의 항목을 눌렀으면 그 동작.</summary>
    private NavAction? HintActionAt(MouseEventArgs e)
    {
        for (var d = Shell.InputHitTest(e.GetPosition(Shell)) as DependencyObject; d is not null; d = VisualTreeHelper.GetParent(d))
            if (d is Button { DataContext: Hint h } b && b.Style == FindResource("HintButton")) return ActionFor(h.Glyph);
        return null;
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
        DependencyObject root = _vm.IsConfirmOpen ? ConfirmLayer
            : _vm.IsQuickAccessOpen ? QuickPanel : _vm.StatusPanel is not null ? StatusFlyout : PageHost;

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
        // 포커스가 움직일 때마다 불리므로 페이지 전체를 모으지 않고 그 요소를 찾을 때까지만 센다 (같은 순서·같은 번호)
        var count = 0;
        if (CountFocusableUntil(PageHost, el, ref count)) _focusMemory[page] = count;
    }

    /// <summary><see cref="CollectFocusable"/>와 같은 순서로 세다가 <paramref name="target"/>을 만나면 true (count = 그 번호).</summary>
    private static bool CountFocusableUntil(DependencyObject node, UIElement target, ref int count)
    {
        if (node is UIElement { IsVisible: false }) return false;
        if (node is ButtonBase or Slider or TextBox && node is UIElement { Focusable: true, IsEnabled: true } el)
        {
            if (ReferenceEquals(el, target)) return true;
            count++;
        }
        for (var i = 0; i < VisualTreeHelper.GetChildrenCount(node); i++)
            if (CountFocusableUntil(VisualTreeHelper.GetChild(node, i), target, ref count)) return true;
        return false;
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

    /// <summary>
    /// 패널 바깥을 누르면 닫힌다. 상단 표시줄의 다른 항목을 누른 거면 그 패널로 바로 바꾸고,
    /// 지금 열린 패널의 항목(같은 아이콘)을 다시 누른 거면 닫기만 한다. 힌트 바(A 선택·B 닫기)는 그 동작으로.
    /// </summary>
    private void Scrim_MouseDown(object sender, MouseButtonEventArgs e)
    {
        e.Handled = true;
        if (HintActionAt(e) is { } hint)
        {
            Route(hint);
            return;
        }
        var target = Shell.InputHitTest(e.GetPosition(Shell)) as DependencyObject;
        while (target is not null and not ButtonBase) target = VisualTreeHelper.GetParent(target);
        var openStatus = _vm.StatusPanel;
        var quickOpen = _vm.IsQuickAccessOpen;
        _vm.ClosePanels();
        if (target is not Button { Command: { } cmd } b || b.Style != FindResource("StatusButton")) return;
        var same = (openStatus is not null && Equals(b.CommandParameter, openStatus)) || (quickOpen && cmd == _vm.OpenQuickSettingsCommand);
        if (!same && cmd.CanExecute(b.CommandParameter)) cmd.Execute(b.CommandParameter);
    }

    [DllImport("user32.dll")] private static extern IntPtr GetForegroundWindow();
    [DllImport("user32.dll")] private static extern uint GetWindowThreadProcessId(IntPtr hwnd, IntPtr pid);
    [DllImport("kernel32.dll")] private static extern uint GetCurrentThreadId();
    [DllImport("user32.dll")] private static extern IntPtr SendMessage(IntPtr hwnd, int msg, IntPtr wParam, IntPtr lParam);
    [DllImport("user32.dll")] private static extern bool AttachThreadInput(uint attach, uint attachTo, bool on);
    [DllImport("user32.dll")] private static extern bool SetForegroundWindow(IntPtr hwnd);
    [DllImport("user32.dll")] private static extern bool BringWindowToTop(IntPtr hwnd);
}
