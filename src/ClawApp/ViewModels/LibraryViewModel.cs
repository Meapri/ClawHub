using System.Collections.ObjectModel;
using ClawApp.Input;
using ClawApp.Services;
using ClawCore.Games;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;

namespace ClawApp.ViewModels;

/// <summary>빈 분류에서 보여 줄 도움 동작.</summary>
public enum LibraryEmptyAction { AddProgram, Rescan, ShowGames, ClearFilter }

/// <summary>라이브러리 분류 탭 하나 (피벗 머리글). 개수는 이름 찾기가 걸려 있으면 그 결과 기준.</summary>
public sealed partial class LibraryTab(string label, Func<GameEntry, bool> filter, string emptyTitle, string emptyText, LibraryEmptyAction emptyAction)
    : ObservableObject
{
    public string Label { get; } = label;
    public Func<GameEntry, bool> Filter { get; } = filter;
    public string EmptyTitle { get; } = emptyTitle;
    public string EmptyText { get; } = emptyText;
    public LibraryEmptyAction EmptyAction { get; } = emptyAction;
    [ObservableProperty] private int _count;
    [ObservableProperty] private bool _isSelected;
}

/// <summary>
/// 라이브러리: 분류 탭(LB/RB) · 이름 찾기(LT) · 정렬(RT) · 커버 타일(A 열기, X 시작 화면 고정, Y 프로그램 추가).
/// 빈 분류에는 무엇을 하면 되는지와 바로 할 수 있는 버튼을 보여 준다.
/// </summary>
public sealed partial class LibraryViewModel : ObservableObject, INavPage
{
    private readonly ShellViewModel _shell;

    /// <summary>탭·정렬·찾기를 바꿀 때 아트를 다시 읽지 않도록 타일을 재사용한다 (페이지가 다시 보이면 비움).</summary>
    private readonly Dictionary<string, GameTileViewModel> _tileCache = [];

    public IReadOnlyList<LibraryTab> Tabs { get; } =
    [
        // 게임 탭들은 앱(게임이 아닌 프로그램)을 빼고, 앱은 마지막 탭에 따로
        new("게임", g => !g.IsApp,
            "아직 게임이 없어요", "Steam·Epic·GOG와 국내 런처에 설치된 게임은 자동으로 나타나요. 목록에 없는 게임은 직접 추가할 수 있어요.",
            LibraryEmptyAction.AddProgram),
        new("최근", g => !g.IsApp && g.LastPlayed is not null,
            "아직 플레이한 게임이 없어요", "게임을 한 번 실행하면 여기에 모여요.",
            LibraryEmptyAction.ShowGames),
        new("Steam", g => !g.IsApp && g.Source == GameSource.Steam,
            "Steam 게임을 찾지 못했어요", "Steam에서 게임을 설치한 뒤 다시 찾아보세요.",
            LibraryEmptyAction.Rescan),
        new("Epic", g => !g.IsApp && g.Source == GameSource.Epic,
            "Epic 게임을 찾지 못했어요", "Epic Games 런처에서 게임을 설치한 뒤 다시 찾아보세요.",
            LibraryEmptyAction.Rescan),
        new("기타", g => !g.IsApp && g.Source is GameSource.Installed or GameSource.Custom,
            "다른 게임이 없어요", "국내 런처 게임·에뮬레이터처럼 자동으로 안 잡히는 게임은 직접 추가해 보세요.",
            LibraryEmptyAction.AddProgram),
        new("앱", g => g.IsApp,
            "추가한 앱이 없어요", "브라우저·메신저 같은 프로그램을 앱으로 넣어 두면 여기서 바로 실행할 수 있어요.",
            LibraryEmptyAction.AddProgram),
    ];

    [ObservableProperty] private int _tabIndex;
    [ObservableProperty] private ObservableCollection<GameTileViewModel> _tiles = [];
    /// <summary>이름 찾기 (InlineEdit — 입력을 끝낼 때 적용).</summary>
    [ObservableProperty] private string _filter = "";

    public LibraryViewModel(ShellViewModel shell)
    {
        _shell = shell;
        Tabs[0].IsSelected = true;
        Apply();
    }

    public string Title => "라이브러리";

    public IReadOnlyList<Hint> Hints
    {
        get
        {
            var hints = new List<Hint> { new("A", FocusedTile is null ? "선택" : "열기") };
            if (FocusedTile is { } t) hints.Add(new("X", t.IsPinned ? "고정 해제" : "시작 화면에 고정"));
            hints.Add(new("Y", "프로그램 추가"));
            hints.Add(new("LT", "찾기"));
            hints.Add(new("RT", "정렬"));
            hints.Add(new("LB", "이전 탭"));
            hints.Add(new("RB", "다음 탭"));
            hints.Add(new("B", HasFilter ? "찾기 지우기" : "뒤로"));
            return hints;
        }
    }

    // ── 표시 ─────────────────────────────────────────────

    public LibraryTab CurrentTab => Tabs[TabIndex];
    public bool IsEmpty => Tiles.Count == 0;
    public bool HasFilter => !string.IsNullOrWhiteSpace(Filter);

    public string EmptyTitle => HasFilter ? $"'{Filter.Trim()}'와(과) 맞는 항목이 없어요" : CurrentTab.EmptyTitle;
    public string EmptyText => HasFilter
        ? (Tabs.Where(t => t.Count > 0).Select(t => $"{t.Label} {t.Count}개").ToList() is { Count: > 0 } other
            ? $"다른 분류에는 있어요: {string.Join(" · ", other)}"
            : "철자를 확인하거나 찾기를 지워 보세요.")
        : CurrentTab.EmptyText;
    public LibraryEmptyAction EmptyAction => HasFilter ? LibraryEmptyAction.ClearFilter : CurrentTab.EmptyAction;
    public string EmptyActionLabel => EmptyAction switch
    {
        LibraryEmptyAction.Rescan => "설치된 게임 다시 찾기",
        LibraryEmptyAction.ShowGames => "게임 목록 보기",
        LibraryEmptyAction.ClearFilter => "찾기 지우기",
        _ => "프로그램 추가",
    };
    /// <summary>다시 찾기 화면에서도 직접 추가로 갈 수 있게 두 번째 버튼.</summary>
    public bool HasSecondaryEmptyAction => EmptyAction == LibraryEmptyAction.Rescan;

    /// <summary>"12개" 또는 "'메이플' 찾는 중 · 3개".</summary>
    public string CountText => HasFilter ? $"'{Filter.Trim()}' 찾는 중 · {Tiles.Count}개" : $"{Tiles.Count}개";

    public LibrarySort Sort
    {
        get => _shell.Settings.LibrarySort;
        private set
        {
            if (_shell.Settings.LibrarySort == value) return;
            _shell.Settings.LibrarySort = value;
            _shell.Settings.Save();
            OnPropertyChanged();
            OnPropertyChanged(nameof(SortLabel));
            Apply();
        }
    }

    public string SortLabel => Sort switch
    {
        LibrarySort.Name => "이름순",
        LibrarySort.Added => "최근 추가순",
        _ => "최근 플레이순",
    };

    // ── 포커스 (뷰가 알려 준다: X 힌트·고정 대상) ─────────────

    private GameTileViewModel? _focusedTile;

    /// <summary>패드 포커스가 있는 타일 (타일 밖이면 null). 힌트의 X가 이 타일 기준으로 바뀐다.</summary>
    public GameTileViewModel? FocusedTile
    {
        get => _focusedTile;
        set
        {
            if (_focusedTile == value) return;
            var hintsChange = (_focusedTile is null) != (value is null) || _focusedTile?.IsPinned != value?.IsPinned;
            _focusedTile = value;
            if (hintsChange) _shell.UpdateHints();
        }
    }

    /// <summary>목록을 다시 만든 뒤 포커스를 돌려줄 타일 (정렬을 바꿔도 보던 게임에 머문다).</summary>
    public GameTileViewModel? PreferredFocus { get; private set; }

    /// <summary>LT: 뷰가 이름 찾기 입력을 연다.</summary>
    public event Action? SearchRequested;

    // ── 동작 ─────────────────────────────────────────────

    partial void OnTabIndexChanged(int value)
    {
        for (var i = 0; i < Tabs.Count; i++) Tabs[i].IsSelected = i == value;
        OnPropertyChanged(nameof(CurrentTab));
        Apply();
    }

    partial void OnFilterChanged(string value)
    {
        Apply();
        _shell.UpdateHints(); // B = 찾기 지우기 / 뒤로
    }

    /// <summary>아트를 새로 받았을 때 (셸) — 타일을 새로 만든다.</summary>
    public void Refresh()
    {
        _tileCache.Clear();
        Apply();
    }

    /// <summary>프로그램 추가·게임 상세에서 돌아오면 새로 넣은 것·바뀐 이름·고정 상태가 보이게.</summary>
    public void OnShown() => Refresh();

    [RelayCommand]
    private void AddProgram() => _shell.OpenAppPickerCommand.Execute(null);

    [RelayCommand]
    private void SelectTab(LibraryTab tab)
    {
        var i = Tabs.ToList().IndexOf(tab);
        if (i >= 0) TabIndex = i;
    }

    /// <summary>최근 플레이순 → 이름순 → 최근 추가순 → …</summary>
    [RelayCommand]
    private void CycleSort()
    {
        Sort = Sort switch
        {
            LibrarySort.Recent => LibrarySort.Name,
            LibrarySort.Name => LibrarySort.Added,
            _ => LibrarySort.Recent,
        };
        _shell.ShowToast($"정렬: {SortLabel}");
    }

    [RelayCommand]
    private void ClearFilter() => Filter = "";

    [RelayCommand]
    private void Search() => SearchRequested?.Invoke();

    [RelayCommand]
    private void RunEmptyAction()
    {
        switch (EmptyAction)
        {
            case LibraryEmptyAction.Rescan: Rescan(); break;
            case LibraryEmptyAction.ShowGames: TabIndex = 0; break;
            case LibraryEmptyAction.ClearFilter: Filter = ""; break;
            default: AddProgram(); break;
        }
    }

    [RelayCommand]
    private void Rescan()
    {
        _shell.Library.Refresh();
        _shell.Home.Reload();
        Refresh();
        _shell.ShowToast($"게임·앱 {_shell.Library.Visible.Count()}개를 찾았어요");
    }

    private void Apply()
    {
        var q = Filter.Trim();
        var visible = _shell.Library.Visible
            .Where(g => q.Length == 0 || g.Title.Contains(q, StringComparison.CurrentCultureIgnoreCase))
            .ToList();
        foreach (var tab in Tabs) tab.Count = visible.Count(tab.Filter);

        var runningId = _shell.RunningGame?.Id;
        var previous = FocusedTile?.Game.Id;
        Tiles = new ObservableCollection<GameTileViewModel>(Sorted(visible.Where(CurrentTab.Filter)).Select(g =>
        {
            if (!_tileCache.TryGetValue(g.Id, out var tile)) _tileCache[g.Id] = tile = new GameTileViewModel(_shell, g);
            tile.IsPinned = _shell.Start.IsPinned(g);
            tile.IsRunning = g.Id == runningId;
            return tile;
        }));
        PreferredFocus = previous is null ? null : Tiles.FirstOrDefault(t => t.Game.Id == previous);

        foreach (var p in new[] { nameof(IsEmpty), nameof(EmptyTitle), nameof(EmptyText), nameof(EmptyAction), nameof(EmptyActionLabel),
                     nameof(HasSecondaryEmptyAction), nameof(CountText), nameof(HasFilter) })
            OnPropertyChanged(p);
    }

    private IEnumerable<GameEntry> Sorted(IEnumerable<GameEntry> games) => Sort switch
    {
        LibrarySort.Name => games.OrderBy(g => g.Title, StringComparer.CurrentCulture),
        LibrarySort.Added => games.OrderByDescending(AddedAt).ThenBy(g => g.Title, StringComparer.CurrentCulture),
        _ => games.OrderByDescending(g => g.LastPlayed ?? DateTime.MinValue).ThenBy(g => g.Title, StringComparer.CurrentCulture),
    };

    /// <summary>추가한 시각: 직접 추가한 기록 → 설치 폴더(또는 실행 파일)가 생긴 시각. 디스크 조회는 한 번만.</summary>
    private static readonly Dictionary<string, DateTime> InstallTimes = [];

    private static DateTime AddedAt(GameEntry g)
    {
        if (g.AddedAt is { } t) return t;
        if (InstallTimes.TryGetValue(g.Id, out var cached)) return cached;
        var time = DateTime.MinValue;
        try
        {
            if (g.InstallDir is { Length: > 0 } dir && Directory.Exists(dir)) time = Directory.GetCreationTime(dir);
            else if (File.Exists(g.LaunchTarget)) time = File.GetCreationTime(g.LaunchTarget);
        }
        catch (Exception) { /* 접근 불가 — 맨 뒤로 */ }
        return InstallTimes[g.Id] = time;
    }

    /// <summary>포커스한 게임·앱을 시작 화면에 고정하거나 푼다.</summary>
    private void TogglePin()
    {
        if (FocusedTile is not { } tile) return;
        if (tile.IsPinned)
        {
            _shell.Start.Unpin(tile.Game);
            _shell.ShowToast($"{tile.Title}: 시작 화면에서 뺐어요");
        }
        else
        {
            _shell.Start.Pin(tile.Game);
            _shell.ShowToast($"{tile.Title}: 시작 화면 끝에 고정했어요");
        }
        tile.IsPinned = _shell.Start.IsPinned(tile.Game);
        _shell.UpdateHints();
    }

    public bool Handle(NavAction action)
    {
        switch (action)
        {
            case NavAction.TabPrev: TabIndex = (TabIndex + Tabs.Count - 1) % Tabs.Count; return true;
            case NavAction.TabNext: TabIndex = (TabIndex + 1) % Tabs.Count; return true;
            case NavAction.Options: AddProgram(); return true;
            case NavAction.Secondary: TogglePin(); return true;
            case NavAction.TriggerLeft: Search(); return true;
            case NavAction.TriggerRight: CycleSort(); return true;
            case NavAction.Back when HasFilter: Filter = ""; return true;
            default: return false;
        }
    }
}
