using System.Collections.ObjectModel;
using System.IO;
using System.Windows.Media;
using ClawApp.Input;
using ClawApp.Services;
using ClawCore.Games;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Microsoft.Win32;

namespace ClawApp.ViewModels;

/// <summary>프로그램 목록의 한 칸.</summary>
public sealed partial class AppItemViewModel(StartMenuApp app, ImageSource? icon, bool added) : ObservableObject
{
    public StartMenuApp App { get; } = app;
    public string Name => App.Name;
    public string? Folder => App.Folder;
    public ImageSource? Icon { get; } = icon;
    public bool HasIcon => Icon is not null;
    [ObservableProperty] [NotifyPropertyChangedFor(nameof(StatusText))] private bool _isAdded = added;
    /// <summary>이 화면에서 방금 추가했으면 "앱"/"게임" (원래 있던 항목은 null).</summary>
    [ObservableProperty] [NotifyPropertyChangedFor(nameof(StatusText))] private string? _addedAs;
    public string StatusText => AddedAs is { } k ? $"{k}으로 추가됨" : "라이브러리에 있어요";
}

/// <summary>
/// 라이브러리에 프로그램 추가: 시작 메뉴의 프로그램을 아이콘과 함께 보여 주고 A로 바로 추가한다 (여러 개 연달아 가능).
/// 추가할 분류(앱/게임)는 위의 큰 선택지 또는 LB/RB. 방금 추가한 것은 X로 되돌린다 (여러 번 누르면 차례로).
/// 목록에 없으면 파일(exe·바로가기)에서 고른다 (Y).
/// </summary>
public sealed partial class AppPickerViewModel : ObservableObject, INavPage
{
    private readonly ShellViewModel _shell;
    private List<AppItemViewModel> _all = [];
    /// <summary>되돌리기용: 이 화면에서 추가한 순서 (파일에서 고른 건 목록 항목이 없음).</summary>
    private readonly Stack<(AppItemViewModel? Item, GameEntry Entry)> _added = new();

    [ObservableProperty] private ObservableCollection<AppItemViewModel> _items = [];
    [ObservableProperty] private bool _isLoading = true;
    [ObservableProperty] private string _query = "";
    /// <summary>앱(게임이 아닌 프로그램)으로 넣을지. 시작 메뉴엔 게임 아닌 게 많아서 기본은 앱.</summary>
    [ObservableProperty] [NotifyPropertyChangedFor(nameof(AddAsGame))] private bool _addAsApp = true;
    public bool AddAsGame { get => !AddAsApp; set => AddAsApp = !value; }

    /// <summary>방금 한 일 (추가·되돌리기). 토스트처럼 사라지지 않고 다음 동작까지 남는다.</summary>
    [ObservableProperty] [NotifyPropertyChangedFor(nameof(HasLastAction))] private string? _lastAction;
    public bool HasLastAction => LastAction is not null;

    public AppPickerViewModel(ShellViewModel shell)
    {
        _shell = shell;
        _ = LoadAsync();
    }

    public string Title => "프로그램 추가";

    public IReadOnlyList<Hint> Hints
    {
        get
        {
            var hints = new List<Hint> { new("A", "추가") };
            if (CanUndo) hints.Add(new("X", "되돌리기"));
            hints.Add(new("Y", "파일에서 고르기"));
            hints.Add(new("LT", "찾기"));
            hints.Add(new("LB", "앱으로"));
            hints.Add(new("RB", "게임으로"));
            hints.Add(new("B", "끝"));
            return hints;
        }
    }

    public bool CanUndo => _added.Count > 0;
    public string SessionText => _added.Count > 0 ? $"이번에 {_added.Count}개 추가" : "";

    public string CountText => IsLoading ? "프로그램 목록을 읽는 중…"
        : _all.Count == 0 ? "시작 메뉴에서 프로그램을 찾지 못했어요"
        : string.IsNullOrWhiteSpace(Query) ? $"시작 메뉴 프로그램 {_all.Count}개 · 이미 추가한 건 체크 표시"
        : $"'{Query.Trim()}' 검색 결과 {Items.Count}개";
    public bool IsEmpty => !IsLoading && Items.Count == 0;
    public bool HasQuery => !string.IsNullOrWhiteSpace(Query);

    public string KindNote => AddAsApp
        ? "앱: 실행하면 런처가 숨고 스틱이 마우스가 돼요"
        : "게임: 실행하면 게임별 매핑·성능 모드가 적용돼요";

    partial void OnAddAsAppChanged(bool value) => OnPropertyChanged(nameof(KindNote));

    /// <summary>LT: 뷰가 검색 입력을 연다.</summary>
    public event Action? SearchRequested;

    private async Task LoadAsync()
    {
        var known = _shell.Library.Games
            .SelectMany(g => new[] { g.LaunchTarget, g.IconSource })
            .Where(p => !string.IsNullOrEmpty(p))
            .ToHashSet(StringComparer.OrdinalIgnoreCase);
        // 바로가기 해석·아이콘 추출은 수백 ms — 화면을 먼저 띄우고 뒤에서 채운다 (아이콘은 Freeze돼서 넘겨도 된다)
        _all = await Task.Run(() => StartMenuApps.Scan()
            .Select(a => new AppItemViewModel(a, ArtCache.LoadIcon(a.TargetPath),
                known.Contains(a.LinkPath) || known.Contains(a.TargetPath)))
            .ToList());
        IsLoading = false;
        ApplyFilter(); // 포커스는 뷰가 첫 프로그램으로 옮긴다
    }

    partial void OnQueryChanged(string value) => ApplyFilter();

    private void ApplyFilter()
    {
        var q = Query.Trim();
        Items = new ObservableCollection<AppItemViewModel>(q.Length == 0 ? _all
            : _all.Where(a => a.Name.Contains(q, StringComparison.CurrentCultureIgnoreCase)
                              || (a.Folder?.Contains(q, StringComparison.CurrentCultureIgnoreCase) ?? false)));
        OnPropertyChanged(nameof(CountText));
        OnPropertyChanged(nameof(IsEmpty));
        OnPropertyChanged(nameof(HasQuery));
    }

    [RelayCommand]
    private void ClearQuery() => Query = "";

    [RelayCommand]
    private void Search() => SearchRequested?.Invoke();

    [RelayCommand]
    private void Add(AppItemViewModel item)
    {
        if (item.IsAdded)
        {
            var last = _added.Count > 0 && _added.Peek().Item == item;
            _shell.ShowToast(last ? $"{item.Name}은(는) 방금 추가했어요 — X로 되돌릴 수 있어요" : $"{item.Name}은(는) 이미 라이브러리에 있어요");
            return;
        }
        var kind = AddAsApp ? "앱" : "게임";
        var entry = _shell.Library.AddCustom(item.Name, item.App.LinkPath, item.App.TargetPath, isApp: AddAsApp);
        if (_shell.Settings.Start.AutoPinNew) _shell.Start.Pin(entry);
        item.IsAdded = true;
        item.AddedAs = kind;
        _shell.Home.Reload();
        Pushed(item, entry, $"'{item.Name}'을(를) {kind}으로 추가했어요{(_shell.Settings.Start.AutoPinNew ? " · 시작 화면에도 고정" : "")}");
    }

    private void Pushed(AppItemViewModel? item, GameEntry entry, string text)
    {
        _added.Push((item, entry));
        LastAction = text;
        OnPropertyChanged(nameof(CanUndo));
        OnPropertyChanged(nameof(SessionText));
        _shell.UpdateHints(); // X 되돌리기
    }

    /// <summary>X: 마지막으로 추가한 것을 라이브러리(와 시작 화면)에서 뺀다. 누를 때마다 하나씩.</summary>
    [RelayCommand]
    private void Undo()
    {
        if (_added.Count == 0) return;
        var (item, entry) = _added.Pop();
        if (_shell.Start.IsPinned(entry)) _shell.Start.Unpin(entry);
        _shell.Library.RemoveCustom(entry);
        if (item is not null)
        {
            item.IsAdded = false;
            item.AddedAs = null;
        }
        _shell.Home.Reload();
        LastAction = $"'{entry.Title}' 추가를 되돌렸어요";
        OnPropertyChanged(nameof(CanUndo));
        OnPropertyChanged(nameof(SessionText));
        _shell.UpdateHints();
    }

    /// <summary>목록에 없는 프로그램: 파일 선택 창 (터치·마우스). 고른 분류(앱/게임)로 넣는다.</summary>
    [RelayCommand]
    private void PickFile()
    {
        var kind = AddAsApp ? "앱" : "게임";
        if (AddFromFile(_shell, AddAsApp, toast: false) is { } entry)
            Pushed(null, entry, $"'{entry.Title}'을(를) {kind}으로 추가했어요 (파일에서)");
    }

    /// <summary>파일 선택 창으로 추가 (설정 → 라이브러리에서도 쓴다). 취소하면 null.</summary>
    public static GameEntry? AddFromFile(ShellViewModel shell, bool isApp = false, bool toast = true)
    {
        var dlg = new OpenFileDialog
        {
            Title = "라이브러리에 추가할 프로그램",
            Filter = "프로그램 (*.exe;*.lnk;*.url)|*.exe;*.lnk;*.url",
        };
        if (dlg.ShowDialog() != true) return null;
        var target = dlg.FileName.EndsWith(".lnk", StringComparison.OrdinalIgnoreCase) ? StartMenuApps.Resolve(dlg.FileName) : null;
        var game = shell.Library.AddCustom(Path.GetFileNameWithoutExtension(dlg.FileName), dlg.FileName, target, isApp);
        if (shell.Settings.Start.AutoPinNew) shell.Start.Pin(game);
        shell.Home.Reload();
        if (toast) shell.ShowToast($"{game.Title}을(를) 라이브러리에 추가했어요");
        return game;
    }

    private void SetKind(bool app)
    {
        if (AddAsApp == app) return;
        AddAsApp = app;
        _shell.ShowToast(app ? "이제 앱으로 추가해요" : "이제 게임으로 추가해요");
    }

    public bool Handle(NavAction action)
    {
        switch (action)
        {
            case NavAction.Options: PickFile(); return true;
            case NavAction.Secondary: Undo(); return true;
            case NavAction.TriggerLeft: Search(); return true;
            case NavAction.TabPrev: SetKind(app: true); return true;
            case NavAction.TabNext: SetKind(app: false); return true;
            default: return false;
        }
    }
}
