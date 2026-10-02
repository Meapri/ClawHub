using System.Windows;
using System.Windows.Controls;
using System.Windows.Markup;

namespace ClawApp.Views;

/// <summary>
/// 처음 보일 때 내용을 만든다 (그 뒤로는 그대로 둔다). 설정처럼 분류가 많은 화면에서 안 보이는 분류까지
/// 한꺼번에 만들어 화면을 여는 데 0.4초씩 멈추지 않게. 내용은 DataTemplate으로 적는다:
/// <code>&lt;v:LazyContent Visibility="..."&gt;&lt;DataTemplate&gt;...&lt;/DataTemplate&gt;&lt;/v:LazyContent&gt;</code>
/// 데이터 컨텍스트는 이 요소의 것을 그대로 물려받는다.
/// </summary>
[ContentProperty(nameof(Deferred))]
public sealed class LazyContent : ContentControl
{
    public LazyContent()
    {
        Focusable = false;
        IsTabStop = false;
        // 만들어질 때는 Visibility 바인딩이 아직 계산되지 않아 기본값(보임)이다 — 화면에 붙은 뒤에 판단한다
        Loaded += (_, _) => Realize();
    }

    /// <summary>보일 때 만들 내용.</summary>
    public DataTemplate? Deferred { get; set; }

    protected override void OnPropertyChanged(DependencyPropertyChangedEventArgs e)
    {
        base.OnPropertyChanged(e);
        if (e.Property == VisibilityProperty) Realize();
    }

    private void Realize()
    {
        if (Content is null && IsLoaded && Visibility == Visibility.Visible && Deferred is not null)
            Content = Deferred.LoadContent();
    }
}
