using ClawApp.Input;
using ClawCore.Mapping;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;

namespace ClawApp.ViewModels;

public sealed partial class GameViewModel(ShellViewModel shell, GameTileViewModel tile) : ObservableObject, INavPage
{
    public GameTileViewModel Tile { get; } = tile;

    public string Title => Tile.Game.Title;
    public IReadOnlyList<Hint> Hints => Tile.Game.IsApp
        ? [new("A", "선택"), new("B", "뒤로")]
        : [new("A", "선택"), new("B", "뒤로"), new("Y", "매핑 편집")];

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

    public string FpsText => Tile.Game.FpsLimit switch
    {
        null => "기본값 따름",
        0 => "끔",
        var f => $"{f} FPS",
    };

    /// <summary>기본값 따름 → 끔 → 30 → 40 → … → 120 → 기본값 따름.</summary>
    [RelayCommand]
    private async Task CycleFps()
    {
        int?[] order = [null, 0, .. ClawCore.Gpu.IntelGraphics.FpsPresets.Select(f => (int?)f)];
        var i = Array.IndexOf(order, Tile.Game.FpsLimit);
        shell.Library.SetFpsLimit(Tile.Game, order[(i + 1) % order.Length]);
        OnPropertyChanged(nameof(FpsText));
        if (IsRunning) await shell.SyncControllerAsync();
    }

    public bool GpuAvailable => ClawCore.Gpu.IntelGraphics.Available;

    // ── 배터리 전용 값 (없으면 위 값 사용) ─────────────────

    public bool OnBattery => shell.OnBattery;

    public string PerformanceOnBatteryText => Tile.Game.PerformanceModeOnBattery switch
    {
        ClawCore.Ec.PerformanceMode.Battery => "절전",
        ClawCore.Ec.PerformanceMode.Balanced => "균형",
        ClawCore.Ec.PerformanceMode.Performance => "성능",
        _ => "따로 안 씀 (위 값 사용)",
    };

    /// <summary>따로 안 씀 → 절전 → 균형 → 성능 → 따로 안 씀.</summary>
    [RelayCommand]
    private async Task CyclePerformanceOnBattery()
    {
        ClawCore.Ec.PerformanceMode? next = Tile.Game.PerformanceModeOnBattery switch
        {
            null => ClawCore.Ec.PerformanceMode.Battery,
            ClawCore.Ec.PerformanceMode.Battery => ClawCore.Ec.PerformanceMode.Balanced,
            ClawCore.Ec.PerformanceMode.Balanced => ClawCore.Ec.PerformanceMode.Performance,
            _ => null,
        };
        shell.Library.SetPerformanceOnBattery(Tile.Game, next);
        OnPropertyChanged(nameof(PerformanceOnBatteryText));
        if (IsRunning) await shell.SyncControllerAsync();
    }

    public string FpsOnBatteryText => Tile.Game.FpsLimitOnBattery switch
    {
        null => "따로 안 씀 (위 값 사용)",
        0 => "끔",
        var f => $"{f} FPS",
    };

    /// <summary>따로 안 씀 → 끔 → 30 → … → 120 → 따로 안 씀.</summary>
    [RelayCommand]
    private async Task CycleFpsOnBattery()
    {
        int?[] order = [null, 0, .. ClawCore.Gpu.IntelGraphics.FpsPresets.Select(f => (int?)f)];
        var i = Array.IndexOf(order, Tile.Game.FpsLimitOnBattery);
        shell.Library.SetFpsLimitOnBattery(Tile.Game, order[(i + 1) % order.Length]);
        OnPropertyChanged(nameof(FpsOnBatteryText));
        if (IsRunning) await shell.SyncControllerAsync();
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

    /// <summary>"아트 바꾸기" — 커버·배경·로고를 검색/파일에서 직접 고른다.</summary>
    [RelayCommand]
    private void OpenArtPicker() => shell.Navigate(new ArtPickerViewModel(shell, Tile));

    // ── 시작 화면 고정 ────────────────────────────────────
    public bool IsPinned => shell.Start.IsPinned(Tile.Game);
    public string PinText => IsPinned ? "시작 화면에서 빼기" : "시작 화면에 고정";

    [RelayCommand]
    private void TogglePin()
    {
        if (IsPinned) shell.Start.Unpin(Tile.Game); else shell.Start.Pin(Tile.Game);
        OnPropertyChanged(nameof(IsPinned));
        OnPropertyChanged(nameof(PinText));
        shell.ShowToast(IsPinned ? $"{Tile.Title}: 시작 화면 끝에 고정했어요" : $"{Tile.Title}: 시작 화면에서 뺐어요");
    }

    // ── 분류: 게임 / 앱 ─────────────────────────────────────
    public bool IsApp => Tile.Game.IsApp;
    public bool IsGame => !Tile.Game.IsApp;
    public string KindText => IsApp ? "앱 (게임 아님)" : "게임";
    public string KindNote => IsApp
        ? "실행하면 런처가 숨고 스틱이 마우스가 돼요. 매핑·성능 모드는 쓰지 않아요."
        : "실행하면 게임별 매핑·성능 모드가 적용되고, 끝나면 원래대로 돌아와요.";

    [RelayCommand]
    private void ToggleKind()
    {
        shell.Library.SetIsApp(Tile.Game, !Tile.Game.IsApp);
        foreach (var p in new[] { nameof(IsApp), nameof(IsGame), nameof(KindText), nameof(KindNote) }) OnPropertyChanged(p);
        shell.Home.Reload();
        shell.UpdateHints(); // 앱이면 "매핑 편집" 힌트가 빠진다
        shell.ShowToast($"{Tile.Title}: {(IsApp ? "앱" : "게임")}으로 바꿨어요");
    }

    [RelayCommand]
    private void Hide()
    {
        shell.Library.SetHidden(Tile.Game, true);
        shell.ShowToast($"{Tile.Title}을(를) 라이브러리에서 숨겼어요 (설정 → 숨긴 게임 복원)");
        shell.GoBack();
    }

    public void OnShown()
    {
        OnPropertyChanged(nameof(Profile));
        OnPropertyChanged(nameof(ProfileText));
        OnPropertyChanged(nameof(IsRunning));
        OnPropertyChanged(nameof(IsPinned));
        OnPropertyChanged(nameof(PinText));
    }

    public bool Handle(NavAction action)
    {
        if (action != NavAction.Options || IsApp) return false;
        EditMapping();
        return true;
    }
}
