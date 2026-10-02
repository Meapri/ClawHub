using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Shapes;
using Path = System.Windows.Shapes.Path;
using ClawApp.Input;
using ClawCore.Mapping;

namespace ClawApp.Views;

/// <summary>
/// 원형 메뉴 화면 (Steam의 Radial Menu). 게임 위에 뜨지만 포커스를 뺏지 않고 클릭도 통과한다 —
/// 게임은 계속 입력을 받고, 고르기는 오른쪽 스틱(엔진이 XInput으로 읽음)으로 한다.
/// 전체 화면 독점 모드 게임에서는 보이지 않을 수 있다 (창 모드·테두리 없는 창은 보인다).
/// </summary>
public sealed class RadialMenuWindow : Window, AdvancedInputEngine.IRadialView
{
    private const double Size = 440, Outer = 200, Inner = 78;
    private readonly Canvas _canvas = new() { Width = Size, Height = Size };
    private readonly TextBlock _center = new()
    {
        FontSize = 20, FontWeight = FontWeights.SemiBold, Foreground = Brushes.White, TextAlignment = TextAlignment.Center,
        TextWrapping = TextWrapping.Wrap, Width = Inner * 2 - 24,
    };
    private readonly List<Path> _wedges = [];
    private readonly List<TextBlock> _labels = [];
    private IReadOnlyList<RadialSlice> _slices = [];
    private int _selected = -1;
    private readonly ScaleTransform _scale = new(1, 1, Size / 2, Size / 2);

    private static readonly Brush Wedge = Frozen(Color.FromArgb(0xD8, 0x1C, 0x1C, 0x1E));
    private static readonly Brush WedgeLine = Frozen(Color.FromArgb(0x40, 0xFF, 0xFF, 0xFF));
    private static readonly Brush Hub = Frozen(Color.FromArgb(0xF0, 0x10, 0x10, 0x12));

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
        Width = Size;
        Height = Size;
        Content = _canvas;
        _canvas.RenderTransform = _scale;
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

    // ── 엔진 쪽 (아무 스레드) ───────────────────────────────

    void AdvancedInputEngine.IRadialView.Show(IReadOnlyList<RadialSlice> slices) => Dispatcher.BeginInvoke(() => Open(slices));
    void AdvancedInputEngine.IRadialView.Select(int index) => Dispatcher.BeginInvoke(() => Highlight(index));
    void AdvancedInputEngine.IRadialView.Hide() => Dispatcher.BeginInvoke(Close2);

    // ── 그리기 (UI 스레드) ─────────────────────────────────

    private void Open(IReadOnlyList<RadialSlice> slices)
    {
        _slices = slices;
        _selected = -1;
        Build();
        // 기본 화면 가운데
        if (!IsVisible) Show();
        // 게임이 있는 모니터 가운데 (물리 픽셀로 맞춘다 — DPI 배율과 상관없이)
        var hwnd = new WindowInteropHelper(this).Handle;
        var mi = new MonitorInfo { Size = Marshal.SizeOf<MonitorInfo>() };
        GetWindowRect(hwnd, out var me);
        if (GetMonitorInfo(MonitorFromWindow(GetForegroundWindow(), 2 /* NEAREST */), ref mi))
        {
            int w = me.Right - me.Left, h = me.Bottom - me.Top;
            var m = mi.Monitor;
            SetWindowPos(hwnd, new IntPtr(-1) /* HWND_TOPMOST */, m.Left + (m.Right - m.Left - w) / 2, m.Top + (m.Bottom - m.Top - h) / 2, 0, 0,
                0x0001 | 0x0010 | 0x0040); // 크기 그대로 · 활성화 없이 · 보이기
        }
        else SetWindowPos(hwnd, new IntPtr(-1), 0, 0, 0, 0, 0x0001 | 0x0002 | 0x0010 | 0x0040);

        var ease = new CubicEase { EasingMode = EasingMode.EaseOut };
        BeginAnimation(OpacityProperty, new DoubleAnimation(1, TimeSpan.FromMilliseconds(110)) { EasingFunction = ease });
        var grow = new DoubleAnimation(0.86, 1, TimeSpan.FromMilliseconds(160)) { EasingFunction = ease };
        _scale.BeginAnimation(ScaleTransform.ScaleXProperty, grow);
        _scale.BeginAnimation(ScaleTransform.ScaleYProperty, grow);
    }

    private void Close2()
    {
        var fade = new DoubleAnimation(0, TimeSpan.FromMilliseconds(120));
        fade.Completed += (_, _) => { if (Opacity == 0) Hide(); };
        BeginAnimation(OpacityProperty, fade);
    }

    private void Build()
    {
        _canvas.Children.Clear();
        _wedges.Clear();
        _labels.Clear();
        var n = Math.Max(1, _slices.Count);
        var sweep = 360.0 / n;
        for (var i = 0; i < n; i++)
        {
            // 0번 칸이 위(12시), 시계 방향
            var mid = i * sweep;
            var wedge = new Path
            {
                Data = WedgeGeometry(mid - sweep / 2 + 1, mid + sweep / 2 - 1),
                Fill = Wedge, Stroke = WedgeLine, StrokeThickness = 1,
            };
            _wedges.Add(wedge);
            _canvas.Children.Add(wedge);

            var label = new TextBlock
            {
                Text = i < _slices.Count ? Display(_slices[i]) : "",
                FontSize = n > 6 ? 15 : 17, Foreground = Brushes.White, TextAlignment = TextAlignment.Center,
                TextWrapping = TextWrapping.Wrap, Width = 104, FontWeight = FontWeights.Medium,
            };
            label.Measure(new Size(104, double.PositiveInfinity));
            var p = Polar(mid, (Outer + Inner) / 2);
            Canvas.SetLeft(label, p.X - 52);
            Canvas.SetTop(label, p.Y - label.DesiredSize.Height / 2);
            _labels.Add(label);
            _canvas.Children.Add(label);
        }
        var hub = new Ellipse { Width = Inner * 2 - 8, Height = Inner * 2 - 8, Fill = Hub, Stroke = WedgeLine, StrokeThickness = 1 };
        Canvas.SetLeft(hub, Size / 2 - Inner + 4);
        Canvas.SetTop(hub, Size / 2 - Inner + 4);
        _canvas.Children.Add(hub);
        _center.Text = "오른쪽 스틱으로 고르기";
        _center.FontSize = 14;
        _center.Opacity = 0.7;
        PlaceCenter();
        _canvas.Children.Add(_center);
    }

    private static string Display(RadialSlice s) => !string.IsNullOrWhiteSpace(s.Label) ? s.Label
        : string.Join(" + ", s.Keys.Select(k => ClawCore.Controller.KeyCodes.Find(k)?.Label ?? k));

    private void PlaceCenter()
    {
        _center.Measure(new Size(_center.Width, double.PositiveInfinity));
        Canvas.SetLeft(_center, Size / 2 - _center.Width / 2);
        Canvas.SetTop(_center, Size / 2 - _center.DesiredSize.Height / 2);
    }

    private void Highlight(int index)
    {
        if (index == _selected || index < 0 || index >= _wedges.Count) return;
        if (_selected >= 0)
        {
            _wedges[_selected].Fill = Wedge;
            _labels[_selected].Foreground = Brushes.White;
        }
        _selected = index;
        _wedges[index].Fill = AccentBrush;
        _labels[index].Foreground = Application.Current.TryFindResource("AccentText") as Brush ?? Brushes.Black;
        _center.Text = index < _slices.Count ? Display(_slices[index]) : "";
        _center.FontSize = 20;
        _center.Opacity = 1;
        PlaceCenter();
    }

    private static Point Polar(double degrees, double r)
    {
        var a = (degrees - 90) * Math.PI / 180;
        return new Point(Size / 2 + r * Math.Cos(a), Size / 2 + r * Math.Sin(a));
    }

    private static Geometry WedgeGeometry(double from, double to)
    {
        var large = to - from > 180;
        var fig = new PathFigure { StartPoint = Polar(from, Inner), IsClosed = true };
        fig.Segments.Add(new LineSegment(Polar(from, Outer), true));
        fig.Segments.Add(new ArcSegment(Polar(to, Outer), new Size(Outer, Outer), 0, large, SweepDirection.Clockwise, true));
        fig.Segments.Add(new LineSegment(Polar(to, Inner), true));
        fig.Segments.Add(new ArcSegment(Polar(from, Inner), new Size(Inner, Inner), 0, large, SweepDirection.Counterclockwise, true));
        var g = new PathGeometry([fig]);
        g.Freeze();
        return g;
    }

    private const int GwlExstyle = -20, WsExNoactivate = 0x08000000, WsExTransparent = 0x20, WsExToolwindow = 0x80, WsExLayered = 0x80000;
    [DllImport("user32.dll")] private static extern int GetWindowLong(IntPtr hwnd, int index);
    [DllImport("user32.dll")] private static extern int SetWindowLong(IntPtr hwnd, int index, int value);
    [StructLayout(LayoutKind.Sequential)] private struct Rect { public int Left, Top, Right, Bottom; }
    [StructLayout(LayoutKind.Sequential)] private struct MonitorInfo { public int Size; public Rect Monitor, Work; public int Flags; }
    [DllImport("user32.dll")] private static extern IntPtr GetForegroundWindow();
    [DllImport("user32.dll")] private static extern IntPtr MonitorFromWindow(IntPtr hwnd, uint flags);
    [DllImport("user32.dll")] private static extern bool GetMonitorInfo(IntPtr monitor, ref MonitorInfo info);
    [DllImport("user32.dll")] private static extern bool GetWindowRect(IntPtr hwnd, out Rect rect);
    [DllImport("user32.dll")] private static extern bool SetWindowPos(IntPtr hwnd, IntPtr after, int x, int y, int cx, int cy, uint flags);
}
