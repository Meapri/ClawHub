namespace ClawCore.Games;

public enum GameSource { Steam, Epic, Installed, Custom }

public sealed class GameEntry
{
    /// <summary>안정적인 ID (예: "steam:570", "installed:MapleStory").</summary>
    public required string Id { get; init; }
    public required string Title { get; set; }
    public GameSource Source { get; init; }

    /// <summary>실행 대상: exe 경로 또는 URI (steam://…, com.epicgames.launcher://…, https://…).</summary>
    public required string LaunchTarget { get; set; }
    public string? LaunchArgs { get; set; }

    /// <summary>이 폴더 아래의 프로세스가 돌고 있으면 "게임 실행 중"으로 본다.</summary>
    public string? InstallDir { get; set; }
    /// <summary>추가로 감시할 프로세스 이름 (확장자 제외). 런처 경유 게임에 필요.</summary>
    public List<string> ProcessNames { get; set; } = [];

    public string? CoverImage { get; set; }
    public string? HeroImage { get; set; }
    public string? LogoImage { get; set; }
    public string? IconSource { get; set; }

    /// <summary>아트 검색용 원래 이름 (설치 목록의 DisplayName 등). 없으면 Title.</summary>
    public string? SearchName { get; set; }
    public int? SteamAppId { get; set; }

    // 사용자 데이터 (library.json에 저장)
    public DateTime? LastPlayed { get; set; }
    public string? MappingProfileId { get; set; }
    /// <summary>게임 중에만 쓸 성능 모드 (null = 사용자 기본값 따름).</summary>
    public Ec.PerformanceMode? PerformanceMode { get; set; }
    /// <summary>게임 중 FPS 제한 (null = 기본값 따름, 0 = 끔).</summary>
    public int? FpsLimit { get; set; }
    /// <summary>배터리로 돌 때만 쓸 성능 모드 (null = 배터리여도 <see cref="PerformanceMode"/>).</summary>
    public Ec.PerformanceMode? PerformanceModeOnBattery { get; set; }
    /// <summary>배터리로 돌 때만 쓸 FPS 제한 (null = 배터리여도 <see cref="FpsLimit"/>, 0 = 끔).</summary>
    public int? FpsLimitOnBattery { get; set; }
    public bool Hidden { get; set; }

    /// <summary>지금 전원 상태에서 이 게임이 쓸 성능 모드 (null = 사용자 기본값).</summary>
    public Ec.PerformanceMode? EffectivePerformanceMode(bool onBattery) =>
        onBattery && PerformanceModeOnBattery is { } b ? b : PerformanceMode;

    /// <summary>지금 전원 상태에서 이 게임이 쓸 FPS 제한 (null = 기본값).</summary>
    public int? EffectiveFpsLimit(bool onBattery) => onBattery && FpsLimitOnBattery is { } b ? b : FpsLimit;
}
