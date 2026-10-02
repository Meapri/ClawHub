using System.ComponentModel;
using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Media.Imaging;
using System.Windows.Shapes;
using ClawApp.Motion;
using ClawApp.ViewModels;
using ClawCore.Controller;

namespace ClawApp.Views;

/// <summary>
/// 매핑 편집 가운데의 본체 그림.
/// <para>
/// 공식 정면 이미지(%LocalAppData%\ClawHub\art\claw8ex-front.png — MSI 제품 렌더, 개인 사용으로 사용자가 받아 둔 것.
/// 저장소·설치 파일에는 넣지 않는다)가 있으면 그 사진 위에 버튼 자리를 표시하고, 고른 버튼 쪽으로 카메라가 부드럽게
/// 다가간다(확대). 목록을 벗어나면 본체 전체로 물러난다. 아래 띠에는 "A → Space"처럼 그 버튼이 내는 키를 보여 준다.
/// </para>
/// <para>이미지가 없으면 단순화한 그림(<see cref="SchematicControllerMap"/>)을 쓴다.</para>
/// 표시: 키가 지정된 버튼 = 강조색 고리, 고른 버튼 = 굵은 고리, 테스트 중 눌린 버튼 = 강조색으로 채움. 사진의 버튼을 누르면 편집.
/// </summary>
public sealed class ControllerMap : Grid
{
    public static readonly DependencyProperty RowsProperty = DependencyProperty.Register(
        nameof(Rows), typeof(IEnumerable<ButtonRowViewModel>), typeof(ControllerMap),
        new PropertyMetadata(null, (d, e) => ((ControllerMap)d).OnRowsChanged(e.OldValue as IEnumerable<ButtonRowViewModel>)));

    public IEnumerable<ButtonRowViewModel>? Rows
    {
        get => (IEnumerable<ButtonRowViewModel>?)GetValue(RowsProperty);
        set => SetValue(RowsProperty, value);
    }

    public static readonly DependencyProperty FocusedProperty = DependencyProperty.Register(
        nameof(Focused), typeof(ClawButton?), typeof(ControllerMap), new PropertyMetadata(null, (d, _) => ((ControllerMap)d).OnFocusedChanged()));

    public ClawButton? Focused
    {
        get => (ClawButton?)GetValue(FocusedProperty);
        set => SetValue(FocusedProperty, value);
    }

    public static readonly DependencyProperty EditCommandProperty = DependencyProperty.Register(
        nameof(EditCommand), typeof(ICommand), typeof(ControllerMap));

    public ICommand? EditCommand
    {
        get => (ICommand?)GetValue(EditCommandProperty);
        set => SetValue(EditCommandProperty, value);
    }

    /// <summary>사용자가 받아 둔 공식 정면 이미지 (없으면 단순 그림).</summary>
    public static string ArtPath => System.IO.Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "ClawHub", "art", "claw8ex-front.png");

    // ── 사진 좌표 (1024×820 렌더 기준) ─────────────────────
    private const double ArtW = 1024, ArtH = 820;
    /// <summary>본체가 차지하는 영역 — 전체 보기에서 이만큼이 꽉 차게.</summary>
    private static readonly Rect Body = new(80, 226, 866, 368);

    private enum Kind { Round, Square, Shoulder, Rear }

    /// <summary>버튼 자리: 중심, 크기(지름·변), 다가갈 때 배율 (본체 전체 보기 대비).</summary>
    private sealed record Place(double X, double Y, double W, double H, Kind Kind, double Zoom);

    private static readonly Dictionary<ClawButton, Place> Places = new()
    {
        [ClawButton.L3] = new(157, 315, 70, 70, Kind.Round, 2.4),
        [ClawButton.DpadUp] = new(213, 357, 26, 26, Kind.Square, 2.8),
        [ClawButton.DpadDown] = new(213, 401, 26, 26, Kind.Square, 2.8),
        [ClawButton.DpadLeft] = new(191, 379, 26, 26, Kind.Square, 2.8),
        [ClawButton.DpadRight] = new(235, 379, 26, 26, Kind.Square, 2.8),
        [ClawButton.Y] = new(869, 290, 30, 30, Kind.Round, 3),
        [ClawButton.X] = new(841, 315, 30, 30, Kind.Round, 3),
        [ClawButton.B] = new(897, 315, 30, 30, Kind.Round, 3),
        [ClawButton.A] = new(869, 340, 30, 30, Kind.Round, 3),
        [ClawButton.R3] = new(814, 379, 70, 70, Kind.Round, 2.4),
        // 어깨: 앞에서는 범퍼 윗면만 보인다. 트리거는 그 뒤(위)쪽
        [ClawButton.LB] = new(178, 247, 76, 14, Kind.Shoulder, 2.4),
        [ClawButton.LT] = new(160, 232, 64, 12, Kind.Shoulder, 2.4),
        [ClawButton.RB] = new(846, 247, 76, 14, Kind.Shoulder, 2.4),
        [ClawButton.RT] = new(864, 232, 64, 12, Kind.Shoulder, 2.4),
        // 뒷면 M1·M2: 정면에서 안 보이므로 손잡이 위에 점선 자리 + 이름
        [ClawButton.M1] = new(165, 488, 74, 34, Kind.Rear, 2),
        [ClawButton.M2] = new(859, 488, 74, 34, Kind.Rear, 2),
    };

    private readonly bool _photo;
    private readonly SchematicControllerMap? _schematic;
    private readonly Canvas _stage = new() { Width = ArtW, Height = ArtH };
    private readonly ScaleTransform _scale = new(1, 1);
    private readonly TranslateTransform _move = new();
    private readonly Dictionary<ClawButton, Shape> _spots = [];
    private readonly Border _caption = new() { HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Bottom, Padding = new Thickness(12, 4, 12, 4), Margin = new Thickness(0, 0, 0, 4), IsHitTestVisible = false, Opacity = 0 };
    private readonly TextBlock _captionName = new() { FontSize = 13, VerticalAlignment = VerticalAlignment.Center };
    private readonly TextBlock _captionKeys = new() { FontSize = 15, FontWeight = FontWeights.SemiBold, VerticalAlignment = VerticalAlignment.Center };
    private bool _cameraReady;

    public ControllerMap()
    {
        var image = TryLoadArt();
        _photo = image is not null;
        if (!_photo)
        {
            _schematic = new SchematicControllerMap();
            _schematic.SetBinding(SchematicControllerMap.RowsProperty, new System.Windows.Data.Binding(nameof(Rows)) { Source = this });
            _schematic.SetBinding(SchematicControllerMap.FocusedProperty, new System.Windows.Data.Binding(nameof(Focused)) { Source = this });
            _schematic.SetBinding(SchematicControllerMap.EditCommandProperty, new System.Windows.Data.Binding(nameof(EditCommand)) { Source = this });
            Children.Add(_schematic);
            return;
        }

        ClipToBounds = true;
        var art = new Image { Source = image, Width = ArtW, Height = ArtH, IsHitTestVisible = false };
        RenderOptions.SetBitmapScalingMode(art, BitmapScalingMode.HighQuality);
        _stage.Children.Add(art);
        foreach (var (button, place) in Places) AddSpot(button, place);
        _stage.RenderTransform = new TransformGroup { Children = { _scale, _move } };
        _stage.HorizontalAlignment = HorizontalAlignment.Left;
        _stage.VerticalAlignment = VerticalAlignment.Top;
        Children.Add(_stage);

        // 아래 띠: 고른 버튼 → 내는 키
        _caption.SetResourceReference(Border.BackgroundProperty, "Bg1");
        _caption.SetResourceReference(Border.BorderBrushProperty, "Accent");
        _caption.BorderThickness = new Thickness(0, 0, 0, 2);
        _captionName.SetResourceReference(TextBlock.ForegroundProperty, "TextDim");
        var arrow = new TextBlock { Text = "  →  ", FontSize = 13, VerticalAlignment = VerticalAlignment.Center };
        arrow.SetResourceReference(TextBlock.ForegroundProperty, "TextDim");
        _caption.Child = new StackPanel { Orientation = Orientation.Horizontal, Children = { _captionName, arrow, _captionKeys } };
        Children.Add(_caption);

        SizeChanged += (_, _) => AimCamera(animate: false);
    }

    private static BitmapImage? TryLoadArt()
    {
        try
        {
            if (!File.Exists(ArtPath)) return null;
            var bmp = new BitmapImage();
            bmp.BeginInit();
            bmp.CacheOption = BitmapCacheOption.OnLoad; // 파일을 잠그지 않게
            bmp.UriSource = new Uri(ArtPath);
            bmp.EndInit();
            bmp.Freeze();
            return bmp;
        }
        catch (Exception) { return null; } // 깨진 파일 — 단순 그림으로
    }

    private void AddSpot(ClawButton button, Place p)
    {
        Shape shape = p.Kind switch
        {
            Kind.Round => new Ellipse(),
            Kind.Square => new Rectangle { RadiusX = 4, RadiusY = 4 },
            Kind.Shoulder => new Rectangle { RadiusX = 7, RadiusY = 7 },
            _ => new Rectangle { RadiusX = 10, RadiusY = 10, StrokeDashArray = [3, 2] },
        };
        // 고리는 버튼보다 조금 크게 (사진의 버튼 가장자리를 가리지 않게)
        var ring = p.Kind is Kind.Round or Kind.Square ? 10 : 6;
        shape.Width = p.W + ring;
        shape.Height = p.H + ring;
        shape.Fill = Brushes.Transparent; // 누를 수 있게
        shape.Cursor = Cursors.Hand;
        shape.MouseLeftButtonUp += (_, e) =>
        {
            e.Handled = true;
            if (EditCommand?.CanExecute(button) == true) EditCommand.Execute(button);
        };
        Canvas.SetLeft(shape, p.X - shape.Width / 2);
        Canvas.SetTop(shape, p.Y - shape.Height / 2);
        _stage.Children.Add(shape);
        _spots[button] = shape;

        if (p.Kind == Kind.Rear)
        {
            var label = new TextBlock { Text = button == ClawButton.M1 ? "M1 · 뒷면" : "M2 · 뒷면", FontSize = 15, FontWeight = FontWeights.SemiBold, IsHitTestVisible = false };
            label.SetResourceReference(TextBlock.ForegroundProperty, "Text");
            label.Measure(new Size(double.PositiveInfinity, double.PositiveInfinity));
            Canvas.SetLeft(label, p.X - label.DesiredSize.Width / 2);
            Canvas.SetTop(label, p.Y - label.DesiredSize.Height / 2);
            _stage.Children.Add(label);
        }
    }

    // ── 상태 ───────────────────────────────────────────────

    private void OnRowsChanged(IEnumerable<ButtonRowViewModel>? old)
    {
        if (!_photo) return;
        if (old is not null) foreach (var r in old) r.PropertyChanged -= OnRowChanged;
        if (Rows is { } rows) foreach (var r in rows) r.PropertyChanged += OnRowChanged;
        RefreshSpots();
        RefreshCaption();
    }

    private void OnRowChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName is nameof(ButtonRowViewModel.IsMapped) or nameof(ButtonRowViewModel.IsLive) or nameof(ButtonRowViewModel.KeyLabels)
            or nameof(ButtonRowViewModel.Summary))
        {
            RefreshSpots();
            RefreshCaption();
        }
    }

    private void OnFocusedChanged()
    {
        if (!_photo) return;
        RefreshSpots();
        RefreshCaption();
        AimCamera(animate: true);
    }

    private ButtonRowViewModel? RowOf(ClawButton b) => Rows?.FirstOrDefault(r => r.Button == b);

    private void RefreshSpots()
    {
        foreach (var (button, shape) in _spots)
        {
            var row = RowOf(button);
            var focused = Focused == button;
            var live = row?.IsLive == true;
            var mapped = row?.IsMapped == true;
            var rear = Places[button].Kind == Kind.Rear;
            // 사진 위라 평소에는 아무것도 덮지 않는다: 지정된 버튼·고른 버튼만 고리
            if (focused || mapped || live || rear) shape.SetResourceReference(Shape.StrokeProperty, focused || mapped || live ? "Accent" : "StrokeStrong");
            else shape.Stroke = null;
            shape.StrokeThickness = focused ? 6 : 3.5;
            if (live) shape.SetResourceReference(Shape.FillProperty, "Accent");
            else shape.Fill = Brushes.Transparent;
            shape.Opacity = live ? 0.75 : 1;
            ToolTipService.SetToolTip(shape, row is null ? null : row.IsMapped ? $"{row.Name} → {KeysText(row)}" : row.Name);
        }
    }

    private static string KeysText(ButtonRowViewModel row) =>
        row.IsMacro ? row.Summary : row.KeyLabels.Count > 0 ? string.Join(" + ", row.KeyLabels.Select(k => k.Label)) : "게임패드 그대로";

    private void RefreshCaption()
    {
        if (Focused is { } f && RowOf(f) is { } row)
        {
            _captionName.Text = row.Name;
            _captionKeys.Text = KeysText(row);
            Fade(_caption, 1);
        }
        else Fade(_caption, 0);
    }

    private static void Fade(UIElement el, double to)
    {
        if (!MotionCore.Enabled) { el.BeginAnimation(OpacityProperty, null); el.Opacity = to; return; }
        el.BeginAnimation(OpacityProperty, new DoubleAnimation(to, new Duration(MotionCore.Ms(to > 0 ? 200 : 160))) { EasingFunction = MotionCore.Out });
    }

    // ── 카메라 ─────────────────────────────────────────────

    /// <summary>고른 버튼이 가운데 오게 다가가고, 없으면 본체 전체가 꽉 차게 물러난다.</summary>
    private void AimCamera(bool animate)
    {
        double w = ActualWidth, h = ActualHeight;
        if (w < 1 || h < 1) return;
        var fit = Math.Min(w / Body.Width, h / Body.Height);
        double scale, cx, cy;
        if (Focused is { } f && Places.TryGetValue(f, out var p))
        {
            scale = fit * p.Zoom;
            cx = p.X;
            cy = p.Y;
            // 가장자리 버튼에 다가가도 사진 밖(빈 곳)이 많이 보이지 않게 중심을 본체 안쪽으로 당긴다
            var halfW = w / 2 / scale;
            var halfH = h / 2 / scale;
            cx = Within(cx, Body.Left + halfW * 0.6, Body.Right - halfW * 0.6);
            cy = Within(cy, Body.Top + halfH * 0.5, Body.Bottom - halfH * 0.5);
        }
        else
        {
            scale = fit;
            cx = Body.Left + Body.Width / 2;
            cy = Body.Top + Body.Height / 2;
        }
        var tx = w / 2 - cx * scale;
        var ty = h / 2 - cy * scale;

        if (!animate || !MotionCore.Enabled || !_cameraReady)
        {
            _cameraReady = true;
            foreach (var (t, prop) in Props()) t.BeginAnimation(prop, null);
            _scale.ScaleX = _scale.ScaleY = scale;
            _move.X = tx;
            _move.Y = ty;
            return;
        }
        var d = MotionCore.Ms(480);
        Animate(_scale, ScaleTransform.ScaleXProperty, scale, d);
        Animate(_scale, ScaleTransform.ScaleYProperty, scale, d);
        Animate(_move, TranslateTransform.XProperty, tx, d);
        Animate(_move, TranslateTransform.YProperty, ty, d);
    }

    /// <summary>범위 안으로 (범위가 뒤집히면 가운데).</summary>
    private static double Within(double v, double min, double max) => min > max ? (min + max) / 2 : Math.Clamp(v, min, max);

    private IEnumerable<(Animatable, DependencyProperty)> Props() =>
    [
        (_scale, ScaleTransform.ScaleXProperty), (_scale, ScaleTransform.ScaleYProperty),
        (_move, TranslateTransform.XProperty), (_move, TranslateTransform.YProperty),
    ];

    /// <summary>지금 보이는 값에서 목표로 감속 (중간에 다른 버튼을 고르면 그 자리에서 이어서).</summary>
    private static void Animate(Animatable target, DependencyProperty prop, double to, TimeSpan duration)
    {
        var from = (double)target.GetValue(prop);
        target.SetValue(prop, to); // 애니메이션을 걷어도 목표 값으로 남는다
        target.BeginAnimation(prop, new DoubleAnimation(from, to, new Duration(duration)) { EasingFunction = MotionCore.Decelerate });
    }
}
