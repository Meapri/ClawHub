using System.Windows;
using System.Windows.Controls.Primitives;

namespace ClawApp.Motion;

/// <summary>
/// 메트로 '틸트' 누름 피드백. A(Accept)·클릭으로 실행되면 살짝 눌렸다 튀어 오른다.
/// 패드 A는 자동화 Invoke로 실행돼 IsPressed가 켜지지 않으므로 Click 이벤트에 건다.
/// Scale = 눌렸을 때 배율 (1이면 끔). 타일 0.94, 목록 행 0.98 정도.
/// </summary>
public static class Press
{
    public static readonly DependencyProperty ScaleProperty = DependencyProperty.RegisterAttached(
        "Scale", typeof(double), typeof(Press), new PropertyMetadata(1.0, OnScaleChanged));

    public static double GetScale(DependencyObject d) => (double)d.GetValue(ScaleProperty);
    public static void SetScale(DependencyObject d, double value) => d.SetValue(ScaleProperty, value);

    private static void OnScaleChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
    {
        if (d is not ButtonBase b) return;
        b.Click -= OnClick;
        if ((double)e.NewValue < 1) b.Click += OnClick;
    }

    private static void OnClick(object sender, RoutedEventArgs e)
    {
        if (sender is ButtonBase b && MotionCore.Enabled && b.IsVisible) MotionCore.Pulse(b, GetScale(b));
    }
}

/// <summary>
/// 포커스 테두리가 바깥에서 안으로 '찰칵' 조여 들어오며 나타난다 (패드로 움직일 때 어디로 갔는지 눈에 잘 띄게).
/// 템플릿 안의 테두리(Ring) 요소에 붙인다. 테두리에는 글자가 없으므로 배율 애니메이션이 선명도에 영향이 없다.
/// </summary>
public static class FocusPop
{
    public static readonly DependencyProperty IsEnabledProperty = DependencyProperty.RegisterAttached(
        "IsEnabled", typeof(bool), typeof(FocusPop), new PropertyMetadata(false, OnChanged));

    public static bool GetIsEnabled(DependencyObject d) => (bool)d.GetValue(IsEnabledProperty);
    public static void SetIsEnabled(DependencyObject d, bool value) => d.SetValue(IsEnabledProperty, value);

    private sealed record Hooked(UIElement Owner, DependencyPropertyChangedEventHandler Handler);

    private static readonly DependencyProperty HookedProperty =
        DependencyProperty.RegisterAttached("Hooked", typeof(Hooked), typeof(FocusPop));

    private static void OnChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
    {
        if (d is not FrameworkElement ring || e.NewValue is not true) return;
        ring.Loaded += (_, _) => Hook(ring);
        ring.Unloaded += (_, _) => Unhook(ring);
        if (ring.IsLoaded) Hook(ring);
    }

    private static void Hook(FrameworkElement ring)
    {
        if (ring.GetValue(HookedProperty) is Hooked || ring.TemplatedParent is not UIElement owner) return;
        DependencyPropertyChangedEventHandler handler = (_, a) =>
        {
            if (a.NewValue is true && MotionCore.Enabled) MotionCore.Snap(ring, 7, MotionCore.Ms(260));
        };
        owner.IsKeyboardFocusedChanged += handler;
        ring.SetValue(HookedProperty, new Hooked(owner, handler));
    }

    private static void Unhook(FrameworkElement ring)
    {
        if (ring.GetValue(HookedProperty) is not Hooked h) return;
        h.Owner.IsKeyboardFocusedChanged -= h.Handler;
        ring.ClearValue(HookedProperty);
    }
}
