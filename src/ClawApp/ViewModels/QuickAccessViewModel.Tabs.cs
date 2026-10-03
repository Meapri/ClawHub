using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;

namespace ClawApp.ViewModels;

/// <summary>빠른 설정 왼쪽 탭 하나 (아이콘 + 이름).</summary>
public sealed partial class QuickTab(string key, string label, string icon) : ObservableObject
{
    public string Key { get; } = key;
    public string Label { get; } = label;
    public string Icon { get; } = System.Windows.Application.Current?.TryFindResource("Icon." + icon) as string ?? "";
    [ObservableProperty] private bool _isSelected;
}

/// <summary>
/// SteamOS 빠른 메뉴처럼 왼쪽 세로 탭으로 나눈다: 게임(실행 중일 때만) · 성능 · 화면·소리 · 전원 · 캡처.
/// LB/RB로 탭을 넘긴다. 게임 중에 열면 게임 탭부터, 아니면 지난번 탭.
/// </summary>
public sealed partial class QuickAccessViewModel
{
    private static readonly QuickTab GameTab = new("game", "게임", "Gamepad2");
    private readonly QuickTab[] _fixedTabs =
    [
        new("perf", "성능", "Gauge"),
        new("display", "화면·소리", "Sun"),
        new("net", "연결", "Wifi"),
        new("power", "전원", "BatteryFull"),
        new("tools", "캡처", "Camera"),
    ];

    public ObservableCollection<QuickTab> Tabs { get; } = [];

    [ObservableProperty] private string _selectedTab = "perf";

    public string TabTitle => SelectedTab switch
    {
        "game" => TargetTitle ?? "게임",
        _ => Tabs.FirstOrDefault(t => t.Key == SelectedTab)?.Label ?? "빠른 설정",
    };

    partial void OnSelectedTabChanged(string value)
    {
        foreach (var t in Tabs) t.IsSelected = t.Key == value;
        OnPropertyChanged(nameof(TabTitle));
        TabChanged?.Invoke();
        if (value == "net") _ = _shell.Connections.RefreshAsync();
    }

    /// <summary>탭이 바뀌면 뷰가 맨 위로 스크롤하고 그 탭의 첫 항목에 포커스.</summary>
    public event Action? TabChanged;

    private string _lastFixedTab = "perf";

    /// <summary>패널을 열 때 (Refresh에서): 탭 목록을 맞추고 시작 탭을 고른다.</summary>
    private void RefreshTabs(bool opening)
    {
        var wanted = (TargetTitle is not null ? new[] { GameTab } : []).Concat(_fixedTabs).ToList();
        if (!Tabs.SequenceEqual(wanted))
        {
            Tabs.Clear();
            foreach (var t in wanted) Tabs.Add(t);
        }
        if (opening) SelectedTab = TargetTitle is not null ? "game" : _lastFixedTab;
        else if (Tabs.All(t => t.Key != SelectedTab)) SelectedTab = _lastFixedTab;
        foreach (var t in Tabs) t.IsSelected = t.Key == SelectedTab;
        OnPropertyChanged(nameof(TabTitle));
        if (opening && SelectedTab == "net") _ = _shell.Connections.RefreshAsync();
    }

    [RelayCommand]
    private void SelectTab(QuickTab tab)
    {
        SelectedTab = tab.Key;
        if (tab.Key != "game") _lastFixedTab = tab.Key;
    }

    /// <summary>LB(-1)·RB(+1): 끝에서 처음으로 돌아간다.</summary>
    public void SwitchTab(int step)
    {
        if (Tabs.Count == 0) return;
        var i = Tabs.ToList().FindIndex(t => t.Key == SelectedTab);
        SelectTab(Tabs[((i < 0 ? 0 : i) + step + Tabs.Count) % Tabs.Count]);
    }
}
