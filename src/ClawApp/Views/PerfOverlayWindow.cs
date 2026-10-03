using System.Globalization;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Interop;
using System.Windows.Media;

namespace ClawApp.Views;

/// <summary>성능 오버레이에 보여 줄 값 한 번 (없는 값은 null → "–").</summary>
public sealed record PerfOverlayData(
    double? Fps, double? FpsLow1, double? FrameTimeMs, IReadOnlyList<float>? FrameTimes,
    double? CpuPercent, int? CpuMhz, int? CpuTemp, double? GpuPercent,
    int? CpuWatts, double? BatteryWatts, int? BatteryPercent, bool Charging, TimeSpan? Remaining,
    double? RamUsedGb, double? RamTotalGb, int? FpsLimit);

/// <summary>
/// SteamOS 성능 오버레이처럼 화면 왼쪽 위에 작게 뜨는 측정값. 단계: 1 FPS만 · 2 한 줄 · 3 자세히(프레임 시간 그래프 포함).
/// 포커스를 뺏지 않고 클릭이 통과한다. 게임 화면이 있는 모니터를 따라간다.
/// </summary>
public sealed class PerfOverlayWindow : Window
{
    private static readonly FontFamily Mono = new("Segoe UI");
    private static readonly Brush Value = Frozen(Color.FromRgb(0xF4, 0xF4, 0xF6));
    private static readonly Brush Dim = Frozen(Color.FromArgb(0xA0, 0xFF, 0xFF, 0xFF));
    private static readonly Brush FpsColor = Frozen(Color.FromRgb(0x7C, 0xE0, 0x8A));
    private static readonly Brush CpuColor = Frozen(Color.FromRgb(0x6C, 0xB8, 0xFF));
    private static readonly Brush GpuColor = Frozen(Color.FromRgb(0xFF, 0xA9, 0x5E));
    private static readonly Brush PowerColor = Frozen(Color.FromRgb(0xFF, 0xD8, 0x5E));
    private static readonly Brush RamColor = Frozen(Color.FromRgb(0xC9, 0x9C, 0xFF));
    private static readonly Brush BatteryColor = Frozen(Color.FromRgb(0x5E, 0xE6, 0xD2));

    private readonly Border _card;
    private readonly StackPanel _root = new();
    private readonly FrameGraph _graph = new() { Height = 42, Margin = new Thickness(0, 6, 0, 4) };

    public PerfOverlayWindow()
    {
        WindowStyle = WindowStyle.None;
        AllowsTransparency = true;
        Background = Brushes.Transparent;
        ResizeMode = ResizeMode.NoResize;
        Topmost = true;
        ShowActivated = false;
        ShowInTaskbar = false;
        Focusable = false;
        IsHitTestVisible = false;
        SizeToContent = SizeToContent.WidthAndHeight;
        FontFamily = Mono;
        _card = new Border
        {
            CornerRadius = new CornerRadius(10),
            Background = Frozen(Color.FromArgb(0xEC, 0x10, 0x10, 0x14)),
            Margin = new Thickness(12),
            Child = _root,
        };
        Content = _card;
        SourceInitialized += (_, _) =>
        {
            var hwnd = new WindowInteropHelper(this).Handle;
            SetWindowLong(hwnd, -20, GetWindowLong(hwnd, -20) | 0x08000000 /* NOACTIVATE */ | 0x20 /* TRANSPARENT */ | 0x80 /* TOOLWINDOW */);
        };
    }

    private static SolidColorBrush Frozen(Color c)
    {
        var b = new SolidColorBrush(c);
        b.Freeze();
        return b;
    }

    /// <summary>값을 그린다 (UI 스레드). 단계가 바뀌면 배치를 새로 만든다.</summary>
    public void Update(int level, PerfOverlayData d)
    {
        _root.Children.Clear();
        switch (level)
        {
            case 1: BuildFps(d); break;
            case 2: BuildLine(d); break;
            default: BuildDetailed(d); break;
        }
        if (!IsVisible) Show();
        Place();
    }

    // ── 1단계: FPS 숫자 하나 ────────────────────────────

    private void BuildFps(PerfOverlayData d)
    {
        _card.Padding = new Thickness(10, 4, 10, 5);
        var row = new StackPanel { Orientation = Orientation.Horizontal };
        row.Children.Add(Text(Num(d.Fps), 20, Value, bold: true));
        row.Children.Add(Text("FPS", 11, FpsColor, bold: true, margin: new Thickness(5, 0, 0, 3), bottom: true));
        _root.Children.Add(row);
    }

    // ── 2단계: 한 줄 ─────────────────────────────────

    private void BuildLine(PerfOverlayData d)
    {
        _card.Padding = new Thickness(12, 6, 12, 6);
        var row = new StackPanel { Orientation = Orientation.Horizontal };
        void Item(string label, Brush color, string value, bool first = false)
        {
            if (!first) row.Children.Add(new Border { Width = 1, Height = 14, Background = Dim, Opacity = 0.35, Margin = new Thickness(10, 0, 10, 0), VerticalAlignment = VerticalAlignment.Center });
            row.Children.Add(Text(label, 11, color, bold: true, margin: new Thickness(0, 0, 5, 0)));
            row.Children.Add(Text(value, 14, Value, bold: true));
        }
        Item("FPS", FpsColor, Num(d.Fps), first: true);
        Item("CPU", CpuColor, Pct(d.CpuPercent));
        Item("GPU", GpuColor, Pct(d.GpuPercent));
        Item("전력", PowerColor, Watts(d));
        Item("배터리", BatteryColor, d.BatteryPercent is { } b ? $"{b}%" + (d.Charging ? "⚡" : "") : "–");
        _root.Children.Add(row);
    }

    // ── 3단계: 자세히 ────────────────────────────────

    private void BuildDetailed(PerfOverlayData d)
    {
        _card.Padding = new Thickness(14, 10, 14, 10);
        var head = new DockPanel { LastChildFill = false };
        var fps = new StackPanel { Orientation = Orientation.Horizontal };
        fps.Children.Add(Text(Num(d.Fps), 26, Value, bold: true));
        fps.Children.Add(Text("FPS", 12, FpsColor, bold: true, margin: new Thickness(6, 0, 0, 5), bottom: true));
        head.Children.Add(fps);
        var side = new StackPanel { Margin = new Thickness(18, 2, 0, 0) };
        side.Children.Add(Text(d.FpsLow1 is { } low ? $"1% 최저  {Num(low)}" : "1% 최저  –", 11, Dim));
        side.Children.Add(Text(d.FrameTimeMs is { } ft ? $"프레임  {ft.ToString("0.0", CultureInfo.InvariantCulture)} ms" : "프레임  –", 11, Dim));
        DockPanel.SetDock(side, Dock.Right);
        head.Children.Add(side);
        _root.Children.Add(head);

        _graph.Frames = d.FrameTimes;
        _graph.TargetMs = d.FpsLimit is > 0 and var lim ? 1000.0 / lim : null;
        _graph.InvalidateVisual();
        _root.Children.Add(_graph);

        var grid = new Grid { Margin = new Thickness(0, 4, 0, 0) };
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(48) });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        var r = 0;
        void Row(string label, Brush color, string value)
        {
            grid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
            var l = Text(label, 11, color, bold: true, margin: new Thickness(0, 2, 0, 2));
            var v = Text(value, 13, Value, margin: new Thickness(0, 1, 0, 1));
            Grid.SetRow(l, r); Grid.SetRow(v, r); Grid.SetColumn(v, 1);
            grid.Children.Add(l); grid.Children.Add(v);
            r++;
        }
        Row("CPU", CpuColor, Join(Pct(d.CpuPercent), d.CpuMhz is { } mhz ? $"{mhz / 1000.0:0.0} GHz" : null, d.CpuTemp is { } t ? $"{t}°C" : null));
        Row("GPU", GpuColor, Pct(d.GpuPercent));
        Row("전력", PowerColor, Join(d.BatteryWatts is { } bw ? $"{bw:0.0} W" : d.Charging ? "충전 중" : null, d.CpuWatts is { } cw ? $"CPU {cw} W" : null));
        Row("RAM", RamColor, d.RamUsedGb is { } u && d.RamTotalGb is { } tot ? $"{u:0.0} / {tot:0} GB" : "–");
        Row("배터리", BatteryColor, Join(d.BatteryPercent is { } bp ? $"{bp}%" : null,
            d.Charging ? "충전 중" : d.Remaining is { } rem ? (rem.TotalHours >= 1 ? $"{(int)rem.TotalHours}시간 {rem.Minutes}분" : $"{rem.Minutes}분") : null));
        _root.Children.Add(grid);
    }

    private static string Num(double? v) => v is { } x ? Math.Round(x).ToString(CultureInfo.InvariantCulture) : "–";
    private static string Pct(double? v) => v is { } x ? $"{Math.Round(x)}%" : "–";
    private static string Watts(PerfOverlayData d) =>
        d.BatteryWatts is { } w ? $"{w:0.0}W" : d.CpuWatts is { } c ? $"{c}W" : "–";
    private static string Join(params string?[] parts) =>
        parts.Where(p => p is not null).ToList() is { Count: > 0 } list ? string.Join("  ·  ", list) : "–";

    private static TextBlock Text(string text, double size, Brush color, bool bold = false, Thickness margin = default, bool bottom = false) => new()
    {
        Text = text,
        FontSize = size,
        Foreground = color,
        FontWeight = bold ? FontWeights.SemiBold : FontWeights.Normal,
        Margin = margin,
        VerticalAlignment = bottom ? VerticalAlignment.Bottom : VerticalAlignment.Center,
    };

    /// <summary>게임이 있는 모니터의 왼쪽 위 (물리 픽셀). 다른 창이 위로 올라와도 다시 맨 위로.</summary>
    private void Place()
    {
        var hwnd = new WindowInteropHelper(this).Handle;
        if (hwnd == IntPtr.Zero) return;
        var mi = new MonitorInfo { Size = Marshal.SizeOf<MonitorInfo>() };
        if (!GetMonitorInfo(MonitorFromWindow(GetForegroundWindow(), 2), ref mi)) return;
        SetWindowPos(hwnd, new IntPtr(-1), mi.Monitor.Left, mi.Monitor.Top, 0, 0, 0x0001 /* NOSIZE */ | 0x0010 /* NOACTIVATE */ | 0x0040 /* SHOW */);
    }

    /// <summary>프레임 시간 그래프: 최근 2초, 위로 튈수록 끊김. 점선 = FPS 제한 목표.</summary>
    private sealed class FrameGraph : FrameworkElement
    {
        private static readonly Pen Line = FrozenPen(Color.FromRgb(0x7C, 0xE0, 0x8A), 1.4);
        private static readonly Pen Spike = FrozenPen(Color.FromRgb(0xFF, 0x6B, 0x6B), 1.4);
        private static readonly Pen Target = FrozenPen(Color.FromArgb(0x70, 0xFF, 0xFF, 0xFF), 1, dashed: true);
        private static readonly Brush Back = Frozen(Color.FromArgb(0x26, 0xFF, 0xFF, 0xFF));

        public IReadOnlyList<float>? Frames { get; set; }
        public double? TargetMs { get; set; }

        private static Pen FrozenPen(Color c, double w, bool dashed = false)
        {
            var p = new Pen(new SolidColorBrush(c), w) { LineJoin = PenLineJoin.Round };
            if (dashed) p.DashStyle = new DashStyle([3, 3], 0);
            p.Freeze();
            return p;
        }

        protected override Size MeasureOverride(Size available) => new(220, Height);

        protected override void OnRender(DrawingContext dc)
        {
            var w = ActualWidth;
            var h = ActualHeight;
            dc.DrawRoundedRectangle(Back, null, new Rect(0, 0, w, h), 4, 4);
            if (Frames is not { Count: > 1 } f)
            {
                var hint = new FormattedText("게임을 실행하면 프레임 그래프가 보여요", CultureInfo.CurrentUICulture, FlowDirection.LeftToRight,
                    new Typeface(Mono, FontStyles.Normal, FontWeights.Normal, FontStretches.Normal), 11, Dim, VisualTreeHelper.GetDpi(this).PixelsPerDip);
                dc.DrawText(hint, new Point((w - hint.Width) / 2, (h - hint.Height) / 2));
                return;
            }

            // 세로 범위: 중앙값의 2배(최소 33ms)까지 — 평소 선은 가운데쯤, 끊김은 위로 튄다
            var sorted = f.OrderBy(x => x).ToList();
            var median = sorted[sorted.Count / 2];
            var top = Math.Max(Math.Max(median * 2, 33.4), TargetMs ?? 0);
            double Y(double ms) => h - 2 - Math.Min(ms, top) / top * (h - 4);

            if (TargetMs is { } tm) dc.DrawLine(Target, new Point(0, Y(tm)), new Point(w, Y(tm)));

            var step = w / (f.Count - 1);
            for (var i = 1; i < f.Count; i++)
            {
                var pen = f[i] > median * 1.8 ? Spike : Line;
                dc.DrawLine(pen, new Point((i - 1) * step, Y(f[i - 1])), new Point(i * step, Y(f[i])));
            }
        }
    }

    [StructLayout(LayoutKind.Sequential)] private struct Rect32 { public int Left, Top, Right, Bottom; }
    [StructLayout(LayoutKind.Sequential)] private struct MonitorInfo { public int Size; public Rect32 Monitor, Work; public int Flags; }
    [DllImport("user32.dll")] private static extern int GetWindowLong(IntPtr hwnd, int index);
    [DllImport("user32.dll")] private static extern int SetWindowLong(IntPtr hwnd, int index, int value);
    [DllImport("user32.dll")] private static extern IntPtr GetForegroundWindow();
    [DllImport("user32.dll")] private static extern IntPtr MonitorFromWindow(IntPtr hwnd, uint flags);
    [DllImport("user32.dll")] private static extern bool GetMonitorInfo(IntPtr monitor, ref MonitorInfo info);
    [DllImport("user32.dll")] private static extern bool SetWindowPos(IntPtr hwnd, IntPtr after, int x, int y, int cx, int cy, uint flags);
}
