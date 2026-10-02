using System.Collections.ObjectModel;
using System.Windows;
using ClawApp.Input;
using ClawApp.Services;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;

namespace ClawApp.ViewModels;

/// <summary>
/// 홈 = Windows 10 Mobile식 시작 화면. 고정한 타일을 정사각형(작게·보통·크게)으로 자유롭게 배치하고, 폴더로 묶는다.
/// 편집: 패드 Y · 터치 길게 누르기 · 마우스 오른쪽 클릭.
///  - 터치/마우스: 끌어서 옮기기, 다른 타일 가운데에 잠시 대고 놓으면 폴더, 타일 모서리 버튼으로 고정 해제·크기.
///  - 패드: A 잡기/놓기(잡은 채 방향키로 이동) · X 크기 · Y 고정 해제 · LB 앞 타일과 폴더로 묶기 · RB 폴더에서 꺼내기/폴더 이름 · B 끝.
/// </summary>
public sealed partial class HomeViewModel : ObservableObject, INavPage
{
    private readonly ShellViewModel _shell;
    private readonly Dictionary<string, StartTileViewModel> _vms = [];
    private string? _openFolderId;

    /// <summary>"이어서 플레이" = 가장 최근 게임 (없으면 첫 게임). 배경 기본 그림에도 쓴다.</summary>
    [ObservableProperty] private GameTileViewModel? _hero;

    /// <summary>화면에 보이는 순서대로 (열린 폴더의 타일은 폴더 바로 뒤).</summary>
    public ObservableCollection<StartTileViewModel> Tiles { get; } = [];

    [ObservableProperty] private bool _isEditing;
    [ObservableProperty] private StartTileViewModel? _selected;
    [ObservableProperty] private StartTileViewModel? _grabbed;

    /// <summary>폴더 이름 바꾸기 창 (null = 닫힘).</summary>
    [ObservableProperty] private StartTileViewModel? _renaming;
    [ObservableProperty] private string _renameText = "";

    /// <summary>뷰에게: 이 타일에 포커스를 줘라 (순서가 바뀐 뒤 등).</summary>
    public event Action<StartTileViewModel>? FocusTile;

    public HomeViewModel(ShellViewModel shell)
    {
        _shell = shell;
        _shell.Start.EnsureDefault(_shell.Library);
        _shell.Start.Changed += Rebuild;
        Reload();
    }

    public string Title => IsEditing ? "시작 화면 편집" : "홈";

    public IReadOnlyList<Hint> Hints => IsEditing
        ? Grabbed is not null
            ? [new("A", "놓기"), new("LB", "폴더로 묶기"), new("B", "편집 끝")]
            : RbLabel is { } rb
                ? [new("A", "잡기"), new("X", "크기"), new("Y", "고정 해제"), new("LB", "앞 타일과 폴더로"), new("RB", rb), new("B", "편집 끝")]
                : [new("A", "잡기"), new("X", "크기"), new("Y", "고정 해제"), new("LB", "앞 타일과 폴더로"), new("B", "편집 끝")]
        : _shell.IsGameRunning
            ? [new("A", "선택"), new("X", "크기"), new("Y", "편집"), new("B", "게임으로"), new("…", "빠른 설정")]
            : [new("A", "선택"), new("X", "크기"), new("Y", "편집"), new("…", "빠른 설정")];

    private string? RbLabel => Selected is { IsInOpenFolder: true } ? "폴더에서 꺼내기" : Selected is { IsFolder: true } ? "폴더 이름" : null;

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
    }

    /// <summary>배치 정의 → 보이는 타일 목록. 같은 정의의 ViewModel(=같은 화면 요소)은 그대로 옮겨 쓴다.</summary>
    private void Rebuild()
    {
        var wanted = new List<StartTileViewModel>();
        foreach (var def in _shell.Start.Layout.Tiles)
        {
            var vm = Vm(def);
            vm.IsInOpenFolder = false;
            vm.IsFolderOpen = def.Kind == StartTileKind.Folder && def.Id == _openFolderId;
            if (vm.IsVisible) wanted.Add(vm);
            if (!vm.IsFolderOpen) continue;
            foreach (var child in def.Children)
            {
                var c = Vm(child);
                c.IsInOpenFolder = true;
                c.IsFolderOpen = false;
                if (c.IsVisible) wanted.Add(c);
            }
        }

        // 목록을 새로 만들지 않고 옮기기·넣기·빼기만 → 타일 요소가 유지되어 미끄러지는 애니메이션과 포커스가 산다
        for (var i = 0; i < wanted.Count; i++)
        {
            var at = Tiles.IndexOf(wanted[i]);
            if (at == i) continue;
            if (at >= 0) Tiles.Move(at, i);
            else Tiles.Insert(i, wanted[i]);
        }
        while (Tiles.Count > wanted.Count) Tiles.RemoveAt(Tiles.Count - 1);
        foreach (var gone in _vms.Keys.Except(AllDefIds()).ToList()) _vms.Remove(gone);

        foreach (var t in Tiles) t.IsEditing = IsEditing;
        if (Selected is not null && !Tiles.Contains(Selected)) Selected = null;
        _shell.UpdateHints();
    }

    private IEnumerable<string> AllDefIds() =>
        _shell.Start.Layout.Tiles.SelectMany(t => t.Children.Prepend(t)).Select(d => d.Id);

    private StartTileViewModel Vm(StartTileDef def)
    {
        if (!_vms.TryGetValue(def.Id, out var vm) || vm.Def != def) _vms[def.Id] = vm = new StartTileViewModel(_shell, def);
        vm.Refresh(Hero);
        return vm;
    }

    private StartTileViewModel? Focused =>
        (System.Windows.Input.Keyboard.FocusedElement as FrameworkElement)?.DataContext as StartTileViewModel;

    // ── 누르기 ────────────────────────────────────────────

    /// <summary>타일을 눌렀을 때: 편집 중이면 고르기, 아니면 실행·열기 (폴더는 펼치기/접기).</summary>
    [RelayCommand]
    private void Activate(StartTileViewModel tile)
    {
        if (IsEditing)
        {
            Select(tile);
            return;
        }
        switch (tile.Kind)
        {
            case StartTileKind.Folder:
                _openFolderId = _openFolderId == tile.Def.Id ? null : tile.Def.Id;
                Rebuild();
                FocusTile?.Invoke(tile);
                break;
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
    }

    private void RunAction(string? key)
    {
        switch (key)
        {
            case "library": _shell.OpenLibraryCommand.Execute(null); break;
            case "profiles": _shell.OpenProfilesCommand.Execute(null); break;
            case "settings": _shell.OpenSettingsCommand.Execute(null); break;
            case "xbox": _shell.OpenXboxAppCommand.Execute(null); break;
            case "desktop": _shell.GoToDesktopCommand.Execute(null); break;
            case "quick": _shell.OpenQuickSettingsCommand.Execute(null); break;
            case "addprogram": _shell.OpenAppPickerCommand.Execute(null); break;
            case "sleep": _shell.SleepCommand.Execute(null); break;
            case "restart": _shell.RestartCommand.Execute(null); break;
            case "shutdown": _shell.ShutdownCommand.Execute(null); break;
            case "exitapp": _shell.ExitAppCommand.Execute(null); break;
        }
    }

    // ── 편집 ──────────────────────────────────────────────

    /// <summary>편집 시작 (길게 누르기·오른쪽 클릭·패드 Y). tile = 처음 고를 타일.</summary>
    public void BeginEdit(StartTileViewModel? tile)
    {
        IsEditing = true;
        foreach (var t in Tiles) t.IsEditing = true;
        Select(tile ?? Focused ?? Tiles.FirstOrDefault());
        OnPropertyChanged(nameof(Title));
        _shell.OnPropertyChangedPageTitle();
        _shell.UpdateHints();
    }

    [RelayCommand]
    public void EndEdit()
    {
        if (Grabbed is not null) Drop();
        IsEditing = false;
        foreach (var t in Tiles) { t.IsEditing = false; t.IsSelected = false; t.IsMergeTarget = false; }
        Selected = null;
        Renaming = null;
        OnPropertyChanged(nameof(Title));
        _shell.OnPropertyChangedPageTitle();
        _shell.UpdateHints();
    }

    public void Select(StartTileViewModel? tile)
    {
        if (Selected is not null) Selected.IsSelected = false;
        Selected = tile;
        if (tile is null) return;
        tile.IsSelected = true;
        FocusTile?.Invoke(tile);
        _shell.UpdateHints();
    }

    [RelayCommand]
    private void Unpin(StartTileViewModel tile)
    {
        var index = Tiles.IndexOf(tile);
        if (!_shell.Start.Unpin(tile.Def))
        {
            _shell.ShowToast("설정 타일은 뺄 수 없어요 (다른 곳에서 설정으로 갈 길이 없어져요)");
            return;
        }
        _shell.ShowToast($"{tile.Title} 고정을 풀었어요 — 라이브러리나 설정 → 홈 화면에서 다시 고정할 수 있어요");
        if (IsEditing && Tiles.Count > 0) Select(Tiles[Math.Clamp(index, 0, Tiles.Count - 1)]);
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
        FocusTile?.Invoke(tile);
    }

    [RelayCommand]
    private void StartRename(StartTileViewModel tile)
    {
        if (!tile.IsFolder) return;
        RenameText = tile.Title;
        Renaming = tile;
    }

    [RelayCommand]
    private void FinishRename()
    {
        if (Renaming is { } folder)
        {
            _shell.Start.Rename(folder.Def, RenameText);
            folder.Refresh(Hero);
            FocusTile?.Invoke(folder);
        }
        Renaming = null;
    }

    /// <summary>앞 타일(순서상 바로 앞)과 폴더로 묶기 — 앞이 폴더면 그 안으로.</summary>
    private void MergeWithPrevious(StartTileViewModel tile)
    {
        var i = Tiles.IndexOf(tile);
        if (i <= 0) { _shell.ShowToast("맨 앞 타일은 묶을 앞 타일이 없어요"); return; }
        var target = Tiles[i - 1];
        if (_shell.Start.Merge(tile.Def, target.Def) is not { } folder)
        {
            _shell.ShowToast("폴더는 다른 폴더에 넣을 수 없어요");
            return;
        }
        _openFolderId = folder.Id; // 넣은 걸 바로 보이게
        Grabbed = null;
        Rebuild();
        if (_vms.TryGetValue(tile.Def.Id, out var vm) && Tiles.Contains(vm)) Select(vm);
        _shell.ShowToast($"'{folder.Title}' 폴더에 넣었어요");
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

    /// <summary>잡은 타일을 그 방향에서 가장 가까운 타일의 자리로.</summary>
    private void MoveGrabbed(NavAction dir)
    {
        if (Grabbed is not { } g) return;
        double cx = g.Col + g.Span / 2.0, cy = g.Row + g.Span / 2.0;
        var target = Tiles.Where(t => t != g)
            .Select(t => (t, dx: t.Col + t.Span / 2.0 - cx, dy: t.Row + t.Span / 2.0 - cy))
            .Where(x => dir switch
            {
                NavAction.Left => x.dx < -0.1,
                NavAction.Right => x.dx > 0.1,
                NavAction.Up => x.dy < -0.1,
                _ => x.dy > 0.1,
            })
            // 가는 방향의 거리 + 옆으로 벗어난 거리(가중)로 가장 가까운 것
            .OrderBy(x => dir is NavAction.Left or NavAction.Right ? Math.Abs(x.dx) + 2 * Math.Abs(x.dy) : Math.Abs(x.dy) + 2 * Math.Abs(x.dx))
            .Select(x => x.t).FirstOrDefault();
        if (target is null) return;
        var after = Tiles.IndexOf(target) > Tiles.IndexOf(g);
        _shell.Start.Move(g.Def, target.Def, after);
        FocusTile?.Invoke(g);
    }

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
    public void DragOver(StartTileViewModel target, bool center, bool after)
    {
        if (_dragging is not { } d || target == d) return;
        foreach (var t in Tiles) t.IsMergeTarget = false;
        if (center && !d.IsFolder)
        {
            target.IsMergeTarget = true;
            return;
        }
        _shell.Start.Move(d.Def, target.Def, after);
    }

    public void ClearMergeHint()
    {
        foreach (var t in Tiles) t.IsMergeTarget = false;
    }

    public void EndDrag()
    {
        if (_dragging is not { } d) return;
        d.IsDragging = false;
        _dragging = null;
        if (Tiles.FirstOrDefault(t => t.IsMergeTarget) is { } target)
        {
            target.IsMergeTarget = false;
            if (_shell.Start.Merge(d.Def, target.Def) is { } folder)
            {
                _openFolderId = folder.Id;
                Rebuild();
                _shell.ShowToast($"'{folder.Title}' 폴더에 넣었어요");
            }
        }
        if (_vms.TryGetValue(d.Def.Id, out var vm) && Tiles.Contains(vm)) Select(vm);
    }

    // ── 패드 ──────────────────────────────────────────────

    public bool Handle(NavAction action)
    {
        if (Renaming is not null)
        {
            if (action == NavAction.Back) { FinishRename(); return true; }
            return false;
        }

        if (!IsEditing)
        {
            switch (action)
            {
                case NavAction.Back:
                    if (_openFolderId is not null) { _openFolderId = null; Rebuild(); return true; }
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
            case NavAction.Accept when Selected is { } s:
                if (Grabbed is null) Grab(s); else Drop();
                return true;
            case NavAction.Up or NavAction.Down or NavAction.Left or NavAction.Right when Grabbed is not null:
                MoveGrabbed(action);
                return true;
            case NavAction.Secondary when Selected is { } s:
                Resize(s);
                return true;
            case NavAction.Options when Selected is { } s:
                Unpin(s);
                return true;
            case NavAction.TabPrev when Selected is { } s:
                MergeWithPrevious(s);
                return true;
            case NavAction.TabNext when Selected is { IsInOpenFolder: true } s:
                TakeOut(s);
                return true;
            case NavAction.TabNext when Selected is { IsFolder: true } s:
                StartRename(s);
                return true;
        }
        return action is NavAction.TabNext; // 편집 중엔 다른 페이지로 넘어가지 않게
    }

    public static string SizeLabel(StartTileSize s) => s switch { StartTileSize.Small => "작게", StartTileSize.Large => "크게", _ => "보통" };
}
