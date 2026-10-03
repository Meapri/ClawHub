using CommunityToolkit.Mvvm.Input;

namespace ClawApp.ViewModels;

/// <summary>빠른 설정 '성능' 탭의 성능 오버레이 단계 칩.</summary>
public sealed partial class QuickAccessViewModel
{
    private IReadOnlyList<TuningChoice<int>>? _overlayChoices;

    public IReadOnlyList<TuningChoice<int>> OverlayChoices => _overlayChoices ??= CreateOverlayChoices();

    public string OverlayNote => _shell.OverlayLevel switch
    {
        1 => "왼쪽 위에 FPS만 보여요",
        2 => "FPS · CPU · GPU · 전력 · 배터리를 한 줄로",
        3 => "프레임 시간 그래프 · 1% 최저 · 클럭 · 온도 · 메모리까지",
        _ => "게임 화면 왼쪽 위에 FPS와 사용률을 띄워요",
    };

    private IReadOnlyList<TuningChoice<int>> CreateOverlayChoices()
    {
        var list = ShellViewModel.OverlayLevelNames.Select((n, i) => new TuningChoice<int>(n, i)).ToList();
        _shell.OverlayLevelChanged += MarkOverlay;
        MarkOverlay(list);
        return list;
    }

    private void MarkOverlay() => MarkOverlay(OverlayChoices);

    private void MarkOverlay(IEnumerable<TuningChoice<int>> list)
    {
        foreach (var c in list) c.IsSelected = c.Value == _shell.OverlayLevel;
        OnPropertyChanged(nameof(OverlayNote));
    }

    [RelayCommand]
    private void PickOverlay(TuningChoice<int> choice) => _shell.SetOverlayLevel(choice.Value);
}
