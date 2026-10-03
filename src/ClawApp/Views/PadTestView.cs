using System.Globalization;
using System.Windows;
using System.Windows.Media;
using ClawApp.Input;

namespace ClawApp.Views;

/// <summary>
/// 입력 테스트 그림: 왼쪽·오른쪽 스틱(원 안의 점, 데드존 원), LT·RT 막대, 버튼 이름표(누르면 강조색).
/// 값은 XInput 원시값 그대로라 펌웨어 데드존·감도 설정이 적용된 결과를 보여 준다.
/// </summary>
public sealed class PadTestView : FrameworkElement
{
    public static readonly DependencyProperty StateProperty = DependencyProperty.Register(
        nameof(State), typeof(PadSnapshot?), typeof(PadTestView), new FrameworkPropertyMetadata(null, FrameworkPropertyMetadataOptions.AffectsRender));

    public PadSnapshot? State
    {
        get => (PadSnapshot?)GetValue(StateProperty);
        set => SetValue(StateProperty, value);
    }

    private static readonly (ushort Mask, string Label)[][] Rows =
    [
        [(0x0100, "LB"), (0x0200, "RB"), (0x0040, "L3"), (0x0080, "R3"), (0x0020, "View"), (0x0010, "Menu")],
        [(0x0001, "↑"), (0x0002, "↓"), (0x0004, "←"), (0x0008, "→"), (0x1000, "A"), (0x2000, "B"), (0x4000, "X"), (0x8000, "Y")],
    ];

    protected override Size MeasureOverride(Size available) =>
        new(double.IsInfinity(available.Width) ? 640 : available.Width, 296);

    private Brush Res(string key, Brush fallback) => TryFindResource(key) as Brush ?? fallback;

    protected override void OnRender(DrawingContext dc)
    {
        var accent = Res("Accent", Brushes.Orange);
        var dim = Res("StrokeStrong", Brushes.Gray);
        var text = Res("Text", Brushes.White);
        var textDim = Res("TextDim", Brushes.Gray);
        var stroke = new Pen(dim, 2);
        var dpi = VisualTreeHelper.GetDpi(this).PixelsPerDip;
        var face = new Typeface(new FontFamily("Segoe UI"), FontStyles.Normal, FontWeights.SemiBold, FontStretches.Normal);
        void Label(string s, Point center, Brush brush, double size = 13)
        {
            var ft = new FormattedText(s, CultureInfo.CurrentUICulture, FlowDirection.LeftToRight, face, size, brush, dpi);
            dc.DrawText(ft, new Point(center.X - ft.Width / 2, center.Y - ft.Height / 2));
        }

        var p = State;
        var w = ActualWidth;
        const double r = 62;
        var leftC = new Point(w * 0.2, 110);
        var rightC = new Point(w * 0.8, 110);

        // 스틱
        foreach (var (c, x, y, name) in new[] { (leftC, (double)(p?.LX ?? 0), (double)(p?.LY ?? 0), "왼쪽 스틱"), (rightC, (double)(p?.RX ?? 0), (double)(p?.RY ?? 0), "오른쪽 스틱") })
        {
            dc.DrawEllipse(null, stroke, c, r, r);
            dc.DrawEllipse(null, new Pen(dim, 1) { DashStyle = DashStyles.Dash }, c, r * 0.25, r * 0.25);
            dc.DrawLine(new Pen(dim, 1), new Point(c.X - r, c.Y), new Point(c.X + r, c.Y));
            dc.DrawLine(new Pen(dim, 1), new Point(c.X, c.Y - r), new Point(c.X, c.Y + r));
            var dot = new Point(c.X + x / 32768 * r, c.Y - y / 32768 * r);
            dc.DrawEllipse(accent, null, dot, 9, 9);
            Label(name, new Point(c.X, c.Y + r + 18), textDim, 12);
            Label($"{x / 327.68:0} , {y / 327.68:0}", new Point(c.X, c.Y + r + 36), text, 12);
        }

        // 트리거 (가운데 위)
        var mid = w / 2;
        foreach (var (val, x, name) in new[] { (p?.LeftTrigger ?? 0, mid - 70, "LT"), (p?.RightTrigger ?? 0, mid + 70, "RT") })
        {
            var bar = new Rect(x - 14, 30, 28, 110);
            dc.DrawRoundedRectangle(null, stroke, bar, 6, 6);
            var h = bar.Height * val / 255.0;
            if (h > 0) dc.DrawRoundedRectangle(accent, null, new Rect(bar.X, bar.Bottom - h, bar.Width, h), 6, 6);
            Label(name, new Point(x, 18), text, 12);
            Label($"{val * 100 / 255}%", new Point(x, 152), textDim, 11);
        }

        // 버튼 이름표 (아래 두 줄)
        const double pillW = 52, pillH = 28, gap = 6;
        for (var row = 0; row < Rows.Length; row++)
        for (var col = 0; col < Rows[row].Length; col++)
        {
            var (mask, label) = Rows[row][col];
            var totalW = Rows[row].Length * pillW + (Rows[row].Length - 1) * gap;
            var rect = new Rect(mid - totalW / 2 + col * (pillW + gap), 232 + row * (pillH + gap), pillW, pillH);
            var down = p is { } s && (s.Buttons & mask) != 0;
            dc.DrawRoundedRectangle(down ? accent : null, down ? null : stroke, rect, 8, 8);
            Label(label, new Point(rect.X + rect.Width / 2, rect.Y + rect.Height / 2), down ? Res("AccentText", Brushes.Black) : text, 12);
        }

        if (p is null) Label("컨트롤러 입력이 없어요", new Point(mid, 110), textDim, 13);
    }
}
