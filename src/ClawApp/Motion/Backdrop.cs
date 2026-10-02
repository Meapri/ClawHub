using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;

namespace ClawApp.Motion;

/// <summary>
/// 전체 화면 배경 = 게임 아트를 크게 흐리고 어둡게 깐 '아크릴' 층.
/// 호스트(MainWindow)에는 이미지 층 두 개가 있고, 그림이 바뀌면 교차 페이드한다.
///  · Default: 기본 그림 (홈의 히어로 게임).
///  · FocusSource: 그 요소에 포커스가 가면 배경을 그 게임으로 (홈·라이브러리 타일).
///  · PageSource: 그 요소가 화면에 올라오면 배경을 그 그림으로 (게임 상세).
/// 흐림은 각 층의 BitmapCache 안에서 한 번만 계산되고, 페이드는 캐시된 비트맵의 불투명도만 바꾼다.
/// </summary>
public static class Backdrop
{
    // ── 호스트 ────────────────────────────────────────────

    /// <summary>호스트 Grid: 자식 [0], [1]이 층(안에 Image 하나).</summary>
    public static readonly DependencyProperty IsHostProperty = DependencyProperty.RegisterAttached(
        "IsHost", typeof(bool), typeof(Backdrop), new PropertyMetadata(false, OnIsHostChanged));

    public static bool GetIsHost(DependencyObject d) => (bool)d.GetValue(IsHostProperty);
    public static void SetIsHost(DependencyObject d, bool value) => d.SetValue(IsHostProperty, value);

    public static readonly DependencyProperty DefaultProperty = DependencyProperty.RegisterAttached(
        "Default", typeof(ImageSource), typeof(Backdrop), new PropertyMetadata(null, OnDefaultChanged));

    public static ImageSource? GetDefault(DependencyObject d) => (ImageSource?)d.GetValue(DefaultProperty);
    public static void SetDefault(DependencyObject d, ImageSource? value) => d.SetValue(DefaultProperty, value);

    // ── 소스 ──────────────────────────────────────────────

    public static readonly DependencyProperty FocusSourceProperty = DependencyProperty.RegisterAttached(
        "FocusSource", typeof(ImageSource), typeof(Backdrop), new PropertyMetadata(null, OnFocusSourceChanged));

    public static ImageSource? GetFocusSource(DependencyObject d) => (ImageSource?)d.GetValue(FocusSourceProperty);
    public static void SetFocusSource(DependencyObject d, ImageSource? value) => d.SetValue(FocusSourceProperty, value);

    public static readonly DependencyProperty PageSourceProperty = DependencyProperty.RegisterAttached(
        "PageSource", typeof(ImageSource), typeof(Backdrop), new PropertyMetadata(null, OnPageSourceChanged));

    public static ImageSource? GetPageSource(DependencyObject d) => (ImageSource?)d.GetValue(PageSourceProperty);
    public static void SetPageSource(DependencyObject d, ImageSource? value) => d.SetValue(PageSourceProperty, value);

    private static WeakReference<Panel>? _host;
    private static int _front;
    private static ImageSource? _shown;
    private static bool _showingDefault = true;
    private static bool _initialized;

    private static void OnIsHostChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
    {
        if (d is Panel p && e.NewValue is true) _host = new WeakReference<Panel>(p);
    }

    private static void OnDefaultChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
    {
        if (_showingDefault || !_initialized) Show(null);
    }

    /// <summary>
    /// false면 포커스·페이지를 따라 바꾸지 않고 항상 기본 그림만 (설정 → 홈 화면 → 배경이 "고정 이미지"·"단색"일 때).
    /// </summary>
    public static bool FollowFocus { get; set; } = true;

    private static void ShowFollowing(ImageSource? source) => Show(FollowFocus ? source : null);

    private static void OnFocusSourceChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
    {
        if (d is not UIElement el || e.OldValue is not null) return; // 처음 한 번만 연결
        el.GotKeyboardFocus += (_, _) => ShowFollowing(GetFocusSource(el));
    }

    private static void OnPageSourceChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
    {
        if (d is not FrameworkElement el) return;
        if (e.OldValue is null) el.Loaded += (_, _) => ShowFollowing(GetPageSource(el));
        if (el.IsLoaded) ShowFollowing(GetPageSource(el));
    }

    /// <summary>기본 그림(홈 히어로)으로 돌아간다. 페이지가 바뀔 때마다 호출.</summary>
    public static void ShowDefault() => Show(null);

    private static (FrameworkElement Layer, Image Image)? LayerAt(Panel host, int i) =>
        host.Children.Count > i && host.Children[i] is FrameworkElement layer && FindImage(layer) is { } img ? (layer, img) : null;

    private static Image? FindImage(DependencyObject node)
    {
        if (node is Image img) return img;
        return node switch
        {
            Decorator { Child: { } c } => FindImage(c),
            Panel p when p.Children.Count > 0 => FindImage(p.Children[0]),
            _ => null,
        };
    }

    /// <summary>
    /// 흐림이 약할 때(설정 → 홈 화면 → 배경 흐림 &lt; 24): 그림을 화면 해상도로 다시 읽고, 층도 반 해상도 캐시 없이 그린다.
    /// 많이 흐릴 땐 작은 그림·반 해상도로도 차이가 안 보이니 가볍게 둔다.
    /// </summary>
    public static bool Sharp
    {
        get => _sharp;
        set
        {
            if (_sharp == value) return;
            _sharp = value;
            if (_host is not null && _host.TryGetTarget(out var host))
            {
                ApplyQuality(host);
                _initialized = false; // 같은 그림이어도 해상도를 바꿔 다시 그린다
                Show(_showingDefault ? null : _shown);
            }
        }
    }

    private static bool _sharp;

    private static void ApplyQuality(Panel host)
    {
        for (var i = 0; i < 2; i++)
        {
            if (LayerAt(host, i) is not { } l) continue;
            l.Layer.CacheMode = new BitmapCache(_sharp ? 1 : 0.5);
            RenderOptions.SetBitmapScalingMode(l.Image, _sharp ? BitmapScalingMode.HighQuality : BitmapScalingMode.LowQuality);
        }
    }

    /// <summary>타일용 작은 그림(600px)이면 화면 해상도 그림으로 바꾼다.</summary>
    private static ImageSource? Sharpen(ImageSource? source) =>
        _sharp && source is System.Windows.Media.Imaging.BitmapImage { UriSource: { IsFile: true } uri }
            ? Services.ArtCache.LoadFullScreen(uri.LocalPath) ?? source
            : source;

    private static void Show(ImageSource? source)
    {
        if (_host is null || !_host.TryGetTarget(out var host)) return;
        _showingDefault = source is null;
        source = Sharpen(source ?? GetDefault(host));
        if (_initialized && ReferenceEquals(source, _shown)) return;
        _shown = source;

        if (LayerAt(host, _front) is not { } front || LayerAt(host, 1 - _front) is not { } back) return;

        if (!_initialized) ApplyQuality(host);
        if (!_initialized || !MotionCore.Enabled)
        {
            _initialized = true;
            front.Image.Source = source;
            front.Layer.BeginAnimation(UIElement.OpacityProperty, null);
            front.Layer.Opacity = source is null ? 0 : 1;
            back.Layer.Opacity = 0;
            back.Layer.Visibility = Visibility.Hidden;
            return;
        }

        // 교차 페이드: 뒤 층에 새 그림 → 서서히 나타남, 앞 층은 사라짐
        back.Image.Source = source;
        back.Layer.Visibility = Visibility.Visible;
        var fadeIn = MotionCore.Hold(back.Layer.Opacity, source is null ? 0 : 1, TimeSpan.Zero, MotionCore.Ms(520), MotionCore.Out);
        var fadeOut = MotionCore.Hold(front.Layer.Opacity, 0, TimeSpan.Zero, MotionCore.Ms(520), MotionCore.Out);
        var old = front.Layer;
        fadeOut.Completed += (_, _) =>
        {
            if (ReferenceEquals(LayerAt(host, _front)?.Layer, old)) return; // 그새 다시 바뀜
            old.BeginAnimation(UIElement.OpacityProperty, null);
            old.Opacity = 0;
            old.Visibility = Visibility.Hidden;
        };
        fadeIn.Completed += (_, _) =>
        {
            if (!ReferenceEquals(LayerAt(host, _front)?.Layer, back.Layer)) return;
            back.Layer.BeginAnimation(UIElement.OpacityProperty, null);
            back.Layer.Opacity = source is null ? 0 : 1;
        };
        back.Layer.BeginAnimation(UIElement.OpacityProperty, fadeIn);
        front.Layer.BeginAnimation(UIElement.OpacityProperty, fadeOut);
        _front = 1 - _front;
    }
}
