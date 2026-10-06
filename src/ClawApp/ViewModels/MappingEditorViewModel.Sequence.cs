using CommunityToolkit.Mvvm.Input;

namespace ClawApp.ViewModels;

/// <summary>
/// 여러 키를 넣은 버튼의 누르는 방식: 동시에(펌웨어 — 기본) 또는 차례로(소프트웨어 — 키마다 간격만큼 누르고 있다가 다음 키).
/// 스킬 딜레이가 긴 게임(메이플 등)에서 동시에 누르면 첫 스킬 동작 중에 들어온 뒤 키가 씹힌다.
/// </summary>
public sealed partial class MappingEditorViewModel
{
    private const int DefaultSequenceGapMs = 700;

    /// <summary>지금 고르는 칸이 '누를 때'이고 키가 둘 이상일 때만 고를 수 있다.</summary>
    public bool CanSequence => EditingRow is { } r && EditingSlot == "press" && Current(r.Button).Count > 1;

    public bool SequenceOn => EditingRow is { } r && Profile.Sequence.TryGetValue(r.Button, out var gap) && gap > 0;
    public bool SequenceOff => !SequenceOn;

    public int SequenceGapMs
    {
        get => EditingRow is { } r && Profile.Sequence.TryGetValue(r.Button, out var gap) && gap > 0 ? gap : DefaultSequenceGapMs;
        set
        {
            if (EditingRow is not { } r || !SequenceOn) return;
            var ms = Math.Clamp((int)Math.Round(value / 50.0) * 50, 100, 3000);
            if (Profile.Sequence[r.Button] == ms) return;
            RememberOnce("sequence-gap");
            Profile.Sequence[r.Button] = ms;
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
        if (on) Profile.Sequence[r.Button] = DefaultSequenceGapMs;
        else Profile.Sequence.Remove(r.Button);
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
