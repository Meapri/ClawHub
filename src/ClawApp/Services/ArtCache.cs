using System.Windows;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Imaging;

namespace ClawApp.Services;

/// <summary>게임 아트 로딩. 커버가 없으면 실행 파일 아이콘 + 제목 기반 그라데이션 타일로 대신한다.</summary>
public static class ArtCache
{
    private static readonly Dictionary<string, ImageSource?> Cache = [];

    /// <summary>타일·미리보기용 (가로 600px로 줄여서 읽음 — 메모리 절약).</summary>
    public static ImageSource? LoadImage(string? path) => Load(path, 600);

    /// <summary>
    /// 화면 전체에 까는 배경용: 화면 실제 픽셀 너비로 읽는다 (원본이 더 작으면 원본 그대로).
    /// 타일용 600px를 화면에 늘리면 뭉개져 보인다.
    /// </summary>
    public static ImageSource? LoadFullScreen(string? path) =>
        Load(path, (int)Math.Ceiling(SystemParameters.PrimaryScreenWidth * DpiScale()));

    private static double DpiScale() =>
        Application.Current?.MainWindow is { } w && PresentationSource.FromVisual(w) is { CompositionTarget: { } ct } ? ct.TransformToDevice.M11 : 1.5;

    private static ImageSource? Load(string? path, int decodeWidth)
    {
        if (string.IsNullOrEmpty(path) || !File.Exists(path)) return null;
        var key = $"{decodeWidth}:{path}";
        if (Cache.TryGetValue(key, out var hit)) return hit;
        try
        {
            // 원본보다 크게 디코딩하지 않는다 (작은 그림을 억지로 키우면 더 흐려짐)
            var frame = BitmapDecoder.Create(new Uri(path), BitmapCreateOptions.DelayCreation, BitmapCacheOption.None).Frames[0];
            var bmp = new BitmapImage();
            bmp.BeginInit();
            bmp.CacheOption = BitmapCacheOption.OnLoad; // 파일 잠금 방지
            bmp.UriSource = new Uri(path);
            if (frame.PixelWidth > decodeWidth) bmp.DecodePixelWidth = decodeWidth;
            bmp.EndInit();
            bmp.Freeze();
            return Cache[key] = bmp;
        }
        catch (Exception) { return Cache[key] = null; }
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
