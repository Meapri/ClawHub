using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json.Serialization;

namespace ClawCore.Games;

public enum ArtKind { Cover, Hero, Logo }

/// <summary>사용자가 고를 아트 후보 하나. <see cref="Urls"/>는 원본(앞에서부터 시도), <see cref="ThumbUrl"/>은 목록 미리보기.</summary>
public sealed record ArtCandidate(string Source, string Title, string ThumbUrl, IReadOnlyList<string> Urls, int? SteamAppId = null);

/// <summary>
/// 아트 직접 고르기용 검색: Steam 스토어 검색(키 불필요) + SteamGridDB(사용자 API 키가 있을 때).
/// 모든 호출에 시간 제한을 건다 (검색 10초, 이미지 20초).
/// </summary>
public static class ArtSources
{
    public static readonly TimeSpan SearchTimeout = TimeSpan.FromSeconds(10);
    public static readonly TimeSpan DownloadTimeout = TimeSpan.FromSeconds(20);
    /// <summary>원본 이미지 최대 크기 (SteamGridDB 히어로 PNG가 수 MB).</summary>
    public const int MaxImageBytes = 25 * 1024 * 1024;

    private static readonly HttpClient Http = CreateClient();
    private const string SgdbBase = "https://www.steamgriddb.com/api/v2/";
    private static readonly string[] SteamCdns =
    [
        "https://shared.akamai.steamstatic.com/store_item_assets/steam/apps/{0}/",
        "https://cdn.cloudflare.steamstatic.com/steam/apps/{0}/",
    ];

    private static HttpClient CreateClient()
    {
        var http = new HttpClient { Timeout = TimeSpan.FromSeconds(30) }; // 요청별 제한은 CancellationToken으로
        http.DefaultRequestHeaders.UserAgent.ParseAdd("ClawHub/1.0");
        return http;
    }

    private static CancellationTokenSource Limit(CancellationToken ct, TimeSpan timeout)
    {
        var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        cts.CancelAfter(timeout);
        return cts;
    }

    // ── Steam ──────────────────────────────────────────────

    /// <summary>Steam CDN의 종류별 원본 파일 (앞에서부터 시도).</summary>
    public static IReadOnlyList<string> SteamUrls(int appId, ArtKind kind)
    {
        string[] names = kind switch
        {
            ArtKind.Cover => ["library_600x900_2x.jpg", "library_600x900.jpg"],
            ArtKind.Hero => ["library_hero.jpg"],
            _ => ["logo.png"],
        };
        return [.. names.SelectMany(n => SteamCdns.Select(cdn => string.Format(cdn, appId) + n))];
    }

    private static string SteamThumb(int appId, ArtKind kind) => string.Format(SteamCdns[0], appId) + kind switch
    {
        ArtKind.Cover => "library_600x900.jpg",
        ArtKind.Hero => "library_hero.jpg",
        _ => "logo.png",
    };

    /// <summary>Steam 스토어 검색 결과 앱들을 후보로 (정확히 일치하지 않아도 여러 개).</summary>
    public static async Task<IReadOnlyList<ArtCandidate>> SearchSteamAsync(string term, ArtKind kind, CancellationToken ct = default)
    {
        using var cts = Limit(ct, SearchTimeout);
        var url = $"https://store.steampowered.com/api/storesearch/?term={Uri.EscapeDataString(term)}&l=english&cc=US";
        var result = await Http.GetFromJsonAsync<SteamSearch>(url, cts.Token);
        return [.. (result?.Items ?? []).Where(i => i.Type is null or "app").Take(12)
            .Select(i => new ArtCandidate("Steam", i.Name, SteamThumb(i.Id, kind), SteamUrls(i.Id, kind), i.Id))];
    }

    private sealed class SteamSearch
    {
        [JsonPropertyName("items")] public List<SteamItem>? Items { get; set; }
    }

    private sealed class SteamItem
    {
        [JsonPropertyName("type")] public string? Type { get; set; }
        [JsonPropertyName("id")] public int Id { get; set; }
        [JsonPropertyName("name")] public string Name { get; set; } = "";
    }

    // ── SteamGridDB ────────────────────────────────────────

    /// <summary>
    /// SteamGridDB: 이름 자동완성으로 게임 최대 3개 → 각 게임의 이미지(커버 600×900 / 히어로 / 로고, 정적 PNG·JPEG만).
    /// API 키가 틀리면 <see cref="UnauthorizedAccessException"/>.
    /// </summary>
    public static async Task<IReadOnlyList<ArtCandidate>> SearchSteamGridDbAsync(string term, ArtKind kind, string apiKey, CancellationToken ct = default)
    {
        using var cts = Limit(ct, SearchTimeout);
        var games = await SgdbGetAsync<List<SgdbGame>>($"search/autocomplete/{Uri.EscapeDataString(term)}", apiKey, cts.Token) ?? [];

        var (path, query) = kind switch
        {
            ArtKind.Cover => ("grids", "dimensions=600x900&mimes=image/png,image/jpeg&types=static&nsfw=false&humor=false"),
            ArtKind.Hero => ("heroes", "mimes=image/png,image/jpeg&types=static&nsfw=false&humor=false"),
            _ => ("logos", "mimes=image/png&types=static&nsfw=false&humor=false"),
        };

        var lists = await Task.WhenAll(games.Take(3).Select(async g =>
        {
            var images = await SgdbGetAsync<List<SgdbImage>>($"{path}/game/{g.Id}?{query}", apiKey, cts.Token) ?? [];
            return images.Where(i => !string.IsNullOrEmpty(i.Url)).Take(12)
                .Select(i => new ArtCandidate("SteamGridDB", g.Name, string.IsNullOrEmpty(i.Thumb) ? i.Url! : i.Thumb!, [i.Url!]));
        }));
        return [.. lists.SelectMany(l => l)];
    }

    private static async Task<T?> SgdbGetAsync<T>(string relative, string apiKey, CancellationToken ct) where T : class
    {
        using var req = new HttpRequestMessage(HttpMethod.Get, SgdbBase + relative);
        req.Headers.Authorization = new AuthenticationHeaderValue("Bearer", apiKey.Trim());
        using var resp = await Http.SendAsync(req, ct);
        if (resp.StatusCode is HttpStatusCode.Unauthorized or HttpStatusCode.Forbidden)
            throw new UnauthorizedAccessException("SteamGridDB API 키가 올바르지 않아요");
        if (resp.StatusCode == HttpStatusCode.NotFound) return null; // 해당 게임에 이미지 없음
        resp.EnsureSuccessStatusCode();
        var body = await resp.Content.ReadFromJsonAsync<SgdbResponse<T>>(ct);
        return body is { Success: true } ? body.Data : null;
    }

    private sealed class SgdbResponse<T>
    {
        [JsonPropertyName("success")] public bool Success { get; set; }
        [JsonPropertyName("data")] public T? Data { get; set; }
    }

    private sealed class SgdbGame
    {
        [JsonPropertyName("id")] public int Id { get; set; }
        [JsonPropertyName("name")] public string Name { get; set; } = "";
    }

    private sealed class SgdbImage
    {
        [JsonPropertyName("url")] public string? Url { get; set; }
        [JsonPropertyName("thumb")] public string? Thumb { get; set; }
    }

    // ── 받기 ───────────────────────────────────────────────

    /// <summary>주소들을 차례로 시도해 처음 받아진 이미지 (형식 확인 포함). 모두 실패하면 null.</summary>
    public static async Task<(byte[] Bytes, string Extension)?> DownloadImageAsync(IEnumerable<string> urls, CancellationToken ct = default)
    {
        foreach (var url in urls)
        {
            using var cts = Limit(ct, DownloadTimeout);
            try
            {
                using var resp = await Http.GetAsync(url, HttpCompletionOption.ResponseHeadersRead, cts.Token);
                if (!resp.IsSuccessStatusCode) continue;
                if (resp.Content.Headers.ContentLength > MaxImageBytes) continue;
                var bytes = await resp.Content.ReadAsByteArrayAsync(cts.Token);
                if (bytes.Length is < 1024 or > MaxImageBytes) continue; // 빈 이미지/오류 페이지
                if (ImageFormat.Extension(bytes) is { } ext) return (bytes, ext);
            }
            catch (HttpRequestException) { /* 다음 주소 */ }
            catch (OperationCanceledException) when (!ct.IsCancellationRequested) { /* 이 주소만 시간 초과 */ }
        }
        return null;
    }
}

/// <summary>파일 앞부분으로 이미지 형식 판별 (확장자/서버 Content-Type을 믿지 않음).</summary>
public static class ImageFormat
{
    public static string? Extension(ReadOnlySpan<byte> b)
    {
        if (b.Length >= 8 && b[0] == 0x89 && b[1] == 'P' && b[2] == 'N' && b[3] == 'G') return ".png";
        if (b.Length >= 3 && b[0] == 0xFF && b[1] == 0xD8 && b[2] == 0xFF) return ".jpg";
        if (b.Length >= 12 && b[..4].SequenceEqual("RIFF"u8) && b[8..12].SequenceEqual("WEBP"u8)) return ".webp";
        if (b.Length >= 6 && b[..3].SequenceEqual("GIF"u8)) return ".gif";
        if (b.Length >= 2 && b[0] == 'B' && b[1] == 'M') return ".bmp";
        return null;
    }
}

/// <summary>
/// 사용자가 직접 고른 아트. 아트 캐시 폴더에 <c>user-{kind}-{시각}.{ext}</c>로 저장하고, 있으면 자동 아트보다 우선한다.
/// 파일 이름에 시각을 넣어 이미지 캐시(경로 기준)가 예전 그림을 돌려주지 않게 한다.
/// </summary>
public static class UserArt
{
    private static string Prefix(ArtKind kind) => $"user-{kind.ToString().ToLowerInvariant()}-";

    public static string? Find(GameEntry g, ArtKind kind)
    {
        var dir = ArtFetcher.CacheDir(g);
        if (!Directory.Exists(dir)) return null;
        return Directory.EnumerateFiles(dir, Prefix(kind) + "*")
            .OrderByDescending(f => f, StringComparer.Ordinal).FirstOrDefault();
    }

    /// <summary>사용자 아트가 있으면 게임에 연결 (자동/런처 아트를 덮어씀).</summary>
    public static void Apply(GameEntry g)
    {
        if (Find(g, ArtKind.Cover) is { } c) g.CoverImage = c;
        if (Find(g, ArtKind.Hero) is { } h) g.HeroImage = h;
        if (Find(g, ArtKind.Logo) is { } l) g.LogoImage = l;
    }

    public static string Save(GameEntry g, ArtKind kind, byte[] bytes)
    {
        var ext = ImageFormat.Extension(bytes) ?? throw new InvalidDataException("지원하지 않는 이미지 형식이에요");
        var dir = ArtFetcher.CacheDir(g);
        Directory.CreateDirectory(dir);
        var target = Path.Combine(dir, $"{Prefix(kind)}{DateTime.UtcNow:yyyyMMddHHmmssfff}{ext}");
        var tmp = Path.Combine(dir, $"pending-{Guid.NewGuid():N}.tmp"); // Clear가 지우지 않도록 접두사 다르게
        File.WriteAllBytes(tmp, bytes);
        Clear(g, kind);
        File.Move(tmp, target, overwrite: true);
        return target;
    }

    public static string SaveFromFile(GameEntry g, ArtKind kind, string path)
    {
        var info = new FileInfo(path);
        if (!info.Exists) throw new FileNotFoundException("파일이 없어요", path);
        if (info.Length > ArtSources.MaxImageBytes) throw new InvalidDataException("이미지 파일이 너무 커요 (25MB 이하)");
        return Save(g, kind, File.ReadAllBytes(path));
    }

    public static void Clear(GameEntry g, ArtKind kind)
    {
        var dir = ArtFetcher.CacheDir(g);
        if (!Directory.Exists(dir)) return;
        foreach (var f in Directory.EnumerateFiles(dir, Prefix(kind) + "*").ToList())
        {
            try { File.Delete(f); }
            catch (IOException) { /* 다른 곳에서 열려 있음 — 이름순으로 새 파일이 우선하므로 무시 */ }
        }
    }
}
