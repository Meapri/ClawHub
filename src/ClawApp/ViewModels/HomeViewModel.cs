using System.Collections.ObjectModel;
using System.Windows;
using ClawApp.Input;
using ClawApp.Services;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;

namespace ClawApp.ViewModels;

/// <summary>
/// 홈 = Windows 10 Mobile식 시작 화면. 이름 붙은 구역마다 정사각형 타일(작게·보통·크게)을 자유롭게 배치하고, 폴더로 묶는다.
/// 폴더를 누르면 폴더 창이 열린다. 편집: 패드 Y · 터치 길게 누르기 · 마우스 오른쪽 클릭.
///  - 터치/마우스: 끌어서 옮기기(다른 구역으로도), 다른 타일 가운데에 잠시 대면 폴더, "새 구역" 자리에 놓으면 새 구역,
///    모서리 버튼으로 고정 해제·크기, 구역 제목을 눌러 이름 바꾸기.
///  - 패드: A 잡기/놓기(잡은 채 방향키로 이동) · X 크기 · Y 고정 해제 · LB 앞 타일과 폴더로 · RB 새 구역으로(폴더 창에선 꺼내기) · B 끝.
/// </summary>
public sealed partial class HomeViewModel : ObservableObject, INavPage
{
    private readonly ShellViewModel _shell;
    private readonly Dictionary<string, StartTileViewModel> _vms = [];
    private readonly Dictionary<string, StartGroupViewModel> _groupVms = [];

    /// <summary>"이어서 플레이" = 가장 최근 게임 (없으면 첫 게임). 배경 기본 그림에도 쓴다.</summary>
    [ObservableProperty] private GameTileViewModel? _hero;

    public ObservableCollection<StartGroupViewModel> Groups { get; } = [];

    /// <summary>열린 폴더 (null = 닫힘)와 그 안의 타일.</summary>
    [ObservableProperty] private StartTileViewModel? _openFolder;
    public ObservableCollection<StartTileViewModel> FolderTiles { get; } = [];

    /// <summary>폴더 창 판 높이: 안의 타일이 대략 정사각형으로 퍼지게 (2~4칸 = 168~344px).</summary>
    public double FolderBoardHeight
    {
        get
        {
            // 판은 세로 칸 수를 정하면 가로로 채워 나간다. 예전에는 칸 수의 제곱근으로 정사각형에 가깝게 잡아
            // 작은 타일 몇 개만 있어도 4줄이 되어 한 열로 쌓이고 오른쪽·아래가 텅 비었다.
            // 이제 2줄(가로로 길게)을 기본으로, 큰 타일이 있거나 한 줄에 다 안 들어갈 만큼 많을 때만 늘린다.
            var cells = FolderTiles.Sum(t => t.Span * t.Span);
            var rows = FolderTiles.Any(t => t.IsLarge) || cells > 2 * MaxFolderColumns ? 4 : 2;
            if (cells > 4 * MaxFolderColumns) rows = 6;
            return rows * Views.StartPanel.Unit - Views.StartPanel.Gap;
        }
    }

    /// <summary>폴더 창 한 줄에 들어가는 작은 칸 수 (이보다 많으면 줄을 늘린다 — 화면 폭 1280 기준).</summary>
    private const int MaxFolderColumns = 12;

    [ObservableProperty] private bool _isEditing;
    [ObservableProperty] private StartTileViewModel? _selected;
    [ObservableProperty] private StartTileViewModel? _grabbed;

    /// <summary>이름 바꾸기 창: 폴더 또는 구역 (null = 닫힘).</summary>
    [ObservableProperty] private object? _renaming;
    [ObservableProperty] private string _renameText = "";
    /// <summary>창 제목. 닫혀서 사라지는 동안(Reveal)에도 바뀌지 않게 열릴 때만 정한다.</summary>
    [ObservableProperty] private string _renameTitle = "폴더 이름";

    partial void OnRenamingChanged(object? value)
    {
        if (value is not null) RenameTitle = value is StartGroupViewModel ? "구역 이름" : "폴더 이름";
    }

    /// <summary>뷰에게: 이 타일에 포커스를 줘라 (순서가 바뀐 뒤 등).</summary>
    public event Action<StartTileViewModel>? FocusTile;

    /// <summary>뷰가 채운다: 타일이 화면 어디 있는지 (패드로 옮길 때 방향 계산). 지금 보이는 판(구역들 또는 폴더 창) 기준.</summary>
    public Func<StartTileViewModel, Rect?>? Locate { get; set; }

    public HomeViewModel(ShellViewModel shell)
    {
        _shell = shell;
        FolderTiles.CollectionChanged += (_, _) => OnPropertyChanged(nameof(FolderBoardHeight));
        _shell.Start.EnsureDefault(_shell.Library);
        _shell.Start.Changed += Rebuild;
        Reload();
    }

    public string Title => IsEditing ? "시작 화면 편집" : "홈";

    public IReadOnlyList<Hint> Hints => IsEditing
        ? Grabbed is not null
            ? [new("A", "놓기"), new("LB", "폴더로 묶기"), new("B", "편집 끝")]
            : [new("A", "잡기"), new("X", "크기"), new("Y", "고정 해제"), new("LB", "앞 타일과 폴더로"), new("RB", OpenFolder is null ? "새 구역으로" : "폴더에서 꺼내기"), new("B", "편집 끝")]
        : OpenFolder is not null
            ? [new("A", "선택"), new("Y", "편집"), new("B", "폴더 닫기")]
            : _shell.IsGameRunning
                ? [new("A", "선택"), new("X", "크기"), new("Y", "편집"), new("B", "게임으로"), new("…", "빠른 설정")]
                : [new("A", "선택"), new("X", "크기"), new("Y", "편집"), new("…", "빠른 설정")];

    /// <summary>지금 조작하는 판의 타일 (폴더 창이 열려 있으면 폴더 안).</summary>
    public IEnumerable<StartTileViewModel> Surface => OpenFolder is not null ? FolderTiles : Groups.SelectMany(g => g.Tiles);
    private IEnumerable<StartTileViewModel> AllShown => Groups.SelectMany(g => g.Tiles).Concat(FolderTiles);

    /// <summary>라이브러리가 바뀌었을 때 (게임 실행·추가·숨기기 등).</summary>
    public void Reload()
    {
        var lib = _shell.Library;
        var hero = lib.Recent.Concat(lib.Visible.Where(g => g.LastPlayed is null)).FirstOrDefault(g => !g.IsApp);
        if (Hero?.Game != hero) Hero = hero is null ? null : new GameTileViewModel(_shell, hero);
        Rebuild();
    }

    public void OnShown() => Reload();

    public void OnHidden()
    {
        if (IsEditing) EndEdit();
        CloseFolder();
    }

    /// <summary>배치 정의 → 화면. 같은 정의의 ViewModel(=같은 화면 요소)은 그대로 옮겨 쓴다 (미끄러지는 애니메이션·포커스 유지).</summary>
    private void Rebuild()
    {
        var groups = _shell.Start.Groups;
        var wantedGroups = groups.Select(g =>
        {
            if (!_groupVms.TryGetValue(g.Id, out var vm) || vm.Def != g) _groupVms[g.Id] = vm = new StartGroupViewModel(g);
            vm.IsEditing = IsEditing;
            vm.Refresh();
            Sync(vm.Tiles, g.Tiles.Select(t => Vm(t, inFolder: false)).Where(t => t.IsVisible).ToList());
            return vm;
        }).ToList();
        Sync(Groups, wantedGroups);
        foreach (var gone in _groupVms.Keys.Except(groups.Select(g => g.Id)).ToList()) _groupVms.Remove(gone);

        // 열린 폴더: 아직 있으면 안의 타일, 풀렸으면 닫는다
        if (OpenFolder is { } open && groups.SelectMany(g => g.Tiles).Contains(open.Def))
            Sync(FolderTiles, open.Def.Children.Select(c => Vm(c, inFolder: true)).Where(t => t.IsVisible).ToList());
        else if (OpenFolder is not null)
            CloseFolder();

        var ids = groups.SelectMany(g => g.Tiles).SelectMany(t => t.Children.Prepend(t)).Select(d => d.Id).ToHashSet();
        foreach (var gone in _vms.Keys.Where(k => !ids.Contains(k)).ToList()) _vms.Remove(gone);

        foreach (var t in AllShown) t.IsEditing = IsEditing;
        if (Selected is not null && !AllShown.Contains(Selected)) Selected = null;
        OnPropertyChanged(nameof(FolderBoardHeight)); // 폴더 안 타일 크기가 바뀌었을 수도
        _shell.UpdateHints();
    }

    /// <summary>목록을 새로 만들지 않고 옮기기·넣기·빼기만.</summary>
    private static void Sync<T>(ObservableCollection<T> list, IReadOnlyList<T> wanted)
    {
        for (var i = 0; i < wanted.Count; i++)
        {
            var at = list.IndexOf(wanted[i]);
            if (at == i) continue;
            if (at >= 0) list.Move(at, i);
            else list.Insert(i, wanted[i]);
        }
        while (list.Count > wanted.Count) list.RemoveAt(list.Count - 1);
    }

    private StartTileViewModel Vm(StartTileDef def, bool inFolder)
    {
        if (!_vms.TryGetValue(def.Id, out var vm) || vm.Def != def) _vms[def.Id] = vm = new StartTileViewModel(_shell, def);
        vm.IsInFolder = inFolder;
        vm.Refresh(Hero);
        return vm;
    }

    private StartTileViewModel? Focused =>
        (System.Windows.Input.Keyboard.FocusedElement as FrameworkElement)?.DataContext as StartTileViewModel;

    // ── 누르기 ────────────────────────────────────────────

    /// <summary>타일을 눌렀을 때: 편집 중이면 고르기, 아니면 실행·열기 (폴더는 폴더 창).</summary>
    [RelayCommand]
    private void Activate(StartTileViewModel tile)
    {
        if (IsEditing)
        {
            if (tile.IsFolder && OpenFolder is null && Selected == tile) OpenFolderWindow(tile); // 고른 폴더를 한 번 더 누르면 열기
            else Select(tile);
            return;
        }
        if (tile.IsFolder)
        {
            OpenFolderWindow(tile);
            return;
        }
        var fromFolder = tile.IsInFolder;
        switch (tile.Kind)
        {
            case StartTileKind.Continue when tile.Game is { } g:
                g.PlayCommand.Execute(null);
                break;
            case StartTileKind.Game when tile.Game is { } g:
                if (g.Game.IsApp) _ = _shell.LaunchAsync(g.Game); // 앱은 바로 실행
                else g.OpenCommand.Execute(null);                  // 게임은 상세로
                break;
            case StartTileKind.Action:
                RunAction(tile.Def.Ref);
                break;
        }
        if (fromFolder) CloseFolder();
    }

    private void RunAction(string? key)
    {
        switch (key)
        {
            case "library": _shell.OpenLibraryCommand.Execute(null); break;
            case "profiles": _shell.OpenProfilesCommand.Execute(null); break;
            case "settings": _shell.OpenSettingsCommand.Execute(null); break;
            case "xbox": _shell.OpenXboxAppCommand.Execute(null); break;
            case "xboxmode": _shell.SwitchShellModeCommand.Execute(null); break;
            case "desktop": _shell.GoToDesktopCommand.Execute(null); break;
            case "desktoppad": _shell.GoToDesktopGamepadCommand.Execute(null); break;
            case "quick": _shell.OpenQuickSettingsCommand.Execute(null); break;
            case "addprogram": _shell.OpenAppPickerCommand.Execute(null); break;
            case "sleep": _shell.SleepCommand.Execute(null); break;
            case "restart": _shell.RestartCommand.Execute(null); break;
            case "shutdown": _shell.ShutdownCommand.Execute(null); break;
            case "exitapp": _shell.ExitAppCommand.Execute(null); break;
        }
    }

    // ── 폴더 창 ───────────────────────────────────────────

    private void OpenFolderWindow(StartTileViewModel folder)
    {
        OpenFolder = folder;
        Sync(FolderTiles, folder.Def.Children.Select(c => Vm(c, inFolder: true)).Where(t => t.IsVisible).ToList());
        foreach (var t in FolderTiles) t.IsEditing = IsEditing;
        if (FolderTiles.FirstOrDefault() is { } first)
        {
            if (IsEditing) Select(first);
            else FocusTile?.Invoke(first);
        }
        _shell.UpdateHints();
    }

    [RelayCommand]
    public void CloseFolder()
    {
        if (OpenFolder is not { } folder) return;
        if (Grabbed is not null) Drop();
        OpenFolder = null;
        FolderTiles.Clear();
        if (Groups.SelectMany(g => g.Tiles).Contains(folder))
        {
            if (IsEditing) Select(folder);
            else FocusTile?.Invoke(folder);
        }
        _shell.UpdateHints();
    }

    // ── 편집 ──────────────────────────────────────────────

    /// <summary>편집 시작 (길게 누르기·오른쪽 클릭·패드 Y). tile = 처음 고를 타일.</summary>
    public void BeginEdit(StartTileViewModel? tile)
    {
        IsEditing = true;
        foreach (var t in AllShown) t.IsEditing = true;
        foreach (var g in Groups) g.IsEditing = true;
        Select(tile ?? Focused ?? Surface.FirstOrDefault());
        OnPropertyChanged(nameof(Title));
        _shell.OnPropertyChangedPageTitle();
        _shell.UpdateHints();
    }

    [RelayCommand]
    public void EndEdit()
    {
        if (Grabbed is not null) Drop();
        IsEditing = false;
        foreach (var t in AllShown) { t.IsEditing = false; t.IsSelected = false; t.IsMergeTarget = false; }
        foreach (var g in Groups) g.IsEditing = false;
        var last = Selected;
        Selected = null;
        Renaming = null;
        OnPropertyChanged(nameof(Title));
        _shell.OnPropertyChangedPageTitle();
        _shell.UpdateHints();
        if (last is not null) FocusTile?.Invoke(last);
    }

    public void Select(StartTileViewModel? tile)
    {
        foreach (var t in AllShown) if (t != tile) t.IsSelected = false; // 고른 타일은 늘 하나
        Selected = tile;
        if (tile is null) return;
        tile.IsSelected = true;
        FocusTile?.Invoke(tile);
        _shell.UpdateHints();
    }

    private async Task ConfirmUnpinAsync(StartTileViewModel tile)
    {
        var where = tile.IsInFolder ? "폴더" : "시작 화면";
        var hint = tile.Kind == ClawApp.Services.StartTileKind.Action
            ? "설정 → 홈 화면에서 다시 고정할 수 있어요."
            : "라이브러리에서 다시 고정할 수 있어요.";
        if (!await _shell.ConfirmAsync($"'{tile.Title}' 타일을 {where}에서 뺄까요?", hint, "빼기", danger: true)) return; // 처음 포커스 = 취소
        Unpin(tile);
    }

    [RelayCommand]
    private void Unpin(StartTileViewModel tile)
    {
        var list = Surface.ToList();
        var index = list.IndexOf(tile);
        if (!_shell.Start.Unpin(tile.Def))
        {
            _shell.ShowToast("설정 타일은 뺄 수 없어요 (다른 곳에서 설정으로 갈 길이 없어져요)");
            return;
        }
        _shell.ShowToast($"{tile.Title} 고정을 풀었어요 — 라이브러리나 설정 → 홈 화면에서 다시 고정할 수 있어요");
        var left = Surface.ToList();
        if (IsEditing && left.Count > 0) Select(left[Math.Clamp(index, 0, left.Count - 1)]);
    }

    [RelayCommand]
    private void Resize(StartTileViewModel tile)
    {
        _shell.Start.Resize(tile.Def);
        tile.Refresh(Hero);
        FocusTile?.Invoke(tile);
    }

    [RelayCommand]
    private void TakeOut(StartTileViewModel tile)
    {
        _shell.Start.TakeOutOfFolder(tile.Def);
        _shell.ShowToast($"{tile.Title}을(를) 폴더에서 꺼냈어요");
        if (IsEditing && FolderTiles.FirstOrDefault() is { } next) Select(next);
    }

    [RelayCommand]
    private void StartRename(StartTileViewModel tile)
    {
        if (!tile.IsFolder) return;
        RenameText = tile.Title;
        Renaming = tile;
    }

    [RelayCommand]
    private void RenameGroup(StartGroupViewModel group)
    {
        RenameText = group.Title;
        Renaming = group;
    }

    [RelayCommand]
    private void FinishRename()
    {
        switch (Renaming)
        {
            case StartTileViewModel folder:
                _shell.Start.Rename(folder.Def, RenameText);
                folder.Refresh(Hero);
                OnPropertyChanged(nameof(OpenFolder));
                FocusTile?.Invoke(folder);
                break;
            case StartGroupViewModel group:
                _shell.Start.RenameGroup(group.Def, RenameText);
                group.Refresh();
                break;
        }
        Renaming = null;
    }

    /// <summary>앞 타일(같은 구역·폴더 안에서 바로 앞)과 폴더로 묶기 — 앞이 폴더면 그 안으로.</summary>
    private void MergeWithPrevious(StartTileViewModel tile)
    {
        var list = OpenFolder is not null ? FolderTiles.ToList() : Groups.FirstOrDefault(g => g.Tiles.Contains(tile))?.Tiles.ToList() ?? [];
        var i = list.IndexOf(tile);
        if (i <= 0) { _shell.ShowToast("이 구역의 맨 앞 타일은 묶을 앞 타일이 없어요"); return; }
        if (_shell.Start.Merge(tile.Def, list[i - 1].Def) is not { } folder)
        {
            _shell.ShowToast("폴더는 다른 폴더에 넣을 수 없어요");
            return;
        }
        Drop();
        _shell.ShowToast($"'{folder.Title}' 폴더에 넣었어요 — 폴더를 눌러 열 수 있어요");
        if (_vms.TryGetValue(folder.Id, out var vm)) Select(vm);
    }

    private void ToNewGroup(StartTileViewModel tile)
    {
        var after = _shell.Start.GroupOf(tile.Def);
        _shell.Start.MoveToNewGroup(tile.Def, after);
        _shell.ShowToast("새 구역을 만들었어요 — 구역 제목을 눌러 이름을 붙일 수 있어요");
        Select(tile);
    }

    // ── 패드: 잡고 옮기기 ──────────────────────────────────

    private void Grab(StartTileViewModel tile)
    {
        Grabbed = tile;
        tile.IsGrabbed = true;
        _shell.UpdateHints();
    }

    private void Drop()
    {
        if (Grabbed is { } g) g.IsGrabbed = false;
        Grabbed = null;
        _shell.UpdateHints();
    }

    /// <summary>잡은 타일을 그 방향에서 가장 가까운 타일의 자리로 (다른 구역으로도). 오른쪽 끝이면 새 구역.</summary>
    private void MoveGrabbed(NavAction dir)
    {
        if (Grabbed is not { } g) return;
        var target = Nearest(g, dir);
        if (target is null)
        {
            if (dir == NavAction.Right && OpenFolder is null) ToNewGroup(g);
            return;
        }
        _shell.Start.Move(g.Def, target.Def, IsAfter(target, g));
        FocusTile?.Invoke(g);
    }

    /// <summary>
    /// 그 방향에서 가장 가까운 타일 (지금 판 — 구역들 또는 폴더 창). 없으면 null.
    /// 편집 중 선택 이동에도 쓴다: WPF 기본 방향 이동은 타일 대신 위쪽 구역 제목(이름 바꾸기 버튼)으로 가곤 해서
    /// 오른쪽으로 한 칸 가려면 세 번 눌러야 했다.
    /// </summary>
    private StartTileViewModel? Nearest(StartTileViewModel from, NavAction dir)
    {
        if (Locate?.Invoke(from) is not { } r) return null;
        double cx = r.X + r.Width / 2, cy = r.Y + r.Height / 2;
        return Surface.Where(t => t != from)
            .Select(t => (t, r: Locate(t)))
            .Where(x => x.r is not null)
            .Select(x => (x.t, dx: x.r!.Value.X + x.r.Value.Width / 2 - cx, dy: x.r.Value.Y + x.r.Value.Height / 2 - cy))
            .Where(x => dir switch
            {
                NavAction.Left => x.dx < -8,
                NavAction.Right => x.dx > 8,
                NavAction.Up => x.dy < -8,
                _ => x.dy > 8,
            })
            .OrderBy(x => dir is NavAction.Left or NavAction.Right ? Math.Abs(x.dx) + 2 * Math.Abs(x.dy) : Math.Abs(x.dy) + 2 * Math.Abs(x.dx))
            .Select(x => x.t).FirstOrDefault();
    }

    /// <summary>a가 b보다 뒤에 있는지 (화면 순서).</summary>
    public bool IsAfter(StartTileViewModel a, StartTileViewModel b) => _shell.Start.OrderOf(a.Def) > _shell.Start.OrderOf(b.Def);

    // ── 터치·마우스 끌기 (뷰가 부른다) ──────────────────────

    private StartTileViewModel? _dragging;

    public void BeginDrag(StartTileViewModel tile)
    {
        if (!IsEditing) BeginEdit(tile);
        Select(tile);
        _dragging = tile;
        tile.IsDragging = true;
    }

    /// <summary>끄는 동안 다른 타일 위에 머물렀을 때: 가운데면 "폴더로" 표시, 가장자리면 그 자리로 옮긴다.</summary>
    public void DragOver(StartTileViewModel target, bool center)
    {
        if (_dragging is not { } d || target == d) return;
        ClearMergeHint();
        if (center && !d.IsFolder)
        {
            target.IsMergeTarget = true;
            return;
        }
        _shell.Start.Move(d.Def, target.Def, IsAfter(target, d));
    }

    /// <summary>"새 구역" 자리에 머물렀을 때.</summary>
    public void DragToNewGroup()
    {
        if (_dragging is not { } d || OpenFolder is not null) return;
        ClearMergeHint();
        _shell.Start.MoveToNewGroup(d.Def);
    }

    /// <summary>폴더 창 밖으로 끌어 놓았을 때: 폴더에서 꺼낸다.</summary>
    public void DragOutOfFolder()
    {
        if (_dragging is not { } d || !d.IsInFolder) return;
        _shell.Start.TakeOutOfFolder(d.Def);
    }

    public void ClearMergeHint()
    {
        foreach (var t in AllShown) t.IsMergeTarget = false;
    }

    public void EndDrag()
    {
        if (_dragging is not { } d) return;
        d.IsDragging = false;
        _dragging = null;
        if (AllShown.FirstOrDefault(t => t.IsMergeTarget) is { } target)
        {
            target.IsMergeTarget = false;
            if (_shell.Start.Merge(d.Def, target.Def) is { } folder)
                _shell.ShowToast($"'{folder.Title}' 폴더에 넣었어요 — 폴더를 눌러 열 수 있어요");
        }
        if (AllShown.Contains(d)) Select(d);
        else if (_vms.Values.FirstOrDefault(v => v.IsFolder && v.Def.Children.Contains(d.Def)) is { } holder && AllShown.Contains(holder)) Select(holder);
    }

    // ── 패드 ──────────────────────────────────────────────

    public bool Handle(NavAction action)
    {
        if (Renaming is not null)
        {
            if (action == NavAction.Back) { Renaming = null; return true; }
            return false;
        }

        if (!IsEditing)
        {
            switch (action)
            {
                case NavAction.Back:
                    if (OpenFolder is not null) { CloseFolder(); return true; }
                    if (_shell.IsGameRunning) _shell.ReturnToBackground();
                    return true;
                case NavAction.Options:
                    BeginEdit(Focused);
                    return true;
                case NavAction.Secondary when Focused is { } t:
                    Resize(t);
                    _shell.ShowToast($"{t.Title}: {SizeLabel(t.Size)}");
                    return true;
            }
            return false;
        }

        var focused = Focused;
        if (focused is not null && focused != Selected && Grabbed is null) Select(focused); // 방향키로 옮겨 가면 그 타일이 선택
        switch (action)
        {
            case NavAction.Back:
                EndEdit();
                return true;
            case NavAction.Accept when Selected is { } s && System.Windows.Input.Keyboard.FocusedElement is FrameworkElement { DataContext: StartTileViewModel }:
                if (Grabbed is null) Grab(s); else Drop();
                return true;
            case NavAction.Up or NavAction.Down or NavAction.Left or NavAction.Right when Grabbed is not null:
                MoveGrabbed(action);
                return true;
            case NavAction.Up or NavAction.Down or NavAction.Left or NavAction.Right
                when Selected is { } from && System.Windows.Input.Keyboard.FocusedElement is FrameworkElement { DataContext: StartTileViewModel }:
                // 타일 → 타일. 그 방향에 타일이 없을 때만 기본 이동 (위쪽 끝에서 ↑ = 구역 제목 이름 바꾸기 등)
                if (Nearest(from, action) is not { } next) return false;
                Select(next);
                FocusTile?.Invoke(next);
                return true;
            case NavAction.Secondary when Selected is { } s:
                Resize(s);
                return true;
            case NavAction.Options when Selected is { } s:
                // 패드 Y는 편집에 들어갈 때도 쓰는 버튼이라, 편집을 끝내려고 한 번 더 누르면 고른 타일이 바로 빠졌다 → 확인을 받는다
                _ = ConfirmUnpinAsync(s);
                return true;
            case NavAction.TabPrev when Selected is { } s:
                MergeWithPrevious(s);
                return true;
            case NavAction.TabNext when Selected is { IsInFolder: true } s:
                TakeOut(s);
                return true;
            case NavAction.TabNext when Selected is { } s:
                ToNewGroup(s);
                return true;
        }
        return action is NavAction.TabNext; // 편집 중엔 다른 페이지로 넘어가지 않게
    }

    public static string SizeLabel(StartTileSize s) => s switch { StartTileSize.Small => "작게", StartTileSize.Large => "크게", _ => "보통" };
}
