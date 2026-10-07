using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;

namespace ClawApp.ViewModels;

/// <summary>차례로 누르기의 키 한 줄: 몇 번째 키 · 키 이름 · 누르고 있을 시간.</summary>
public sealed partial class SequenceStepViewModel : ObservableObject
{
    private readonly Action<int, int>? _changed;
    private readonly bool _loading;

    public SequenceStepViewModel(int index, string label, int ms, Action<int, int> changed)
    {
        Index = index;
        Label = $"{index + 1}. {label}";
        _loading = true;
        Ms = ms;
        _loading = false;
        _changed = changed;
    }

    public int Index { get; }
    public string Label { get; }
    [ObservableProperty] private int _ms;
    public string Text => $"{Ms / 1000.0:0.00}초";

    partial void OnMsChanged(int value)
    {
        OnPropertyChanged(nameof(Text));
        if (!_loading) _changed?.Invoke(Index, value);
    }
}

/// <summary>
/// 여러 키를 넣은 칸의 누르는 방식: 동시에(기본) 또는 차례로(키마다 정한 시간만큼 누르고 있다가 다음 키).
/// 스킬 딜레이가 긴 게임(메이플 등)에서 동시에 누르면 첫 스킬 동작 중에 들어온 뒤 키가 씹힌다. 딜레이가 긴 스킬만 길게 줄 수 있게
/// 시간은 키마다 따로다. '누를 때' 칸은 프로필(<see cref="ClawCore.Mapping.MappingProfile.Sequence"/>·SequenceSteps),
/// 길게·두 번·레이어 칸은 고급 동작(<see cref="ClawCore.Mapping.AdvancedButton.Sequence"/>·SequenceSteps)에 저장한다.
/// </summary>
public sealed partial class MappingEditorViewModel
{
    private const int DefaultSequenceGapMs = 700;

    private static readonly string[] SequenceSlots = ["press", "long", "double", "layer"];

    /// <summary>지금 고르는 칸 (고급 입력이 꺼져 있으면 늘 '누를 때').</summary>
    private string SequenceSlot => AdvancedEnabled ? EditingSlot : "press";

    /// <summary>지금 칸에 키가 둘 이상일 때만 고를 수 있다.</summary>
    public bool CanSequence => EditingRow is { } r && SequenceSlots.Contains(SequenceSlot) && SlotKeys(r.Button).Count > 1;

    public bool SequenceOn => EditingRow is { } r && StoredGap(r.Button) > 0;
    public bool SequenceOff => !SequenceOn;

    /// <summary>누를 때 칸이 차례로인데 토글·연사도 켜져 있으면 안내 (차례로가 우선).</summary>
    public string? SequenceConflictNote => SequenceOn && SequenceSlot == "press" && (EditingToggle || EditingTurbo)
        ? "차례로가 켜져 있어 이 버튼의 토글·연사는 쓰지 않아요"
        : null;

    /// <summary>버튼을 누르고 있는 동안의 동작 (트리거는 기본 반복).</summary>
    public IReadOnlyList<TuningChoice<ClawCore.Mapping.SequenceMode>> SequenceModeChoices { get; } =
    [
        new("한 번 끝까지", ClawCore.Mapping.SequenceMode.Once),
        new("누르는 동안 반복", ClawCore.Mapping.SequenceMode.Repeat),
        new("누르는 동안만", ClawCore.Mapping.SequenceMode.WhileHeld),
    ];

    private ClawCore.Mapping.SequenceMode StoredMode(ClawCore.Controller.ClawButton b) => SequenceSlot == "press"
        ? Profile.SequenceModeOf(b)
        : Adv(b)?.SequenceModeOf(SequenceSlot, b) ?? ClawCore.Mapping.MappingProfile.DefaultSequenceMode(b);

    [RelayCommand]
    private void PickSequenceMode(TuningChoice<ClawCore.Mapping.SequenceMode> choice)
    {
        if (EditingRow is not { } r || !SequenceOn || StoredMode(r.Button) == choice.Value) return;
        Remember();
        if (SequenceSlot == "press") Profile.SequenceModes[r.Button] = choice.Value;
        else AdvFor(r.Button).SequenceModes[SequenceSlot] = choice.Value;
        Save();
        foreach (var c in SequenceModeChoices) c.IsSelected = c == choice;
    }

    /// <summary>차례로일 때 키마다 한 줄.</summary>
    public ObservableCollection<SequenceStepViewModel> SequenceSteps { get; } = [];

    private int StoredGap(ClawCore.Controller.ClawButton b) => SequenceSlot == "press"
        ? (Profile.Sequence.TryGetValue(b, out var g) ? g : 0)
        : Adv(b)?.SequenceGap(SequenceSlot) ?? 0;

    private List<int>? StoredSteps(ClawCore.Controller.ClawButton b) => SequenceSlot == "press"
        ? Profile.SequenceSteps.GetValueOrDefault(b)
        : Adv(b)?.SequenceSteps.GetValueOrDefault(SequenceSlot);

    /// <summary>켜기(기본 간격) · 끄기(간격과 키별 시간 지우기).</summary>
    private void StoreOn(ClawCore.Controller.ClawButton b, bool on)
    {
        if (SequenceSlot == "press")
        {
            if (on) Profile.Sequence[b] = DefaultSequenceGapMs;
            else { Profile.Sequence.Remove(b); Profile.SequenceSteps.Remove(b); Profile.SequenceModes.Remove(b); }
        }
        else if (on) AdvFor(b).Sequence[SequenceSlot] = DefaultSequenceGapMs;
        else if (Adv(b) is { } a) { a.Sequence.Remove(SequenceSlot); a.SequenceSteps.Remove(SequenceSlot); a.SequenceModes.Remove(SequenceSlot); }
    }

    /// <summary>i번째 키의 시간을 바꾼다 (목록이 짧으면 지금 값으로 채운다).</summary>
    private void SetStepMs(int index, int value)
    {
        if (EditingRow is not { } r || !SequenceOn) return;
        var ms = Math.Clamp((int)Math.Round(value / 50.0) * 50, 100, 3000);
        var gaps = ClawCore.Mapping.MappingProfile.StepGaps(SlotKeys(r.Button).Count, StoredGap(r.Button), StoredSteps(r.Button));
        if (index >= gaps.Length || gaps[index] == ms) return;
        RememberOnce("sequence-step");
        gaps[index] = ms;
        if (SequenceSlot == "press") Profile.SequenceSteps[r.Button] = [.. gaps];
        else AdvFor(r.Button).SequenceSteps[SequenceSlot] = [.. gaps];
        Save();
    }

    /// <summary>"chord" = 동시에, "sequence" = 차례로.</summary>
    [RelayCommand]
    private void SetSequence(string mode)
    {
        if (EditingRow is not { } r) return;
        var on = mode == "sequence";
        if (on == SequenceOn) return;
        Remember();
        StoreOn(r.Button, on);
        Save();
        NotifySequence();
        if (on) _shell.ShowToast("키를 하나씩 눌러요 — 딜레이가 긴 스킬은 그 키의 시간만 길게 맞춰 주세요");
    }

    private void NotifySequence()
    {
        OnPropertyChanged(nameof(CanSequence));
        OnPropertyChanged(nameof(SequenceOn));
        OnPropertyChanged(nameof(SequenceOff));
        OnPropertyChanged(nameof(SequenceConflictNote));
        SequenceSteps.Clear();
        if (EditingRow is not { } r || !CanSequence || !SequenceOn) return;
        var mode = StoredMode(r.Button);
        foreach (var c in SequenceModeChoices) c.IsSelected = c.Value == mode;
        var keys = SlotKeys(r.Button);
        var gaps = ClawCore.Mapping.MappingProfile.StepGaps(keys.Count, StoredGap(r.Button), StoredSteps(r.Button));
        for (var i = 0; i < keys.Count; i++) SequenceSteps.Add(new SequenceStepViewModel(i, Label(keys[i]), gaps[i], SetStepMs));
    }
}
