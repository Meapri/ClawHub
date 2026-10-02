using System.Collections.ObjectModel;
using System.Windows.Threading;
using ClawApp.Input;
using ClawCore.Controller;
using ClawCore.Mapping;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;

namespace ClawApp.ViewModels;

/// <summary>프로필 목록의 한 줄.</summary>
public sealed partial class ProfileRow(MappingProfile profile, string summary, IReadOnlyList<string> games, bool isPreset, bool isActive,
    IReadOnlyList<ProfilesViewModel.Pair> preview) : ObservableObject
{
    public MappingProfile Profile { get; } = profile;
    public string Name => Profile.Name;
    public string Summary { get; } = summary;
    public IReadOnlyList<string> Games { get; } = games;
    public bool InUse => Games.Count > 0;
    /// <summary>"메이플스토리, 던전앤파이터 외 2개" / "아직 쓰는 게임이 없어요".</summary>
    public string GamesText => Games.Count switch
    {
        0 => "아직 쓰는 게임이 없어요",
        <= 3 => string.Join(", ", Games),
        _ => $"{string.Join(", ", Games.Take(3))} 외 {Games.Count - 3}개",
    };
    /// <summary>기본·데스크톱 프로필 (늘 있고 이름 바꾸기·삭제가 없다).</summary>
    public bool IsSystem => Profile.IsSystem;
    public bool IsDesktop => Profile.Id == MappingProfile.DesktopId;
    /// <summary>처음부터 들어 있던 템플릿 (지우면 다시 생기지 않음).</summary>
    public bool IsPreset { get; } = isPreset;
    /// <summary>지금 실행 중인 게임이 쓰는 프로필.</summary>
    public bool IsActive { get; } = isActive;
    public IReadOnlyList<ProfilesViewModel.Pair> Preview { get; } = preview;
}

/// <summary>
/// 매핑 프로필 목록: 내 프로필 / 기본 제공 템플릿으로 나눠 보여 주고, 줄마다 쓰는 게임·주요 버튼 미리보기.
/// 패드: A 편집, X 복제, Y 이름 바꾸기 (포커스한 줄), 삭제는 줄 오른쪽 휴지통(확인 창). 터치는 줄 오른쪽 버튼.
/// </summary>
public sealed partial class ProfilesViewModel : ObservableObject, INavPage
{
    /// <summary>미리보기 한 칸: 버튼 → 키.</summary>
    public sealed record Pair(ClawButton Button, string Keys);

    /// <summary>미리보기에 먼저 보여 줄 버튼 (자주 바꾸는 순).</summary>
    private static readonly ClawButton[] PreviewOrder =
        [ClawButton.A, ClawButton.B, ClawButton.X, ClawButton.Y, ClawButton.LB, ClawButton.RB, ClawButton.LT, ClawButton.RT, ClawButton.M1, ClawButton.M2];

    private readonly ShellViewModel _shell;

    public ProfilesViewModel(ShellViewModel shell)
    {
        _shell = shell;
    }

    /// <summary>맨 위 고정: 기본(모든 게임) · 데스크톱.</summary>
    [ObservableProperty] private ObservableCollection<ProfileRow> _systemRows = [];
    [ObservableProperty] private ObservableCollection<ProfileRow> _userRows = [];
    [ObservableProperty] private ObservableCollection<ProfileRow> _presetRows = [];

    public string Title => "매핑 프로필";

    public IReadOnlyList<Hint> Hints =>
        RenameRow is not null ? [new("A", "키보드"), new("B", "이름 저장")]
        : FocusedRow is null ? [new("A", "선택"), new("B", "뒤로")]
        : [new("A", _focusOnMain ? "편집" : "선택"), new("X", "복제"), new("Y", "이름 바꾸기"), new("B", "뒤로")];

    public bool HasUserRows => UserRows.Count > 0;
    public bool HasPresetRows => PresetRows.Count > 0;
    public string CountText => $"내 프로필 {UserRows.Count}개 · 기본 제공 {PresetRows.Count}개 · 게임마다 다른 버튼 배치를 쓸 수 있어요";

    // ── 포커스한 줄 (뷰가 알려 준다) ──────────────────────────

    private ProfileRow? _focusedRow;
    private bool _focusOnMain;

    /// <summary>패드 포커스가 있는 줄 (줄 본문이든 오른쪽 버튼이든). X/Y가 이 줄에 적용된다.</summary>
    public ProfileRow? FocusedRow => _focusedRow;

    /// <param name="onMain">줄 본문(A = 편집)인지, 오른쪽 동작 버튼인지.</param>
    public void SetFocus(ProfileRow? row, bool onMain)
    {
        if (_focusedRow == row && _focusOnMain == onMain) return;
        var changed = (_focusedRow is null) != (row is null) || _focusOnMain != onMain;
        _focusedRow = row;
        _focusOnMain = onMain;
        if (changed) _shell.UpdateHints();
    }

    /// <summary>목록을 다시 만든 뒤 이 프로필 줄에 포커스를 달라 (복제·삭제·이름 바꾸기 후).</summary>
    public event Action<string?>? FocusProfileRequested;

    /// <summary>이름 바꾸기 입력을 열어 달라.</summary>
    public event Action? RenameRequested;

    public void OnShown() => Reload();

    private void Reload()
    {
        var presetIds = MappingPresets.All.Select(p => p.Id).ToHashSet();
        // 실행 중 게임이 실제로 쓰는 프로필 (기본 프로필을 따르면 기본 프로필), "데스크톱으로" 중이면 데스크톱
        var activeId = _shell.RunningGame is { } running ? _shell.Profiles.ForGame(running.MappingProfileId)?.Id : null;
        var store = _shell.Profiles;
        var rows = store.All.Select(p =>
        {
            // 기본 프로필을 쓰는 게임 = 프로필을 따로 정하지 않은(또는 지워진 프로필을 가리키는) 게임
            var games = _shell.Library.Games
                .Where(g => !g.Hidden && (p.Id == MappingProfile.DefaultId
                    ? g.MappingProfileId != MappingProfile.NoneId && store.Find(g.MappingProfileId) is not { IsSystem: false }
                    : g.MappingProfileId == p.Id))
                .Select(g => g.Title).ToList();
            var preview = PreviewOrder
                .Where(b => p.Bindings.TryGetValue(b, out var l) && l.Count > 0)
                .Take(6)
                .Select(b => new Pair(b, string.Join("+", p.Bindings[b].Select(id => KeyCodes.Find(id)?.Label ?? id))))
                .ToList();
            var keys = p.Bindings.Count(kv => kv.Value.Count > 0) + p.Macros.Count(kv => kv.Value.Count > 0);
            var summary = p.Id switch
            {
                MappingProfile.DefaultId when keys == 0 => "비어 있음 · 프로필을 따로 정하지 않은 게임은 게임패드 그대로",
                MappingProfile.DefaultId => $"버튼 {keys}개 · 프로필을 따로 정하지 않은 모든 게임에 써요",
                MappingProfile.DesktopId => $"버튼 {keys}개 · '데스크톱으로'(스틱 = 마우스)에서 Windows를 쓸 때",
                _ => $"버튼 {keys}개 · {(p.Mode == GamepadMode.Desktop ? "데스크톱 모드" : "XInput 모드")}",
            };
            if (p.Id == MappingProfile.DesktopId) games = []; // 게임이 아니라 Windows 화면용
            return new ProfileRow(p, summary, games, presetIds.Contains(p.Id),
                p.Id == activeId || (p.Id == MappingProfile.DesktopId && _shell.IsInDesktop), preview);
        }).ToList();
        SystemRows = new ObservableCollection<ProfileRow>(rows.Where(r => r.IsSystem));
        UserRows = new ObservableCollection<ProfileRow>(rows.Where(r => !r.IsPreset && !r.IsSystem));
        PresetRows = new ObservableCollection<ProfileRow>(rows.Where(r => r.IsPreset && !r.IsSystem));
        OnPropertyChanged(nameof(HasUserRows));
        OnPropertyChanged(nameof(HasPresetRows));
        OnPropertyChanged(nameof(CountText));
    }

    private ProfileRow? FindRow(string id) => SystemRows.Concat(UserRows).Concat(PresetRows).FirstOrDefault(r => r.Profile.Id == id);

    [RelayCommand] private void Edit(ProfileRow row) => _shell.Navigate(new MappingEditorViewModel(_shell, row.Profile));

    [RelayCommand]
    private void New()
    {
        var profile = new MappingProfile { Name = $"프로필 {_shell.Profiles.All.Count(p => !p.IsSystem) + 1}" };
        _shell.Profiles.Upsert(profile);
        _shell.Navigate(new MappingEditorViewModel(_shell, profile));
    }

    [RelayCommand]
    private void Duplicate(ProfileRow? row)
    {
        if ((row ?? FocusedRow) is not { } r) return;
        var copy = r.Profile.Clone();
        _shell.Profiles.Upsert(copy);
        Reload();
        _shell.ShowToast($"'{copy.Name}'을(를) 만들었어요 — Y로 이름을 바꿀 수 있어요");
        FocusProfileRequested?.Invoke(copy.Id);
    }

    [RelayCommand(AllowConcurrentExecutions = true)]
    private async Task Delete(ProfileRow? row)
    {
        if ((row ?? FocusedRow) is not { } r) return;
        if (r.IsSystem) { _shell.ShowToast("기본·데스크톱 프로필은 지울 수 없어요 — 비우려면 편집에서 버튼을 지우세요"); return; }
        var ok = await _shell.ConfirmAsync($"'{r.Name}' 프로필을 지울까요?",
            r.IsPreset ? "기본 제공 템플릿은 지우면 다시 생기지 않아요."
            : r.InUse ? $"이 프로필을 쓰는 게임 {r.Games.Count}개는 기본 프로필로 실행돼요." : "",
            "삭제", danger: true);
        if (!ok) return;

        // 지운 뒤 포커스: 같은 묶음의 다음 줄 → 이전 줄
        var list = r.IsPreset ? PresetRows : UserRows;
        var i = list.IndexOf(r);
        var neighbor = i + 1 < list.Count ? list[i + 1] : i > 0 ? list[i - 1] : null;

        _shell.Profiles.Delete(r.Profile.Id);
        foreach (var g in _shell.Library.Games.Where(g => g.MappingProfileId == r.Profile.Id).ToList())
            _shell.Library.SetProfile(g, null);
        Reload();
        _shell.ShowToast(r.IsPreset ? $"'{r.Name}' 템플릿을 지웠어요 (다시 생기지 않아요)" : $"'{r.Name}'을(를) 지웠어요");
        FocusProfileRequested?.Invoke(neighbor?.Profile.Id);
    }

    // ── 이름 바꾸기: 목록 위 입력줄 (A/Y/터치로 열고 Enter·B로 저장) ──────

    [ObservableProperty] private ProfileRow? _renameRow;
    [ObservableProperty] private string _renameText = "";

    partial void OnRenameRowChanged(ProfileRow? value) => _shell.UpdateHints();

    [RelayCommand]
    private void BeginRename(ProfileRow? row)
    {
        if ((row ?? FocusedRow) is not { } r) return;
        if (r.IsSystem) { _shell.ShowToast("기본·데스크톱 프로필은 이름을 바꿀 수 없어요"); return; }
        RenameText = r.Name;
        RenameRow = r;
        RenameRequested?.Invoke();
    }

    /// <summary>입력이 끝났을 때 (뷰가 InlineEdit.Committed에서 부른다). 빈 이름은 무시.</summary>
    public void EndRename()
    {
        if (RenameRow is not { } r) return;
        RenameRow = null;
        var name = RenameText.Trim();
        if (name.Length > 0 && name != r.Profile.Name)
        {
            r.Profile.Name = name;
            _shell.Profiles.Upsert(r.Profile);
            _ = _shell.PushAppStateAsync(); // Game Bar 위젯의 프로필 이름도 바로
            _shell.ShowToast($"이름을 '{name}'(으)로 바꿨어요");
        }
        Reload();
        FocusProfileRequested?.Invoke(r.Profile.Id);
    }

    public bool Handle(NavAction action)
    {
        if (FocusedRow is null) return false;
        switch (action)
        {
            case NavAction.Secondary: Duplicate(null); return true;
            case NavAction.Options: BeginRename(null); return true;
            default: return false;
        }
    }
}
