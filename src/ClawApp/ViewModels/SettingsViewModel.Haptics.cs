using System.Windows.Threading;
using ClawApp.Input;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;

namespace ClawApp.ViewModels;

/// <summary>
/// 컨트롤러 세부: 메뉴 진동(SteamOS처럼 패드로 메뉴를 움직이면 톡), 입력 테스트(스틱·트리거·버튼이 제대로 읽히는지 그림으로).
/// 입력 테스트 중에는 버튼이 화면 이동으로 쓰이지 않게 패드 입력을 가져간다 — B를 1초 누르면 끝.
/// </summary>
public sealed partial class SettingsViewModel
{
    private IReadOnlyList<TuningChoice<int>>? _hapticChoices;

    public IReadOnlyList<TuningChoice<int>> HapticChoices => _hapticChoices ??= CreateHapticChoices();

    private IReadOnlyList<TuningChoice<int>> CreateHapticChoices()
    {
        var list = new[] { new TuningChoice<int>("끔", 0), new TuningChoice<int>("약하게", 1), new TuningChoice<int>("강하게", 2) };
        foreach (var c in list) c.IsSelected = c.Value == _shell.Settings.MenuHaptics;
        return list;
    }

    [RelayCommand]
    private void PickHaptics(TuningChoice<int> choice)
    {
        _shell.Settings.MenuHaptics = GamepadInput.MenuHaptics = choice.Value;
        _shell.Settings.Save();
        foreach (var c in HapticChoices) c.IsSelected = c == choice;
    }

    // ── 입력 테스트 ─────────────────────────────────────

    [ObservableProperty] private bool _inputTesting;
    [ObservableProperty] private PadSnapshot? _padState;
    [ObservableProperty] private string _inputTestHint = "A로 시작 · 테스트 중에는 B를 1초 누르면 끝나요";

    private DispatcherTimer? _inputTimer;
    private DateTime? _bHeldSince;

    [RelayCommand]
    private void ToggleInputTest()
    {
        if (InputTesting) StopInputTest();
        else StartInputTest();
    }

    private void StartInputTest()
    {
        if (GamepadInput.Current is not { } pad) return;
        pad.SwallowAll = true; // 버튼·스틱을 화면 이동으로 쓰지 않는다
        InputTesting = true;
        InputTestHint = "버튼·스틱·트리거를 움직여 보세요 · B를 1초 누르면 끝";
        _bHeldSince = null;
        _inputTimer ??= new DispatcherTimer(DispatcherPriority.Render) { Interval = TimeSpan.FromMilliseconds(33) };
        _inputTimer.Tick -= OnInputTick;
        _inputTimer.Tick += OnInputTick;
        _inputTimer.Start();
    }

    /// <summary>테스트 끝 (B 길게, 다시 누름, 화면을 떠날 때).</summary>
    public void StopInputTest()
    {
        if (!InputTesting) return;
        _inputTimer?.Stop();
        if (GamepadInput.Current is { } pad) pad.SwallowAll = false;
        InputTesting = false;
        InputTestHint = "A로 시작 · 테스트 중에는 B를 1초 누르면 끝나요";
    }

    private void OnInputTick(object? sender, EventArgs e)
    {
        var s = GamepadInput.Current?.Latest;
        PadState = s;
        if (s is { } p && (p.Buttons & 0x2000) != 0)
        {
            _bHeldSince ??= DateTime.UtcNow;
            if (DateTime.UtcNow - _bHeldSince > TimeSpan.FromSeconds(1)) StopInputTest();
        }
        else _bHeldSince = null;
    }
}
