using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;

namespace ClawApp.Motion;

/// <summary>
/// Windows 10 Mobile "타일 그림": 사진 한 장이 화면 전체에 붙어 있고, 타일들이 그 사진을 들여다보는 창이 된다.
/// 각 타일 템플릿의 맨 아래 층(Host)에 같은 그림을 창 기준 좌표로 칠한다 — 타일이 어디 있든 이어져 보인다.
/// 가로로 스크롤하면 그림은 타일보다 느리게(30%) 움직여 깊이감이 생긴다.
/// 진입 애니메이션(RenderTransform) 동안만 프레임마다 위치를 맞추고, 평소에는 레이아웃·스크롤이 바뀔 때만 계산한다.
/// </summary>
public static class TilePicture
{
    private const double Parallax = 0.3;
    private static readonly TimeSpan BurstLength = TimeSpan.FromMilliseconds(1200);

    /// <summary>타일 템플릿의 그림 층 (Border).</summary>
    public static readonly DependencyProperty HostProperty = DependencyProperty.RegisterAttached(
        "Host", typeof(bool), typeof(TilePicture), new PropertyMetadata(false, OnHostChanged));

    public static bool GetHost(DependencyObject d) => (bool)d.GetValue(HostProperty);
    public static void SetHost(DependencyObject d, bool value) => d.SetValue(HostProperty, value);

    /// <summary>타일(Button)에 두면 그림을 칠하지 않는다 (게임 종료처럼 색이 의미인 타일).</summary>
    public static readonly DependencyProperty SkipProperty = DependencyProperty.RegisterAttached(
        "Skip", typeof(bool), typeof(TilePicture), new PropertyMetadata(false));

    public static bool GetSkip(DependencyObject d) => (bool)d.GetValue(SkipProperty);
    public static void SetSkip(DependencyObject d, bool value) => d.SetValue(SkipProperty, value);

    private static readonly List<WeakReference<Border>> Hosts = [];
    private static ImageSource? _source;
    private static DateTime _burstUntil;
    private static bool _rendering;

    /// <summary>타일에 보일 그림 (null = 타일 그림 끔).</summary>
    public static void SetSource(ImageSource? source)
    {
        if (ReferenceEquals(source, _source)) return;
        _source = source;
        ForEachHost(Update);
    }

    private static void OnHostChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
    {
        if (d is not Border b || e.NewValue is not true) return;
        Hosts.Add(new WeakReference<Border>(b));
        b.IsHitTestVisible = false;
        b.Loaded += (_, _) => { HookScroller(b); Update(b); StartBurst(); };
        b.IsVisibleChanged += (_, a) => { if (a.NewValue is true) { Update(b); StartBurst(); } };
        b.LayoutUpdated += (_, _) => Update(b);
    }

    private static void HookScroller(Border b)
    {
        for (var p = VisualTreeHelper.GetParent(b); p is not null; p = VisualTreeHelper.GetParent(p))
        {
            if (p is not ScrollViewer sv) continue;
            sv.ScrollChanged -= OnScroll; // 같은 스크롤러에 한 번만
            sv.ScrollChanged += OnScroll;
            return;
        }
    }

    private static void OnScroll(object sender, ScrollChangedEventArgs e)
    {
        if (_source is not null) ForEachHost(Update);
    }

    /// <summary>진입 애니메이션 동안 (레이아웃은 그대로, 렌더 변환만 바뀜) 잠깐 프레임마다 맞춘다.</summary>
    private static void StartBurst()
    {
        if (_source is null || !MotionCore.Enabled) return;
        _burstUntil = DateTime.UtcNow + BurstLength;
        if (_rendering) return;
        _rendering = true;
        CompositionTarget.Rendering += OnFrame;
    }

    private static void OnFrame(object? sender, EventArgs e)
    {
        ForEachHost(Update);
        if (DateTime.UtcNow < _burstUntil && _source is not null) return;
        CompositionTarget.Rendering -= OnFrame;
        _rendering = false;
    }

    private static void ForEachHost(Action<Border> action)
    {
        for (var i = Hosts.Count - 1; i >= 0; i--)
        {
            if (Hosts[i].TryGetTarget(out var b)) action(b);
            else Hosts.RemoveAt(i);
        }
    }

    private static void Update(Border b)
    {
        var skip = b.TemplatedParent is DependencyObject tile && GetSkip(tile);
        if (_source is null || skip || !b.IsVisible || Window.GetWindow(b) is not { } win)
        {
            if (b.Background is not null) b.Background = null;
            if (b.Child is not null) b.Child.Visibility = Visibility.Collapsed;
            return;
        }

        Point at;
        try { at = b.TransformToAncestor(win).Transform(new Point(0, 0)); }
        catch (InvalidOperationException) { return; } // 아직 창에 붙지 않음

        // 그림은 창에 붙어 있고, 스크롤의 30%만 따라간다 (가로로 그만큼 넓게 그린다)
        double offset = 0, extra = 0;
        for (var p = VisualTreeHelper.GetParent(b); p is not null; p = VisualTreeHelper.GetParent(p))
        {
            if (p is not ScrollViewer sv) continue;
            offset = sv.HorizontalOffset * Parallax;
            extra = sv.ScrollableWidth * Parallax;
            break;
        }
        var viewport = new Rect(-offset - at.X, -at.Y, win.ActualWidth + extra, win.ActualHeight);

        if (b.Background is ImageBrush brush && ReferenceEquals(brush.ImageSource, _source))
        {
            if (brush.Viewport != viewport) brush.Viewport = viewport;
        }
        else
        {
            b.Background = new ImageBrush(_source)
            {
                ViewportUnits = BrushMappingMode.Absolute,
                Viewport = viewport,
                Stretch = Stretch.UniformToFill,
                AlignmentX = AlignmentX.Left,
            };
        }

        // 글자가 읽히게 살짝 어둡게 (W10M 타일 그림처럼)
        b.Child ??= new Border { Background = new SolidColorBrush(Color.FromArgb(0x47, 0, 0, 0)) };
        b.Child.Visibility = Visibility.Visible;
    }
}
