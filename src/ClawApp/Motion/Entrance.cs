using System.Collections.Specialized;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Media;
using System.Windows.Threading;

namespace ClawApp.Motion;

public enum StaggerMode
{
    /// <summary>지정 안 함 (Key만 쓰면 Self처럼 동작).</summary>
    None,
    /// <summary>요소 자신만 움직인다 (보일 때마다: 패널·토스트 등).</summary>
    Self,
    /// <summary>직계 자식(Panel) 또는 항목 컨테이너(ItemsControl)를 순서대로 30ms 간격으로.</summary>
    Items,
    /// <summary>안쪽의 모든 타일(버튼)을 왼쪽 열부터 차례로 — Windows 8 시작 화면처럼 오른쪽에서 쓸려 들어온다.</summary>
    Tiles,
}

/// <summary>
/// 진입 애니메이션 (turnstile/continuum 느낌의 계단식 등장).
/// 언제 재생되나: 화면에 처음 올라올 때(Loaded), 숨겨졌다 다시 보일 때(설정 분류 전환·패널 열기),
/// ItemsSource가 통째로 바뀔 때(라이브러리 탭), Key 값이 바뀔 때(정수면 증감 방향으로 미끄러짐).
/// </summary>
public static class Entrance
{
    /// <summary>한 번에 움직이는 요소 수 상한 (휴대 기기 성능). 나머지는 바로 보인다.</summary>
    private const int MaxAnimated = 18;

    public static readonly DependencyProperty StaggerProperty = DependencyProperty.RegisterAttached(
        "Stagger", typeof(StaggerMode), typeof(Entrance), new PropertyMetadata(StaggerMode.None, OnStaggerChanged));

    public static StaggerMode GetStagger(DependencyObject d) => (StaggerMode)d.GetValue(StaggerProperty);
    public static void SetStagger(DependencyObject d, StaggerMode value) => d.SetValue(StaggerProperty, value);

    /// <summary>이 값이 바뀌면 다시 재생. 정수면 커질 때 오른쪽에서, 작아질 때 왼쪽에서 (LB/RB 피벗).</summary>
    public static readonly DependencyProperty KeyProperty = DependencyProperty.RegisterAttached(
        "Key", typeof(object), typeof(Entrance), new PropertyMetadata(null, OnKeyChanged));

    public static object? GetKey(DependencyObject d) => d.GetValue(KeyProperty);
    public static void SetKey(DependencyObject d, object? value) => d.SetValue(KeyProperty, value);

    /// <summary>Tiles 모드에서 버튼이 아니어도 함께 움직일 요소 (그룹 제목 등).</summary>
    public static readonly DependencyProperty IncludeProperty = DependencyProperty.RegisterAttached(
        "Include", typeof(bool), typeof(Entrance), new PropertyMetadata(false));

    public static bool GetInclude(DependencyObject d) => (bool)d.GetValue(IncludeProperty);
    public static void SetInclude(DependencyObject d, bool value) => d.SetValue(IncludeProperty, value);

    /// <summary>미끄러져 들어오는 거리 (px). 기본: Tiles 72, 그 외 40.</summary>
    public static readonly DependencyProperty OffsetProperty = DependencyProperty.RegisterAttached(
        "Offset", typeof(double), typeof(Entrance), new PropertyMetadata(double.NaN));

    public static double GetOffset(DependencyObject d) => (double)d.GetValue(OffsetProperty);
    public static void SetOffset(DependencyObject d, double value) => d.SetValue(OffsetProperty, value);

    private sealed class State
    {
        public long LastPlay;
        public bool Scheduled;
        public int Direction = 1;
    }

    private static readonly DependencyProperty StateProperty =
        DependencyProperty.RegisterAttached("State", typeof(State), typeof(Entrance));

    private static void OnStaggerChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
    {
        if (d is FrameworkElement fe) Attach(fe);
    }

    private static void OnKeyChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
    {
        if (d is not FrameworkElement fe) return;
        Attach(fe);
        if (!fe.IsLoaded || e.OldValue is null || Equals(e.OldValue, e.NewValue)) return;
        var dir = 1;
        if (e.OldValue is int a && e.NewValue is int b)
        {
            var delta = b - a;
            // 마지막 → 처음(또는 반대)으로 한 바퀴 돈 경우는 한 칸 이동으로 본다
            dir = Math.Abs(delta) > 1 ? -Math.Sign(delta) : Math.Sign(delta);
            if (dir == 0) dir = 1;
        }
        Schedule(fe, dir);
    }

    private static State Attach(FrameworkElement fe)
    {
        if (fe.GetValue(StateProperty) is State s) return s;
        s = new State();
        fe.SetValue(StateProperty, s);
        fe.Loaded += (_, _) => Schedule(fe, null);
        fe.IsVisibleChanged += (_, a) =>
        {
            if (a.NewValue is true && fe.IsLoaded) Schedule(fe, null);
        };
        if (fe is ItemsControl ic)
        {
            ((INotifyCollectionChanged)ic.Items).CollectionChanged += (_, a) =>
            {
                if (a.Action == NotifyCollectionChangedAction.Reset && fe.IsLoaded && fe.IsVisible) Schedule(fe, null);
            };
        }
        return s;
    }

    /// <summary>
    /// 레이아웃(항목 컨테이너 생성)이 끝난 뒤 재생한다. 그 사이 한 프레임 보이지 않게 즉시 투명하게 잡아 둔다.
    /// </summary>
    private static void Schedule(FrameworkElement fe, int? direction)
    {
        if (!MotionCore.Enabled) return;
        var s = Attach(fe);
        if (direction is { } dir) s.Direction = dir;
        if (s.Scheduled) return;
        if (direction is null && Environment.TickCount64 - s.LastPlay < 200) return;
        s.Scheduled = true;
        fe.BeginAnimation(UIElement.OpacityProperty, MotionCore.Hold(0, 0, TimeSpan.Zero, MotionCore.Ms(600), null));
        fe.Dispatcher.BeginInvoke(DispatcherPriority.Loaded, () =>
        {
            s.Scheduled = false;
            s.LastPlay = Environment.TickCount64;
            fe.BeginAnimation(UIElement.OpacityProperty, null);
            if (fe.IsVisible) Play(fe, s.Direction);
            s.Direction = 1;
        });
    }

    private static void Play(FrameworkElement root, int dir)
    {
        var mode = GetStagger(root);
        var offset = GetOffset(root);
        if (double.IsNaN(offset)) offset = mode == StaggerMode.Tiles ? 72 : 40;
        double dx = offset * dir, dy = 0;

        switch (mode)
        {
            case StaggerMode.None:
            case StaggerMode.Self:
                MotionCore.SlideIn(root, dx, dy, TimeSpan.Zero, MotionCore.Ms(420));
                break;

            case StaggerMode.Items:
                var n = 0;
                foreach (var el in Items(root))
                {
                    if (n >= MaxAnimated || !InViewport(el)) break;
                    MotionCore.SlideIn(el, dx, dy, MotionCore.Ms(n * 32), MotionCore.Ms(460));
                    n++;
                }
                break;

            case StaggerMode.Tiles:
                var tiles = new List<UIElement>();
                CollectTiles(root, tiles);
                var placed = tiles
                    .Where(InViewport)
                    .Select(t => (t, p: t.TranslatePoint(new Point(0, 0), root)))
                    .ToList();
                if (placed.Count == 0) break;
                double minX = placed.Min(x => x.p.X), minY = placed.Min(x => x.p.Y);
                foreach (var (t, p) in placed.OrderBy(x => x.p.X).ThenBy(x => x.p.Y).Take(MaxAnimated))
                {
                    // 열(왼쪽→오른쪽) 순서가 주, 같은 열 안에서는 위→아래로 살짝
                    var delay = Math.Min(560, (p.X - minX) / 176 * 55 + (p.Y - minY) / 176 * 18);
                    MotionCore.SlideIn(t, dx, dy, MotionCore.Ms(delay), MotionCore.Ms(520));
                }
                break;
        }
    }

    private static IEnumerable<UIElement> Items(FrameworkElement root)
    {
        if (root is ItemsControl ic)
        {
            for (var i = 0; i < ic.Items.Count; i++)
                if (ic.ItemContainerGenerator.ContainerFromIndex(i) is UIElement { IsVisible: true } c)
                    yield return c;
        }
        else if (root is Panel p)
        {
            foreach (UIElement c in p.Children)
                if (c.IsVisible) yield return c;
        }
        else if (root is Decorator { Child: Panel inner })
        {
            foreach (UIElement c in inner.Children)
                if (c.IsVisible) yield return c;
        }
    }

    private static void CollectTiles(DependencyObject node, List<UIElement> into)
    {
        if (node is UIElement { IsVisible: false }) return;
        if (node is UIElement el && (node is ButtonBase || GetInclude(node)))
        {
            into.Add(el);
            return;
        }
        for (var i = 0; i < VisualTreeHelper.GetChildrenCount(node); i++) CollectTiles(VisualTreeHelper.GetChild(node, i), into);
    }

    /// <summary>창 밖(스크롤로 가려진 곳)에 있는 요소는 움직일 필요가 없다.</summary>
    private static bool InViewport(UIElement el)
    {
        if (Window.GetWindow(el) is not { } w) return true;
        try
        {
            var p = el.TranslatePoint(new Point(0, 0), w);
            return p.X < w.ActualWidth && p.Y < w.ActualHeight && p.X + el.RenderSize.Width > 0 && p.Y + el.RenderSize.Height > 0;
        }
        catch (InvalidOperationException)
        {
            return false;
        }
    }
}
