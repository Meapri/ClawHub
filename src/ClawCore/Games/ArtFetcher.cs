using System.Net.Http.Json;
using System.Text.Json.Serialization;

namespace ClawCore.Games;

/// <summary>
/// 커버(600×900)·배경(히어로)·로고 아트를 Steam 스토어 공개 CDN에서 받아 로컬에 캐시한다.
/// Steam 외 게임은 이름으로 스토어를 검색해 정확히 일치하는 항목만 쓴다(엉뚱한 아트 방지).
/// </summary>
public sealed class ArtFetcher
{
    private static readonly HttpClient Http = new() { Timeout = TimeSpan.FromSeconds(20) };
    private static readonly TimeSpan RetryMissAfter = TimeSpan.FromDays(7);
    private static readonly string[] CdnBases =
    [
        "https://shared.akamai.steamstatic.com/store_item_assets/steam/apps/{0}/",
        "https://cdn.cloudflare.steamstatic.com/steam/apps/{0}/",
    ];

    public static string CacheDir(GameEntry g) =>
        Path.Combine(JsonStore.UserDir, "art", string.Concat(g.Id.Select(c => char.IsLetterOrDigit(c) ? c : '_')));

    /// <summary>이미 받아 둔 아트를 게임에 연결 (네트워크 없음).</summary>
    public static void ApplyCached(GameEntry g)
    {
        var dir = CacheDir(g);
        string? Hit(string name) => File.Exists(Path.Combine(dir, name)) ? Path.Combine(dir, name) : null;
        g.CoverImage ??= Hit("cover.jpg");
        g.HeroImage ??= Hit("hero.jpg");
        g.LogoImage ??= Hit("logo.png");
    }

    /// <summary>아트가 없는 게임들의 아트를 받는다. 받은 게임 수를 돌려준다.</summary>
    public async Task<int> FetchMissingAsync(IEnumerable<GameEntry> games, CancellationToken ct = default)
    {
        var count = 0;
        foreach (var g in games.Where(g => g.CoverImage is null || g.HeroImage is null || g.LogoImage is null))
        {
            try
            {
                if (await FetchAsync(g, ct)) count++;
            }
            catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException or IOException)
            {
                // 오프라인 등: 다음 실행 때 다시
            }
        }
        return count;
    }

    private async Task<bool> FetchAsync(GameEntry g, CancellationToken ct)
    {
        var dir = CacheDir(g);
        var miss = Path.Combine(dir, "not-found");
        if (File.Exists(miss) && DateTime.UtcNow - File.GetLastWriteTimeUtc(miss) < RetryMissAfter) return false;
        Directory.CreateDirectory(dir);

        var appId = g.SteamAppId ?? await SearchSteamAsync(g.SearchName ?? g.Title, ct);
        if (appId is null)
        {
            await File.WriteAllTextAsync(miss, g.SearchName ?? g.Title, ct);
            return false;
        }

        var got = false;
        if (g.CoverImage is null && await DownloadAsync(appId.Value, ["library_600x900_2x.jpg", "library_600x900.jpg"], Path.Combine(dir, "cover.jpg"), ct))
        { g.CoverImage = Path.Combine(dir, "cover.jpg"); got = true; }
        if (g.HeroImage is null && await DownloadAsync(appId.Value, ["library_hero.jpg", "header.jpg"], Path.Combine(dir, "hero.jpg"), ct))
        { g.HeroImage = Path.Combine(dir, "hero.jpg"); got = true; }
        if (g.LogoImage is null && await DownloadAsync(appId.Value, ["logo.png"], Path.Combine(dir, "logo.png"), ct))
        { g.LogoImage = Path.Combine(dir, "logo.png"); got = true; }
        return got;
    }

    private static async Task<bool> DownloadAsync(int appId, string[] names, string target, CancellationToken ct)
    {
        foreach (var name in names)
            foreach (var cdn in CdnBases)
            {
                using var resp = await Http.GetAsync(string.Format(cdn, appId) + name, ct);
                if (!resp.IsSuccessStatusCode) continue;
                var bytes = await resp.Content.ReadAsByteArrayAsync(ct);
                if (bytes.Length < 1024) continue; // 빈 이미지/오류 페이지
                var tmp = target + ".tmp";
                await File.WriteAllBytesAsync(tmp, bytes, ct);
                File.Move(tmp, target, overwrite: true);
                return true;
            }
        return false;
    }

    /// <summary>이름이 정규화 후 정확히 같은 첫 항목. "MapleStory"는 맞추되 "MapleStory M"은 고르지 않는다.</summary>
    private static async Task<int?> SearchSteamAsync(string name, CancellationToken ct)
    {
        var url = $"https://store.steampowered.com/api/storesearch/?term={Uri.EscapeDataString(name)}&l=english&cc=US";
        var result = await Http.GetFromJsonAsync<SearchResult>(url, ct);
        var key = Normalize(name);
        return result?.Items?.FirstOrDefault(i => Normalize(i.Name) == key)?.Id;
    }

    private static string Normalize(string s) => string.Concat(s.Where(char.IsLetterOrDigit)).ToLowerInvariant();

    private sealed class SearchResult
    {
        [JsonPropertyName("items")] public List<SearchItem>? Items { get; set; }
    }

    private sealed class SearchItem
    {
        [JsonPropertyName("id")] public int Id { get; set; }
        [JsonPropertyName("name")] public string Name { get; set; } = "";
    }
}
