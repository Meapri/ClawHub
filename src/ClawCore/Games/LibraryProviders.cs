using System.Text.Json;
using System.Text.RegularExpressions;
using Microsoft.Win32;

namespace ClawCore.Games;

public interface ILibraryProvider
{
    IEnumerable<GameEntry> Scan();
}

public sealed partial class SteamProvider : ILibraryProvider
{
    // Steam 자체 도구/런타임은 라이브러리에서 제외
    private static readonly HashSet<string> IgnoredAppIds = ["228980", "1070560", "1391110", "1628350", "1493710", "2180100"];

    public IEnumerable<GameEntry> Scan()
    {
        var steam = Registry.GetValue(@"HKEY_CURRENT_USER\Software\Valve\Steam", "SteamPath", null) as string;
        if (steam is null || !Directory.Exists(steam)) yield break;
        steam = Path.GetFullPath(steam);

        var vdf = Path.Combine(steam, "steamapps", "libraryfolders.vdf");
        var libraries = new List<string> { steam };
        if (File.Exists(vdf))
            libraries.AddRange(VdfValue().Matches(File.ReadAllText(vdf))
                .Where(m => m.Groups[1].Value == "path")
                .Select(m => m.Groups[2].Value.Replace(@"\\", @"\")));

        foreach (var lib in libraries.Distinct(StringComparer.OrdinalIgnoreCase))
        {
            var apps = Path.Combine(lib, "steamapps");
            if (!Directory.Exists(apps)) continue;
            foreach (var acf in Directory.EnumerateFiles(apps, "appmanifest_*.acf"))
            {
                var kv = VdfValue().Matches(File.ReadAllText(acf))
                    .GroupBy(m => m.Groups[1].Value).ToDictionary(g => g.Key, g => g.First().Groups[2].Value);
                if (!kv.TryGetValue("appid", out var appId) || IgnoredAppIds.Contains(appId)) continue;

                yield return new GameEntry
                {
                    Id = $"steam:{appId}",
                    Title = kv.GetValueOrDefault("name") ?? appId,
                    Source = GameSource.Steam,
                    LaunchTarget = $"steam://rungameid/{appId}",
                    InstallDir = kv.TryGetValue("installdir", out var dir) ? Path.Combine(apps, "common", dir) : null,
                    CoverImage = FindArt(steam, appId, "library_600x900.jpg"),
                    HeroImage = FindArt(steam, appId, "library_hero.jpg"),
                };
            }
        }
    }

    private static string? FindArt(string steam, string appId, string name)
    {
        var cache = Path.Combine(steam, "appcache", "librarycache");
        string[] candidates = [Path.Combine(cache, appId, name), Path.Combine(cache, $"{appId}_{name}")];
        var hit = candidates.FirstOrDefault(File.Exists);
        if (hit is not null) return hit;
        // 신형 캐시는 appId 폴더 아래 해시 하위 폴더에 둔다
        var dir = Path.Combine(cache, appId);
        return Directory.Exists(dir) ? Directory.EnumerateFiles(dir, name, SearchOption.AllDirectories).FirstOrDefault() : null;
    }

    [GeneratedRegex("\"([^\"]+)\"\\s+\"([^\"]*)\"")]
    private static partial Regex VdfValue();
}

public sealed class EpicProvider : ILibraryProvider
{
    public IEnumerable<GameEntry> Scan()
    {
        var dir = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData),
            "Epic", "EpicGamesLauncher", "Data", "Manifests");
        if (!Directory.Exists(dir)) yield break;

        foreach (var item in Directory.EnumerateFiles(dir, "*.item"))
        {
            JsonElement root;
            try { root = JsonDocument.Parse(File.ReadAllText(item)).RootElement; }
            catch (JsonException) { continue; }

            string S(string key) => root.TryGetProperty(key, out var v) ? v.GetString() ?? "" : "";
            if (root.TryGetProperty("bIsApplication", out var isApp) && !isApp.GetBoolean()) continue;

            var install = S("InstallLocation");
            var exe = S("LaunchExecutable");
            yield return new GameEntry
            {
                Id = $"epic:{S("AppName")}",
                Title = S("DisplayName"),
                Source = GameSource.Epic,
                LaunchTarget = $"com.epicgames.launcher://apps/{S("CatalogNamespace")}%3A{S("CatalogItemId")}%3A{S("AppName")}?action=launch&silent=true",
                InstallDir = install,
                IconSource = exe.Length > 0 ? Path.Combine(install, exe) : null,
            };
        }
    }
}

/// <summary>
/// 설치 프로그램 목록에서 게임을 찾는다 (Steam/Epic 외: 넥슨·엔씨·블리자드·라이엇 등 자체 런처 게임).
/// 게임사 이름으로 거르고, 런처/도구 자체는 제외한다.
/// </summary>
public sealed class InstalledGamesProvider : ILibraryProvider
{
    private static readonly string[] GamePublishers =
    [
        "NEXON", "넥슨", "NCSOFT", "엔씨소프트", "Smilegate", "스마일게이트", "KRAFTON", "크래프톤", "Pearl Abyss", "펄어비스",
        "Netmarble", "넷마블", "Kakao Games", "카카오게임즈", "Com2uS", "컴투스", "Wemade", "위메이드", "NEOWIZ", "네오위즈",
        "Blizzard", "Riot Games", "Electronic Arts", "Ubisoft", "Bethesda", "HoYoverse", "miHoYo", "COGNOSPHERE",
        "Rockstar Games", "Square Enix", "BANDAI NAMCO", "CAPCOM", "SEGA", "Grinding Gear Games",
    ];

    private static readonly string[] NotGames =
    [
        "Launcher", "런처", "Plug", "플러그", "Battle.net", "EA app", "Origin", "Ubisoft Connect", "Riot Client", "Rockstar Games Launcher",
        "Anti-Cheat", "AntiCheat", "Redistributable", "Runtime", "Steam", "Updater", "Uninstall", "Purple", "퍼플", "NGM", "Easy Anti",
    ];

    /// <summary>프로세스 이름이 설치 폴더와 다르거나 기본 프로필이 있는 게임.</summary>
    private static readonly Dictionary<string, (string Title, string[] Processes, string? ProfileId)> Known =
        new(StringComparer.OrdinalIgnoreCase)
        {
            ["MapleStory"] = ("메이플스토리", ["MapleStory"], Mapping.MappingPresets.MapleStoryId),
        };

    public IEnumerable<GameEntry> Scan()
    {
        foreach (var e in UninstallEntries())
        {
            var known = Known.TryGetValue(e.Name, out var k);
            if (!known && !GamePublishers.Any(p => e.Publisher?.Contains(p, StringComparison.OrdinalIgnoreCase) == true)) continue;
            if (!known && NotGames.Any(n => e.Name.Contains(n, StringComparison.OrdinalIgnoreCase))) continue;

            // Steam/Epic 게임도 설치 목록에 올라오지만 각 런처 제공자가 이미 처리
            if (e.Location?.Contains(@"\steamapps\", StringComparison.OrdinalIgnoreCase) == true) continue;

            var exe = ResolveExe(e.Icon, e.Location);
            if (exe is null) continue;

            yield return new GameEntry
            {
                Id = $"installed:{e.Name}",
                Title = known ? k.Title : e.Name,
                Source = GameSource.Installed,
                LaunchTarget = exe,
                InstallDir = e.Location is { Length: > 0 } ? e.Location : Path.GetDirectoryName(exe),
                ProcessNames = known ? [.. k.Processes] : [],
                IconSource = exe,
                MappingProfileId = known ? k.ProfileId : null,
            };
        }
    }

    /// <summary>DisplayIcon("C:\…\Game.exe,0")이 exe면 그걸, 아니면 설치 폴더의 유일한 exe를 쓴다.</summary>
    private static string? ResolveExe(string? icon, string? location)
    {
        var path = icon?.Split(',')[0].Trim('"', ' ');
        if (path is not null && path.EndsWith(".exe", StringComparison.OrdinalIgnoreCase) && File.Exists(path)
            && !Path.GetFileName(path).Contains("unins", StringComparison.OrdinalIgnoreCase))
            return path;
        if (location is null || !Directory.Exists(location)) return null;
        var exes = Directory.GetFiles(location, "*.exe").Where(f => !Path.GetFileName(f).Contains("unins", StringComparison.OrdinalIgnoreCase)).ToArray();
        return exes.Length == 1 ? exes[0] : null;
    }

    private static IEnumerable<(string Name, string? Publisher, string? Location, string? Icon)> UninstallEntries()
    {
        (RegistryKey Hive, string Path)[] roots =
        [
            (Registry.CurrentUser, @"Software\Microsoft\Windows\CurrentVersion\Uninstall"),
            (Registry.LocalMachine, @"Software\Microsoft\Windows\CurrentVersion\Uninstall"),
            (Registry.LocalMachine, @"Software\WOW6432Node\Microsoft\Windows\CurrentVersion\Uninstall"),
        ];
        foreach (var (hive, path) in roots)
        {
            using var key = hive.OpenSubKey(path);
            if (key is null) continue;
            foreach (var sub in key.GetSubKeyNames())
            {
                using var app = key.OpenSubKey(sub);
                if (app?.GetValue("DisplayName") is string name && app.GetValue("SystemComponent") is not 1)
                    yield return (name, app.GetValue("Publisher") as string, app.GetValue("InstallLocation") as string,
                        app.GetValue("DisplayIcon") as string);
            }
        }
    }
}

public sealed class GogProvider : ILibraryProvider
{
    public IEnumerable<GameEntry> Scan()
    {
        using var games = Registry.LocalMachine.OpenSubKey(@"SOFTWARE\WOW6432Node\GOG.com\Games");
        if (games is null) yield break;
        foreach (var id in games.GetSubKeyNames())
        {
            using var g = games.OpenSubKey(id);
            if (g?.GetValue("exe") is not string exe || !File.Exists(exe)) continue;
            yield return new GameEntry
            {
                Id = $"gog:{id}",
                Title = g.GetValue("gameName") as string ?? id,
                Source = GameSource.Installed,
                LaunchTarget = exe,
                LaunchArgs = g.GetValue("launchParam") as string,
                InstallDir = g.GetValue("path") as string,
                IconSource = exe,
            };
        }
    }
}
