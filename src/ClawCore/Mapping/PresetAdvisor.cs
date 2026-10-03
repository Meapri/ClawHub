using ClawCore.Games;

namespace ClawCore.Mapping;

/// <summary>
/// 게임에 맞는 버튼 배치 추천. <see cref="PresetId"/>가 null이면 "게임패드 그대로" (매핑 안 함).
/// <see cref="Alternative"/>는 함께 보여 줄 두 번째 안 (부분 지원 게임의 키보드 배치 등).
/// </summary>
public sealed record PresetAdvice(
    string? PresetId,
    string Headline,
    string Reason,
    string Source,
    string? Alternative = null,
    int? SteamAppId = null);

/// <summary>
/// 키·계정 없이 게임별 추천 배치를 고른다:
/// 1) ClawHub에 그 게임 전용 프리셋이 있으면 그것 (지금은 메이플스토리),
/// 2) Steam 스토어에서 같은 이름의 게임을 찾아 컨트롤러 지원이 완전하면 "게임패드 그대로",
/// 3) 지원이 없으면 장르로 키보드·마우스 템플릿을 고른다.
/// 결과는 2주 동안 캐시한다 (스토어 조회를 매번 하지 않게).
/// </summary>
public static class PresetAdvisor
{
    private static readonly TimeSpan CacheFor = TimeSpan.FromDays(14);
    private const string CacheFile = "preset-advice.json";

    /// <summary>ClawHub에 들어 있는 게임 전용 프리셋 (정규화한 이름 → 프리셋). 한국판·Steam판 이름을 모두 둔다.</summary>
    private static readonly Dictionary<string, string> Curated = new()
    {
        ["maplestory"] = MappingPresets.MapleStoryId,
        ["메이플스토리"] = MappingPresets.MapleStoryId,
    };

    public static async Task<PresetAdvice> AdviseAsync(GameEntry game, CancellationToken ct = default)
    {
        var name = game.SearchName ?? game.Title;
        var key = SteamStore.Normalize(name);
        if (Curated.TryGetValue(key, out var curated) || Curated.TryGetValue(SteamStore.Normalize(game.Title), out curated))
            return new PresetAdvice(curated, Label(curated), "이 게임의 기본 키 설정에 맞춘 ClawHub 내장 배치예요.", "ClawHub 내장");

        var path = Path.Combine(JsonStore.UserDir, CacheFile);
        var cache = JsonStore.Load<Dictionary<string, CachedAdvice>>(path);
        if (cache.TryGetValue(key, out var hit) && DateTime.UtcNow - hit.At < CacheFor) return hit.Advice;

        var advice = await LookupAsync(game, name, ct);
        cache[key] = new CachedAdvice { At = DateTime.UtcNow, Advice = advice };
        try { JsonStore.Save(path, cache); } catch (IOException) { /* 캐시는 없어도 된다 */ }
        return advice;
    }

    private static async Task<PresetAdvice> LookupAsync(GameEntry game, string name, CancellationToken ct)
    {
        var appId = game.SteamAppId ?? await SteamStore.FindAppIdAsync(name, ct);
        if (appId is null || await SteamStore.GetAppAsync(appId.Value, ct) is not { } app)
            return new PresetAdvice(null, "정보를 찾지 못했어요",
                "Steam 스토어에 같은 이름의 게임이 없어요. 패드를 지원하는 게임이면 '게임패드 그대로', 아니면 장르에 맞는 템플릿을 골라 보세요.",
                "Steam 스토어", Alternative: "preset-wasd");

        var source = $"Steam 스토어 · {app.Name}";
        var genreText = app.Genres.Count > 0 ? string.Join(", ", app.Genres.Take(3)) : "장르 정보 없음";
        var byGenre = ByGenre(app.Genres);
        return app.Controller switch
        {
            ControllerSupport.Full => new PresetAdvice(null, "게임패드 그대로",
                "컨트롤러를 완전히 지원하는 게임이에요. 버튼을 바꾸지 않는 게 가장 자연스러워요.", source, SteamAppId: app.AppId),
            ControllerSupport.Partial => new PresetAdvice(null, "게임패드 그대로",
                "컨트롤러를 일부 지원해요. 게임은 패드로 하고, 패드가 안 되는 메뉴만 터치나 키 배치를 쓰면 돼요.", source,
                Alternative: byGenre, SteamAppId: app.AppId),
            _ => new PresetAdvice(byGenre, Label(byGenre),
                $"컨트롤러를 지원하지 않는 게임이라 키보드·마우스 배치가 필요해요. 장르: {genreText}.", source, SteamAppId: app.AppId),
        };
    }

    /// <summary>장르 → 템플릿. 전략·시뮬레이션은 마우스, 액션·슈터·레이싱은 WASD, 액션이 없는 RPG는 방향키.</summary>
    private static string ByGenre(IReadOnlyList<string> genres)
    {
        bool Has(string g) => genres.Any(x => x.Equals(g, StringComparison.OrdinalIgnoreCase));
        if (Has("Strategy") || Has("Simulation") || (Has("Casual") && !Has("Action"))) return "preset-mouse";
        if (Has("Action") || Has("Shooter") || Has("Racing") || Has("Sports") || Has("Adventure")) return "preset-wasd";
        if (Has("RPG") || Has("Massively Multiplayer")) return "preset-arrows";
        return "preset-wasd";
    }

    public static string Label(string? presetId) => presetId is null ? "게임패드 그대로"
        : MappingPresets.All.FirstOrDefault(p => p.Id == presetId)?.Name ?? presetId;

    private sealed class CachedAdvice
    {
        public DateTime At { get; set; }
        public PresetAdvice Advice { get; set; } = null!;
    }
}
