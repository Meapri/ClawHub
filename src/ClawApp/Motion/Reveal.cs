using System.Windows;

namespace ClawApp.Motion;

public enum RevealKind
{
    /// <summary>위에 뜨는 층(패널·대화 상자·알림·덮개): 닫힐 때 Exit 쪽으로 미끄러지며 사라진 뒤 접힌다.</summary>
    Overlay,
    /// <summary>본문 안에서 펼쳐지는 부분(설정의 하위 항목 등): 나타날 때만 살짝 내려오며 보이고, 숨길 때는 바로 접는다
    /// (늦게 접으면 아래 항목이 한 박자 뒤에 튄다).</summary>
    Inline,
}

/// <summary>
/// Visibility 대신 쓰는 표시 스위치: <c>m:Reveal.IsShown="{Binding …}"</c>.
/// Visibility 바인딩은 값이 바뀌는 순간 접혀 버려서 닫힐 때 모션을 넣을 수 없다. 이것은 접기 전에 퇴장 모션을 돌린다.
/// · 퇴장 중에는 누를 수 없다 (IsHitTestVisible 끔). 포커스·입력 경로는 ViewModel 상태를 따르므로 영향 없음.
/// · 나타날 때: 요소에 Entrance가 붙어 있으면 그것이 재생되고, 없으면 짧게 페이드 인.
/// · 퇴장 중에 다시 열리면 그 자리에서 다시 나타난다.
/// · 상위 요소가 숨었다 보일 때(설정 분류 전환 등)는 재생하지 않는다 — 자기 값이 바뀔 때만.
/// </summary>
public static class Reveal
{
    /// <summary>null = 아직 정해지지 않음 (처음 값이 false여도 접히도록 기본값을 bool이 아닌 null로).</summary>
    public static readonly DependencyProperty IsShownProperty = DependencyProperty.RegisterAttached(
        "IsShown", typeof(bool?), typeof(Reveal), new PropertyMetadata(null, OnShownChanged));

    public static bool? GetIsShown(DependencyObject d) => (bool?)d.GetValue(IsShownProperty);
    public static void SetIsShown(DependencyObject d, bool? value) => d.SetValue(IsShownProperty, value);

    public static readonly DependencyProperty KindProperty = DependencyProperty.RegisterAttached(
        "Kind", typeof(RevealKind), typeof(Reveal), new PropertyMetadata(RevealKind.Overlay));

    public static RevealKind GetKind(DependencyObject d) => (RevealKind)d.GetValue(KindProperty);
    public static void SetKind(DependencyObject d, RevealKind value) => d.SetValue(KindProperty, value);

    /// <summary>퇴장할 때 미끄러져 가는 거리 (px). 0이면 제자리에서 사라진다.</summary>
    public static readonly DependencyProperty ExitXProperty = DependencyProperty.RegisterAttached(
        "ExitX", typeof(double), typeof(Reveal), new PropertyMetadata(0.0));

    public static double GetExitX(DependencyObject d) => (double)d.GetValue(ExitXProperty);
    public static void SetExitX(DependencyObject d, double value) => d.SetValue(ExitXProperty, value);

    public static readonly DependencyProperty ExitYProperty = DependencyProperty.RegisterAttached(
        "ExitY", typeof(double), typeof(Reveal), new PropertyMetadata(0.0));

    public static double GetExitY(DependencyObject d) => (double)d.GetValue(ExitYProperty);
    public static void SetExitY(DependencyObject d, double value) => d.SetValue(ExitYProperty, value);

    /// <summary>Entrance가 없는 Overlay가 나타날 때 미끄러져 오는 거리 (px, 세로). 0이면 페이드만.</summary>
    public static readonly DependencyProperty EnterYProperty = DependencyProperty.RegisterAttached(
        "EnterY", typeof(double), typeof(Reveal), new PropertyMetadata(0.0));

    public static double GetEnterY(DependencyObject d) => (double)d.GetValue(EnterYProperty);
    public static void SetEnterY(DependencyObject d, double value) => d.SetValue(EnterYProperty, value);

    /// <summary>퇴장 시간 (ms). 등장(약 400ms)보다 훨씬 짧게 — 닫는 동작은 바로 끝난 것처럼 느껴져야 한다.</summary>
    public static readonly DependencyProperty ExitMsProperty = DependencyProperty.RegisterAttached(
        "ExitMs", typeof(double), typeof(Reveal), new PropertyMetadata(170.0));

    public static double GetExitMs(DependencyObject d) => (double)d.GetValue(ExitMsProperty);
    public static void SetExitMs(DependencyObject d, double value) => d.SetValue(ExitMsProperty, value);

    private sealed class State
    {
        public bool Exiting;
        public bool HitTest = true;
    }

    private static readonly DependencyProperty StateProperty =
        DependencyProperty.RegisterAttached("RevealState", typeof(State), typeof(Reveal));

    private static void OnShownChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
    {
        if (d is not UIElement el) return;
        if (el.GetValue(StateProperty) is not State s)
        {
            s = new State();
            el.SetValue(StateProperty, s);
        }
        var animate = MotionCore.Enabled && e.OldValue is not null && el is FrameworkElement { IsLoaded: true };

        if (e.NewValue is true) Show(el, s, animate);
        else Hide(el, s, animate);
    }

    private static void Show(UIElement el, State s, bool animate)
    {
        if (s.Exiting)
        {
            // 사라지던 중 다시 열림: 아직 보이는 상태라 Entrance가 다시 돌지 않는다 → 여기서 바로 되돌린다
            s.Exiting = false;
            el.IsHitTestVisible = s.HitTest;
            MotionCore.Cancel(el);
            if (animate) MotionCore.SlideIn(el, GetExitX(el) * 0.5, GetExitY(el) * 0.5, TimeSpan.Zero, MotionCore.Ms(260), fadeRatio: 0.5);
            return;
        }
        var wasHidden = el.Visibility != Visibility.Visible;
        el.Visibility = Visibility.Visible;
        if (!animate || !wasHidden) return;

        // 상위가 보이는 상태에서 자기만 나타날 때. Entrance가 붙은 요소는 IsVisibleChanged로 Entrance가 맡는다
        if (Entrance.GetStagger(el) != StaggerMode.None) return;
        if (GetKind(el) == RevealKind.Inline)
        {
            if (el.IsVisible) MotionCore.SlideIn(el, 0, -12, TimeSpan.Zero, MotionCore.Ms(320), fadeRatio: 0.6);
        }
        else if (el.IsVisible && GetEnterY(el) is var dy and not 0)
            MotionCore.SlideIn(el, 0, dy, TimeSpan.Zero, MotionCore.Ms(360), fadeRatio: 0.5);
        else if (el.IsVisible) MotionCore.SlideIn(el, 0, 0, TimeSpan.Zero, MotionCore.Ms(220), fadeRatio: 1);
    }

    private static void Hide(UIElement el, State s, bool animate)
    {
        if (!animate || !el.IsVisible || GetKind(el) == RevealKind.Inline)
        {
            MotionCore.Cancel(el);
            if (s.Exiting) el.IsHitTestVisible = s.HitTest;
            s.Exiting = false;
            el.Visibility = Visibility.Collapsed;
            return;
        }
        // 이미 사라지는 중이면 처음부터 다시 (다른 모션이 끼어들어 완료를 놓쳤어도 반드시 접히게)
        if (!s.Exiting) s.HitTest = el.IsHitTestVisible;
        s.Exiting = true;
        el.IsHitTestVisible = false;
        MotionCore.SlideOut(el, GetExitX(el), GetExitY(el), MotionCore.Ms(GetExitMs(el)), () =>
        {
            s.Exiting = false;
            el.IsHitTestVisible = s.HitTest;
            el.Visibility = Visibility.Collapsed;
        });
    }
}
