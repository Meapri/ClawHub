using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Threading;

namespace ClawApp.Motion;

/// <summary>
/// 라이브 타일: 앞면(아트)과 뒷면(정보)이 6~8초마다 위로 굴러 넘어가며 바뀐다.
/// Grid(또는 Panel)에 IsLive=True, 자식 [0] = 앞면, [1] = 뒷면. 패널은 ClipToBounds=True여야 한다.
/// 타일마다 시작 시점을 엇갈리게 하고, 한 번에 하나만 넘긴다 (성능 + 시선이 흩어지지 않게).
/// 쉬는 동안 두 면 모두 변환이 없어서 글자는 선명하다.
/// </summary>
public static class LiveTile
{
    public static readonly DependencyProperty IsLiveProperty = DependencyProperty.RegisterAttached(
        "IsLive", typeof(bool), typeof(LiveTile), new PropertyMetadata(false, OnChanged));

    public static bool GetIsLive(DependencyObject d) => (bool)d.GetValue(IsLiveProperty);
    public static void SetIsLive(DependencyObject d, bool value) => d.SetValue(IsLiveProperty, value);

    private sealed class Entry(Panel panel)
    {
        public Panel Panel { get; } = panel;
        public long Due;
        public bool ShowingBack;
        public bool Busy;
    }

    private static readonly List<Entry> Tiles = [];
    private static readonly Random Rng = new();
    private static DispatcherTimer? _timer;
    private static long _lastFlip;

    private static void OnChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
    {
        if (d is not Panel p || e.NewValue is not true) return;
        p.Loaded += (_, _) => Register(p);
        p.Unloaded += (_, _) => Tiles.RemoveAll(t => t.Panel == p);
        if (p.IsLoaded) Register(p);
    }

    private static void Register(Panel p)
    {
        if (p.Children.Count < 2 || Tiles.Any(t => t.Panel == p)) return;
        p.Children[1].Visibility = Visibility.Hidden;
        p.Children[0].Visibility = Visibility.Visible;
        // 첫 넘김은 화면이 자리 잡은 뒤 타일마다 엇갈리게 (3초 + 1.6초씩 + 약간의 무작위)
        var now = Environment.TickCount64;
        Tiles.Add(new Entry(p) { Due = now + 3000 + Tiles.Count * 1600 + Rng.Next(0, 900) });
        if (_timer is null)
        {
            _timer = new DispatcherTimer(DispatcherPriority.Background) { Interval = TimeSpan.FromMilliseconds(500) };
            _timer.Tick += (_, _) => Tick();
        }
        _timer.Start();
    }

    private static void Tick()
    {
        if (Tiles.Count == 0)
        {
            _timer?.Stop();
            return;
        }
        if (!MotionCore.Enabled || Tiles.Any(t => t.Busy)) return;
        // 창이 가려졌거나 메뉴·빠른 설정이 열려 있으면 쉰다 (패널 뒤는 흐림 효과라 다시 그리면 비싸다)
        if (Application.Current?.MainWindow is not { IsVisible: true, IsActive: true }) return;
        if (ViewModels.ShellViewModel.Instance is { IsAnyPanelOpen: true }) return;

        var now = Environment.TickCount64;
        if (now - _lastFlip < 1800) return; // 연달아 넘어가지 않게
        var next = Tiles.Where(t => t.Due <= now && t.Panel.IsVisible).MinBy(t => t.Due);
        if (next is null) return;
        Flip(next);
        _lastFlip = now;
        next.Due = now + Rng.Next(6000, 8000);
    }

    private static void Flip(Entry e)
    {
        var p = e.Panel;
        var h = p.ActualHeight;
        if (h < 1 || p.Children.Count < 2) return;
        UIElement from = p.Children[e.ShowingBack ? 1 : 0], to = p.Children[e.ShowingBack ? 0 : 1];

        var outgoing = new TranslateTransform();
        var incoming = new TranslateTransform(0, h);
        from.RenderTransform = outgoing;
        to.RenderTransform = incoming;
        to.Visibility = Visibility.Visible;
        e.Busy = true;

        var duration = MotionCore.Ms(720);
        var inAnim = MotionCore.Hold(h, 0, TimeSpan.Zero, duration, MotionCore.InOut);
        inAnim.Completed += (_, _) =>
        {
            from.Visibility = Visibility.Hidden;
            from.ClearValue(UIElement.RenderTransformProperty);
            to.ClearValue(UIElement.RenderTransformProperty);
            e.ShowingBack = !e.ShowingBack;
            e.Busy = false;
        };
        outgoing.BeginAnimation(TranslateTransform.YProperty, MotionCore.Hold(0, -h, TimeSpan.Zero, duration, MotionCore.InOut));
        incoming.BeginAnimation(TranslateTransform.YProperty, inAnim);
    }
}
