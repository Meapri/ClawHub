using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Shapes;

namespace ClawApp.Motion;

/// <summary>
/// Windows Phone식 '흐르는 점' 진행 표시. 점 다섯 개가 왼쪽에서 빠르게 들어와 가운데서 천천히 지나고
/// 다시 빠르게 빠져나간다. 보일 때만 돌고, 모션을 끈 환경에서는 가운데에 멈춰 있다.
/// </summary>
public sealed class MetroDots : Canvas
{
    private const int Count = 5;
    private const double Dot = 6, Gap = 10;
    private readonly Rectangle[] _dots = new Rectangle[Count];

    public MetroDots()
    {
        Height = Dot;
        ClipToBounds = true;
        IsHitTestVisible = false;
        for (var i = 0; i < Count; i++)
        {
            var r = new Rectangle { Width = Dot, Height = Dot, RenderTransform = new TranslateTransform() };
            r.SetResourceReference(Shape.FillProperty, "Accent");
            _dots[i] = r;
            Children.Add(r);
        }
        IsVisibleChanged += (_, _) => Restart();
        SizeChanged += (_, _) => Restart();
    }

    private void Restart()
    {
        var w = ActualWidth;
        for (var i = 0; i < Count; i++)
        {
            var t = (TranslateTransform)_dots[i].RenderTransform;
            t.BeginAnimation(TranslateTransform.XProperty, null);
            _dots[i].BeginAnimation(OpacityProperty, null);
            // 정지 상태: 가운데에 나란히
            t.X = Math.Round(w / 2 + (i - Count / 2.0) * Gap);
        }
        if (!IsVisible || w < 1 || !MotionCore.Enabled) return;

        var cycle = MotionCore.Ms(2600);
        for (var i = 0; i < Count; i++)
        {
            var t = (TranslateTransform)_dots[i].RenderTransform;
            // 뒤쪽 점일수록 늦게 출발 → 가운데서 촘촘히 모였다 흩어짐
            var start = MotionCore.Ms(i * 150);
            double center = w / 2 + (Count / 2.0 - i) * Gap;
            var x = new DoubleAnimationUsingKeyFrames { Duration = cycle, RepeatBehavior = RepeatBehavior.Forever };
            x.KeyFrames.Add(new DiscreteDoubleKeyFrame(-Dot * 2, KeyTime.FromTimeSpan(TimeSpan.Zero)));
            x.KeyFrames.Add(new DiscreteDoubleKeyFrame(-Dot * 2, KeyTime.FromTimeSpan(start)));
            x.KeyFrames.Add(new EasingDoubleKeyFrame(center - w * 0.06, KeyTime.FromTimeSpan(start + MotionCore.Ms(550)), MotionCore.Decelerate));
            x.KeyFrames.Add(new LinearDoubleKeyFrame(center + w * 0.06, KeyTime.FromTimeSpan(start + MotionCore.Ms(1250))));
            x.KeyFrames.Add(new EasingDoubleKeyFrame(w + Dot * 2, KeyTime.FromTimeSpan(start + MotionCore.Ms(1800)),
                new ExponentialEase { EasingMode = EasingMode.EaseIn, Exponent = 6 }));
            t.BeginAnimation(TranslateTransform.XProperty, x);
        }
    }
}
