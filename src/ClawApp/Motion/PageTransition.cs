using System.Runtime.CompilerServices;
using System.Windows;

namespace ClawApp.Motion;

/// <summary>
/// 페이지 전환: 새 페이지는 오른쪽에서, 뒤로 돌아온 페이지는 왼쪽에서 미끄러져 들어온다.
/// 피벗 제목(상단의 큰 글씨)은 조금 더 멀리서, 조금 늦게 따라 들어와 깊이감을 만든다.
/// 페이지 안쪽 타일·목록의 계단식 등장은 각 뷰의 Entrance가 맡는다.
/// </summary>
public static class PageTransition
{
    /// <summary>한 번 보였던 페이지 = 다시 나타나면 '뒤로' 온 것 (앞으로 가는 페이지는 항상 새로 만들어진다).</summary>
    private static readonly ConditionalWeakTable<object, object> Seen = new();
    private static WeakReference<object>? _current;

    public static void Play(FrameworkElement host, FrameworkElement? header, object? page)
    {
        if (_current?.TryGetTarget(out var previous) == true) Seen.AddOrUpdate(previous, previous);
        var back = page is not null && Seen.TryGetValue(page, out _);
        _current = page is null ? null : new WeakReference<object>(page);

        Backdrop.ShowDefault();
        if (!MotionCore.Enabled) return;

        var dir = back ? -1 : 1;
        MotionCore.SlideIn(host, 32 * dir, 0, TimeSpan.Zero, MotionCore.Ms(380), fadeRatio: 0.45);
        if (header is not null) MotionCore.SlideIn(header, 64 * dir, 0, MotionCore.Ms(30), MotionCore.Ms(460), fadeRatio: 0.5);
    }
}
