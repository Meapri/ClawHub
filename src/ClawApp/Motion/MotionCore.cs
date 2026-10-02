using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Animation;

namespace ClawApp.Motion;

/// <summary>
/// 모션 공통 도구. 규칙:
///  · RenderTransform / Opacity만 움직인다 (레이아웃을 다시 계산하지 않음).
///  · 애니메이션이 끝나면 변환과 애니메이션을 걷어 낸다 → 정지 상태의 글자는 항상 정수 위치·배율 1이라 선명하다.
///  · Windows 설정에서 애니메이션 효과를 끄면(ClientAreaAnimation = false) 아무것도 움직이지 않는다.
/// </summary>
internal static class MotionCore
{
    /// <summary>모션을 쓸지. 원격 데스크톱 등 소프트웨어 렌더링(Tier 0)에서도 끈다.</summary>
    public static bool Enabled => SystemParameters.ClientAreaAnimation && RenderCapability.Tier >> 16 > 0;

    /// <summary>메트로 기본 곡선: 빠르게 출발해 길게 감속 (Windows Phone 'Exponential out').</summary>
    public static readonly IEasingFunction Decelerate = Frozen(new ExponentialEase { EasingMode = EasingMode.EaseOut, Exponent = 6 });
    public static readonly IEasingFunction Out = Frozen(new CubicEase { EasingMode = EasingMode.EaseOut });
    public static readonly IEasingFunction InOut = Frozen(new QuinticEase { EasingMode = EasingMode.EaseInOut });
    /// <summary>퇴장 곡선: 천천히 출발해 빨라지며 사라진다 (메트로 퇴장은 짧고 가속).</summary>
    public static readonly IEasingFunction Accelerate = Frozen(new CubicEase { EasingMode = EasingMode.EaseIn });

    private static IEasingFunction Frozen(EasingFunctionBase e)
    {
        e.Freeze();
        return e;
    }

    public static TimeSpan Ms(double ms) => TimeSpan.FromMilliseconds(ms);

    /// <summary>
    /// 처음부터 <paramref name="from"/> 값이 걸려 있다가 delay 뒤 <paramref name="to"/>로 움직이는 애니메이션.
    /// BeginTime 대신 앞쪽을 '정지 키프레임'으로 채워서, 기다리는 동안 원래 값이 잠깐 보이는 깜빡임이 없다.
    /// </summary>
    public static DoubleAnimationUsingKeyFrames Hold(double from, double to, TimeSpan delay, TimeSpan duration, IEasingFunction? ease)
    {
        var a = new DoubleAnimationUsingKeyFrames { Duration = new Duration(delay + duration) };
        a.KeyFrames.Add(new DiscreteDoubleKeyFrame(from, KeyTime.FromTimeSpan(TimeSpan.Zero)));
        if (delay > TimeSpan.Zero) a.KeyFrames.Add(new DiscreteDoubleKeyFrame(from, KeyTime.FromTimeSpan(delay)));
        a.KeyFrames.Add(new EasingDoubleKeyFrame(to, KeyTime.FromTimeSpan(delay + duration), ease));
        return a;
    }

    // 요소마다 "지금 내 모션"을 표시해, 늦게 끝난 이전 모션이 새 모션의 결과를 지우지 않게 한다.
    private static readonly DependencyProperty TokenProperty =
        DependencyProperty.RegisterAttached("MotionToken", typeof(object), typeof(MotionCore));

    private static bool CanOwnTransform(UIElement el)
    {
        var local = el.ReadLocalValue(UIElement.RenderTransformProperty);
        return local == DependencyProperty.UnsetValue || ReferenceEquals(local, el.GetValue(TokenProperty));
    }

    /// <summary>
    /// (dx, dy)만큼 떨어진 곳에서 제자리로 미끄러져 들어오며 나타난다 (메트로 continuum/turnstile의 2D 버전).
    /// </summary>
    public static void SlideIn(UIElement el, double dx, double dy, TimeSpan delay, TimeSpan duration, double fadeRatio = 0.6)
    {
        var token = new TranslateTransform(dx, dy);
        var ownTransform = CanOwnTransform(el);
        el.SetValue(TokenProperty, token);
        if (ownTransform) el.RenderTransform = token;

        var target = (double)el.GetAnimationBaseValue(UIElement.OpacityProperty);
        var fade = Hold(0, target, delay, TimeSpan.FromTicks((long)(duration.Ticks * fadeRatio)), Out);
        var move = Hold(1, 0, delay, duration, Decelerate);
        move.Completed += (_, _) =>
        {
            if (!ReferenceEquals(el.GetValue(TokenProperty), token)) return;
            el.ClearValue(TokenProperty);
            if (ReferenceEquals(el.ReadLocalValue(UIElement.RenderTransformProperty), token)) el.ClearValue(UIElement.RenderTransformProperty);
            el.BeginAnimation(UIElement.OpacityProperty, null);
        };

        el.BeginAnimation(UIElement.OpacityProperty, fade);
        // 이동량(1→0)을 X/Y에 곱하는 대신 각 축을 따로 움직인다
        if (ownTransform)
        {
            if (dx != 0) token.BeginAnimation(TranslateTransform.XProperty, Hold(dx, 0, delay, duration, Decelerate));
            if (dy != 0) token.BeginAnimation(TranslateTransform.YProperty, Hold(dy, 0, delay, duration, Decelerate));
        }
        // 완료 시점 감지용 (값은 쓰지 않음)
        token.BeginAnimation(ProgressProperty, move);
    }

    /// <summary>
    /// (dx, dy) 쪽으로 미끄러지며 사라진다. 끝나면 변환·애니메이션을 걷고 <paramref name="done"/>을 부른다
    /// (그 사이 다른 모션이 시작되면 부르지 않는다 — 다시 열린 경우).
    /// </summary>
    public static void SlideOut(UIElement el, double dx, double dy, TimeSpan duration, Action done)
    {
        var token = new TranslateTransform();
        var ownTransform = CanOwnTransform(el);
        el.SetValue(TokenProperty, token);
        if (ownTransform) el.RenderTransform = token;

        var from = el.Opacity; // 등장 중이었다면 지금 보이는 만큼에서
        var move = Hold(0, 1, TimeSpan.Zero, duration, Accelerate);
        move.Completed += (_, _) =>
        {
            if (!ReferenceEquals(el.GetValue(TokenProperty), token)) return;
            done();
            Cancel(el);
        };

        el.BeginAnimation(UIElement.OpacityProperty, Hold(from, 0, TimeSpan.Zero, duration, Accelerate));
        if (ownTransform)
        {
            if (dx != 0) token.BeginAnimation(TranslateTransform.XProperty, Hold(0, dx, TimeSpan.Zero, duration, Accelerate));
            if (dy != 0) token.BeginAnimation(TranslateTransform.YProperty, Hold(0, dy, TimeSpan.Zero, duration, Accelerate));
        }
        token.BeginAnimation(ProgressProperty, move);
    }

    /// <summary>진행 중인 모션을 멈추고 정지 상태(변환 없음·원래 불투명도)로 되돌린다.</summary>
    public static void Cancel(UIElement el)
    {
        if (el.GetValue(TokenProperty) is { } token && ReferenceEquals(el.ReadLocalValue(UIElement.RenderTransformProperty), token))
            el.ClearValue(UIElement.RenderTransformProperty);
        el.ClearValue(TokenProperty);
        el.BeginAnimation(UIElement.OpacityProperty, null);
    }

    /// <summary>완료 이벤트만 받기 위한 더미 속성 (TranslateTransform에 붙여서 돌린다).</summary>
    private static readonly DependencyProperty ProgressProperty =
        DependencyProperty.RegisterAttached("Progress", typeof(double), typeof(MotionCore), new PropertyMetadata(0.0));

    /// <summary>누름 피드백: 살짝 눌렸다가(scale) 되돌아온다. 끝나면 변환을 걷어 배율 1로.</summary>
    public static void Pulse(UIElement el, double depth)
    {
        if (!CanOwnTransform(el)) return;
        el.BeginAnimation(UIElement.OpacityProperty, null); // 진입 중이었다면 바로 다 보이게
        var token = new ScaleTransform(1, 1);
        el.SetValue(TokenProperty, token);
        el.RenderTransformOrigin = new Point(0.5, 0.5);
        el.RenderTransform = token;

        DoubleAnimationUsingKeyFrames Anim()
        {
            var a = new DoubleAnimationUsingKeyFrames { Duration = new Duration(Ms(300)) };
            a.KeyFrames.Add(new EasingDoubleKeyFrame(depth, KeyTime.FromTimeSpan(Ms(70)), Out));
            a.KeyFrames.Add(new EasingDoubleKeyFrame(1, KeyTime.FromTimeSpan(Ms(300)), Decelerate));
            return a;
        }

        var y = Anim();
        y.Completed += (_, _) =>
        {
            if (!ReferenceEquals(el.GetValue(TokenProperty), token)) return;
            el.ClearValue(TokenProperty);
            if (ReferenceEquals(el.ReadLocalValue(UIElement.RenderTransformProperty), token)) el.ClearValue(UIElement.RenderTransformProperty);
        };
        token.BeginAnimation(ScaleTransform.ScaleXProperty, Anim());
        token.BeginAnimation(ScaleTransform.ScaleYProperty, y);
    }

    /// <summary>글자 없는 장식(포커스 테두리 등)을 바깥에서 안쪽으로 '찰칵' 조이며 나타나게.</summary>
    public static void Snap(FrameworkElement el, double grow, TimeSpan duration)
    {
        double w = el.ActualWidth, h = el.ActualHeight;
        if (w < 1 || h < 1) return;
        var token = new ScaleTransform((w + grow * 2) / w, (h + grow * 2) / h);
        el.SetValue(TokenProperty, token);
        el.RenderTransformOrigin = new Point(0.5, 0.5);
        el.RenderTransform = token;

        var sy = Hold(token.ScaleY, 1, TimeSpan.Zero, duration, Decelerate);
        sy.Completed += (_, _) =>
        {
            if (!ReferenceEquals(el.GetValue(TokenProperty), token)) return;
            el.ClearValue(TokenProperty);
            el.ClearValue(UIElement.RenderTransformProperty);
            el.BeginAnimation(UIElement.OpacityProperty, null);
        };
        token.BeginAnimation(ScaleTransform.ScaleXProperty, Hold(token.ScaleX, 1, TimeSpan.Zero, duration, Decelerate));
        token.BeginAnimation(ScaleTransform.ScaleYProperty, sy);
        el.BeginAnimation(UIElement.OpacityProperty, Hold(0, 1, TimeSpan.Zero, Ms(duration.TotalMilliseconds * 0.5), Out));
    }
}
