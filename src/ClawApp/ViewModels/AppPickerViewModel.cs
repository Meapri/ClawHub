using System.Collections.ObjectModel;
using System.IO;
using System.Windows.Media;
using ClawApp.Input;
using ClawApp.Services;
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
    [ObservableProperty] private bool _isAdded = added;
}

/// <summary>
/// 라이브러리에 프로그램 추가: 시작 메뉴의 프로그램을 아이콘과 함께 보여 주고 A로 바로 추가한다 (여러 개 연달아 가능).
/// 목록에 없으면 파일(exe·바로가기)에서 고른다.
/// </summary>
public sealed partial class AppPickerViewModel : ObservableObject, INavPage
{
    private readonly ShellViewModel _shell;
    private List<AppItemViewModel> _all = [];

    [ObservableProperty] private ObservableCollection<AppItemViewModel> _items = [];
    [ObservableProperty] private bool _isLoading = true;
    [ObservableProperty] private string _query = "";

    public AppPickerViewModel(ShellViewModel shell)
    {
        _shell = shell;
        _ = LoadAsync();
    }

    public string Title => "프로그램 추가";
    public IReadOnlyList<Hint> Hints => [new("A", "추가"), new("Y", "파일에서 고르기"), new("B", "끝")];

    public string CountText => IsLoading ? "프로그램 목록을 읽는 중…"
        : _all.Count == 0 ? "시작 메뉴에서 프로그램을 찾지 못했어요"
        : string.IsNullOrWhiteSpace(Query) ? $"프로그램 {_all.Count}개 · 이미 추가한 건 표시돼요"
        : $"'{Query.Trim()}' 검색 결과 {Items.Count}개";
    public bool IsEmpty => !IsLoading && Items.Count == 0;

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
    }

    [RelayCommand]
    private void Add(AppItemViewModel item)
    {
        if (item.IsAdded)
        {
            _shell.ShowToast($"{item.Name}은(는) 이미 라이브러리에 있어요");
            return;
        }
        _shell.Library.AddCustom(item.Name, item.App.LinkPath, item.App.TargetPath);
        item.IsAdded = true;
        _shell.Home.Reload();
        _shell.ShowToast($"{item.Name}을(를) 라이브러리에 추가했어요");
    }

    /// <summary>목록에 없는 프로그램: 파일 선택 창 (터치·마우스).</summary>
    [RelayCommand]
    private void PickFile() => AddFromFile(_shell);

    public static void AddFromFile(ShellViewModel shell)
    {
        var dlg = new OpenFileDialog
        {
            Title = "라이브러리에 추가할 프로그램",
            Filter = "프로그램 (*.exe;*.lnk;*.url)|*.exe;*.lnk;*.url",
        };
        if (dlg.ShowDialog() != true) return;
        var target = dlg.FileName.EndsWith(".lnk", StringComparison.OrdinalIgnoreCase) ? StartMenuApps.Resolve(dlg.FileName) : null;
        var game = shell.Library.AddCustom(Path.GetFileNameWithoutExtension(dlg.FileName), dlg.FileName, target);
        shell.Home.Reload();
        shell.ShowToast($"{game.Title}을(를) 라이브러리에 추가했어요");
    }

    public bool Handle(NavAction action)
    {
        if (action != NavAction.Options) return false;
        PickFile();
        return true;
    }
}
