using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Animation;

namespace ClawApp.Views;

/// <summary>시작 화면 패널이 배치하는 칸 (타일 ViewModel이 구현).</summary>
public interface IStartCell
{
    /// <summary>한 변의 칸 수 (정사각형: 1·2·4).</summary>
    int Span { get; }
    /// <summary>패널이 정한 위치 (칸 단위). 패드 이동·끌어 놓기 계산에 쓴다.</summary>
    int Col { get; set; }
    int Row { get; set; }
    /// <summary>끌고 있는 타일: 패널이 위치 애니메이션을 하지 않는다 (끄는 쪽이 직접 움직임).</summary>
    bool IsDragging { get; }
}

/// <summary>
/// Windows 10 Mobile 시작 화면처럼 정사각형 타일을 순서대로 빈틈없이 채운다 (가로로 흐르는 판 — 세로 칸 수는 화면 높이로).
/// 보통(2칸)·크게(4칸) 타일은 2칸 격자에 맞추고, 작은 타일이 그 사이를 채운다.
/// 타일이 옮겨지면 이전 자리에서 새 자리로 미끄러진다.
/// </summary>
public sealed class StartPanel : Panel
{
    public const double Unit = 88;
    public const double Gap = 8;

    public int Rows { get; private set; } = 6;

    private readonly Dictionary<UIElement, Point> _last = [];

    private static IStartCell? Cell(UIElement child) => (child as FrameworkElement)?.DataContext as IStartCell;

    // ── 타일 크기가 바뀌면 다시 배치 ──────────────────────────
    // 예전에는 크기를 바꿔도(패드 X·편집) 패널이 다시 재지 않아, 타일은 예전 크기 칸에 남고 안의 내용만 새 크기 배치로 바뀌어
    // 글자·아이콘이 칸 밖으로 밀려 빈 타일처럼 보였다 (화면이 한 단계씩 늦게 따라옴). 칸의 Span이 바뀌면 바로 다시 잰다.

    protected override void OnVisualChildrenChanged(DependencyObject? added, DependencyObject? removed)
    {
        base.OnVisualChildrenChanged(added, removed);
        if (removed is FrameworkElement r)
        {
            r.DataContextChanged -= OnChildContextChanged;
            Watch(r.DataContext, false);
        }
        if (added is FrameworkElement a)
        {
            a.DataContextChanged += OnChildContextChanged;
            Watch(a.DataContext, true);
        }
    }

    private void OnChildContextChanged(object sender, DependencyPropertyChangedEventArgs e)
    {
        Watch(e.OldValue, false);
        Watch(e.NewValue, true);
        InvalidateMeasure();
    }

    private void Watch(object? context, bool on)
    {
        if (context is not (IStartCell and System.ComponentModel.INotifyPropertyChanged n)) return;
        n.PropertyChanged -= OnCellChanged; // 두 번 붙지 않게
        if (on) n.PropertyChanged += OnCellChanged;
    }

    private void OnCellChanged(object? sender, System.ComponentModel.PropertyChangedEventArgs e)
    {
        if (e.PropertyName is null or "" or nameof(IStartCell.Span) or "Size") InvalidateMeasure();
    }

    protected override Size MeasureOverride(Size available)
    {
        var height = double.IsInfinity(available.Height) ? 6 * Unit : available.Height + Gap;
        // 가장 큰 타일이 들어갈 만큼은 늘 둔다 (배율이 커서 화면이 낮아도 큰 타일이 옆 타일을 덮지 않게 — 넘치면 아래가 잘릴 뿐)
        var tallest = 2;
        foreach (UIElement child in InternalChildren) tallest = Math.Max(tallest, Cell(child)?.Span ?? 2);
        var rows = Math.Max(tallest, (int)(height / Unit));
        Rows = rows - rows % 2;

        var cols = Pack();
        foreach (UIElement child in InternalChildren)
        {
            var span = Cell(child)?.Span ?? 2;
            var size = span * Unit - Gap;
            child.Measure(new Size(size, size));
        }
        return new Size(Math.Max(0, cols * Unit - Gap), Rows * Unit - Gap);
    }

    /// <summary>순서대로 놓되, 직전 타일이 놓인 열부터 빈자리를 찾는다 (순서가 크게 뒤섞이지 않으면서 빈칸을 메움).</summary>
    private int Pack()
    {
        var used = new List<bool[]>();
        bool Free(int c, int r) => c >= used.Count || !used[c][r];
        void Take(int c, int r)
        {
            while (used.Count <= c) used.Add(new bool[Rows]);
            used[c][r] = true;
        }

        var cursor = 0;
        var maxCol = 0;
        foreach (UIElement child in InternalChildren)
        {
            if (Cell(child) is not { } cell) continue;
            var span = Math.Min(cell.Span, Rows);
            var align = Math.Min(span, 2);
            for (var c = cursor - cursor % align; ; c += align)
            {
                var placed = false;
                for (var r = 0; r + span <= Rows && !placed; r += align)
                {
                    var fits = true;
                    for (var dc = 0; dc < span && fits; dc++)
                        for (var dr = 0; dr < span && fits; dr++)
                            fits = Free(c + dc, r + dr);
                    if (!fits) continue;
                    for (var dc = 0; dc < span; dc++)
                        for (var dr = 0; dr < span; dr++)
                            Take(c + dc, r + dr);
                    cell.Col = c;
                    cell.Row = r;
                    placed = true;
                }
                if (!placed) continue;
                cursor = c;
                maxCol = Math.Max(maxCol, c + span);
                break;
            }
        }
        return maxCol;
    }

    protected override Size ArrangeOverride(Size finalSize)
    {
        var live = new HashSet<UIElement>();
        foreach (UIElement child in InternalChildren)
        {
            live.Add(child);
            if (Cell(child) is not { } cell) continue;
            var size = cell.Span * Unit - Gap;
            var at = new Point(cell.Col * Unit, cell.Row * Unit);
            child.Arrange(new Rect(at, new Size(size, size)));

            // 자리가 바뀌었으면 이전 자리에서 미끄러져 온다
            if (_last.TryGetValue(child, out var old) && old != at && !cell.IsDragging && Motion.MotionCore.Enabled)
            {
                var t = child.RenderTransform as TranslateTransform ?? new TranslateTransform();
                child.RenderTransform = t;
                var dx = old.X - at.X + t.X;
                var dy = old.Y - at.Y + t.Y;
                var ease = new CubicEase { EasingMode = EasingMode.EaseOut };
                t.BeginAnimation(TranslateTransform.XProperty, new DoubleAnimation(dx, 0, TimeSpan.FromMilliseconds(260)) { EasingFunction = ease });
                t.BeginAnimation(TranslateTransform.YProperty, new DoubleAnimation(dy, 0, TimeSpan.FromMilliseconds(260)) { EasingFunction = ease });
            }
            _last[child] = at;
        }
        foreach (var gone in _last.Keys.Where(k => !live.Contains(k)).ToList()) _last.Remove(gone);
        return finalSize;
    }
}
