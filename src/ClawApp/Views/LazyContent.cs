using System.Windows;
using System.Windows.Controls;
using System.Windows.Markup;

namespace ClawApp.Views;

/// <summary>
/// 처음 보일 때 내용을 만든다 (그 뒤로는 그대로 둔다). 설정처럼 분류가 많은 화면에서 안 보이는 분류까지
/// 한꺼번에 만들어 화면을 여는 데 0.4초씩 멈추지 않게. 내용은 DataTemplate으로 적는다:
/// <code>&lt;v:LazyContent Visibility="..."&gt;&lt;DataTemplate&gt;...&lt;/DataTemplate&gt;&lt;/v:LazyContent&gt;</code>
/// 데이터 컨텍스트는 이 요소의 것을 그대로 물려받는다.
/// <para>
/// "보인다" = 실제로 화면에 보이는 상태(IsVisible — 자기와 모든 상위가 보임). 접혀 있는 패널 안에 두어도
/// 패널이 처음 열릴 때 만든다 (매핑 편집의 키 선택판·옆 패널: 접혀 있어도 미리 만들어 편집기 열기가 0.4초 늦었다).
/// </para>
/// </summary>
[ContentProperty(nameof(Deferred))]
public sealed class LazyContent : ContentControl
{
    public LazyContent()
    {
        Focusable = false;
        IsTabStop = false;
        // 만들어질 때는 Visibility 바인딩이 아직 계산되지 않아 기본값(보임)이다 — 화면에 붙은(Loaded) 뒤에 판단한다
        Loaded += (_, _) => Realize();
        IsVisibleChanged += (_, _) => Realize();
    }

    /// <summary>보일 때 만들 내용.</summary>
    public DataTemplate? Deferred { get; set; }

    /// <summary>내용이 만들어졌는지.</summary>
    public bool IsRealized => Content is not null;

    /// <summary>보이지 않아도 지금 만든다 (미리 만들어 두기).</summary>
    public void RealizeNow()
    {
        if (Content is null && Deferred is not null) Content = Deferred.LoadContent();
    }

    private void Realize()
    {
        if (Content is null && IsLoaded && IsVisible && Deferred is not null)
            Content = Deferred.LoadContent();
    }
}
