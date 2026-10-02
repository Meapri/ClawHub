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
        // 끝을 넘어 당긴 양 (스크롤 좌표, 처음보다 앞 = 음수, 끝보다 뒤 = 양수). 화면에는 고무줄처럼 줄여서 보인다
        public double OverX, OverY;
        public Spring? SpringX, SpringY;
        public TranslateTransform? Shift;
    }

    /// <summary>
    /// 끝에서 되돌아오는 임계 감쇠 스프링: x(t) = (x0 + (v0 + ωx0)t)·e^(−ωt). 놓았을 때(v0 = 0)와
    /// 관성으로 끝에 부딪혔을 때(x0 = 0, v0 = 남은 속도 → 살짝 넘어갔다 돌아옴) 둘 다 이것 하나로.
    /// </summary>
    private sealed record Spring(double X0, double V0, TimeSpan Start)
    {
        public double At(TimeSpan now)
        {
            var t = Math.Max(0, (now - Start).TotalMilliseconds);
            return (X0 + (V0 + SpringOmega * X0) * t) * Math.Exp(-SpringOmega * t);
        }

        public bool Done(TimeSpan now) => (now - Start).TotalMilliseconds > 120 && Math.Abs(At(now)) < 0.3;
    }

    /// <summary>스프링 세기 (1/ms). 0.0125 ≈ 놓은 뒤 0.4초 안에 제자리 (iOS 느낌).</summary>
    private const double SpringOmega = 0.0125;

    /// <summary>iOS 고무줄: 당긴 거리 x에 대해 화면에는 (1 − 1/(0.55x/d + 1))·d 만큼만 (d = 보이는 영역 크기).</summary>
    private static double Rubber(double x, double d) =>
        d <= 0 ? 0 : Math.Sign(x) * (1 - 1 / (Math.Abs(x) * 0.55 / d + 1)) * d;

    /// <summary><see cref="Rubber"/>의 역 (보이는 만큼 늘어나 있을 때 손가락이 얼마나 당긴 것인지).</summary>
    private static double Unrubber(double y, double d)
    {
        if (d <= 0) return 0;
        var r = Math.Min(Math.Abs(y) / d, 0.95);
        return Math.Sign(y) * d / 0.55 * (1 / (1 - r) - 1);
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
        FreezeSprings(p); // 되돌아오던 고무줄을 손가락이 그 자리에서 잡는다
        StopFling(sv, p);
        Stop(sv);
        p.Device = e.TouchDevice;
        p.Start = e.GetTouchPoint(sv).Position;
        (p.StartX, p.StartY) = RawStart(sv, p);
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
            (p.StartX, p.StartY) = RawStart(sv, p);
            p.TargetX = sv.HorizontalOffset;
            p.TargetY = sv.VerticalOffset;
            // 손가락 아래 버튼의 눌림을 풀고(떼어도 클릭되지 않게) 이 손가락은 이 영역이 가져간다
            Mouse.Capture(null);
            sv.CaptureTouch(e.TouchDevice);
            StartMoving(sv, p);
        }

        e.Handled = true;
        // 끝을 넘어 당기면 스크롤은 끝에 두고 넘친 만큼을 고무줄처럼 줄여 내용물을 민다 (iOS)
        if (ax)
        {
            var raw = p.StartX - (pos.X - p.Start.X);
            p.TargetX = Math.Clamp(raw, 0, sv.ScrollableWidth);
            p.OverX = Rubber(raw - p.TargetX, sv.ViewportWidth);
        }
        else p.TargetX = sv.HorizontalOffset;
        if (ay)
        {
            var raw = p.StartY - (pos.Y - p.Start.Y);
            p.TargetY = Math.Clamp(raw, 0, sv.ScrollableHeight);
            p.OverY = Rubber(raw - p.TargetY, sv.ViewportHeight);
        }
        else p.TargetY = sv.VerticalOffset;
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
            ReleaseSprings(sv, p); // 늘어난 채 잡았다가 끌지 않고 뗐으면 다시 되돌아간다
        }
    }

    /// <summary>
    /// 지금 화면 상태에 해당하는 "손가락 기준" 스크롤 위치. 늘어나 있으면 끝 + 그만큼 당긴 거리 —
    /// 늘어난 채로 다시 잡아 끌어도 튀지 않고 이어진다.
    /// </summary>
    private static (double X, double Y) RawStart(ScrollViewer sv, Pan p) =>
        (sv.HorizontalOffset + Unrubber(p.OverX, sv.ViewportWidth), sv.VerticalOffset + Unrubber(p.OverY, sv.ViewportHeight));

    private static void FreezeSprings(Pan p)
    {
        var now = TouchClock.Elapsed;
        if (p.SpringX is { } x) p.OverX = x.At(now);
        if (p.SpringY is { } y) p.OverY = y.At(now);
        p.SpringX = p.SpringY = null;
    }

    /// <summary>늘어난 축은 제자리로 되돌아가기 시작한다.</summary>
    private static void ReleaseSprings(ScrollViewer sv, Pan p)
    {
        var now = TouchClock.Elapsed;
        if (p.OverX != 0) p.SpringX = new Spring(p.OverX, 0, now);
        if (p.OverY != 0) p.SpringY = new Spring(p.OverY, 0, now);
        if (p.SpringX is not null || p.SpringY is not null) StartMoving(sv, p);
    }

    /// <summary>늘어난 만큼 내용물을 옮긴다 (스크롤 범위 밖이라 ScrollViewer가 아니라 내용물의 RenderTransform으로).</summary>
    private static void ApplyShift(ScrollViewer sv, Pan p)
    {
        if (sv.Content is not UIElement content) return;
        var local = content.ReadLocalValue(UIElement.RenderTransformProperty);
        if (p.OverX == 0 && p.OverY == 0)
        {
            // 멈추면 변환을 걷어 낸다 — 정지 상태의 글자는 정수 위치라 선명하다
            if (p.Shift is not null && ReferenceEquals(local, p.Shift)) content.ClearValue(UIElement.RenderTransformProperty);
            p.Shift = null;
            return;
        }
        if (p.Shift is null || !ReferenceEquals(local, p.Shift))
        {
            // 다른 모션이 내용물 변환을 쓰는 중이면 늘이기만 생략 (스크롤 자체는 그대로)
            if (local != DependencyProperty.UnsetValue) return;
            p.Shift = new TranslateTransform();
            content.RenderTransform = p.Shift;
        }
        p.Shift.X = -p.OverX;
        p.Shift.Y = -p.OverY;
    }

    /// <summary>관성으로 끝에 부딪힌 속도 → 살짝 넘어갔다 돌아오는 튕김. 너무 세게 넘어가지 않게 보이는 크기의 12%까지.</summary>
    private static Spring Bounce(double v, double viewport) =>
        new(0, Math.Sign(v) * Math.Min(Math.Abs(v), viewport * 0.12 * SpringOmega * Math.E), TouchClock.Elapsed);

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
        // 끝을 넘어 당긴 채 놓은 축은 관성 없이 제자리로 되돌아간다
        if (p.OverX != 0) p.VX = 0;
        if (p.OverY != 0) p.VY = 0;
        if (!MotionCore.Enabled) p.OverX = p.OverY = 0;
        p.Flinging = Math.Max(Math.Abs(p.VX), Math.Abs(p.VY)) >= MinFling && MotionCore.Enabled;
        p.TargetX = Math.Clamp(p.TargetX, 0, sv.ScrollableWidth);
        p.TargetY = Math.Clamp(p.TargetY, 0, sv.ScrollableHeight);
        sv.ScrollToHorizontalOffset(p.TargetX);
        sv.ScrollToVerticalOffset(p.TargetY);
        ApplyShift(sv, p);
        ReleaseSprings(sv, p);
        if (p.Flinging) StartMoving(sv, p);
        else if (p.SpringX is null && p.SpringY is null) StopMoving(sv);
    }

    private static void StopFling(ScrollViewer sv, Pan p)
    {
        if (!p.Flinging) return;
        p.Flinging = false;
        p.VX = p.VY = 0;
        if (p.SpringX is null && p.SpringY is null) StopMoving(sv); // 튕겨 돌아오는 중이면 그것은 마저
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
            if (sv.GetValue(PanProperty) is not Pan p || !sv.IsLoaded)
            {
                if (sv.GetValue(PanProperty) is Pan gone) { gone.OverX = gone.OverY = 0; gone.SpringX = gone.SpringY = null; ApplyShift(sv, gone); }
                Moving.RemoveAt(i);
                continue;
            }
            if (p.Last == now) continue;
            var dt = p.Last == TimeSpan.Zero ? 8.3 : Math.Clamp((now - p.Last).TotalMilliseconds, 1, 50);
            p.Last = now;

            if (p.Active)
            {
                if (Math.Abs(sv.HorizontalOffset - p.TargetX) > 0.01) sv.ScrollToHorizontalOffset(p.TargetX);
                if (Math.Abs(sv.VerticalOffset - p.TargetY) > 0.01) sv.ScrollToVerticalOffset(p.TargetY);
                ApplyShift(sv, p);
                continue;
            }

            if (p.Flinging)
            {
                p.TargetX = Math.Clamp(p.TargetX + p.VX * dt, 0, sv.ScrollableWidth);
                p.TargetY = Math.Clamp(p.TargetY + p.VY * dt, 0, sv.ScrollableHeight);
                sv.ScrollToHorizontalOffset(p.TargetX);
                sv.ScrollToVerticalOffset(p.TargetY);
                var decay = Math.Exp(-dt / FlingDecayMs);
                p.VX *= decay;
                p.VY *= decay;
                // 끝에 부딪힌 방향: 남은 속도로 살짝 넘어갔다 돌아온다 (iOS)
                if (p.VX < 0 && p.TargetX <= 0 || p.VX > 0 && p.TargetX >= sv.ScrollableWidth) { p.SpringX = Bounce(p.VX, sv.ViewportWidth); p.VX = 0; }
                if (p.VY < 0 && p.TargetY <= 0 || p.VY > 0 && p.TargetY >= sv.ScrollableHeight) { p.SpringY = Bounce(p.VY, sv.ViewportHeight); p.VY = 0; }
                if (Math.Max(Math.Abs(p.VX), Math.Abs(p.VY)) < 0.01) p.Flinging = false;
            }

            var t = TouchClock.Elapsed;
            if (p.SpringX is { } sx) { p.OverX = sx.Done(t) ? 0 : sx.At(t); if (p.OverX == 0) p.SpringX = null; }
            if (p.SpringY is { } sy) { p.OverY = sy.Done(t) ? 0 : sy.At(t); if (p.OverY == 0) p.SpringY = null; }
            ApplyShift(sv, p);
            if (p.Flinging || p.SpringX is not null || p.SpringY is not null) continue;
            Moving.RemoveAt(i);
        }
        if (Moving.Count == 0) CompositionTarget.Rendering -= OnTouchFrame;
    }
}
