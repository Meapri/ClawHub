using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Media;

namespace ClawApp.Motion;

/// <summary>
/// 부드러운 스크롤: 휠과 "보이게 하기"(패드로 포커스를 옮길 때 BringIntoView)가 순간 이동 대신 목표 위치로 미끄러진다.
/// WPF 기본은 휠 한 칸·포커스 이동마다 화면이 툭 끊겨 넘어가서, 120Hz 화면에서 특히 덜컥거려 보인다.
/// 터치로 끄는 동안은 손가락이 그대로 움직이게 애니메이션을 멈춘다. 기본 ScrollViewer 스타일에서 켠다.
/// </summary>
public static class SmoothScroll
{
    public static readonly DependencyProperty IsEnabledProperty = DependencyProperty.RegisterAttached(
        "IsEnabled", typeof(bool), typeof(SmoothScroll), new PropertyMetadata(false, OnChanged));

    public static bool GetIsEnabled(DependencyObject d) => (bool)d.GetValue(IsEnabledProperty);
    public static void SetIsEnabled(DependencyObject d, bool value) => d.SetValue(IsEnabledProperty, value);

    /// <summary>목표까지 남은 거리가 이 시간 상수로 줄어든다 (작을수록 빠르게 따라감).</summary>
    private const double TimeConstantMs = 70;
    /// <summary>휠 한 칸(120)이 움직이는 거리.</summary>
    private const double WheelStep = 1.1;

    private sealed class State
    {
        public double X, Y;          // 목표
        public bool Animating;
        public TimeSpan Last;
        public FrameworkElement? Content;
    }

    private static readonly DependencyProperty StateProperty =
        DependencyProperty.RegisterAttached("State", typeof(State), typeof(SmoothScroll));

    private static readonly List<ScrollViewer> Active = [];

    private static void OnChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
    {
        if (d is not ScrollViewer sv || e.NewValue is not true) return;
        sv.SetValue(StateProperty, new State());
        sv.PreviewMouseWheel += OnWheel;
        sv.ManipulationStarting += (_, _) => Stop(sv);
        sv.PreviewMouseDown += (_, _) => Stop(sv); // 마우스로 잡아 끄는 쪽(홈)이 직접 움직인다
        sv.Loaded += (_, _) => HookContent(sv);
        if (sv.IsLoaded) HookContent(sv);
    }

    private static State? StateOf(ScrollViewer sv) => sv.GetValue(StateProperty) as State;

    /// <summary>글 상자 안쪽 스크롤은 그대로 둔다 (캐럿 따라가기는 즉시여야 함).</summary>
    private static bool Skip(ScrollViewer sv) => sv.TemplatedParent is TextBoxBase;

    /// <summary>
    /// "보이게 하기" 요청은 ScrollViewer에 닿기 전에 내용물에서 가로챈다 — ScrollViewer가 직접 받으면 즉시 넘겨 버린다.
    /// </summary>
    private static void HookContent(ScrollViewer sv)
    {
        if (StateOf(sv) is not { } s || Skip(sv) || sv.Content is not FrameworkElement content || ReferenceEquals(s.Content, content)) return;
        s.Content?.RemoveHandler(FrameworkElement.RequestBringIntoViewEvent, (RequestBringIntoViewEventHandler)OnBringIntoView);
        s.Content = content;
        content.AddHandler(FrameworkElement.RequestBringIntoViewEvent, (RequestBringIntoViewEventHandler)OnBringIntoView);
    }

    private static void OnBringIntoView(object sender, RequestBringIntoViewEventArgs e)
    {
        if (sender is not FrameworkElement content || VisualTreeHelper.GetParent(content) is null) return;
        if (FindScroller(content) is not { } sv || StateOf(sv) is not { } s || !MotionCore.Enabled) return;
        if (e.TargetObject is not Visual target || !content.IsAncestorOf(target) && !ReferenceEquals(content, target)) return;

        Rect rect;
        try
        {
            var r = e.TargetRect.IsEmpty ? new Rect(((target as UIElement)?.RenderSize) ?? default) : e.TargetRect;
            // 뷰포트 기준 위치 + 지금 스크롤 위치 = 스크롤 범위 안의 위치 (내용물의 Margin까지 포함)
            var view = target.TransformToAncestor(sv).TransformBounds(r);
            rect = new Rect(view.X + sv.HorizontalOffset, view.Y + sv.VerticalOffset, view.Width, view.Height);
        }
        catch (InvalidOperationException) { return; }
        e.Handled = true;

        var fromX = s.Animating ? s.X : sv.HorizontalOffset;
        var fromY = s.Animating ? s.Y : sv.VerticalOffset;
        var x = Fit(fromX, rect.Left, rect.Right, sv.ViewportWidth, sv.ScrollableWidth);
        var y = Fit(fromY, rect.Top, rect.Bottom, sv.ViewportHeight, sv.ScrollableHeight);
        ScrollTo(sv, x, y);

        // 바깥 스크롤 영역도 이어서 (중첩된 목록)
        sv.BringIntoView(new Rect(rect.X - x, rect.Y - y, rect.Width, rect.Height));
    }

    /// <summary>ScrollViewer.MakeVisible과 같은 규칙: 이미 보이면 그대로, 앞이 잘리면 앞에, 뒤가 잘리면 뒤에 맞춘다.</summary>
    private static double Fit(double offset, double start, double end, double viewport, double max)
    {
        if (viewport <= 0 || max <= 0) return Math.Clamp(offset, 0, Math.Max(0, max));
        if (start < offset || end - start > viewport) offset = start;
        else if (end > offset + viewport) offset = end - viewport;
        return Math.Clamp(offset, 0, max);
    }

    private static ScrollViewer? FindScroller(DependencyObject from)
    {
        for (var p = VisualTreeHelper.GetParent(from); p is not null; p = VisualTreeHelper.GetParent(p))
            if (p is ScrollViewer sv) return sv;
        return null;
    }

    private static void OnWheel(object sender, MouseWheelEventArgs e)
    {
        if (sender is not ScrollViewer sv || StateOf(sv) is not { } s || Skip(sv)) return;
        // 안쪽부터 이 방향으로 움직일 수 있는 첫 스크롤 영역만 받는다 (Preview는 바깥부터 오므로 안쪽이 있으면 양보)
        for (var d = e.OriginalSource as DependencyObject; d is not null && !ReferenceEquals(d, sv); d = d is Visual ? VisualTreeHelper.GetParent(d) : LogicalTreeHelper.GetParent(d))
            if (d is ScrollViewer inner && GetIsEnabled(inner) && CanScroll(inner, e.Delta)) return;
        if (!CanScroll(sv, e.Delta)) return;
        e.Handled = true;

        var step = -e.Delta * WheelStep;
        if (sv.ScrollableHeight > 0)
            ScrollTo(sv, sv.HorizontalOffset, (s.Animating ? s.Y : sv.VerticalOffset) + step, wheel: true);
        else
            ScrollTo(sv, (s.Animating ? s.X : sv.HorizontalOffset) + step, sv.VerticalOffset, wheel: true);
    }

    private static bool CanScroll(ScrollViewer sv, int delta)
    {
        var s = StateOf(sv);
        if (sv.ScrollableHeight > 0)
        {
            var y = s is { Animating: true } ? s.Y : sv.VerticalOffset;
            return delta > 0 ? y > 0.5 : y < sv.ScrollableHeight - 0.5;
        }
        if (sv.ScrollableWidth > 0)
        {
            var x = s is { Animating: true } ? s.X : sv.HorizontalOffset;
            return delta > 0 ? x > 0.5 : x < sv.ScrollableWidth - 0.5;
        }
        return false;
    }

    /// <summary>목표 위치로 미끄러지게 한다 (애니메이션이 꺼져 있으면 바로).</summary>
    public static void ScrollTo(ScrollViewer sv, double x, double y, bool wheel = false)
    {
        if (StateOf(sv) is not { } s) return;
        s.X = Math.Clamp(x, 0, Math.Max(0, sv.ScrollableWidth));
        s.Y = Math.Clamp(y, 0, Math.Max(0, sv.ScrollableHeight));
        if (!MotionCore.Enabled)
        {
            sv.ScrollToHorizontalOffset(s.X);
            sv.ScrollToVerticalOffset(s.Y);
            return;
        }
        if (s.Animating) return;
        s.Animating = true;
        s.Last = TimeSpan.Zero;
        Active.Add(sv);
        if (Active.Count == 1) CompositionTarget.Rendering += OnFrame;
    }

    /// <summary>진행 중인 미끄러짐을 지금 자리에서 멈춘다.</summary>
    public static void Stop(ScrollViewer sv)
    {
        if (StateOf(sv) is not { Animating: true } s) return;
        s.Animating = false;
        Active.Remove(sv);
        if (Active.Count == 0) CompositionTarget.Rendering -= OnFrame;
    }

    private static void OnFrame(object? sender, EventArgs e)
    {
        var now = (e as RenderingEventArgs)?.RenderingTime ?? TimeSpan.FromMilliseconds(Environment.TickCount64);
        for (var i = Active.Count - 1; i >= 0; i--)
        {
            var sv = Active[i];
            if (StateOf(sv) is not { } s) { Active.RemoveAt(i); continue; }
            if (s.Last == now) continue; // 같은 프레임에 두 번 오는 경우
            var dt = s.Last == TimeSpan.Zero ? 16.7 : Math.Clamp((now - s.Last).TotalMilliseconds, 1, 50);
            s.Last = now;
            var k = 1 - Math.Exp(-dt / TimeConstantMs);
            double cx = sv.HorizontalOffset, cy = sv.VerticalOffset;
            double nx = cx + (s.X - cx) * k, ny = cy + (s.Y - cy) * k;
            var done = Math.Abs(s.X - nx) < 0.5 && Math.Abs(s.Y - ny) < 0.5;
            if (done || !sv.IsLoaded) { nx = s.X; ny = s.Y; }
            if (Math.Abs(nx - cx) > 0.01) sv.ScrollToHorizontalOffset(nx);
            if (Math.Abs(ny - cy) > 0.01) sv.ScrollToVerticalOffset(ny);
            if (!done && sv.IsLoaded) continue;
            s.Animating = false;
            Active.RemoveAt(i);
        }
        if (Active.Count == 0) CompositionTarget.Rendering -= OnFrame;
    }
}
