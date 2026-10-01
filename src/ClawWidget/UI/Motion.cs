using System;
using System.Numerics;
using Windows.UI.Composition;
using Windows.UI.Xaml;
using Windows.UI.Xaml.Hosting;

namespace ClawWidget.UI
{
    /// <summary>Windows 11 느낌의 작은 움직임 — Visibility가 바뀔 때 암묵적(implicit) 페이드 + 살짝 미끄러짐.</summary>
    public static class Motion
    {
        public static void EnableShowHide(UIElement element, float offsetY = -6)
        {
            try
            {
                var visual = ElementCompositionPreview.GetElementVisual(element);
                var c = visual.Compositor;
                var ease = c.CreateCubicBezierEasingFunction(new Vector2(0.1f, 0.9f), new Vector2(0.2f, 1f));

                var fadeIn = c.CreateScalarKeyFrameAnimation();
                fadeIn.Target = "Opacity";
                fadeIn.InsertKeyFrame(0, 0);
                fadeIn.InsertKeyFrame(1, 1, ease);
                fadeIn.Duration = TimeSpan.FromMilliseconds(220);

                var slideIn = c.CreateVector3KeyFrameAnimation();
                slideIn.Target = "Translation";
                slideIn.InsertKeyFrame(0, new Vector3(0, offsetY, 0));
                slideIn.InsertKeyFrame(1, Vector3.Zero, ease);
                slideIn.Duration = TimeSpan.FromMilliseconds(260);

                var show = c.CreateAnimationGroup();
                show.Add(fadeIn);
                show.Add(slideIn);

                var fadeOut = c.CreateScalarKeyFrameAnimation();
                fadeOut.Target = "Opacity";
                fadeOut.InsertKeyFrame(0, 1);
                fadeOut.InsertKeyFrame(1, 0);
                fadeOut.Duration = TimeSpan.FromMilliseconds(140);

                ElementCompositionPreview.SetIsTranslationEnabled(element, true);
                ElementCompositionPreview.SetImplicitShowAnimation(element, show);
                ElementCompositionPreview.SetImplicitHideAnimation(element, fadeOut);
            }
            catch (Exception)
            {
                // 애니메이션은 장식 — 실패해도 기능에는 영향 없음
            }
        }
    }
}
