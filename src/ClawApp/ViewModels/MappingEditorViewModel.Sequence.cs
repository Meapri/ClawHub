using CommunityToolkit.Mvvm.Input;

namespace ClawApp.ViewModels;

/// <summary>
/// 여러 키를 넣은 칸의 누르는 방식: 동시에(기본) 또는 차례로(키마다 간격만큼 누르고 있다가 다음 키).
/// 스킬 딜레이가 긴 게임(메이플 등)에서 동시에 누르면 첫 스킬 동작 중에 들어온 뒤 키가 씹힌다.
/// '누를 때' 칸은 프로필(<see cref="ClawCore.Mapping.MappingProfile.Sequence"/>), 길게·두 번·레이어 칸은 고급 동작
/// (<see cref="ClawCore.Mapping.AdvancedButton.Sequence"/>)에 저장한다. 원형 메뉴 칸은 해당 없음.
/// </summary>
public sealed partial class MappingEditorViewModel
{
    private const int DefaultSequenceGapMs = 700;

    private static readonly string[] SequenceSlots = ["press", "long", "double", "layer"];

    /// <summary>지금 고르는 칸 (고급 입력이 꺼져 있으면 늘 '누를 때').</summary>
    private string SequenceSlot => AdvancedEnabled ? EditingSlot : "press";

    /// <summary>지금 칸에 키가 둘 이상일 때만 고를 수 있다.</summary>
    public bool CanSequence => EditingRow is { } r && SequenceSlots.Contains(SequenceSlot) && SlotKeys(r.Button).Count > 1;

    private int StoredGap(ClawCore.Controller.ClawButton b) => SequenceSlot == "press"
        ? (Profile.Sequence.TryGetValue(b, out var g) ? g : 0)
        : Adv(b)?.SequenceGap(SequenceSlot) ?? 0;

    private void StoreGap(ClawCore.Controller.ClawButton b, int ms)
    {
        if (SequenceSlot == "press")
        {
            if (ms > 0) Profile.Sequence[b] = ms;
            else Profile.Sequence.Remove(b);
        }
        else if (ms > 0) AdvFor(b).Sequence[SequenceSlot] = ms;
        else Adv(b)?.Sequence.Remove(SequenceSlot);
    }

    public bool SequenceOn => EditingRow is { } r && StoredGap(r.Button) > 0;
    public bool SequenceOff => !SequenceOn;

    public int SequenceGapMs
    {
        get => EditingRow is { } r && StoredGap(r.Button) is > 0 and var g ? g : DefaultSequenceGapMs;
        set
        {
            if (EditingRow is not { } r || !SequenceOn) return;
            var ms = Math.Clamp((int)Math.Round(value / 50.0) * 50, 100, 3000);
            if (StoredGap(r.Button) == ms) return;
            RememberOnce("sequence-gap");
            StoreGap(r.Button, ms);
            Save();
            OnPropertyChanged();
            OnPropertyChanged(nameof(SequenceGapText));
        }
    }

    public string SequenceGapText => $"{SequenceGapMs / 1000.0:0.00}초";

    /// <summary>"chord" = 동시에, "sequence" = 차례로.</summary>
    [RelayCommand]
    private void SetSequence(string mode)
    {
        if (EditingRow is not { } r) return;
        var on = mode == "sequence";
        if (on == SequenceOn) return;
        Remember();
        StoreGap(r.Button, on ? DefaultSequenceGapMs : 0);
        Save();
        NotifySequence();
        if (on) _shell.ShowToast("키를 하나씩 눌러요 — 간격은 앞 스킬 딜레이보다 조금 길게 맞춰 주세요");
    }

    private void NotifySequence()
    {
        OnPropertyChanged(nameof(CanSequence));
        OnPropertyChanged(nameof(SequenceOn));
        OnPropertyChanged(nameof(SequenceOff));
        OnPropertyChanged(nameof(SequenceGapMs));
        OnPropertyChanged(nameof(SequenceGapText));
    }
}
