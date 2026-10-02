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

public sealed class StartLayout
{
    /// <summary>false면 처음 실행 — 기본 배치를 만든다.</summary>
    public bool Initialized { get; set; }
    public List<StartTileDef> Tiles { get; set; } = [];
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
/// 시작 화면 배치 편집 (고정·해제·이동·크기·폴더). 설정 파일의 <see cref="AppSettings.Start"/>를 고치고 저장한다.
/// 홈 화면과 게임 상세·라이브러리·프로그램 추가가 함께 쓴다.
/// </summary>
public sealed class StartScreen(AppSettings settings)
{
    public StartLayout Layout => settings.Start;

    /// <summary>배치가 바뀌었을 때 (홈이 다시 그린다).</summary>
    public event Action? Changed;

    public void Save()
    {
        settings.Save();
        Changed?.Invoke();
    }

    /// <summary>처음 실행: 이어서 플레이 · 최근 게임 · 앱 · 기능 · 전원 폴더.</summary>
    public void EnsureDefault(GameLibrary library)
    {
        if (Layout.Initialized) return;
        Layout.Tiles = BuildDefault(library);
        Layout.Initialized = true;
        settings.Save();
    }

    public void ResetToDefault(GameLibrary library)
    {
        Layout.Tiles = BuildDefault(library);
        Save();
    }

    private static List<StartTileDef> BuildDefault(GameLibrary library)
    {
        StartTileDef Act(string key, StartTileSize size = StartTileSize.Medium) => new() { Kind = StartTileKind.Action, Ref = key, Size = size };
        var games = library.Recent.Concat(library.Visible.Where(g => g.LastPlayed is null)).Where(g => !g.IsApp).ToList();
        var tiles = new List<StartTileDef> { new() { Kind = StartTileKind.Continue, Size = StartTileSize.Large } };
        tiles.Add(Act("library"));
        tiles.Add(Act("profiles"));
        tiles.AddRange(games.Skip(1).Take(6).Select(g => new StartTileDef { Kind = StartTileKind.Game, Ref = g.Id }));
        tiles.AddRange(library.Visible.Where(g => g.IsApp).Take(4)
            .Select(g => new StartTileDef { Kind = StartTileKind.Game, Ref = g.Id, Size = StartTileSize.Small }));
        tiles.Add(Act("settings"));
        tiles.Add(Act("xbox"));
        tiles.Add(Act("desktop"));
        tiles.Add(Act("quick"));
        tiles.Add(new StartTileDef
        {
            Kind = StartTileKind.Folder, Title = "전원",
            Children = [Act("sleep", StartTileSize.Small), Act("restart", StartTileSize.Small),
                        Act("shutdown", StartTileSize.Small), Act("exitapp", StartTileSize.Small)],
        });
        return tiles;
    }

    // ── 찾기 ──────────────────────────────────────────────

    private IEnumerable<(StartTileDef Def, List<StartTileDef> Owner)> All()
    {
        foreach (var t in Layout.Tiles)
        {
            yield return (t, Layout.Tiles);
            foreach (var c in t.Children) yield return (c, t.Children);
        }
    }

    public StartTileDef? Find(string id) => All().FirstOrDefault(x => x.Def.Id == id).Def;
    private List<StartTileDef>? OwnerOf(StartTileDef def) => All().FirstOrDefault(x => x.Def == def).Owner;
    public StartTileDef? FolderOf(StartTileDef def) => Layout.Tiles.FirstOrDefault(f => f.Children.Contains(def));

    public bool IsPinned(GameEntry game) => All().Any(x => x.Def.Kind == StartTileKind.Game && x.Def.Ref == game.Id);
    public bool IsActionPinned(string key) => All().Any(x => x.Def.Kind == StartTileKind.Action && x.Def.Ref == key);

    // ── 고정·해제 ─────────────────────────────────────────

    public StartTileDef Pin(GameEntry game)
    {
        if (All().FirstOrDefault(x => x.Def.Kind == StartTileKind.Game && x.Def.Ref == game.Id).Def is { } existing) return existing;
        var def = new StartTileDef { Kind = StartTileKind.Game, Ref = game.Id, Size = game.IsApp ? StartTileSize.Small : StartTileSize.Medium };
        Layout.Tiles.Add(def); // W10M처럼 맨 끝에 붙는다
        Save();
        return def;
    }

    public void PinAction(string key)
    {
        if (IsActionPinned(key)) return;
        Layout.Tiles.Add(new StartTileDef { Kind = StartTileKind.Action, Ref = key });
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
        if (folder is null) return;
        if (folder.Children.Count == 1) Replace(folder, folder.Children[0]);
        else if (folder.Children.Count == 0) Layout.Tiles.Remove(folder);
    }

    private void Replace(StartTileDef old, StartTileDef with)
    {
        var i = Layout.Tiles.IndexOf(old);
        if (i >= 0) Layout.Tiles[i] = with;
    }

    // ── 이동·크기·폴더 ─────────────────────────────────────

    public void Resize(StartTileDef def)
    {
        def.Size = def.Size switch { StartTileSize.Small => StartTileSize.Medium, StartTileSize.Medium => StartTileSize.Large, _ => StartTileSize.Small };
        Save();
    }

    /// <summary>def를 target 앞(또는 뒤)으로. target이 열린 폴더 안이면 그 폴더로 들어간다 (폴더는 폴더에 못 들어감).</summary>
    public void Move(StartTileDef def, StartTileDef target, bool after)
    {
        if (def == target) return;
        var targetOwner = OwnerOf(target);
        if (targetOwner is null) return;
        if (def.Kind == StartTileKind.Folder && targetOwner != Layout.Tiles)
        {
            // 폴더는 폴더 밖으로만: 대상이 든 폴더 옆으로
            target = FolderOf(target)!;
            targetOwner = Layout.Tiles;
        }
        var oldFolder = FolderOf(def);
        OwnerOf(def)!.Remove(def);
        var i = targetOwner.IndexOf(target);
        targetOwner.Insert(after ? i + 1 : i, def);
        if (oldFolder is not null && oldFolder.Children.Count <= 1 && !oldFolder.Children.Contains(target))
        {
            if (oldFolder.Children.Count == 1) Replace(oldFolder, oldFolder.Children[0]);
            else Layout.Tiles.Remove(oldFolder);
        }
        Save();
    }

    /// <summary>def를 target 위에 놓기: target이 폴더면 그 안으로, 아니면 둘로 새 폴더를 만든다. 만든(또는 넣은) 폴더를 돌려준다.</summary>
    public StartTileDef? Merge(StartTileDef def, StartTileDef target)
    {
        if (def == target || def.Kind == StartTileKind.Folder) return null;
        if (FolderOf(target) is { } inside) target = inside; // 열린 폴더 안의 타일 위 = 그 폴더
        if (FolderOf(def) == target) return target;
        Remove(def);
        if (target.Kind == StartTileKind.Folder)
        {
            target.Children.Add(def);
            Save();
            return target;
        }
        var folder = new StartTileDef { Kind = StartTileKind.Folder, Title = "폴더", Size = StartTileSize.Medium };
        Replace(target, folder);
        folder.Children.AddRange([target, def]);
        Save();
        return folder;
    }

    /// <summary>폴더에서 꺼내 폴더 바로 뒤에 둔다.</summary>
    public void TakeOutOfFolder(StartTileDef def)
    {
        if (FolderOf(def) is not { } folder) return;
        folder.Children.Remove(def);
        Layout.Tiles.Insert(Layout.Tiles.IndexOf(folder) + 1, def);
        if (folder.Children.Count == 1) Replace(folder, folder.Children[0]);
        else if (folder.Children.Count == 0) Layout.Tiles.Remove(folder);
        Save();
    }

    public void Rename(StartTileDef folder, string title)
    {
        if (string.IsNullOrWhiteSpace(title)) return;
        folder.Title = title.Trim();
        Save();
    }
}
