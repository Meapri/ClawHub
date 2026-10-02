using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Media;

namespace ClawApp.Motion;

/// <summary>
/// 터치 끌기와 손을 뗀 뒤의 관성을 화면 프레임마다(120Hz면 120번) 직접 움직인다.
/// WPF 기본 패닝은 끄는 동안 입력이 올 때마다, 관성은 약 15ms 타이머로 움직여서 120Hz 화면에서 걸음이 고르지 않고
/// 손을 떼면 너무 멀리 미끄러진다. 그래서 ScrollViewer의 기본 조작(Manipulation)은 취소하고 여기서 처리한다.
/// PanningMode(HorizontalOnly 등)는 그대로 "어느 방향으로 끌 수 있나"의 뜻으로 읽는다 — None이면 끌지 않는다(홈 편집 중).
/// 끌기가 시작되기 전(10px)까지는 손을 대지 않아 탭은 평소처럼 버튼을 누른다.
/// </summary>
public static partial class SmoothScroll
{
    private const double PanThreshold = 10;
    /// <summary>관성 속도가 이 시간 상수로 줄어든다 (Windows·iOS 기본 느낌 ≈ 325ms).</summary>
    private const double FlingDecayMs = 325;
    /// <summary>이보다 느리면(px/ms) 관성 없이 멈춘다.</summary>
    private const double MinFling = 0.05;

    private sealed class Pan
    {
        public TouchDevice? Device;
        public Point Start;
        public double StartX, StartY;
        public bool Active;       // 끄는 중 (손가락을 따라감)
        public bool Blocked;      // 슬라이더 등 위에서 시작 — 이 손가락은 건드리지 않는다
        public bool Swallow;      // 관성 중 탭으로 멈춤 — 이 손가락의 탭은 버튼을 누르지 않는다
        public double TargetX, TargetY;
        public readonly List<(double T, Point P)> Samples = [];
        public double VX, VY;     // 관성 속도 (px/ms, 스크롤 위치 기준)
        public bool Flinging;
        public TimeSpan Last;
    }

    private static readonly DependencyProperty PanProperty =
        DependencyProperty.RegisterAttached("Pan", typeof(Pan), typeof(SmoothScroll));

    private static readonly List<ScrollViewer> Moving = [];
    private static readonly System.Diagnostics.Stopwatch TouchClock = System.Diagnostics.Stopwatch.StartNew();

    /// <summary>손가락으로 끄는 중인지 (홈의 "길게 눌러 편집"이 끄는 도중에 시작되지 않게).</summary>
    public static bool IsPanning(ScrollViewer? sv) => sv?.GetValue(PanProperty) is Pan { Active: true };

    private static void HookTouch(ScrollViewer sv)
    {
        if (Skip(sv)) return;
        sv.SetValue(PanProperty, new Pan());
        // 기본 패닝·관성은 쓰지 않는다 (취소하면 터치가 평소처럼 마우스로도 올라가 버튼이 눌린다)
        sv.ManipulationStarting += (_, e) => { if (ReferenceEquals(e.Source, sv)) e.Cancel(); };
        sv.PreviewTouchDown += OnTouchDown;
        sv.PreviewTouchMove += OnTouchMove;
        sv.PreviewTouchUp += OnTouchUp;
        sv.LostTouchCapture += (_, e) =>
        {
            if (sv.GetValue(PanProperty) is Pan { Active: true } p && e.TouchDevice == p.Device) EndPan(sv, p, fling: false);
        };
    }

    private static (bool X, bool Y) Axes(ScrollViewer sv) => sv.PanningMode switch
    {
        PanningMode.None => (false, false),
        PanningMode.HorizontalOnly => (sv.ScrollableWidth > 0, false),
        PanningMode.VerticalOnly => (false, sv.ScrollableHeight > 0),
        _ => (sv.ScrollableWidth > 0, sv.ScrollableHeight > 0),
    };

    private static void OnTouchDown(object? sender, TouchEventArgs e)
    {
        if (sender is not ScrollViewer sv || sv.GetValue(PanProperty) is not Pan p) return;
        if (p.Device is not null && p.Device != e.TouchDevice) return; // 두 번째 손가락은 무시
        var wasFast = p.Flinging && Math.Max(Math.Abs(p.VX), Math.Abs(p.VY)) > 0.15;
        StopFling(sv, p);
        Stop(sv);
        p.Device = e.TouchDevice;
        p.Start = e.GetTouchPoint(sv).Position;
        p.StartX = sv.HorizontalOffset;
        p.StartY = sv.VerticalOffset;
        p.Active = false;
        p.Samples.Clear();
        p.Blocked = StartsOnControl(e.OriginalSource as DependencyObject, sv);
        // 빠르게 미끄러지던 화면을 손가락으로 잡아 세운 것 — 그 아래 타일이 눌리지 않게
        p.Swallow = wasFast;
        if (p.Swallow) e.Handled = true;
    }

    /// <summary>슬라이더·글 상자·스크롤바처럼 자기가 끌기를 쓰는 컨트롤 위에서 시작했는지.</summary>
    private static bool StartsOnControl(DependencyObject? d, ScrollViewer sv)
    {
        for (; d is not null && !ReferenceEquals(d, sv); d = d is Visual ? VisualTreeHelper.GetParent(d) : LogicalTreeHelper.GetParent(d))
            if (d is Thumb or RangeBase or TextBoxBase or PasswordBox) return true;
        return false;
    }

    private static void OnTouchMove(object? sender, TouchEventArgs e)
    {
        if (sender is not ScrollViewer sv || sv.GetValue(PanProperty) is not Pan p || e.TouchDevice != p.Device || p.Blocked) return;
        var pos = e.GetTouchPoint(sv).Position;
        var (ax, ay) = Axes(sv);

        if (!p.Active)
        {
            if (p.Swallow) { e.Handled = true; }
            var d = pos - p.Start;
            var along = Math.Max(ax ? Math.Abs(d.X) : 0, ay ? Math.Abs(d.Y) : 0);
            if (along < PanThreshold) return;
            // 끄는 방향이 이 영역의 방향이 아니면(가로 영역에서 세로로 끎 등) 양보
            if (!ax && Math.Abs(d.X) > Math.Abs(d.Y) || !ay && Math.Abs(d.Y) > Math.Abs(d.X)) { p.Blocked = true; return; }
            // 안쪽에 같은 방향으로 끌 수 있는 영역이 있으면 그쪽이 받는다 (Preview는 바깥부터 온다)
            if (InnerCanPan(e.OriginalSource as DependencyObject, sv, Math.Abs(d.X) > Math.Abs(d.Y))) { p.Blocked = true; return; }

            p.Active = true;
            // 첫 10px 만큼 튀지 않게 시작점을 지금 자리로 옮긴다
            p.Start = pos;
            p.StartX = sv.HorizontalOffset;
            p.StartY = sv.VerticalOffset;
            p.TargetX = p.StartX;
            p.TargetY = p.StartY;
            // 손가락 아래 버튼의 눌림을 풀고(떼어도 클릭되지 않게) 이 손가락은 이 영역이 가져간다
            Mouse.Capture(null);
            sv.CaptureTouch(e.TouchDevice);
            StartMoving(sv, p);
        }

        e.Handled = true;
        p.TargetX = ax ? Math.Clamp(p.StartX - (pos.X - p.Start.X), 0, sv.ScrollableWidth) : sv.HorizontalOffset;
        p.TargetY = ay ? Math.Clamp(p.StartY - (pos.Y - p.Start.Y), 0, sv.ScrollableHeight) : sv.VerticalOffset;
        var now = TouchClock.Elapsed.TotalMilliseconds;
        p.Samples.Add((now, pos));
        while (p.Samples.Count > 2 && now - p.Samples[0].T > 100) p.Samples.RemoveAt(0);
    }

    private static bool InnerCanPan(DependencyObject? d, ScrollViewer outer, bool horizontal)
    {
        for (; d is not null && !ReferenceEquals(d, outer); d = d is Visual ? VisualTreeHelper.GetParent(d) : LogicalTreeHelper.GetParent(d))
            if (d is ScrollViewer inner && inner.GetValue(PanProperty) is Pan)
            {
                var (ax, ay) = Axes(inner);
                if (horizontal ? ax : ay) return true;
            }
        return false;
    }

    private static void OnTouchUp(object? sender, TouchEventArgs e)
    {
        if (sender is not ScrollViewer sv || sv.GetValue(PanProperty) is not Pan p || e.TouchDevice != p.Device) return;
        if (p.Active)
        {
            e.Handled = true; // 끌고 나서 뗀 손가락은 버튼을 누르지 않는다
            EndPan(sv, p, fling: true);
        }
        else
        {
            if (p.Swallow) e.Handled = true;
            p.Device = null;
        }
    }

    private static void EndPan(ScrollViewer sv, Pan p, bool fling)
    {
        p.Active = false;
        p.Device = null;
        if (sv.AreAnyTouchesCaptured) sv.ReleaseAllTouchCaptures();
        p.VX = p.VY = 0;
        var now = TouchClock.Elapsed.TotalMilliseconds;
        // 마지막 100ms의 평균 속도. 떼기 직전에 멈춰 있었으면(50ms) 관성 없음
        if (fling && p.Samples.Count >= 2 && now - p.Samples[^1].T < 50)
        {
            var (t0, p0) = p.Samples[0];
            var (t1, p1) = p.Samples[^1];
            if (t1 - t0 > 5)
            {
                var (ax, ay) = Axes(sv);
                if (ax) p.VX = -(p1.X - p0.X) / (t1 - t0);
                if (ay) p.VY = -(p1.Y - p0.Y) / (t1 - t0);
            }
        }
        p.Flinging = Math.Max(Math.Abs(p.VX), Math.Abs(p.VY)) >= MinFling && MotionCore.Enabled;
        if (p.Flinging)
        {
            // 남은 끌기 목표를 먼저 반영하고 거기서 미끄러진다
            p.TargetX = Math.Clamp(p.TargetX, 0, sv.ScrollableWidth);
            p.TargetY = Math.Clamp(p.TargetY, 0, sv.ScrollableHeight);
            StartMoving(sv, p);
        }
        else
        {
            sv.ScrollToHorizontalOffset(p.TargetX);
            sv.ScrollToVerticalOffset(p.TargetY);
            StopMoving(sv);
        }
    }

    private static void StopFling(ScrollViewer sv, Pan p)
    {
        if (!p.Flinging) return;
        p.Flinging = false;
        p.VX = p.VY = 0;
        StopMoving(sv);
    }

    /// <summary>휠·"보이게 하기"가 스크롤을 넘겨받을 때 관성을 멈춘다.</summary>
    private static void StopTouchMotion(ScrollViewer sv)
    {
        if (sv.GetValue(PanProperty) is Pan p) StopFling(sv, p);
    }

    private static void StartMoving(ScrollViewer sv, Pan p)
    {
        p.Last = TimeSpan.Zero;
        if (Moving.Contains(sv)) return;
        Moving.Add(sv);
        if (Moving.Count == 1) CompositionTarget.Rendering += OnTouchFrame;
    }

    private static void StopMoving(ScrollViewer sv)
    {
        if (!Moving.Remove(sv)) return;
        if (Moving.Count == 0) CompositionTarget.Rendering -= OnTouchFrame;
    }

    /// <summary>프레임마다: 끄는 중이면 손가락의 마지막 위치로, 관성 중이면 속도만큼 움직이고 감속한다.</summary>
    private static void OnTouchFrame(object? sender, EventArgs e)
    {
        var now = (e as RenderingEventArgs)?.RenderingTime ?? TimeSpan.FromMilliseconds(Environment.TickCount64);
        for (var i = Moving.Count - 1; i >= 0; i--)
        {
            var sv = Moving[i];
            if (sv.GetValue(PanProperty) is not Pan p || !sv.IsLoaded) { Moving.RemoveAt(i); continue; }
            if (p.Last == now) continue;
            var dt = p.Last == TimeSpan.Zero ? 8.3 : Math.Clamp((now - p.Last).TotalMilliseconds, 1, 50);
            p.Last = now;

            if (p.Active)
            {
                if (Math.Abs(sv.HorizontalOffset - p.TargetX) > 0.01) sv.ScrollToHorizontalOffset(p.TargetX);
                if (Math.Abs(sv.VerticalOffset - p.TargetY) > 0.01) sv.ScrollToVerticalOffset(p.TargetY);
                continue;
            }
            if (!p.Flinging) { Moving.RemoveAt(i); continue; }

            p.TargetX = Math.Clamp(p.TargetX + p.VX * dt, 0, sv.ScrollableWidth);
            p.TargetY = Math.Clamp(p.TargetY + p.VY * dt, 0, sv.ScrollableHeight);
            sv.ScrollToHorizontalOffset(p.TargetX);
            sv.ScrollToVerticalOffset(p.TargetY);
            var decay = Math.Exp(-dt / FlingDecayMs);
            p.VX *= decay;
            p.VY *= decay;
            // 끝에 닿은 방향은 멈춘다
            if (p.TargetX <= 0 || p.TargetX >= sv.ScrollableWidth) p.VX = 0;
            if (p.TargetY <= 0 || p.TargetY >= sv.ScrollableHeight) p.VY = 0;
            if (Math.Max(Math.Abs(p.VX), Math.Abs(p.VY)) >= 0.01) continue;
            p.Flinging = false;
            Moving.RemoveAt(i);
        }
        if (Moving.Count == 0) CompositionTarget.Rendering -= OnTouchFrame;
    }
}
