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
        /// <summary>-1 = 기본값 따름을 명시적으로 고른 경우.</summary>
        public int? FpsLimit { get; set; }
        public string? LaunchTarget { get; set; }
        public string? LaunchArgs { get; set; }
        /// <summary>배터리 전용. "None" = 따로 안 씀.</summary>
        public string? PerformanceModeOnBattery { get; set; }
        /// <summary>배터리 전용. -1 = 따로 안 씀.</summary>
        public int? FpsLimitOnBattery { get; set; }
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
            g.FpsLimit = o.FpsLimit is >= 0 ? o.FpsLimit : null;
            g.LaunchTarget = o.LaunchTarget ?? g.LaunchTarget;
            g.LaunchArgs = o.LaunchArgs ?? g.LaunchArgs;
            g.PerformanceModeOnBattery = Enum.TryParse<Ec.PerformanceMode>(o.PerformanceModeOnBattery, out var pmb) ? pmb : null;
            g.FpsLimitOnBattery = o.FpsLimitOnBattery is >= 0 ? o.FpsLimitOnBattery : null;
        }

        _autoArt.Clear();
        foreach (var g in games)
        {
            ArtFetcher.ApplyCached(g);
            _autoArt[g.Id] = (g.CoverImage, g.HeroImage, g.LogoImage); // "기본으로 되돌리기"용
            UserArt.Apply(g); // 사용자가 고른 아트가 런처/자동 아트보다 우선
        }
        Games =games.DistinctBy(g => g.Id).OrderBy(g => g.Title, StringComparer.CurrentCulture).ToList();
    }

    /// <summary>아트가 없는 게임의 아트를 인터넷에서 받아 온다 (백그라운드에서 호출).</summary>
    public Task<int> FetchArtAsync(CancellationToken ct = default) => new ArtFetcher().FetchMissingAsync(Games.ToList(), ct);

    public void MarkPlayed(GameEntry game) => Update(game, o => o.LastPlayed = game.LastPlayed = DateTime.Now);

    public void SetProfile(GameEntry game, string? profileId) =>
        Update(game, o => o.MappingProfileId = game.MappingProfileId = profileId);

    public void SetHidden(GameEntry game, bool hidden) => Update(game, o => o.Hidden = game.Hidden = hidden);

    public void SetPerformance(GameEntry game, Ec.PerformanceMode? mode) =>
        Update(game, o => o.PerformanceMode = (game.PerformanceMode = mode)?.ToString() ?? "None");

    public void SetFpsLimit(GameEntry game, int? fps) => Update(game, o => o.FpsLimit = (game.FpsLimit = fps) ?? -1);

    public void SetPerformanceOnBattery(GameEntry game, Ec.PerformanceMode? mode) =>
        Update(game, o => o.PerformanceModeOnBattery = (game.PerformanceModeOnBattery = mode)?.ToString() ?? "None");

    public void SetFpsLimitOnBattery(GameEntry game, int? fps) =>
        Update(game, o => o.FpsLimitOnBattery = (game.FpsLimitOnBattery = fps) ?? -1);

    public void Rename(GameEntry game, string title) => Update(game, o => o.Title = game.Title = title);

    // ── 사용자 아트 (아트 캐시 폴더의 user-* 파일, library.json에는 저장하지 않음) ──

    /// <summary>런처/자동으로 받은 아트 (사용자 아트 적용 전). 되돌리기에 쓴다.</summary>
    private readonly Dictionary<string, (string? Cover, string? Hero, string? Logo)> _autoArt = [];

    /// <summary>받은/고른 이미지를 이 게임의 아트로 저장하고 바로 연결한다.</summary>
    public void SetUserArt(GameEntry game, ArtKind kind, byte[] image) => AssignArt(game, kind, UserArt.Save(game, kind, image));

    public void SetUserArtFromFile(GameEntry game, ArtKind kind, string path) => AssignArt(game, kind, UserArt.SaveFromFile(game, kind, path));

    public bool HasUserArt(GameEntry game, ArtKind kind) => UserArt.Find(game, kind) is not null;

    /// <summary>사용자 아트를 지우고 런처/자동 아트로 되돌린다.</summary>
    public void ResetArt(GameEntry game, ArtKind kind)
    {
        UserArt.Clear(game, kind);
        var auto = _autoArt.GetValueOrDefault(game.Id);
        AssignArt(game, kind, kind switch { ArtKind.Cover => auto.Cover, ArtKind.Hero => auto.Hero, _ => auto.Logo });
        ArtFetcher.ApplyCached(game); // 그 사이 자동으로 받아 둔 아트가 있으면 연결
    }

    private static void AssignArt(GameEntry game, ArtKind kind, string? path)
    {
        switch (kind)
        {
            case ArtKind.Cover: game.CoverImage = path; break;
            case ArtKind.Hero: game.HeroImage = path; break;
            default: game.LogoImage = path; break;
        }
    }

    /// <summary>
    /// 라이브러리에 없는 앱(Xbox 앱 게임, 에뮬레이터 등)에 프로필을 붙인다.
    /// 같은 프로세스를 가리키는 항목이 있으면 그걸 쓰고, 없으면 직접 추가 항목으로 만든다.
    /// </summary>
    public GameEntry BindProcess(string processName, string? exePath, string? profileId)
    {
        var game = Games.FirstOrDefault(g => g.ProcessNames.Contains(processName, StringComparer.OrdinalIgnoreCase)
            || (exePath is not null && ProcessScanner.SafeInstallDir(g.InstallDir) is { } d
                && exePath.StartsWith(d, StringComparison.OrdinalIgnoreCase)));
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

    /// <param name="targetExe">바로가기면 그 대상 실행 파일 — 설치 폴더(실행 감지)·아이콘에 쓴다. 없으면 launchTarget 기준.</param>
    public GameEntry AddCustom(string title, string launchTarget, string? targetExe = null)
    {
        var exe = targetExe is not null && File.Exists(targetExe) ? targetExe : File.Exists(launchTarget) ? launchTarget : null;
        var game = new GameEntry
        {
            Id = $"custom:{Guid.NewGuid():N}",
            Title = title,
            Source = GameSource.Custom,
            LaunchTarget = launchTarget,
            InstallDir = exe is null ? null : Path.GetDirectoryName(exe),
            IconSource = exe,
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
