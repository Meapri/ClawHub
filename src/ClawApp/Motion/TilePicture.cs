using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;

namespace ClawApp.Motion;

/// <summary>
/// Windows 10 Mobile "타일 그림": 사진 한 장이 화면 전체에 붙어 있고, 타일들이 그 사진을 들여다보는 창이 된다.
/// 각 타일 템플릿의 맨 아래 층(Host)에 같은 그림을 창 기준 좌표로 칠한다 — 타일이 어디 있든 이어져 보인다.
/// 가로로 스크롤하면 그림은 타일보다 느리게(30%) 움직여 깊이감이 생긴다.
/// 진입 애니메이션(RenderTransform) 동안만 프레임마다 위치를 맞추고, 평소에는 레이아웃·스크롤이 바뀔 때만 계산한다.
/// 성능: 레이아웃 패스 알림(LayoutUpdated)은 창 안 어느 요소의 레이아웃이든 매번 오므로 타일마다 구독하지 않는다.
/// 그림이 켜져 있을 때만 하나를 구독해서 모든 타일을 한 번에 맞추고, 타일 그림을 안 쓰면(기본) 아무 일도 하지 않는다.
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

    /// <summary>호스트가 들어 있는 스크롤 영역 (Loaded 때 한 번 찾아 둔다 — 레이아웃 패스마다 부모를 거슬러 올라가지 않게).</summary>
    private sealed class HostState
    {
        public ScrollViewer? Scroller;
        public bool Resolved;
    }

    private static readonly DependencyProperty StateProperty =
        DependencyProperty.RegisterAttached("State", typeof(HostState), typeof(TilePicture));

    private static readonly List<WeakReference<Border>> Hosts = [];
    private static ImageSource? _source;
    private static DateTime _burstUntil;
    private static bool _rendering;

    /// <summary>레이아웃 패스 알림을 받는 대표 요소 (LayoutUpdated는 요소와 관계없이 이 Dispatcher의 모든 레이아웃 패스마다 온다).</summary>
    private static UIElement? _layoutProbe;
    private static bool _layoutHooked;

    /// <summary>글자가 읽히게 살짝 어둡게 (W10M 타일 그림처럼). 모든 타일이 같은 색이라 하나를 얼려서 같이 쓴다.</summary>
    private static readonly SolidColorBrush Shade = Frozen(new SolidColorBrush(Color.FromArgb(0x47, 0, 0, 0)));

    private static SolidColorBrush Frozen(SolidColorBrush b)
    {
        b.Freeze();
        return b;
    }

    /// <summary>타일에 보일 그림 (null = 타일 그림 끔).</summary>
    public static void SetSource(ImageSource? source)
    {
        if (ReferenceEquals(source, _source)) return;
        _source = source;
        UpdateLayoutHook();
        ForEachHost(Update);
    }

    private static void OnHostChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
    {
        if (d is not Border b || e.NewValue is not true) return;
        Hosts.Add(new WeakReference<Border>(b));
        b.IsHitTestVisible = false;
        b.Loaded += (_, _) => { HookScroller(b); Update(b); StartBurst(); };
        b.IsVisibleChanged += (_, a) => { if (a.NewValue is true) { Update(b); StartBurst(); } };
        UpdateLayoutHook();
    }

    /// <summary>그림이 켜져 있고 타일이 있을 때만 레이아웃 패스를 구독한다.</summary>
    private static void UpdateLayoutHook()
    {
        var want = _source is not null && Hosts.Count > 0;
        if (want == _layoutHooked) return;
        _layoutProbe ??= new UIElement();
        if (want) _layoutProbe.LayoutUpdated += OnLayoutUpdated;
        else _layoutProbe.LayoutUpdated -= OnLayoutUpdated;
        _layoutHooked = want;
    }

    private static void OnLayoutUpdated(object? sender, EventArgs e)
    {
        if (_source is null) return;
        ForEachHost(Update);
    }

    private static void HookScroller(Border b)
    {
        var state = b.GetValue(StateProperty) as HostState;
        if (state is null) b.SetValue(StateProperty, state = new HostState());
        state.Scroller = FindScroller(b);
        state.Resolved = true;
        if (state.Scroller is not { } sv) return;
        sv.ScrollChanged -= OnScroll; // 같은 스크롤러에 한 번만
        sv.ScrollChanged += OnScroll;
    }

    private static ScrollViewer? FindScroller(DependencyObject from)
    {
        for (var p = VisualTreeHelper.GetParent(from); p is not null; p = VisualTreeHelper.GetParent(p))
            if (p is ScrollViewer sv) return sv;
        return null;
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
        if (Hosts.Count == 0) UpdateLayoutHook();
    }

    private static void Update(Border b)
    {
        if (_source is null || !b.IsVisible || (b.TemplatedParent is DependencyObject tile && GetSkip(tile)) || Window.GetWindow(b) is not { } win)
        {
            if (b.Background is not null) b.Background = null;
            if (b.Child is { Visibility: not Visibility.Collapsed } child) child.Visibility = Visibility.Collapsed;
            return;
        }

        Point at;
        try { at = b.TransformToAncestor(win).Transform(new Point(0, 0)); }
        catch (InvalidOperationException) { return; } // 아직 창에 붙지 않음

        // 그림은 창에 붙어 있고, 스크롤의 30%만 따라간다 (가로로 그만큼 넓게 그린다)
        var sv = b.GetValue(StateProperty) is HostState { Resolved: true } state ? state.Scroller : FindScroller(b);
        double offset = 0, extra = 0;
        if (sv is not null)
        {
            offset = sv.HorizontalOffset * Parallax;
            extra = sv.ScrollableWidth * Parallax;
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
        b.Child ??= new Border { Background = Shade };
        if (b.Child.Visibility != Visibility.Visible) b.Child.Visibility = Visibility.Visible;
    }
}
