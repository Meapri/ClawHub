using System.Globalization;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Data;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Shapes;

namespace ClawApp.Views;

/// <summary>
/// 패드 버튼 그림 (Kenney Input Prompts, CC0). Key = "A"·"LB"·"DpadUp"·"M1"·"☰" 등 (문자열이나 ClawButton 값).
/// 모두 흰 실루엣이라 글자색(Text)으로 칠한다 → 라이트/다크 모두 맞고 Lucide 아이콘과 같은 단색 톤.
/// 모르는 키는 글자로 보여 준다.
/// </summary>
public sealed class ButtonGlyph : Grid
{
    public static readonly DependencyProperty KeyProperty = DependencyProperty.Register(
        nameof(Key), typeof(object), typeof(ButtonGlyph), new PropertyMetadata(null, (d, _) => ((ButtonGlyph)d).Update()));

    public object? Key
    {
        get => GetValue(KeyProperty);
        set => SetValue(KeyProperty, value);
    }

    /// <summary>칠할 색. 지정하지 않으면 테마 글자색.</summary>
    public static readonly DependencyProperty TintProperty = DependencyProperty.Register(
        nameof(Tint), typeof(Brush), typeof(ButtonGlyph), new PropertyMetadata(null, (d, _) => ((ButtonGlyph)d).Update()));

    public Brush? Tint
    {
        get => (Brush?)GetValue(TintProperty);
        set => SetValue(TintProperty, value);
    }

    private static readonly Dictionary<string, string> Map = new(StringComparer.OrdinalIgnoreCase)
    {
        ["A"] = "button_a", ["B"] = "button_b", ["X"] = "button_x", ["Y"] = "button_y",
        ["LB"] = "lb", ["RB"] = "rb", ["LT"] = "lt", ["RT"] = "rt",
        ["Dpad"] = "dpad", ["DpadUp"] = "dpad_up", ["DpadDown"] = "dpad_down", ["DpadLeft"] = "dpad_left", ["DpadRight"] = "dpad_right",
        ["L3"] = "stick_l_press", ["R3"] = "stick_r_press", ["LS"] = "stick_l", ["RS"] = "stick_r",
        ["M1"] = "elite_paddle_bottom_left", ["M2"] = "elite_paddle_bottom_right",
        ["☰"] = "button_menu", ["Menu"] = "button_menu", ["…"] = "button_view", ["View"] = "button_view",
        ["Guide"] = "guide",
        ["MouseLeft"] = "mouse_left", ["MouseRight"] = "mouse_right", ["MouseMiddle"] = "mouse_scroll",
        ["MouseWheelUp"] = "mouse_scroll_up", ["MouseWheelDown"] = "mouse_scroll_down",
        ["MouseBack"] = "mouse_side_back", ["MouseForward"] = "mouse_side_forward",
    };

    private static readonly Dictionary<string, ImageSource> Cache = [];

    private static ImageSource Load(string file)
    {
        if (Cache.TryGetValue(file, out var hit)) return hit;
        var bmp = new BitmapImage();
        bmp.BeginInit();
        bmp.UriSource = new Uri($"pack://application:,,,/Assets/Input/{file}.png");
        bmp.DecodePixelWidth = 64;
        bmp.CacheOption = BitmapCacheOption.OnLoad;
        bmp.EndInit();
        bmp.Freeze();
        return Cache[file] = bmp;
    }

    private void Update()
    {
        Children.Clear();
        var key = Key?.ToString();
        if (string.IsNullOrEmpty(key)) return;

        if (!Map.TryGetValue(key, out var file))
        {
            var text = new TextBlock
            {
                Text = key, FontSize = 11, FontWeight = FontWeights.Bold,
                HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center,
            };
            if (Tint is { } tt) text.Foreground = tt;
            Children.Add(text);
            return;
        }

        var mask = new ImageBrush(Load(file)) { Stretch = Stretch.Uniform };
        RenderOptions.SetBitmapScalingMode(mask, BitmapScalingMode.HighQuality);
        var r = new Rectangle { OpacityMask = mask };
        if (Tint is { } t) r.Fill = t;
        else r.SetResourceReference(Shape.FillProperty, "Text");
        Children.Add(r);
    }
}

/// <summary>설정 분류 키(general·controller …) → Lucide 아이콘 글자 (Themes/Icons.xaml의 Icon.* 문자열).</summary>
public sealed class CategoryIconConverter : IValueConverter
{
    public static readonly CategoryIconConverter Instance = new();

    private static readonly Dictionary<string, string> Map = new(StringComparer.OrdinalIgnoreCase)
    {
        ["general"] = "Settings", ["controller"] = "Gamepad2", ["light"] = "Lightbulb", ["performance"] = "Zap",
        ["display"] = "Monitor", ["battery"] = "Battery", ["windows"] = "AppWindow", ["library"] = "Library", ["about"] = "Info",
    };

    public object? Convert(object? value, Type targetType, object? parameter, CultureInfo culture) =>
        value?.ToString() is { } k && Map.TryGetValue(k, out var name) && Application.Current?.TryFindResource("Icon." + name) is string glyph
            ? glyph
            : "";

    public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture) => throw new NotSupportedException();
}
