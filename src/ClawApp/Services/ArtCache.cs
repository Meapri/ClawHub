using System.Collections.Concurrent;
using System.Windows;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Imaging;

namespace ClawApp.Services;

/// <summary>
/// 게임 아트 로딩. 커버가 없으면 실행 파일 아이콘 + 제목 기반 그라데이션 타일로 대신한다.
/// 스레드 안전: 같은 그림을 여러 스레드가 동시에 찾으면 한 번만 디코딩하고 나머지는 그 결과를 기다린다
/// (시작할 때 <see cref="Prewarm"/>가 뒤에서 미리 디코딩해 두면 UI 스레드는 기다릴 것 없이 꺼내 쓴다).
/// 그림은 모두 Freeze돼 있어 어느 스레드에서 만들었든 UI에서 쓸 수 있다.
/// </summary>
public static class ArtCache
{
    private static readonly ConcurrentDictionary<string, Lazy<ImageSource?>> Cache = new();

    /// <summary>
    /// 화면 전체 크기 그림은 한 장에 ~18MB(2880×1620 BGRA)라 최근 것 몇 장만 둔다 (게임 아트 배경을 선명하게 쓰면
    /// 포커스한 게임마다 한 장씩 늘어난다). 타일용 600px 그림은 라이브러리 크기만큼만 생기므로 그대로 둔다.
    /// </summary>
    private const int MaxFullScreen = 4;
    private static readonly LinkedList<string> FullScreenOrder = new();
    private static readonly object FullScreenLock = new();

    private const int TileWidth = 600;

    /// <summary>타일·미리보기용 (가로 600px로 줄여서 읽음 — 메모리 절약).</summary>
    public static ImageSource? LoadImage(string? path) => Load(path, TileWidth);

    /// <summary>
    /// 화면 전체에 까는 배경용: 화면 실제 픽셀 너비로 읽는다 (원본이 더 작으면 원본 그대로).
    /// 타일용 600px를 화면에 늘리면 뭉개져 보인다.
    /// </summary>
    public static ImageSource? LoadFullScreen(string? path) => Load(path, FullScreenWidth());

    /// <summary>화면 전체 그림의 디코딩 너비 (UI 스레드에서 부를 것 — 창의 DPI를 본다).</summary>
    public static int FullScreenWidth() => (int)Math.Ceiling(SystemParameters.PrimaryScreenWidth * DpiScale());

    private static double DpiScale() =>
        Application.Current?.MainWindow is { } w && PresentationSource.FromVisual(w) is { CompositionTarget: { } ct } ? ct.TransformToDevice.M11 : 1.5;

    private static ImageSource? Load(string? path, int decodeWidth)
    {
        if (string.IsNullOrEmpty(path) || !File.Exists(path)) return null;
        var key = $"{decodeWidth}:{path}";
        var image = Cache.GetOrAdd(key, k => new Lazy<ImageSource?>(() => Decode(path, decodeWidth))).Value;
        if (decodeWidth > TileWidth) TouchFullScreen(key);
        return image;
    }

    private static ImageSource? Decode(string path, int decodeWidth)
    {
        try
        {
            // 원본보다 크게 디코딩하지 않는다 (작은 그림을 억지로 키우면 더 흐려짐).
            // 크기는 헤더만 읽고 파일을 바로 닫는다 (디코더를 열어 둔 채로 두면 GC 전까지 파일이 잠긴다)
            int pixelWidth;
            using (var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete))
                pixelWidth = BitmapDecoder.Create(stream, BitmapCreateOptions.DelayCreation, BitmapCacheOption.None).Frames[0].PixelWidth;

            var bmp = new BitmapImage();
            bmp.BeginInit();
            bmp.CacheOption = BitmapCacheOption.OnLoad; // 파일 잠금 방지
            bmp.UriSource = new Uri(path);
            if (pixelWidth > decodeWidth) bmp.DecodePixelWidth = decodeWidth;
            bmp.EndInit();
            bmp.Freeze();
            return bmp;
        }
        catch (Exception) { return null; }
    }

    /// <summary>화면 전체 그림을 최근 사용 순서로 두고, 넘치면 가장 오래된 것을 캐시에서 뺀다.</summary>
    private static void TouchFullScreen(string key)
    {
        lock (FullScreenLock)
        {
            FullScreenOrder.Remove(key);
            FullScreenOrder.AddFirst(key);
            while (FullScreenOrder.Count > MaxFullScreen)
            {
                Cache.TryRemove(FullScreenOrder.Last!.Value, out _);
                FullScreenOrder.RemoveLast();
            }
        }
    }

    /// <summary>
    /// 시작할 때 뒤에서 미리 디코딩해 둔다 (UI 스레드가 창을 만드는 동안). 실패는 조용히 무시 — 그때는 UI 스레드가 직접 읽는다.
    /// <paramref name="fullScreenWidth"/>는 UI 스레드에서 <see cref="FullScreenWidth"/>로 구해 넘긴다.
    /// </summary>
    public static void Prewarm(IEnumerable<string?> tileImages, string? fullScreenImage, int fullScreenWidth)
    {
        if (fullScreenImage is not null) _ = Load(fullScreenImage, fullScreenWidth);
        foreach (var p in tileImages) _ = Load(p, TileWidth);
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
        return Cache.GetOrAdd("icon:" + exe, _ => new Lazy<ImageSource?>(() => ExtractIcon(exe))).Value;
    }

    private static ImageSource? ExtractIcon(string exe)
    {
        if (Path.GetExtension(exe).ToLowerInvariant() is ".png" or ".jpg" or ".jpeg") return Decode(exe, 96); // 패키지 앱 로고
        try
        {
            using var icon = System.Drawing.Icon.ExtractAssociatedIcon(exe);
            if (icon is null) return null;
            var src = Imaging.CreateBitmapSourceFromHIcon(icon.Handle, Int32Rect.Empty, BitmapSizeOptions.FromEmptyOptions());
            src.Freeze();
            return src;
        }
        catch (Exception) { return null; }
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
