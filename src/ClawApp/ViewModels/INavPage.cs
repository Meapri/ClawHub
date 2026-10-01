using ClawApp.Input;

namespace ClawApp.ViewModels;

public sealed record Hint(string Glyph, string Label);

/// <summary>셸에 표시되는 페이지. 방향 이동/선택은 공통 포커스 내비게이션이 처리하고, 나머지 버튼만 페이지가 받는다.</summary>
public interface INavPage
{
    string Title { get; }
    IReadOnlyList<Hint> Hints { get; }

    /// <summary>처리했으면 true. Back을 처리하지 않으면 셸이 이전 페이지로 돌아간다.</summary>
    bool Handle(NavAction action) => false;

    /// <summary>페이지가 다시 보일 때 (뒤로 가기로 돌아온 경우 포함).</summary>
    void OnShown() { }

    /// <summary>페이지를 떠날 때.</summary>
    void OnHidden() { }
}
