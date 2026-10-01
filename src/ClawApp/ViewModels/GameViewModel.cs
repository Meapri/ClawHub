using ClawApp.Input;
using ClawCore.Mapping;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;

namespace ClawApp.ViewModels;

public sealed partial class GameViewModel(ShellViewModel shell, GameTileViewModel tile) : ObservableObject, INavPage
{
    public GameTileViewModel Tile { get; } = tile;

    public string Title => Tile.Game.Title;
    public IReadOnlyList<Hint> Hints => [new("A", "선택"), new("B", "뒤로"), new("Y", "매핑 편집")];

    public MappingProfile? Profile => shell.Profiles.Find(Tile.Game.MappingProfileId);
    public string ProfileText => Profile?.Name ?? "사용 안 함 (게임패드 그대로)";
    public bool IsRunning => shell.RunningGame?.Id == Tile.Game.Id;

    [RelayCommand]
    private Task Play()
    {
        if (!IsRunning) return shell.LaunchAsync(Tile.Game);
        shell.ReturnToBackground(); // 이미 실행 중이면 게임으로 돌아가기
        return Task.CompletedTask;
    }

    /// <summary>A를 누를 때마다 다음 프로필로 (마지막 다음은 "사용 안 함").</summary>
    [RelayCommand]
    private void CycleProfile()
    {
        var all = shell.Profiles.All;
        var i = all.ToList().FindIndex(p => p.Id == Tile.Game.MappingProfileId);
        var next = i + 1 < all.Count ? all[i + 1].Id : null;
        shell.Library.SetProfile(Tile.Game, next);
        OnPropertyChanged(nameof(Profile));
        OnPropertyChanged(nameof(ProfileText));
    }

    [RelayCommand]
    private void EditMapping()
    {
        var profile = Profile;
        if (profile is null)
        {
            // 프로필이 없으면 이 게임 전용으로 새로 만든다
            profile = new MappingProfile { Name = Tile.Title };
            shell.Profiles.Upsert(profile);
            shell.Library.SetProfile(Tile.Game, profile.Id);
        }
        shell.Navigate(new MappingEditorViewModel(shell, profile));
    }

    public string PerformanceText => Tile.Game.PerformanceMode switch
    {
        ClawCore.Ec.PerformanceMode.Battery => "절전",
        ClawCore.Ec.PerformanceMode.Balanced => "균형",
        ClawCore.Ec.PerformanceMode.Performance => "성능",
        _ => "기본값 따름",
    };

    /// <summary>기본값 따름 → 절전 → 균형 → 성능 → 기본값 따름.</summary>
    [RelayCommand]
    private async Task CyclePerformance()
    {
        ClawCore.Ec.PerformanceMode? next = Tile.Game.PerformanceMode switch
        {
            null => ClawCore.Ec.PerformanceMode.Battery,
            ClawCore.Ec.PerformanceMode.Battery => ClawCore.Ec.PerformanceMode.Balanced,
            ClawCore.Ec.PerformanceMode.Balanced => ClawCore.Ec.PerformanceMode.Performance,
            _ => null,
        };
        shell.Library.SetPerformance(Tile.Game, next);
        OnPropertyChanged(nameof(PerformanceText));
        if (IsRunning) await shell.SyncControllerAsync(); // 실행 중이면 바로 반영
    }

    /// <summary>TextBox에서 벗어날 때 저장된다.</summary>
    public string GameTitle
    {
        get => Tile.Game.Title;
        set
        {
            if (string.IsNullOrWhiteSpace(value) || value == Tile.Game.Title) return;
            shell.Library.Rename(Tile.Game, value.Trim());
            OnPropertyChanged();
            OnPropertyChanged(nameof(Title));
            shell.OnPropertyChangedPageTitle();
        }
    }

    [RelayCommand]
    private void Hide()
    {
        shell.Library.SetHidden(Tile.Game, true);
        shell.ShowToast($"{Tile.Title}을(를) 라이브러리에서 숨겼습니다 (설정 → 숨긴 게임 복원)");
        shell.GoBack();
    }

    public void OnShown()
    {
        OnPropertyChanged(nameof(Profile));
        OnPropertyChanged(nameof(ProfileText));
        OnPropertyChanged(nameof(IsRunning));
    }

    public bool Handle(NavAction action)
    {
        if (action != NavAction.Options) return false;
        EditMapping();
        return true;
    }
}
