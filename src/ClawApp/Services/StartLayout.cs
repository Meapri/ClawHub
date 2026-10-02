using System.Text.Json.Serialization;
using ClawCore.Games;

namespace ClawApp.Services;

/// <summary>시작 화면 타일 크기 — 모두 정사각형 (칸 = 88px: 작게 1칸 80 · 보통 2칸 168 · 크게 4칸 344).</summary>
public enum StartTileSize { Small, Medium, Large }

/// <summary>타일 종류: 게임·앱(라이브러리 항목) · 이어서 플레이(가장 최근 게임) · ClawHub 기능 · 폴더.</summary>
public enum StartTileKind { Game, Continue, Action, Folder }

/// <summary>시작 화면 타일 하나 (저장용).</summary>
public sealed class StartTileDef
{
    public string Id { get; set; } = Guid.NewGuid().ToString("N");
    public StartTileKind Kind { get; set; }
    /// <summary>Game = 라이브러리 항목 ID, Action = 기능 키 (<see cref="StartActions"/>).</summary>
    public string? Ref { get; set; }
    public StartTileSize Size { get; set; } = StartTileSize.Medium;
    /// <summary>폴더 이름.</summary>
    public string? Title { get; set; }
    /// <summary>폴더 안의 타일 (폴더 안에 폴더는 없다).</summary>
    public List<StartTileDef> Children { get; set; } = [];
}

/// <summary>이름 붙은 타일 묶음 (구역). 가로로 나란히 놓이고 위에 제목이 보인다.</summary>
public sealed class StartGroupDef
{
    public string Id { get; set; } = Guid.NewGuid().ToString("N");
    /// <summary>구역 제목 (비어 있으면 제목 없이).</summary>
    public string Title { get; set; } = "";
    public List<StartTileDef> Tiles { get; set; } = [];
}

public sealed class StartLayout
{
    /// <summary>false면 처음 실행 — 기본 배치를 만든다.</summary>
    public bool Initialized { get; set; }
    public List<StartGroupDef> Groups { get; set; } = [];
    /// <summary>예전(구역 없던) 형식. 읽기만 하고 다시 쓰지 않는다.</summary>
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public List<StartTileDef>? Tiles { get; set; }
    /// <summary>프로그램 추가로 넣은 게임·앱을 시작 화면에 바로 고정.</summary>
    public bool AutoPinNew { get; set; } = true;
}

/// <summary>ClawHub 기능 타일. 아이콘은 Themes/Icons.xaml의 Icon.&lt;이름&gt;.</summary>
public static class StartActions
{
    public sealed record Info(string Key, string Label, string Icon, bool Glass);

    public static readonly IReadOnlyList<Info> All =
    [
        new("library", "라이브러리", "Library", false),
        new("profiles", "키 매핑", "Keyboard", false),
        new("settings", "설정", "Settings", true),
        new("xbox", "Xbox 앱", "Gamepad2", true),
        new("desktop", "데스크톱으로", "Monitor", false),
        new("desktoppad", "데스크톱 (게임패드)", "Gamepad2", true),
        new("quick", "빠른 설정", "SlidersHorizontal", false),
        new("addprogram", "프로그램 추가", "Plus", true),
        new("sleep", "절전", "Moon", true),
        new("restart", "다시 시작", "RotateCcw", true),
        new("shutdown", "시스템 종료", "Power", true),
        new("exitapp", "ClawHub 종료", "LogOut", true),
    ];

    public static Info? Find(string? key) => All.FirstOrDefault(a => a.Key == key);

    /// <summary>설정 타일은 빼지 않는다 (다른 곳에서 설정으로 갈 길이 없어진다).</summary>
    public const string Required = "settings";
}

/// <summary>
/// 시작 화면 배치 편집 (구역·고정·해제·이동·크기·폴더). 설정 파일의 <see cref="AppSettings.Start"/>를 고치고 저장한다.
/// 홈 화면과 게임 상세·라이브러리·프로그램 추가가 함께 쓴다.
/// </summary>
public sealed class StartScreen(AppSettings settings)
{
    public StartLayout Layout => settings.Start;
    public List<StartGroupDef> Groups => Layout.Groups;

    /// <summary>배치가 바뀌었을 때 (홈이 다시 그린다).</summary>
    public event Action? Changed;

    public void Save()
    {
        Groups.RemoveAll(g => g.Tiles.Count == 0); // 빈 구역은 사라진다 (Windows처럼)
        settings.Save();
        Changed?.Invoke();
    }

    /// <summary>처음 실행 (또는 구역이 없던 예전 형식): 기본 배치.</summary>
    public void EnsureDefault(GameLibrary library)
    {
        if (Layout.Initialized && Groups.Count > 0) return;
        Layout.Tiles = null;
        Layout.Groups = BuildDefault(library);
        Layout.Initialized = true;
        settings.Save();
    }

    public void ResetToDefault(GameLibrary library)
    {
        Layout.Groups = BuildDefault(library);
        Save();
    }

    private static StartTileDef Act(string key, StartTileSize size = StartTileSize.Medium) =>
        new() { Kind = StartTileKind.Action, Ref = key, Size = size };

    private static List<StartGroupDef> BuildDefault(GameLibrary library)
    {
        var games = library.Recent.Concat(library.Visible.Where(g => g.LastPlayed is null)).Where(g => !g.IsApp).ToList();
        var play = new StartGroupDef { Title = "게임" };
        play.Tiles.Add(new StartTileDef { Kind = StartTileKind.Continue, Size = StartTileSize.Large });
        play.Tiles.AddRange(games.Skip(1).Take(6).Select(g => new StartTileDef { Kind = StartTileKind.Game, Ref = g.Id }));
        play.Tiles.Add(Act("library"));

        var groups = new List<StartGroupDef> { play };
        var apps = library.Visible.Where(g => g.IsApp).Take(8)
            .Select(g => new StartTileDef { Kind = StartTileKind.Game, Ref = g.Id, Size = StartTileSize.Small }).ToList();
        if (apps.Count > 0) groups.Add(new StartGroupDef { Title = "앱", Tiles = [.. apps, Act("addprogram", StartTileSize.Small)] });

        groups.Add(new StartGroupDef
        {
            Title = "ClawHub",
            Tiles =
            [
                Act("profiles"), Act("settings"), Act("xbox"), Act("desktop"), Act("quick"),
                new StartTileDef
                {
                    Kind = StartTileKind.Folder, Title = "전원",
                    Children = [Act("sleep", StartTileSize.Small), Act("restart", StartTileSize.Small),
                                Act("shutdown", StartTileSize.Small), Act("exitapp", StartTileSize.Small)],
                },
            ],
        });
        return groups;
    }

    // ── 찾기 ──────────────────────────────────────────────

    private IEnumerable<(StartTileDef Def, List<StartTileDef> Owner)> All()
    {
        foreach (var g in Groups)
            foreach (var t in g.Tiles)
            {
                yield return (t, g.Tiles);
                foreach (var c in t.Children) yield return (c, t.Children);
            }
    }

    private List<StartTileDef>? OwnerOf(StartTileDef def) => All().FirstOrDefault(x => x.Def == def).Owner;
    public StartTileDef? FolderOf(StartTileDef def) => Groups.SelectMany(g => g.Tiles).FirstOrDefault(f => f.Children.Contains(def));
    public StartGroupDef? GroupOf(StartTileDef def) =>
        Groups.FirstOrDefault(g => g.Tiles.Contains(def) || g.Tiles.Any(f => f.Children.Contains(def)));

    public bool IsPinned(GameEntry game) => All().Any(x => x.Def.Kind == StartTileKind.Game && x.Def.Ref == game.Id);
    public bool IsActionPinned(string key) => All().Any(x => x.Def.Kind == StartTileKind.Action && x.Def.Ref == key);

    /// <summary>화면에 보이는 순서 (구역 → 타일). 폴더 안은 제외.</summary>
    public int OrderOf(StartTileDef def)
    {
        var i = 0;
        foreach (var t in Groups.SelectMany(g => g.Tiles))
        {
            if (t == def) return i;
            var c = t.Children.IndexOf(def);
            if (c >= 0) return i * 100 + c; // 같은 폴더 안 비교용
            i++;
        }
        return -1;
    }

    // ── 고정·해제 ─────────────────────────────────────────

    private List<StartTileDef> LastGroupTiles()
    {
        if (Groups.Count == 0) Groups.Add(new StartGroupDef());
        return Groups[^1].Tiles;
    }

    public StartTileDef Pin(GameEntry game)
    {
        if (All().FirstOrDefault(x => x.Def.Kind == StartTileKind.Game && x.Def.Ref == game.Id).Def is { } existing) return existing;
        var def = new StartTileDef { Kind = StartTileKind.Game, Ref = game.Id, Size = game.IsApp ? StartTileSize.Small : StartTileSize.Medium };
        LastGroupTiles().Add(def); // Windows처럼 맨 끝에 붙는다
        Save();
        return def;
    }

    public void PinAction(string key)
    {
        if (IsActionPinned(key)) return;
        LastGroupTiles().Add(Act(key));
        Save();
    }

    public void Unpin(GameEntry game)
    {
        foreach (var (def, _) in All().Where(x => x.Def.Kind == StartTileKind.Game && x.Def.Ref == game.Id).ToList()) Remove(def);
        Save();
    }

    /// <summary>타일 빼기. 폴더에 하나만 남으면 폴더를 풀고, 비면 폴더도 지운다. 설정 타일은 못 뺀다(false).</summary>
    public bool Unpin(StartTileDef def)
    {
        if (def.Kind == StartTileKind.Action && def.Ref == StartActions.Required) return false;
        if (def.Kind == StartTileKind.Folder && def.Children.Any(c => c.Kind == StartTileKind.Action && c.Ref == StartActions.Required))
            return false;
        Remove(def);
        Save();
        return true;
    }

    private void Remove(StartTileDef def)
    {
        var folder = FolderOf(def);
        OwnerOf(def)?.Remove(def);
        if (folder is not null) Collapse(folder);
    }

    /// <summary>폴더에 하나만 남으면 그 타일로 바꾸고, 비면 지운다.</summary>
    private void Collapse(StartTileDef folder)
    {
        if (folder.Children.Count == 1) Replace(folder, folder.Children[0]);
        else if (folder.Children.Count == 0) OwnerOf(folder)?.Remove(folder);
    }

    private void Replace(StartTileDef old, StartTileDef with)
    {
        if (OwnerOf(old) is not { } owner) return;
        owner[owner.IndexOf(old)] = with;
    }

    // ── 이동·크기·폴더·구역 ────────────────────────────────

    public void Resize(StartTileDef def)
    {
        def.Size = def.Size switch { StartTileSize.Small => StartTileSize.Medium, StartTileSize.Medium => StartTileSize.Large, _ => StartTileSize.Small };
        Save();
    }

    /// <summary>def를 target 앞(또는 뒤)으로 — 다른 구역이면 그 구역으로. 폴더는 폴더에 못 들어간다.</summary>
    public void Move(StartTileDef def, StartTileDef target, bool after)
    {
        if (def == target) return;
        if (def.Kind == StartTileKind.Folder && FolderOf(target) is { } targetFolder) target = targetFolder;
        if (def == target || OwnerOf(target) is not { } targetOwner) return;
        var oldFolder = FolderOf(def);
        OwnerOf(def)!.Remove(def);
        var i = targetOwner.IndexOf(target);
        targetOwner.Insert(after ? i + 1 : i, def);
        if (oldFolder is not null && oldFolder != FolderOf(def)) Collapse(oldFolder);
        Save();
    }

    /// <summary>def를 새 구역(맨 끝, 또는 group 바로 뒤)으로.</summary>
    public StartGroupDef MoveToNewGroup(StartTileDef def, StartGroupDef? after = null)
    {
        var oldFolder = FolderOf(def);
        OwnerOf(def)?.Remove(def);
        if (oldFolder is not null) Collapse(oldFolder);
        var group = new StartGroupDef { Tiles = [def] };
        var at = after is null ? -1 : Groups.IndexOf(after);
        if (at >= 0) Groups.Insert(at + 1, group); else Groups.Add(group);
        Save();
        return group;
    }

    /// <summary>def를 target 위에 놓기: target이 폴더면 그 안으로, 아니면 둘로 새 폴더. 만든(넣은) 폴더를 돌려준다.</summary>
    public StartTileDef? Merge(StartTileDef def, StartTileDef target)
    {
        if (def == target || def.Kind == StartTileKind.Folder) return null;
        if (FolderOf(target) is { } inside) target = inside;
        if (FolderOf(def) == target) return target;
        Remove(def);
        if (target.Kind == StartTileKind.Folder)
        {
            target.Children.Add(def);
            Save();
            return target;
        }
        var folder = new StartTileDef { Kind = StartTileKind.Folder, Title = "새 폴더", Size = StartTileSize.Medium };
        Replace(target, folder);
        folder.Children.AddRange([target, def]);
        Save();
        return folder;
    }

    /// <summary>폴더에서 꺼내 폴더 바로 뒤에 둔다.</summary>
    public void TakeOutOfFolder(StartTileDef def)
    {
        if (FolderOf(def) is not { } folder || OwnerOf(folder) is not { } owner) return;
        folder.Children.Remove(def);
        owner.Insert(owner.IndexOf(folder) + 1, def);
        Collapse(folder);
        Save();
    }

    public void Rename(StartTileDef folder, string title)
    {
        if (string.IsNullOrWhiteSpace(title)) return;
        folder.Title = title.Trim();
        Save();
    }

    /// <summary>구역 이름 (비우면 제목 없는 구역).</summary>
    public void RenameGroup(StartGroupDef group, string title)
    {
        group.Title = title.Trim();
        Save();
    }
}
