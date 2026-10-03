using System.Net.Http.Json;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace ClawCore.Games;

/// <summary>컨트롤러 지원 정도 (Steam 스토어 기준).</summary>
public enum ControllerSupport { Unknown, None, Partial, Full }

/// <summary>Steam 스토어 게임 정보 중 프리셋 추천에 쓰는 것.</summary>
public sealed record SteamAppInfo(int AppId, string Name, ControllerSupport Controller, IReadOnlyList<string> Genres);

/// <summary>
/// Steam 스토어 공개 API (키·계정 없이 쓰는 것만): 이름 → 앱 ID 검색, 앱 상세(장르·컨트롤러 지원).
/// 커뮤니티 컨트롤러 설정 목록은 Web API 키가 있어야 해서 쓰지 않는다.
/// </summary>
public static class SteamStore
{
    private static readonly HttpClient Http = new() { Timeout = TimeSpan.FromSeconds(20) };

    /// <summary>이름이 정규화 후 정확히 같은 첫 항목. "MapleStory"는 맞추되 "MapleStory M"이나 DLC는 고르지 않는다.</summary>
    public static async Task<int?> FindAppIdAsync(string name, CancellationToken ct = default)
    {
        var url = $"https://store.steampowered.com/api/storesearch/?term={Uri.EscapeDataString(name)}&l=english&cc=US";
        var result = await Http.GetFromJsonAsync<SearchResult>(url, ct);
        var key = Normalize(name);
        return result?.Items?.FirstOrDefault(i => Normalize(i.Name) == key)?.Id;
    }

    /// <summary>앱 상세: 장르(영어)와 컨트롤러 지원. 스토어에 없으면 null.</summary>
    public static async Task<SteamAppInfo?> GetAppAsync(int appId, CancellationToken ct = default)
    {
        var url = $"https://store.steampowered.com/api/appdetails?appids={appId}&l=english&cc=US&filters=basic,genres,categories";
        using var doc = await JsonDocument.ParseAsync(await Http.GetStreamAsync(url, ct), cancellationToken: ct);
        if (!doc.RootElement.TryGetProperty(appId.ToString(), out var entry)
            || !entry.TryGetProperty("success", out var ok) || !ok.GetBoolean()
            || !entry.TryGetProperty("data", out var data)) return null;

        var name = data.TryGetProperty("name", out var n) ? n.GetString() ?? "" : "";
        var genres = data.TryGetProperty("genres", out var g)
            ? g.EnumerateArray().Select(x => x.GetProperty("description").GetString() ?? "").Where(s => s.Length > 0).ToList()
            : [];
        // controller_support: "full"/"partial" (없으면 지원 안 함). 카테고리 28 = 완전 지원, 18 = 부분 지원으로도 한 번 더 본다
        var support = data.TryGetProperty("controller_support", out var cs) ? cs.GetString() : null;
        var categories = data.TryGetProperty("categories", out var c)
            ? c.EnumerateArray().Select(x => x.TryGetProperty("id", out var id) ? id.GetInt32() : 0).ToHashSet()
            : [];
        var controller = support == "full" || categories.Contains(28) ? ControllerSupport.Full
            : support == "partial" || categories.Contains(18) ? ControllerSupport.Partial
            : ControllerSupport.None;
        return new SteamAppInfo(appId, name, controller, genres);
    }

    public static string Normalize(string s) => string.Concat(s.Where(char.IsLetterOrDigit)).ToLowerInvariant();

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
