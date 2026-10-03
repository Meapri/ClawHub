using System.ComponentModel;
using ClawApp.Input;
using ClawCore.Ec;
using ClawCore.Mapping;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;

namespace ClawApp.ViewModels;

/// <summary>
/// 게임 상세. 위: 플레이(게임으로 돌아가기) + 지금 적용되는 값 요약. 아래: 컨트롤러 매핑 · 성능·FPS(전원 연결/배터리) · 관리.
/// 패드: A 선택, X 시작 화면 고정/해제, Y 매핑 편집(게임만), B 뒤로.
/// </summary>
public sealed partial class GameViewModel(ShellViewModel shell, GameTileViewModel tile) : ObservableObject, INavPage
{
    public GameTileViewModel Tile { get; } = tile;

    public string Title => Tile.Game.Title;

    public IReadOnlyList<Hint> Hints
    {
        get
        {
            var hints = new List<Hint> { new("A", "선택"), new("X", IsPinned ? "고정 해제" : "시작 화면에 고정") };
            if (!IsApp) hints.Add(new("Y", "매핑 편집"));
            hints.Add(new("B", "뒤로"));
            return hints;
        }
    }

    /// <summary>실제로 적용되는 프로필 (게임 전용 → 기본 프로필).</summary>
    public MappingProfile? Profile => shell.Profiles.ForGame(Tile.Game.MappingProfileId);
    public string ProfileText => shell.Profiles.LabelForGame(Tile.Game.MappingProfileId);
    public bool IsRunning => shell.RunningGame?.Id == Tile.Game.Id;

    [RelayCommand]
    private Task Play()
    {
        if (!IsRunning) return shell.LaunchAsync(Tile.Game);
        shell.ReturnToBackground(); // 이미 실행 중이면 게임으로 돌아가기
        return Task.CompletedTask;
    }

    /// <summary>A를 누를 때마다 다음으로: 기본 프로필 따름 → 프로필들 → 사용 안 함 → 처음으로.</summary>
    [RelayCommand]
    private void CycleProfile()
    {
        shell.Library.SetProfile(Tile.Game, shell.Profiles.NextForGame(Tile.Game.MappingProfileId));
        OnPropertyChanged(nameof(Profile));
        OnPropertyChanged(nameof(ProfileText));
        RefreshApplied();
    }

    [RelayCommand]
    private void EditMapping()
    {
        // 기본 프로필을 따르거나 "사용 안 함"이면 이 게임 전용으로 새로 만든다 (기본 프로필의 배치에서 시작)
        shell.Navigate(new MappingEditorViewModel(shell, shell.ProfileForEditing(Tile.Game)));
    }

    // ── 추천 배치 (Steam 스토어 공개 정보 · ClawHub 내장 프리셋 — 키·계정 없음) ──────────

    [ObservableProperty] private PresetAdvice? _advice;
    [ObservableProperty] private bool _isAdvising;

    public bool HasAdvice => Advice is not null;
    public string ApplyText => Advice?.PresetId is null ? "게임패드 그대로 쓰기" : $"'{PresetAdvisor.Label(Advice.PresetId)}' 적용";
    public string? AlternativeText => Advice?.Alternative is { } alt ? $"대신 '{PresetAdvisor.Label(alt)}'" : null;
    public string FindPresetText => IsAdvising ? "찾는 중…" : HasAdvice ? "추천 다시 찾기" : "추천 배치 찾기";

    partial void OnAdviceChanged(PresetAdvice? value)
    {
        OnPropertyChanged(nameof(HasAdvice));
        OnPropertyChanged(nameof(ApplyText));
        OnPropertyChanged(nameof(AlternativeText));
        OnPropertyChanged(nameof(FindPresetText));
    }

    partial void OnIsAdvisingChanged(bool value) => OnPropertyChanged(nameof(FindPresetText));

    [RelayCommand]
    private async Task FindPreset()
    {
        if (IsAdvising) return;
        IsAdvising = true;
        try
        {
            Advice = await Task.Run(() => PresetAdvisor.AdviseAsync(Tile.Game));
        }
        catch (Exception ex) when (ex is System.Net.Http.HttpRequestException or TaskCanceledException or System.Text.Json.JsonException)
        {
            shell.ShowToast("추천 정보를 받지 못했어요 — 인터넷 연결을 확인해 주세요");
        }
        finally
        {
            IsAdvising = false;
        }
    }

    /// <summary>
    /// 추천(main) 또는 두 번째 안(alt) 적용. 게임패드 그대로 = 이 게임은 매핑 안 함.
    /// 템플릿은 이 게임 전용 프로필로 복사해서 지정한다 (편집해도 템플릿은 그대로).
    /// </summary>
    [RelayCommand]
    private async Task ApplyAdvice(string which)
    {
        if (Advice is not { } advice) return;
        var game = Tile.Game;
        var presetId = which == "alt" ? advice.Alternative : advice.PresetId;
        if (presetId is null)
        {
            shell.Library.SetProfile(game, MappingProfile.NoneId);
            shell.ShowToast($"{game.Title}: 게임패드 그대로 (버튼을 바꾸지 않아요)");
        }
        else if (MappingPresets.All.FirstOrDefault(p => p.Id == presetId) is { } preset)
        {
            var id = "rec-" + string.Concat(game.Id.Select(c => char.IsLetterOrDigit(c) ? c : '_'));
            if (shell.Profiles.Find(id) is not null
                && !await shell.ConfirmAsync("추천 프로필을 다시 만들까요?", $"전에 만든 '{game.Title}' 추천 프로필을 '{preset.Name}' 배치로 덮어써요.", "덮어쓰기"))
                return;
            var profile = new MappingProfile
            {
                Id = id,
                Name = $"{game.Title} (추천)",
                Mode = preset.Mode,
                Bindings = preset.Bindings.ToDictionary(kv => kv.Key, kv => kv.Value.ToList()),
            };
            shell.Profiles.Upsert(profile);
            shell.Library.SetProfile(game, profile.Id);
            shell.ShowToast($"{game.Title}: '{preset.Name}' 배치를 적용했어요 — Y로 다듬을 수 있어요");
        }
        OnPropertyChanged(nameof(Profile));
        OnPropertyChanged(nameof(ProfileText));
        RefreshApplied();
        if (IsRunning) await shell.SyncControllerAsync();
    }

    private static string ModeName(PerformanceMode m) => m switch
    {
        PerformanceMode.Battery => "절전",
        PerformanceMode.Balanced => "균형",
        PerformanceMode.Performance => "성능",
        _ => "수동",
    };

    private static string FpsName(int fps) => fps == 0 ? "제한 없음" : $"{fps} FPS";

    public string PerformanceText => Tile.Game.PerformanceMode is { } m ? ModeName(m) : "기본값 따름";

    /// <summary>기본값 따름 → 절전 → 균형 → 성능 → 기본값 따름.</summary>
    [RelayCommand]
    private async Task CyclePerformance()
    {
        PerformanceMode? next = Tile.Game.PerformanceMode switch
        {
            null => PerformanceMode.Battery,
            PerformanceMode.Battery => PerformanceMode.Balanced,
            PerformanceMode.Balanced => PerformanceMode.Performance,
            _ => null,
        };
        shell.Library.SetPerformance(Tile.Game, next);
        OnPropertyChanged(nameof(PerformanceText));
        RefreshApplied();
        if (IsRunning) await shell.SyncControllerAsync(); // 실행 중이면 바로 반영
    }

    public string FpsText => Tile.Game.FpsLimit switch
    {
        null => "기본값 따름",
        var f => FpsName(f.Value),
    };

    /// <summary>기본값 따름 → 끔 → 30 → 40 → … → 120 → 기본값 따름.</summary>
    [RelayCommand]
    private async Task CycleFps()
    {
        int?[] order = [null, 0, .. ClawCore.Gpu.IntelGraphics.FpsPresets.Select(f => (int?)f)];
        var i = Array.IndexOf(order, Tile.Game.FpsLimit);
        shell.Library.SetFpsLimit(Tile.Game, order[(i + 1) % order.Length]);
        OnPropertyChanged(nameof(FpsText));
        RefreshApplied();
        if (IsRunning) await shell.SyncControllerAsync();
    }

    public bool GpuAvailable => ClawCore.Gpu.IntelGraphics.Available;

    // ── 배터리 전용 값 (없으면 전원 연결 값 사용) ─────────────

    public bool OnBattery => shell.OnBattery;
    public bool OnAc => !shell.OnBattery;

    public string PerformanceOnBatteryText => Tile.Game.PerformanceModeOnBattery is { } m ? ModeName(m) : "전원 연결과 같게";

    /// <summary>따로 안 씀 → 절전 → 균형 → 성능 → 따로 안 씀.</summary>
    [RelayCommand]
    private async Task CyclePerformanceOnBattery()
    {
        PerformanceMode? next = Tile.Game.PerformanceModeOnBattery switch
        {
            null => PerformanceMode.Battery,
            PerformanceMode.Battery => PerformanceMode.Balanced,
            PerformanceMode.Balanced => PerformanceMode.Performance,
            _ => null,
        };
        shell.Library.SetPerformanceOnBattery(Tile.Game, next);
        OnPropertyChanged(nameof(PerformanceOnBatteryText));
        RefreshApplied();
        if (IsRunning) await shell.SyncControllerAsync();
    }

    public string FpsOnBatteryText => Tile.Game.FpsLimitOnBattery switch
    {
        null => "전원 연결과 같게",
        var f => FpsName(f.Value),
    };

    /// <summary>따로 안 씀 → 끔 → 30 → … → 120 → 따로 안 씀.</summary>
    [RelayCommand]
    private async Task CycleFpsOnBattery()
    {
        int?[] order = [null, 0, .. ClawCore.Gpu.IntelGraphics.FpsPresets.Select(f => (int?)f)];
        var i = Array.IndexOf(order, Tile.Game.FpsLimitOnBattery);
        shell.Library.SetFpsLimitOnBattery(Tile.Game, order[(i + 1) % order.Length]);
        OnPropertyChanged(nameof(FpsOnBatteryText));
        RefreshApplied();
        if (IsRunning) await shell.SyncControllerAsync();
    }

    // ── 지금 적용되는 값 (게임 값 → 기본값, 지금 전원 상태 기준) ─────

    public string PowerNowText => OnBattery ? "지금: 배터리 사용 중" : "지금: 전원 연결됨";

    public string AppliedCaption => (IsRunning ? "지금 적용 중" : "게임을 켜면 적용") + (OnBattery ? " · 배터리 기준" : " · 전원 연결 기준");

    /// <summary>사용자 기본 성능 모드 (서비스가 전원 상태에 맞춰 고른 값).</summary>
    private string DefaultPerformanceName()
    {
        var s = shell.Status;
        var state = (OnBattery ? s?.PerformanceOnBattery ?? s?.PerformanceOnAc : s?.PerformanceOnAc) ?? s?.Performance;
        return state is null ? "기본 성능" : state.Mode == PerformanceMode.Manual ? $"수동 {state.Pl1}W" : ModeName(state.Mode);
    }

    public string AppliedPerformance => Tile.Game.EffectivePerformanceMode(OnBattery) is { } m ? ModeName(m) : DefaultPerformanceName();
    public bool AppliedPerformanceIsDefault => Tile.Game.EffectivePerformanceMode(OnBattery) is null;

    private int? DefaultFps => OnBattery && shell.Settings.FpsLimitOnBattery is { } b ? b : shell.Settings.FpsLimit;

    public string AppliedFps => Tile.Game.EffectiveFpsLimit(OnBattery) is { } f ? FpsName(f)
        : DefaultFps is { } d ? FpsName(d)
        : "드라이버 설정대로";
    public bool AppliedFpsIsDefault => Tile.Game.EffectiveFpsLimit(OnBattery) is null;

    public string AppliedMapping => Profile?.Name ?? "매핑 없음";

    private void RefreshApplied()
    {
        foreach (var p in new[] { nameof(OnBattery), nameof(OnAc), nameof(PowerNowText), nameof(AppliedCaption), nameof(AppliedPerformance),
                     nameof(AppliedPerformanceIsDefault), nameof(AppliedFps), nameof(AppliedFpsIsDefault), nameof(AppliedMapping) })
            OnPropertyChanged(p);
    }

    private void OnShellChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName is nameof(ShellViewModel.OnBattery) or nameof(ShellViewModel.Status)) RefreshApplied();
        else if (e.PropertyName == nameof(ShellViewModel.RunningGame))
        {
            OnPropertyChanged(nameof(IsRunning));
            RefreshApplied();
        }
    }

    // ── 관리 ─────────────────────────────────────────────

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
    public string PinNote => IsPinned ? "지금 시작 화면에 있어요" : "홈 시작 화면 끝에 타일로 붙어요";

    [RelayCommand]
    private void TogglePin()
    {
        if (IsPinned) shell.Start.Unpin(Tile.Game); else shell.Start.Pin(Tile.Game);
        OnPropertyChanged(nameof(IsPinned));
        OnPropertyChanged(nameof(PinText));
        OnPropertyChanged(nameof(PinNote));
        shell.UpdateHints(); // X 힌트: 고정 ↔ 고정 해제
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
        shell.ShowToast($"{Tile.Title}을(를) 라이브러리에서 숨겼어요 (설정 → 라이브러리 → 숨긴 게임 다시 표시)");
        shell.GoBack();
    }

    public void OnShown()
    {
        shell.PropertyChanged -= OnShellChanged;
        shell.PropertyChanged += OnShellChanged;
        foreach (var p in new[] { nameof(Profile), nameof(ProfileText), nameof(IsRunning), nameof(IsPinned), nameof(PinText), nameof(PinNote) })
            OnPropertyChanged(p);
        RefreshApplied();
    }

    public void OnHidden() => shell.PropertyChanged -= OnShellChanged;

    public bool Handle(NavAction action)
    {
        switch (action)
        {
            case NavAction.Secondary:
                TogglePin();
                return true;
            case NavAction.Options when !IsApp:
                EditMapping();
                return true;
            default:
                return false;
        }
    }
}
