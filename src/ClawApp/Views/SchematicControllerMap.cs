using System.ComponentModel;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Shapes;
using ClawApp.ViewModels;
using ClawCore.Controller;

namespace ClawApp.Views;

/// <summary>
/// 공식 본체 이미지가 없을 때 쓰는 단순화한 Claw 그림 (<see cref="ControllerMap"/>가 고른다). 버튼마다 자리가 있고:
///  · 키가 지정된 버튼 = 강조색 테두리, 테스트 중 눌린 버튼 = 강조색으로 채움
///  · 지금 고른 줄(포커스·마우스·편집 중) = 강조색으로 채우고 바깥 고리가 '찰칵' 조여 든다
///  · 그림의 버튼을 누르면(터치·마우스) 그 버튼 편집
/// 그림은 실제 배치를 단순화한 것 (왼쪽: 스틱 위·방향 버튼 아래, 오른쪽: ABXY 위·스틱 아래, 뒷면 M1·M2는 아래 탭).
/// </summary>
public sealed class SchematicControllerMap : Viewbox
{
    public static readonly DependencyProperty RowsProperty = DependencyProperty.Register(
        nameof(Rows), typeof(IEnumerable<ButtonRowViewModel>), typeof(SchematicControllerMap),
        new PropertyMetadata(null, (d, e) => ((SchematicControllerMap)d).OnRowsChanged(e.OldValue as IEnumerable<ButtonRowViewModel>)));

    public IEnumerable<ButtonRowViewModel>? Rows
    {
        get => (IEnumerable<ButtonRowViewModel>?)GetValue(RowsProperty);
        set => SetValue(RowsProperty, value);
    }

    public static readonly DependencyProperty FocusedProperty = DependencyProperty.Register(
        nameof(Focused), typeof(ClawButton?), typeof(SchematicControllerMap), new PropertyMetadata(null, (d, _) => ((SchematicControllerMap)d).Refresh(pop: true)));

    public ClawButton? Focused
    {
        get => (ClawButton?)GetValue(FocusedProperty);
        set => SetValue(FocusedProperty, value);
    }

    public static readonly DependencyProperty EditCommandProperty = DependencyProperty.Register(
        nameof(EditCommand), typeof(ICommand), typeof(SchematicControllerMap));

    public ICommand? EditCommand
    {
        get => (ICommand?)GetValue(EditCommandProperty);
        set => SetValue(EditCommandProperty, value);
    }

    private const double W = 340, H = 150;
    private readonly Canvas _canvas = new() { Width = W, Height = H };
    private readonly Dictionary<ClawButton, Shape> _spots = [];
    private readonly Rectangle _halo = new() { StrokeThickness = 2, IsHitTestVisible = false, Visibility = Visibility.Collapsed };

    public SchematicControllerMap()
    {
        Stretch = Stretch.Uniform;
        Child = _canvas;

        // 본체 · 화면
        var body = new Rectangle { Width = 312, Height = 112, RadiusX = 40, RadiusY = 40, StrokeThickness = 2, IsHitTestVisible = false };
        body.SetResourceReference(Shape.StrokeProperty, "StrokeStrong");
        body.SetResourceReference(Shape.FillProperty, "TileFill");
        Place(body, 14, 18);
        var screen = new Rectangle { Width = 132, Height = 88, StrokeThickness = 1, IsHitTestVisible = false };
        screen.SetResourceReference(Shape.StrokeProperty, "Stroke");
        screen.SetResourceReference(Shape.FillProperty, "SurfacePressed");
        Place(screen, 104, 30);

        // 어깨 (위쪽 탭: 트리거가 더 바깥)
        Spot(ClawButton.LT, new Rectangle { Width = 48, Height = 8 }, 40, 1);
        Spot(ClawButton.LB, new Rectangle { Width = 56, Height = 8 }, 36, 10);
        Spot(ClawButton.RT, new Rectangle { Width = 48, Height = 8 }, W - 40 - 48, 1);
        Spot(ClawButton.RB, new Rectangle { Width = 56, Height = 8 }, W - 36 - 56, 10);

        // 왼쪽: 스틱(누름 = L3) 위, 방향 버튼 아래
        Spot(ClawButton.L3, new Ellipse { Width = 30, Height = 30 }, 43, 37);
        Spot(ClawButton.DpadUp, new Rectangle { Width = 12, Height = 12 }, 52, 78);
        Spot(ClawButton.DpadDown, new Rectangle { Width = 12, Height = 12 }, 52, 102);
        Spot(ClawButton.DpadLeft, new Rectangle { Width = 12, Height = 12 }, 40, 90);
        Spot(ClawButton.DpadRight, new Rectangle { Width = 12, Height = 12 }, 64, 90);

        // 오른쪽: ABXY 위, 스틱(누름 = R3) 아래
        Spot(ClawButton.Y, new Ellipse { Width = 15, Height = 15 }, 274.5, 30.5, "Y");
        Spot(ClawButton.X, new Ellipse { Width = 15, Height = 15 }, 260.5, 44.5, "X");
        Spot(ClawButton.B, new Ellipse { Width = 15, Height = 15 }, 288.5, 44.5, "B");
        Spot(ClawButton.A, new Ellipse { Width = 15, Height = 15 }, 274.5, 58.5, "A");
        Spot(ClawButton.R3, new Ellipse { Width = 30, Height = 30 }, 267, 81);

        // 뒷면 M1·M2 (아래 탭)
        Spot(ClawButton.M1, new Rectangle { Width = 36, Height = 9 }, 60, 133, "M1", labelBelow: true);
        Spot(ClawButton.M2, new Rectangle { Width = 36, Height = 9 }, W - 60 - 36, 133, "M2", labelBelow: true);

        _halo.SetResourceReference(Shape.StrokeProperty, "Accent");
        _canvas.Children.Add(_halo);
        Refresh(pop: false);
    }

    /// <summary>그 버튼의 가운데가 <paramref name="relativeTo"/> 안 어디에 보이는지 (안내선 끝점).</summary>
    public Point? ButtonPoint(ClawButton button, UIElement relativeTo)
    {
        if (!_spots.TryGetValue(button, out var s)) return null;
        try { return s.TranslatePoint(new Point(s.Width / 2, s.Height / 2), relativeTo); }
        catch (InvalidOperationException) { return null; }
    }

    private void Place(UIElement el, double x, double y)
    {
        Canvas.SetLeft(el, x);
        Canvas.SetTop(el, y);
        _canvas.Children.Add(el);
    }

    private void Spot(ClawButton button, Shape shape, double x, double y, string? label = null, bool labelBelow = false)
    {
        shape.StrokeThickness = 1.5;
        shape.Cursor = Cursors.Hand;
        shape.MouseLeftButtonUp += (_, e) =>
        {
            e.Handled = true;
            if (EditCommand?.CanExecute(button) == true) EditCommand.Execute(button);
        };
        Place(shape, x, y);
        _spots[button] = shape;
        if (label is null) return;

        var text = new TextBlock { Text = label, FontSize = 8, FontWeight = FontWeights.SemiBold, IsHitTestVisible = false };
        text.SetResourceReference(TextBlock.ForegroundProperty, "TextDim");
        text.Measure(new Size(double.PositiveInfinity, double.PositiveInfinity));
        var tx = x + (shape.Width - text.DesiredSize.Width) / 2;
        var ty = labelBelow ? y + shape.Height : y + (shape.Height - text.DesiredSize.Height) / 2;
        Place(text, tx, ty);
        shape.Tag = text; // 채워질 때 글자색을 바꾸려고
    }

    // ── 상태 ───────────────────────────────────────────────

    private void OnRowsChanged(IEnumerable<ButtonRowViewModel>? old)
    {
        if (old is not null) foreach (var r in old) r.PropertyChanged -= OnRowChanged;
        if (Rows is { } rows) foreach (var r in rows) r.PropertyChanged += OnRowChanged;
        Refresh(pop: false);
    }

    private void OnRowChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName is nameof(ButtonRowViewModel.IsMapped) or nameof(ButtonRowViewModel.IsLive) or nameof(ButtonRowViewModel.Conflict))
            Refresh(pop: false);
    }

    private void Refresh(bool pop)
    {
        var rows = Rows?.ToDictionary(r => r.Button) ?? [];
        foreach (var (button, shape) in _spots)
        {
            rows.TryGetValue(button, out var row);
            var filled = row?.IsLive == true || Focused == button;
            shape.SetResourceReference(Shape.FillProperty, filled ? "Accent" : "TileFill");
            shape.SetResourceReference(Shape.StrokeProperty, row?.IsMapped == true || filled ? "Accent" : "StrokeStrong");
            shape.StrokeThickness = row?.IsMapped == true ? 2.5 : 1.5;
            if (shape.Tag is TextBlock label)
                label.SetResourceReference(TextBlock.ForegroundProperty, filled && button is not (ClawButton.M1 or ClawButton.M2) ? "AccentText" : "TextDim");
            ToolTipService.SetToolTip(shape, row is null ? null : row.IsMapped ? $"{row.Name} · 키 지정됨" : row.Name);
        }

        if (Focused is { } f && _spots.TryGetValue(f, out var target))
        {
            const double pad = 4;
            _halo.Width = target.Width + pad * 2;
            _halo.Height = target.Height + pad * 2;
            _halo.RadiusX = _halo.RadiusY = target is Ellipse ? _halo.Width / 2 : 2;
            Canvas.SetLeft(_halo, Canvas.GetLeft(target) - pad);
            Canvas.SetTop(_halo, Canvas.GetTop(target) - pad);
            var wasHidden = _halo.Visibility != Visibility.Visible;
            _halo.Visibility = Visibility.Visible;
            if (pop && Motion.MotionCore.Enabled)
            {
                // 크기가 정해진 뒤 조여 든다 (Snap은 ActualWidth를 쓴다)
                Dispatcher.BeginInvoke(() => Motion.MotionCore.Snap(_halo, wasHidden ? 10 : 6, Motion.MotionCore.Ms(240)),
                    System.Windows.Threading.DispatcherPriority.Loaded);
            }
        }
        else _halo.Visibility = Visibility.Collapsed;
    }
}
