using System.Collections.ObjectModel;
using ClawApp.Services;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;

namespace ClawApp.ViewModels;

/// <summary>드라이브 사용량 한 줄.</summary>
public sealed record DriveRow(string Name, string Detail, double UsedPercent);

/// <summary>게임 폴더 크기 한 줄.</summary>
public sealed record GameSizeRow(string Title, string Size);

/// <summary>이름·값 한 줄 (시스템 정보).</summary>
public sealed record InfoRow(string Name, string Value);

/// <summary>
/// 설정의 '저장소·시스템'과 '잠금·보안': 드라이브 사용량, 큰 게임 순서, 기기·드라이버·Windows 정보,
/// 다시 로그인 요구 시간, 지금 잠그기. 보일 때 읽고, 게임 크기는 한 번 계산해 둔다 (폴더를 다 훑어 느림).
/// </summary>
public sealed partial class SystemInfoViewModel(ShellViewModel shell) : ObservableObject
{
    public ObservableCollection<DriveRow> Drives { get; } = [];
    public ObservableCollection<GameSizeRow> GameSizes { get; } = [];
    public ObservableCollection<InfoRow> Details { get; } = [];

    [ObservableProperty] private bool _measuringGames;
    public bool HasGameSizes => GameSizes.Count > 0;
    [ObservableProperty] private string _gamesNote = "";

    private bool _gamesMeasured;
    private bool _loading;

    public async Task RefreshStorageAsync()
    {
        if (_loading) return;
        _loading = true;
        try
        {
            var drives = await Task.Run(SystemInfo.Drives);
            Drives.Clear();
            foreach (var d in drives)
            {
                var used = d.Total - d.Free;
                var name = string.IsNullOrWhiteSpace(d.Label) ? d.Name : $"{d.Label} ({d.Name})";
                Drives.Add(new DriveRow(name, $"{Gb(d.Free)} 남음 · 전체 {Gb(d.Total)}", d.Total > 0 ? used * 100.0 / d.Total : 0));
            }
            if (!_gamesMeasured) _ = MeasureGamesAsync();
        }
        finally { _loading = false; }
    }

    private async Task MeasureGamesAsync()
    {
        _gamesMeasured = true;
        MeasuringGames = true;
        GamesNote = "게임 폴더 크기를 재는 중…";
        var games = shell.Library.Visible.Where(g => !g.IsApp && g.InstallDir is { Length: > 3 } dir && Directory.Exists(dir))
            .DistinctBy(g => g.InstallDir, StringComparer.OrdinalIgnoreCase).ToList();
        var sizes = await Task.Run(() => games.Select(g => (g.Title, Size: SystemInfo.FolderSize(g.InstallDir!, CancellationToken.None))).ToList());
        GameSizes.Clear();
        foreach (var (title, size) in sizes.Where(s => s.Size > 0).OrderByDescending(s => s.Size).Take(12))
            GameSizes.Add(new GameSizeRow(title, Gb(size)));
        MeasuringGames = false;
        OnPropertyChanged(nameof(HasGameSizes));
        GamesNote = GameSizes.Count == 0 ? "설치 폴더를 아는 게임이 없어요" : $"큰 순서 · 게임 {sizes.Count}개 중 {GameSizes.Count}개";
    }

    public async Task RefreshDetailsAsync()
    {
        if (Details.Count > 0) return; // 바뀌지 않는 값
        var rows = await Task.Run(SystemInfo.Details);
        foreach (var (name, value) in rows) Details.Add(new InfoRow(name, value));
    }

    private static string Gb(long bytes) => bytes >= 1L << 40 ? $"{bytes / (double)(1L << 40):0.0} TB" : $"{bytes / (double)(1L << 30):0.#} GB";

    [RelayCommand]
    private void OpenStorageSense() => shell.OpenWindowsPageCommand.Execute("storagesense");

    [RelayCommand]
    private void OpenWindowsUpdate() => shell.OpenWindowsPageCommand.Execute("windowsupdate");

    // ── 잠금·보안 ─────────────────────────────────────

    private IReadOnlyList<TuningChoice<int>>? _relockChoices;

    /// <summary>자리를 비웠다 돌아오면 다시 로그인: 깨어날 때마다 · 1분 · 3분 · 5분 · 15분 · 안 함.</summary>
    public IReadOnlyList<TuningChoice<int>> RelockChoices => _relockChoices ??= CreateRelockChoices();

    private static IReadOnlyList<TuningChoice<int>> CreateRelockChoices()
    {
        var list = new List<TuningChoice<int>>
        {
            new("깨어날 때마다", 0), new("1분 뒤", 60), new("3분 뒤", 180), new("5분 뒤", 300), new("15분 뒤", 900), new("요구 안 함", -1),
        };
        var now = SystemInfo.GetRelockDelay();
        foreach (var c in list) c.IsSelected = c.Value == now;
        return list;
    }

    [RelayCommand]
    private void PickRelock(TuningChoice<int> choice)
    {
        try
        {
            SystemInfo.SetRelockDelay(choice.Value);
            foreach (var c in RelockChoices) c.IsSelected = c == choice;
        }
        catch (Exception) { shell.ShowToast("바꾸지 못했어요"); }
    }

    [RelayCommand]
    private void LockNow() => SystemInfo.LockWorkStation();

    [RelayCommand]
    private void OpenSignInOptions() => shell.OpenWindowsPageCommand.Execute("signinoptions");

    [RelayCommand]
    private void OpenNotifications() => shell.OpenWindowsPageCommand.Execute("notifications");
}
