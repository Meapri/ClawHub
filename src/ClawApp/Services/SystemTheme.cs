using System.Windows;
using System.Windows.Media;
using Microsoft.Win32;

namespace ClawApp.Services;

/// <summary>Windows 설정의 앱 모드(라이트/다크)와 강조색을 따라간다.</summary>
public static class SystemTheme
{
    public static bool IsLight =>
        Registry.GetValue(@"HKEY_CURRENT_USER\Software\Microsoft\Windows\CurrentVersion\Themes\Personalize", "AppsUseLightTheme", 1) is not 0;

    public static void Apply(Application app)
    {
        var colors = new ResourceDictionary
        {
            Source = new Uri($"pack://application:,,,/ClawApp;component/Themes/Colors.{(IsLight ? "Light" : "Dark")}.xaml"),
        };

        // Windows 11 규칙: 다크 모드는 밝은 강조색(Light2), 라이트 모드는 어두운 강조색(Dark1)
        if (AccentPalette() is { } palette)
        {
            var accent = IsLight ? palette[4] : palette[1];
            var hover = IsLight ? palette[3] : palette[2];
            colors["Accent"] = Frozen(accent);
            colors["AccentHover"] = Frozen(hover);
        }

        app.Resources.MergedDictionaries.Insert(0, colors);
    }

    /// <summary>
    /// HKCU\...\Explorer\Accent\AccentPalette: RGBA 8색 = [Light3, Light2, Light1, 기본, Dark1, Dark2, Dark3, ?].
    /// </summary>
    private static Color[]? AccentPalette()
    {
        if (Registry.GetValue(@"HKEY_CURRENT_USER\Software\Microsoft\Windows\CurrentVersion\Explorer\Accent", "AccentPalette", null)
            is not byte[] { Length: >= 32 } b)
            return null;
        return Enumerable.Range(0, 8).Select(i => Color.FromRgb(b[i * 4], b[i * 4 + 1], b[i * 4 + 2])).ToArray();
    }

    private static SolidColorBrush Frozen(Color c)
    {
        var brush = new SolidColorBrush(c);
        brush.Freeze();
        return brush;
    }
}
