using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Animation;

namespace ClawApp.Motion;

/// <summary>
/// 상태가 바뀔 때 색 층을 짧게 교차 페이드한다 (칩·빠른 설정 타일이 켜질 때 강조색이 '탁' 바뀌지 않게).
/// 템플릿 안 글자 없는 색 층에 붙인다: <c>m:Fade.On="{Binding IsChecked, RelativeSource={RelativeSource TemplatedParent}}"</c>.
/// 켜짐 = Opacity 1, 꺼짐 = 0. 화면에 보이지 않을 때(처음 만들어질 때 포함)는 바로 그 값으로.
/// </summary>
public static class Fade
{
    public static readonly DependencyProperty OnProperty = DependencyProperty.RegisterAttached(
        "On", typeof(bool), typeof(Fade), new PropertyMetadata(false, OnChanged));

    public static bool GetOn(DependencyObject d) => (bool)d.GetValue(OnProperty);
    public static void SetOn(DependencyObject d, bool value) => d.SetValue(OnProperty, value);

    private static void OnChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
    {
        if (d is not UIElement el) return;
        var to = (bool)e.NewValue ? 1.0 : 0.0;
        // 템플릿이 막 만들어질 때(아직 Loaded 전)는 처음 값이므로 움직이지 않는다
        if (!MotionCore.Enabled || el is not FrameworkElement { IsLoaded: true } || !el.IsVisible)
        {
            el.BeginAnimation(UIElement.OpacityProperty, null);
            el.Opacity = to;
            return;
        }
        var from = el.Opacity; // 지금 보이는 값 (페이드 중이었다면 그 중간)
        el.Opacity = to; // 애니메이션을 걷어도 이 값으로 남는다
        var a = new DoubleAnimation(from, to, new Duration(MotionCore.Ms(to > 0 ? 140 : 200))) { EasingFunction = MotionCore.Out };
        a.Completed += (_, _) =>
        {
            if (el.Opacity == to) el.BeginAnimation(UIElement.OpacityProperty, null);
        };
        el.BeginAnimation(UIElement.OpacityProperty, a);
    }
}

/// <summary>
/// 토글 스위치 손잡이가 반대편으로 '미끄러진다'. 위치(왼쪽/오른쪽 정렬)는 템플릿 트리거가 바꾸고,
/// 여기서는 바뀌기 직전 자리만큼 되돌려 놓았다가 0으로 감속시킨다. 손잡이는 글자가 없어 변환이 선명도에 영향이 없다.
/// <c>m:Knob.On="{Binding IsChecked, …}" m:Knob.Travel="24"</c>
/// </summary>
public static class Knob
{
    public static readonly DependencyProperty OnProperty = DependencyProperty.RegisterAttached(
        "On", typeof(bool), typeof(Knob), new PropertyMetadata(false, OnChanged));

    public static bool GetOn(DependencyObject d) => (bool)d.GetValue(OnProperty);
    public static void SetOn(DependencyObject d, bool value) => d.SetValue(OnProperty, value);

    /// <summary>꺼짐 자리에서 켜짐 자리까지 거리 (px).</summary>
    public static readonly DependencyProperty TravelProperty = DependencyProperty.RegisterAttached(
        "Travel", typeof(double), typeof(Knob), new PropertyMetadata(0.0));

    public static double GetTravel(DependencyObject d) => (double)d.GetValue(TravelProperty);
    public static void SetTravel(DependencyObject d, double value) => d.SetValue(TravelProperty, value);

    private static void OnChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
    {
        if (d is not FrameworkElement { IsLoaded: true, IsVisible: true } el || !MotionCore.Enabled) return;
        var from = GetTravel(el) * ((bool)e.NewValue ? -1 : 1);
        var t = el.RenderTransform as TranslateTransform;
        if (t is null || t.IsFrozen) el.RenderTransform = t = new TranslateTransform();
        else from += t.X; // 왔다 갔다 빠르게 누르면 지금 보이는 자리에서 이어서
        t.BeginAnimation(TranslateTransform.XProperty, MotionCore.Hold(from, 0, TimeSpan.Zero, MotionCore.Ms(260), MotionCore.Decelerate));
    }
}

/// <summary>
/// 선택 표시(설정 분류의 강조색 막대 등)가 보이게 될 때 가운데에서 위아래로 자라난다.
/// 글자 없는 장식에만 붙인다. <c>m:Grow.OnShow="True"</c>
/// </summary>
public static class Grow
{
    public static readonly DependencyProperty OnShowProperty = DependencyProperty.RegisterAttached(
        "OnShow", typeof(bool), typeof(Grow), new PropertyMetadata(false, OnChanged));

    public static bool GetOnShow(DependencyObject d) => (bool)d.GetValue(OnShowProperty);
    public static void SetOnShow(DependencyObject d, bool value) => d.SetValue(OnShowProperty, value);

    private static void OnChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
    {
        if (d is not UIElement el || e.NewValue is not true) return;
        el.IsVisibleChanged += (_, a) =>
        {
            if (a.NewValue is not true || !MotionCore.Enabled) return;
            var s = new ScaleTransform(1, 0);
            el.RenderTransformOrigin = new Point(0.5, 0.5);
            el.RenderTransform = s;
            var grow = MotionCore.Hold(0, 1, TimeSpan.Zero, MotionCore.Ms(300), MotionCore.Decelerate);
            grow.Completed += (_, _) =>
            {
                if (ReferenceEquals(el.RenderTransform, s)) el.ClearValue(UIElement.RenderTransformProperty);
            };
            s.BeginAnimation(ScaleTransform.ScaleYProperty, grow);
        };
    }
}
