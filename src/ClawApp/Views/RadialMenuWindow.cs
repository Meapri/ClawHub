using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Media.Effects;
using System.Windows.Shapes;
using ClawApp.Input;
using ClawCore.Mapping;
using Path = System.Windows.Shapes.Path;

namespace ClawApp.Views;

/// <summary>
/// 원형 메뉴 화면 (SteamOS의 Radial Menu). 게임 위에 뜨지만 포커스를 뺏지 않고 클릭도 통과한다 —
/// 게임은 계속 입력을 받고, 고르기는 스틱(엔진이 XInput으로 읽음)으로 한다.
/// <para>
/// 모션 (짧게 — 게임을 가리는 시간이 길지 않게):
/// · 열림: 전체가 살짝 커지며 나타나고 칸이 시계 방향으로 차례로 펼쳐진다 (≈180ms)
/// · 고름: 강조 칸이 가장 가까운 방향으로 미끄러져 돌아가고, 고른 칸은 바깥으로 조금 나오며, 가운데 이름이 바뀐다
/// · 닫힘: 실행이면 고른 칸이 한 번 반짝이고 사라지고, 취소면 바로 작아지며 사라진다
/// </para>
/// 화면 전체를 어둡게 하지 않고, 반투명한 칸만 그린다. 전체 화면 독점 모드 게임에서는 보이지 않을 수 있다.
/// </summary>
public sealed class RadialMenuWindow : Window, AdvancedInputEngine.IRadialView
{
    // 크기 단계 (작게·보통·크게): 바깥 반지름 / 안쪽 반지름
    private static readonly (double Outer, double Inner)[] Sizes = [(132, 50), (168, 62), (204, 74)];
    private const double Pad = 24; // 고른 칸이 나오는 여유 + 그림자

    private readonly Canvas _canvas = new();
    private readonly ScaleTransform _scale = new(1, 1);
    private readonly TextBlock _center = new()
    {
        FontWeight = FontWeights.SemiBold, Foreground = Brushes.White, TextAlignment = TextAlignment.Center,
        TextWrapping = TextWrapping.Wrap, TextTrimming = TextTrimming.CharacterEllipsis,
    };
    private readonly List<Path> _wedges = [];
    private readonly List<TextBlock> _labels = [];
    private readonly List<TranslateTransform> _lifts = [];
    private readonly Path _highlight = new() { IsHitTestVisible = false };
    private readonly RotateTransform _highlightTurn = new();
    private IReadOnlyList<RadialSlice> _slices = [];
    private int _selected = -1;
    private double _outer, _inner, _size, _sweep, _turn;

    private static readonly Brush Wedge = Frozen(Color.FromArgb(0xB8, 0x18, 0x18, 0x1C));
    private static readonly Brush WedgeEmpty = Frozen(Color.FromArgb(0x70, 0x18, 0x18, 0x1C));
    private static readonly Brush WedgeLine = Frozen(Color.FromArgb(0x38, 0xFF, 0xFF, 0xFF));
    private static readonly Brush Hub = Frozen(Color.FromArgb(0xD8, 0x10, 0x10, 0x14));

    public RadialMenuWindow()
    {
        WindowStyle = WindowStyle.None;
        AllowsTransparency = true;
        Background = Brushes.Transparent;
        ResizeMode = ResizeMode.NoResize;
        Topmost = true;
        ShowActivated = false;
        ShowInTaskbar = false;
        Focusable = false;
        Content = _canvas;
        _canvas.RenderTransform = _scale;
        // 글자가 밝은 게임 화면 위에서도 읽히게 옅은 그림자 하나 (칸 전체에 — 글자마다 따로 두지 않는다)
        _canvas.Effect = new DropShadowEffect { BlurRadius = 18, ShadowDepth = 0, Opacity = 0.45, Color = Colors.Black, RenderingBias = RenderingBias.Performance };
        _highlight.RenderTransform = _highlightTurn;
        Opacity = 0;
        SourceInitialized += (_, _) =>
        {
            var hwnd = new WindowInteropHelper(this).Handle;
            SetWindowLong(hwnd, GwlExstyle, GetWindowLong(hwnd, GwlExstyle) | WsExNoactivate | WsExTransparent | WsExToolwindow | WsExLayered);
        };
    }

    private static Brush Frozen(Color c)
    {
        var b = new SolidColorBrush(c);
        b.Freeze();
        return b;
    }

    private Brush AccentBrush => Application.Current.TryFindResource("Accent") as Brush ?? Brushes.DeepSkyBlue;
    private Brush AccentText => Application.Current.TryFindResource("AccentText") as Brush ?? Brushes.Black;

    // ── 엔진 쪽 (아무 스레드) ───────────────────────────────

    void AdvancedInputEngine.IRadialView.Show(IReadOnlyList<RadialSlice> slices, int size) => Dispatcher.BeginInvoke(() => Open(slices, size));
    void AdvancedInputEngine.IRadialView.Select(int index) => Dispatcher.BeginInvoke(() => Highlight(index));
    void AdvancedInputEngine.IRadialView.Hide(bool activated) => Dispatcher.BeginInvoke(() => Close2(activated));

    // ── 그리기 (UI 스레드) ─────────────────────────────────

    private static readonly IEasingFunction Out = new CubicEase { EasingMode = EasingMode.EaseOut };
    private static readonly IEasingFunction Pop = new BackEase { EasingMode = EasingMode.EaseOut, Amplitude = 0.35 };

    private static DoubleAnimation Anim(double? from, double to, int ms, IEasingFunction ease, int delayMs = 0) =>
        new(to, TimeSpan.FromMilliseconds(ms)) { From = from, EasingFunction = ease, BeginTime = TimeSpan.FromMilliseconds(delayMs) };

    private void Open(IReadOnlyList<RadialSlice> slices, int size)
    {
        (_outer, _inner) = Sizes[Math.Clamp(size, 0, Sizes.Length - 1)];
        _size = 2 * (_outer + Pad);
        _slices = slices;
        _selected = -1;
        Width = Height = _size;
        _canvas.Width = _canvas.Height = _size;
        _scale.CenterX = _scale.CenterY = _size / 2;
        Build();
        if (!IsVisible) Show();
        CenterOnGame();

        // 열림: 0.86 → 1 (살짝 튐) + 칸이 시계 방향으로 차례로
        BeginAnimation(OpacityProperty, Anim(null, 1, 110, Out));
        var grow = Anim(0.86, 1, 200, Pop);
        _scale.BeginAnimation(ScaleTransform.ScaleXProperty, grow);
        _scale.BeginAnimation(ScaleTransform.ScaleYProperty, grow);
        var step = Math.Max(4, 60 / Math.Max(1, _wedges.Count));
        for (var i = 0; i < _wedges.Count; i++)
        {
            _wedges[i].BeginAnimation(OpacityProperty, Anim(0, 1, 120, Out, i * step));
            _labels[i].BeginAnimation(OpacityProperty, Anim(0, 1, 140, Out, 30 + i * step));
        }
    }

    /// <summary>게임이 있는 모니터 가운데 (물리 픽셀로 맞춘다 — DPI 배율과 상관없이).</summary>
    private void CenterOnGame()
    {
        var hwnd = new WindowInteropHelper(this).Handle;
        var mi = new MonitorInfo { Size = Marshal.SizeOf<MonitorInfo>() };
        UpdateLayout();
        GetWindowRect(hwnd, out var me);
        if (GetMonitorInfo(MonitorFromWindow(GetForegroundWindow(), 2 /* NEAREST */), ref mi))
        {
            int w = me.Right - me.Left, h = me.Bottom - me.Top;
            var m = mi.Monitor;
            SetWindowPos(hwnd, new IntPtr(-1) /* HWND_TOPMOST */, m.Left + (m.Right - m.Left - w) / 2, m.Top + (m.Bottom - m.Top - h) / 2, 0, 0,
                0x0001 | 0x0010 | 0x0040); // 크기 그대로 · 활성화 없이 · 보이기
        }
        else SetWindowPos(hwnd, new IntPtr(-1), 0, 0, 0, 0, 0x0001 | 0x0002 | 0x0010 | 0x0040);
        if (Input.EngineLog.On)
        {
            GetWindowBand(hwnd, out var band);
            GetWindowBand(GetForegroundWindow(), out var fgBand);
            GetWindowRect(hwnd, out var at);
            Input.EngineLog.Write($"radial window {hwnd:X} band={band} visible={IsVisible} opacity={Opacity:0.00} at {at.Left},{at.Top} · foreground {GetForegroundWindow():X} band={fgBand}");
        }
    }

    [DllImport("user32.dll")] private static extern bool GetWindowBand(IntPtr hwnd, out uint band);

    private void Close2(bool activated)
    {
        if (!IsVisible) return;
        if (activated && _selected >= 0 && _selected < _wedges.Count)
        {
            // 실행: 고른 칸이 한 번 밝아졌다가 전체가 살짝 커지며 사라진다
            _highlight.BeginAnimation(OpacityProperty, Anim(1, 0.55, 70, Out));
            var outScale = Anim(null, 1.05, 160, Out, 60);
            _scale.BeginAnimation(ScaleTransform.ScaleXProperty, outScale);
            _scale.BeginAnimation(ScaleTransform.ScaleYProperty, outScale);
            Fade(Anim(null, 0, 140, Out, 60));
        }
        else
        {
            // 취소: 작아지며 바로 사라진다
            var shrink = Anim(null, 0.92, 110, Out);
            _scale.BeginAnimation(ScaleTransform.ScaleXProperty, shrink);
            _scale.BeginAnimation(ScaleTransform.ScaleYProperty, shrink);
            Fade(Anim(null, 0, 110, Out));
        }
    }

    private void Fade(DoubleAnimation fade)
    {
        fade.Completed += (_, _) => { if (Opacity < 0.01) Hide(); };
        BeginAnimation(OpacityProperty, fade);
    }

    private Point Polar(double degrees, double r)
    {
        var a = (degrees - 90) * Math.PI / 180;
        return new Point(_size / 2 + r * Math.Cos(a), _size / 2 + r * Math.Sin(a));
    }

    private Geometry WedgeGeometry(double from, double to, double inner, double outer)
    {
        var large = to - from > 180;
        var fig = new PathFigure { StartPoint = Polar(from, inner), IsClosed = true };
        fig.Segments.Add(new LineSegment(Polar(from, outer), true));
        fig.Segments.Add(new ArcSegment(Polar(to, outer), new Size(outer, outer), 0, large, SweepDirection.Clockwise, true));
        fig.Segments.Add(new LineSegment(Polar(to, inner), true));
        fig.Segments.Add(new ArcSegment(Polar(from, inner), new Size(inner, inner), 0, large, SweepDirection.Counterclockwise, true));
        var g = new PathGeometry([fig]);
        g.Freeze();
        return g;
    }

    private void Build()
    {
        _canvas.Children.Clear();
        _wedges.Clear();
        _labels.Clear();
        _lifts.Clear();
        var n = Math.Max(1, _slices.Count);
        _sweep = 360.0 / n;
        var gap = n > 8 ? 0.8 : 1.2;
        var font = n > 8 ? 13 : _outer < 150 ? 14 : 16;
        var labelWidth = Math.Min(110, (_outer - _inner) * 1.25);

        for (var i = 0; i < n; i++)
        {
            // 0번 칸이 위(12시), 시계 방향
            var mid = i * _sweep;
            var empty = i >= _slices.Count || _slices[i].Keys.Count == 0;
            var lift = new TranslateTransform();
            var wedge = new Path
            {
                Data = WedgeGeometry(mid - _sweep / 2 + gap, mid + _sweep / 2 - gap, _inner, _outer),
                Fill = empty ? WedgeEmpty : Wedge, Stroke = WedgeLine, StrokeThickness = 1, RenderTransform = lift,
            };
            var label = new TextBlock
            {
                Text = i < _slices.Count ? Display(_slices[i]) : "",
                FontSize = font, Foreground = Brushes.White, TextAlignment = TextAlignment.Center,
                TextWrapping = TextWrapping.Wrap, Width = labelWidth, FontWeight = FontWeights.Medium,
                Opacity = 0, RenderTransform = lift,
            };
            if (empty) label.Foreground = Frozen(Color.FromArgb(0x80, 0xFF, 0xFF, 0xFF));
            label.Measure(new Size(labelWidth, double.PositiveInfinity));
            var p = Polar(mid, (_outer + _inner) / 2);
            Canvas.SetLeft(label, p.X - labelWidth / 2);
            Canvas.SetTop(label, p.Y - label.DesiredSize.Height / 2);
            _wedges.Add(wedge);
            _labels.Add(label);
            _lifts.Add(lift);
            _canvas.Children.Add(wedge);
        }

        // 강조 칸 (한 개를 돌려서 움직인다 — 칸마다 색을 바꾸면 툭툭 끊겨 보인다)
        _highlight.Data = WedgeGeometry(-_sweep / 2 + gap, _sweep / 2 - gap, _inner, _outer + 6);
        _highlight.Fill = AccentBrush;
        // 지난번 모션(닫힐 때의 반짝임·회전)이 값을 붙잡고 있으면 새로 열 때 예전 칸이 강조된 채 보인다 → 모두 풀고 시작
        _highlight.BeginAnimation(OpacityProperty, null);
        _highlightTurn.BeginAnimation(RotateTransform.AngleProperty, null);
        _center.BeginAnimation(OpacityProperty, null);
        _highlight.Opacity = 0;
        _highlightTurn.CenterX = _highlightTurn.CenterY = _size / 2;
        _canvas.Children.Add(_highlight);
        foreach (var l in _labels) _canvas.Children.Add(l); // 글자는 강조 칸 위에

        var hub = new Ellipse { Width = _inner * 2 - 10, Height = _inner * 2 - 10, Fill = Hub, Stroke = WedgeLine, StrokeThickness = 1 };
        Canvas.SetLeft(hub, _size / 2 - _inner + 5);
        Canvas.SetTop(hub, _size / 2 - _inner + 5);
        _canvas.Children.Add(hub);
        _center.Width = _inner * 2 - 22;
        _center.FontSize = _inner < 60 ? 13 : 15;
        _center.Text = "스틱으로 고르기";
        _center.Opacity = 0.65;
        PlaceCenter();
        _canvas.Children.Add(_center);
    }

    private static string Display(RadialSlice s) => !string.IsNullOrWhiteSpace(s.Label) ? s.Label
        : s.Keys.Count == 0 ? "" : string.Join(" + ", s.Keys.Select(k => ClawCore.Controller.KeyCodes.Find(k)?.Label ?? k));

    private void PlaceCenter()
    {
        _center.Measure(new Size(_center.Width, double.PositiveInfinity));
        Canvas.SetLeft(_center, _size / 2 - _center.Width / 2);
        Canvas.SetTop(_center, _size / 2 - _center.DesiredSize.Height / 2);
    }

    private void Highlight(int index)
    {
        if (index == _selected || index < 0 || index >= _wedges.Count) return;
        var previous = _selected;
        _selected = index;

        // 강조 칸: 가장 가까운 방향으로 돌아간다 (11시 → 1시는 12시를 지나서)
        var target = index * _sweep;
        if (previous < 0)
        {
            _turn = target;
            _highlightTurn.BeginAnimation(RotateTransform.AngleProperty, null);
            _highlightTurn.Angle = target;
            _highlight.BeginAnimation(OpacityProperty, Anim(0, 1, 90, Out));
        }
        else
        {
            var delta = ((target - _turn) % 360 + 540) % 360 - 180;
            var from = _turn;
            _turn += delta;
            _highlightTurn.BeginAnimation(RotateTransform.AngleProperty, Anim(from, _turn, 110, Out));
        }

        // 고른 칸은 바깥으로 조금 나오고 글자 색이 바뀐다
        if (previous >= 0)
        {
            _lifts[previous].BeginAnimation(TranslateTransform.XProperty, Anim(null, 0, 120, Out));
            _lifts[previous].BeginAnimation(TranslateTransform.YProperty, Anim(null, 0, 120, Out));
            _labels[previous].Foreground = Brushes.White;
        }
        var a = (target - 90) * Math.PI / 180;
        _lifts[index].BeginAnimation(TranslateTransform.XProperty, Anim(null, 5 * Math.Cos(a), 140, Pop));
        _lifts[index].BeginAnimation(TranslateTransform.YProperty, Anim(null, 5 * Math.Sin(a), 140, Pop));
        _labels[index].Foreground = AccentText;

        // 가운데 이름 바꾸기 (짧게 사라졌다가 나타남)
        _center.Text = index < _slices.Count ? Display(_slices[index]) : "";
        _center.FontSize = _inner < 60 ? 15 : 18;
        PlaceCenter();
        _center.BeginAnimation(OpacityProperty, Anim(0.2, 1, 120, Out));
    }

    private const int GwlExstyle = -20, WsExNoactivate = 0x08000000, WsExTransparent = 0x20, WsExToolwindow = 0x80, WsExLayered = 0x80000;
    [StructLayout(LayoutKind.Sequential)] private struct Rect { public int Left, Top, Right, Bottom; }
    [StructLayout(LayoutKind.Sequential)] private struct MonitorInfo { public int Size; public Rect Monitor, Work; public int Flags; }
    [DllImport("user32.dll")] private static extern int GetWindowLong(IntPtr hwnd, int index);
    [DllImport("user32.dll")] private static extern int SetWindowLong(IntPtr hwnd, int index, int value);
    [DllImport("user32.dll")] private static extern IntPtr GetForegroundWindow();
    [DllImport("user32.dll")] private static extern IntPtr MonitorFromWindow(IntPtr hwnd, uint flags);
    [DllImport("user32.dll")] private static extern bool GetMonitorInfo(IntPtr monitor, ref MonitorInfo info);
    [DllImport("user32.dll")] private static extern bool GetWindowRect(IntPtr hwnd, out Rect rect);
    [DllImport("user32.dll")] private static extern bool SetWindowPos(IntPtr hwnd, IntPtr after, int x, int y, int cx, int cy, uint flags);
}
