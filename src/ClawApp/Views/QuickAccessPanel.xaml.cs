using System.Windows.Controls;
using System.Windows.Threading;
using ClawApp.Motion;

namespace ClawApp.Views;

public partial class QuickAccessPanel : UserControl
{
    public QuickAccessPanel() => InitializeComponent();

    /// <summary>
    /// 열 때 맨 위로: 첫 포커스 타일은 위쪽인데도, 등장 애니메이션 도중에 포커스를 받으면 "보이게 스크롤"이
    /// 한 번 밀어 올려 제목이 잘렸다. 등장이 끝날 무렵(≈300ms)까지 한 번 더 맞춘다.
    /// </summary>
    public void ScrollToTopAfterOpen()
    {
        void Top()
        {
            if (System.Windows.Input.Keyboard.FocusedElement is System.Windows.FrameworkElement f && Scroller.IsAncestorOf(f)
                && f.TranslatePoint(new System.Windows.Point(0, f.ActualHeight), Scroller).Y > Scroller.ViewportHeight) return; // 아래쪽 항목이면 그대로
            SmoothScroll.Stop(Scroller);
            Scroller.ScrollToVerticalOffset(0);
        }
        Dispatcher.BeginInvoke(Top, DispatcherPriority.Background);
        var later = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(320) };
        later.Tick += (_, _) => { later.Stop(); Top(); };
        later.Start();
    }
}
