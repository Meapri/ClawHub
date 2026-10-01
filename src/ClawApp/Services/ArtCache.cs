using System.Windows;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Imaging;

namespace ClawApp.Services;

/// <summary>게임 아트 로딩. 커버가 없으면 실행 파일 아이콘 + 제목 기반 그라데이션 타일로 대신한다.</summary>
public static class ArtCache
{
    private static readonly Dictionary<string, ImageSource?> Cache = [];

    public static ImageSource? LoadImage(string? path)
    {
        if (string.IsNullOrEmpty(path) || !File.Exists(path)) return null;
        if (Cache.TryGetValue(path, out var hit)) return hit;
        try
        {
            var bmp = new BitmapImage();
            bmp.BeginInit();
            bmp.CacheOption = BitmapCacheOption.OnLoad; // 파일 잠금 방지
            bmp.UriSource = new Uri(path);
            bmp.DecodePixelWidth = 600;
            bmp.EndInit();
            bmp.Freeze();
            return Cache[path] = bmp;
        }
        catch (Exception) { return Cache[path] = null; }
    }

    /// <summary>받은 이미지 바이트 → 미리보기 (캐시하지 않음, 다른 스레드에서 불러도 됨).</summary>
    public static ImageSource? FromBytes(byte[] bytes, int decodeWidth = 320)
    {
        try
        {
            using var stream = new MemoryStream(bytes);
            var bmp = new BitmapImage();
            bmp.BeginInit();
            bmp.CacheOption = BitmapCacheOption.OnLoad;
            bmp.StreamSource = stream;
            bmp.DecodePixelWidth = decodeWidth;
            bmp.EndInit();
            bmp.Freeze();
            return bmp;
        }
        catch (Exception) { return null; } // 디코더 없음(WebP 등)/손상
    }

    public static ImageSource? LoadIcon(string? exe)
    {
        if (string.IsNullOrEmpty(exe) || !File.Exists(exe)) return null;
        var key = "icon:" + exe;
        if (Cache.TryGetValue(key, out var hit)) return hit;
        try
        {
            using var icon = System.Drawing.Icon.ExtractAssociatedIcon(exe);
            if (icon is null) return Cache[key] = null;
            var src = Imaging.CreateBitmapSourceFromHIcon(icon.Handle, Int32Rect.Empty, BitmapSizeOptions.FromEmptyOptions());
            src.Freeze();
            return Cache[key] = src;
        }
        catch (Exception) { return Cache[key] = null; }
    }

    /// <summary>제목 해시로 정해지는 안정적인 그라데이션 (같은 게임은 항상 같은 색).</summary>
    public static Brush PlaceholderBrush(string title)
    {
        var h = (uint)title.Aggregate(17, (a, c) => a * 31 + c);
        var hue = h % 360;
        var brush = new LinearGradientBrush(FromHsl(hue, 0.55, 0.38), FromHsl((hue + 40) % 360, 0.6, 0.18), 45);
        brush.Freeze();
        return brush;
    }

    private static System.Windows.Media.Color FromHsl(double h, double s, double l)
    {
        double c = (1 - Math.Abs(2 * l - 1)) * s, x = c * (1 - Math.Abs(h / 60 % 2 - 1)), m = l - c / 2;
        var (r, g, b) = h switch
        {
            < 60 => (c, x, 0d), < 120 => (x, c, 0d), < 180 => (0d, c, x),
            < 240 => (0d, x, c), < 300 => (x, 0d, c), _ => (c, 0d, x),
        };
        return System.Windows.Media.Color.FromRgb((byte)((r + m) * 255), (byte)((g + m) * 255), (byte)((b + m) * 255));
    }
}
