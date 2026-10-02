using System.IO;
using System.Windows.Media;
using System.Windows.Media.Imaging;

namespace ClawApp.Services;

/// <summary>
/// 배경 사진에서 강조색 뽑기 (Windows 10 Mobile의 "배경에서 강조색 선택").
/// 작게 줄인 그림에서 색상(hue)별로 채도·밝기 가중치를 모아 가장 눈에 띄는 색을 고르고,
/// 테마에 맞게 밝기를 맞춘다 (라이트 = 진하게 + 흰 글자, 다크 = 밝게 + 검은 글자).
/// </summary>
public static class AccentFromImage
{
    public readonly record struct Palette(Color Accent, Color AccentHover, Color TileAccent);

    private static readonly object CacheLock = new();
    private static (string Path, DateTime Written, bool Light, Palette? Result)? _last;

    /// <summary>
    /// 같은 사진(경로·수정 시각)·같은 테마면 지난 결과를 그대로 쓴다. 큰 PNG는 64px로 줄여 읽어도 전체를 디코딩해야 해서
    /// 한 번에 ~110ms (10MB PNG 실측) — 홈 화면 설정의 슬라이더를 움직일 때마다 다시 계산하면 그만큼 멈칫한다.
    /// 다른 스레드에서 불러도 된다 (시작할 때 미리 계산).
    /// </summary>
    public static Palette? FromFile(string? path, bool lightTheme)
    {
        if (string.IsNullOrEmpty(path) || !File.Exists(path)) return null;
        DateTime written;
        try { written = File.GetLastWriteTimeUtc(path); }
        catch (Exception) { return null; }
        // 계산하는 동안에도 잠가 둔다: 시작할 때 뒤에서 계산 중이면 UI 스레드는 같은 계산을 또 하지 않고 그 결과를 기다린다
        lock (CacheLock)
        {
            if (_last is { } c && c.Light == lightTheme && c.Written == written && string.Equals(c.Path, path, StringComparison.OrdinalIgnoreCase))
                return c.Result;
            var result = Compute(path, lightTheme);
            _last = (path, written, lightTheme, result);
            return result;
        }
    }

    private static Palette? Compute(string path, bool lightTheme)
    {
        try
        {
            var bmp = new BitmapImage();
            bmp.BeginInit();
            bmp.UriSource = new Uri(path);
            bmp.DecodePixelWidth = 64;
            bmp.CacheOption = BitmapCacheOption.OnLoad;
            bmp.EndInit();
            var src = new FormatConvertedBitmap(bmp, PixelFormats.Bgra32, null, 0);
            var stride = src.PixelWidth * 4;
            var px = new byte[stride * src.PixelHeight];
            src.CopyPixels(px, stride, 0);
            return Pick(px, lightTheme);
        }
        catch (Exception) { return null; } // 디코더 없음/손상
    }

    private static Palette? Pick(byte[] px, bool light)
    {
        const int buckets = 36;
        var weight = new double[buckets];
        var hueSum = new double[buckets];
        var satSum = new double[buckets];
        for (var i = 0; i < px.Length; i += 4)
        {
            var (h, s, v) = ToHsv(px[i + 2], px[i + 1], px[i]);
            if (v < 0.2 || s < 0.15) continue; // 검정·회색은 강조색 후보가 아님
            var w = s * s * v;
            var b = (int)(h / 360 * buckets) % buckets;
            weight[b] += w;
            hueSum[b] += h * w;
            satSum[b] += s * w;
        }
        // 이웃 칸까지 합쳐 가장 무거운 색상대
        var best = -1;
        var bestW = 0.0;
        for (var b = 0; b < buckets; b++)
        {
            var w = weight[b] + 0.5 * (weight[(b + 1) % buckets] + weight[(b + buckets - 1) % buckets]);
            if (w > bestW) { bestW = w; best = b; }
        }
        if (best < 0 || weight[best] <= 0) return null; // 흑백 사진

        var hue = hueSum[best] / weight[best];
        var sat = Math.Clamp(satSum[best] / weight[best], 0.45, 0.85);
        return light
            ? new Palette(FromHsl(hue, sat, 0.36), FromHsl(hue, sat, 0.42), FromHsl(hue, sat, 0.36))
            : new Palette(FromHsl(hue, sat, 0.72), FromHsl(hue, sat, 0.66), FromHsl(hue, sat, 0.38));
    }

    private static (double H, double S, double V) ToHsv(byte r8, byte g8, byte b8)
    {
        double r = r8 / 255.0, g = g8 / 255.0, b = b8 / 255.0;
        var max = Math.Max(r, Math.Max(g, b));
        var min = Math.Min(r, Math.Min(g, b));
        var d = max - min;
        double h = d == 0 ? 0
            : max == r ? 60 * (((g - b) / d + 6) % 6)
            : max == g ? 60 * ((b - r) / d + 2)
            : 60 * ((r - g) / d + 4);
        return (h, max == 0 ? 0 : d / max, max);
    }

    private static Color FromHsl(double h, double s, double l)
    {
        var c = (1 - Math.Abs(2 * l - 1)) * s;
        var x = c * (1 - Math.Abs(h / 60 % 2 - 1));
        var m = l - c / 2;
        var (r, g, b) = h switch
        {
            < 60 => (c, x, 0.0),
            < 120 => (x, c, 0.0),
            < 180 => (0.0, c, x),
            < 240 => (0.0, x, c),
            < 300 => (x, 0.0, c),
            _ => (c, 0.0, x),
        };
        return Color.FromRgb((byte)Math.Round((r + m) * 255), (byte)Math.Round((g + m) * 255), (byte)Math.Round((b + m) * 255));
    }
}
