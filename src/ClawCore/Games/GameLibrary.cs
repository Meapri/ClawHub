namespace ClawCore.Games;

/// <summary>
/// 감지된 게임 + 직접 추가한 게임을 합친 라이브러리.
/// library.json에는 사용자 데이터(최근 플레이, 매핑 프로필, 숨김, 실행 대상 수정)와 직접 추가한 게임만 저장한다.
/// </summary>
public sealed class GameLibrary
{
    private sealed class UserData
    {
        public List<GameEntry> CustomGames { get; set; } = [];
        public Dictionary<string, Overrides> Overrides { get; set; } = [];
    }

    private sealed class Overrides
    {
        public DateTime? LastPlayed { get; set; }
        public string? MappingProfileId { get; set; }
        /// <summary>"None" = 기본값 따름을 명시적으로 고른 경우.</summary>
        public string? PerformanceMode { get; set; }
        public bool? Hidden { get; set; }
        public string? Title { get; set; }
        public string? LaunchTarget { get; set; }
        public string? LaunchArgs { get; set; }
    }

    private static string FilePath => Path.Combine(JsonStore.UserDir, "library.json");

    private readonly ILibraryProvider[] _providers = [new SteamProvider(), new EpicProvider(), new GogProvider(), new InstalledGamesProvider()];
    private UserData _data = new();

    public IReadOnlyList<GameEntry> Games { get; private set; } = [];

    public IEnumerable<GameEntry> Visible => Games.Where(g => !g.Hidden);

    public IEnumerable<GameEntry> Recent => Visible.Where(g => g.LastPlayed is not null).OrderByDescending(g => g.LastPlayed);

    public void Refresh()
    {
        _data = JsonStore.Load<UserData>(FilePath);
        var games = new List<GameEntry>();
        foreach (var provider in _providers)
        {
            try { games.AddRange(provider.Scan()); }
            catch (Exception) { /* 런처 하나가 깨져도 나머지는 표시 */ }
        }
        games.AddRange(_data.CustomGames);

        foreach (var g in games)
        {
            if (!_data.Overrides.TryGetValue(g.Id, out var o)) continue;
            g.LastPlayed = o.LastPlayed ?? g.LastPlayed;
            g.MappingProfileId = o.MappingProfileId ?? g.MappingProfileId;
            g.PerformanceMode = Enum.TryParse<Ec.PerformanceMode>(o.PerformanceMode, out var pm) ? pm : null;
            g.Hidden = o.Hidden ?? g.Hidden;
            g.Title = o.Title ?? g.Title;
            g.LaunchTarget = o.LaunchTarget ?? g.LaunchTarget;
            g.LaunchArgs = o.LaunchArgs ?? g.LaunchArgs;
        }

        Games = games.DistinctBy(g => g.Id).OrderBy(g => g.Title, StringComparer.CurrentCulture).ToList();
    }

    public void MarkPlayed(GameEntry game) => Update(game, o => o.LastPlayed = game.LastPlayed = DateTime.Now);

    public void SetProfile(GameEntry game, string? profileId) =>
        Update(game, o => o.MappingProfileId = game.MappingProfileId = profileId);

    public void SetHidden(GameEntry game, bool hidden) => Update(game, o => o.Hidden = game.Hidden = hidden);

    public void SetPerformance(GameEntry game, Ec.PerformanceMode? mode) =>
        Update(game, o => o.PerformanceMode = (game.PerformanceMode = mode)?.ToString() ?? "None");

    public void Rename(GameEntry game, string title) => Update(game, o => o.Title = game.Title = title);

    /// <summary>
    /// 라이브러리에 없는 앱(Xbox 앱 게임, 에뮬레이터 등)에 프로필을 붙인다.
    /// 같은 프로세스를 가리키는 항목이 있으면 그걸 쓰고, 없으면 직접 추가 항목으로 만든다.
    /// </summary>
    public GameEntry BindProcess(string processName, string? exePath, string? profileId)
    {
        var game = Games.FirstOrDefault(g => g.ProcessNames.Contains(processName, StringComparer.OrdinalIgnoreCase)
            || (exePath is not null && g.InstallDir is { Length: > 0 } d
                && exePath.StartsWith(Path.GetFullPath(d).TrimEnd('\\') + "\\", StringComparison.OrdinalIgnoreCase)));
        if (game is null)
        {
            game = new GameEntry
            {
                Id = $"custom:{Guid.NewGuid():N}",
                Title = exePath is not null && File.Exists(exePath)
                    ? System.Diagnostics.FileVersionInfo.GetVersionInfo(exePath).FileDescription is { Length: > 0 } desc ? desc : processName
                    : processName,
                Source = GameSource.Custom,
                LaunchTarget = exePath ?? processName,
                ProcessNames = [processName],
                IconSource = exePath,
            };
            _data.CustomGames.Add(game);
            JsonStore.Save(FilePath, _data);
            Refresh();
            game = Games.First(g => g.Id == game.Id);
        }
        SetProfile(game, profileId);
        return game;
    }

    public int UnhideAll()
    {
        var hidden = Games.Where(g => g.Hidden).ToList();
        foreach (var g in hidden) Update(g, o => o.Hidden = g.Hidden = false);
        return hidden.Count;
    }

    public GameEntry AddCustom(string title, string launchTarget)
    {
        var game = new GameEntry
        {
            Id = $"custom:{Guid.NewGuid():N}",
            Title = title,
            Source = GameSource.Custom,
            LaunchTarget = launchTarget,
            InstallDir = File.Exists(launchTarget) ? Path.GetDirectoryName(launchTarget) : null,
            IconSource = File.Exists(launchTarget) ? launchTarget : null,
        };
        _data.CustomGames.Add(game);
        JsonStore.Save(FilePath, _data);
        Refresh();
        return game;
    }

    private void Update(GameEntry game, Action<Overrides> change)
    {
        if (!_data.Overrides.TryGetValue(game.Id, out var o)) _data.Overrides[game.Id] = o = new Overrides();
        change(o);
        JsonStore.Save(FilePath, _data);
    }
}
