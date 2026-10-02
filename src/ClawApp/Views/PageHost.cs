using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using ClawApp.ViewModels;

namespace ClawApp.Views;

/// <summary>
/// 페이지 자리. 자주 오가는 페이지(홈·라이브러리·설정·프로필·게임 상세)는 한 번 만든 화면을 숨겨 두었다가 다시 쓴다.
/// 예전처럼 ContentControl 하나에 바꿔 끼우면 홈으로 돌아올 때마다 타일 전체, 설정을 열 때마다 화면 전체를
/// 새로 만들어 0.3~0.9초씩 멈췄다. 화면은 페이지 종류마다 하나: 같은 종류의 새 페이지(다른 게임 등)가 오면
/// 데이터만 바꾼다 (각 뷰는 DataContextChanged로 새 ViewModel에 붙는다). 편집기·선택판은 그때그때 만든다.
/// </summary>
public sealed class PageHost : Grid
{
    public static readonly DependencyProperty PageProperty = DependencyProperty.Register(
        nameof(Page), typeof(object), typeof(PageHost), new PropertyMetadata(null, (d, _) => ((PageHost)d).Show()));

    public object? Page
    {
        get => GetValue(PageProperty);
        set => SetValue(PageProperty, value);
    }

    private static bool Cached(object page) =>
        page is HomeViewModel or LibraryViewModel or SettingsViewModel or ProfilesViewModel or GameViewModel;

    private readonly Dictionary<Type, ContentPresenter> _cache = [];
    private ContentPresenter? _current;

    private void Show()
    {
        var sw = Services.FrameMeter.Enabled ? System.Diagnostics.Stopwatch.StartNew() : null;
        ShowCore();
        if (sw is null) return;
        UpdateLayout(); // 진단: 만들기 + 배치까지 걸린 시간
        Services.FrameMeter.Note($"page {Page?.GetType().Name} {sw.ElapsedMilliseconds}ms");
    }

    private void ShowCore()
    {
        var page = Page;
        ContentPresenter? next = null;
        if (page is not null)
        {
            if (Cached(page) && _cache.TryGetValue(page.GetType(), out var kept))
            {
                next = kept;
                if (!ReferenceEquals(kept.Content, page))
                {
                    kept.Content = page;
                    // 새로 연 페이지(뒤로 온 게 아님)는 맨 위에서 시작
                    Dispatcher.BeginInvoke(() => ResetScroll(kept), System.Windows.Threading.DispatcherPriority.Loaded);
                }
            }
            else
            {
                next = new ContentPresenter { Content = page };
                if (Cached(page)) _cache[page.GetType()] = next;
                Children.Add(next);
            }
        }

        if (_current is not null && !ReferenceEquals(_current, next))
        {
            if (_cache.ContainsValue(_current)) _current.Visibility = Visibility.Collapsed;
            else Children.Remove(_current); // 편집기·선택판은 버린다
        }
        if (next is not null) next.Visibility = Visibility.Visible;
        _current = next;
    }

    private static void ResetScroll(DependencyObject node)
    {
        if (node is ScrollViewer sv)
        {
            sv.ScrollToHorizontalOffset(0);
            sv.ScrollToVerticalOffset(0);
        }
        for (var i = 0; i < VisualTreeHelper.GetChildrenCount(node); i++) ResetScroll(VisualTreeHelper.GetChild(node, i));
    }
}
