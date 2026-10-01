using System.Collections.ObjectModel;
using System.Windows.Threading;
using ClawCore.Controller;
using CommunityToolkit.Mvvm.ComponentModel;

namespace ClawApp.ViewModels;

/// <summary>
/// 키 입력을 화면 이동 대신 직접 받는 페이지 (매핑 테스트). MainWindow가 키보드·마우스 입력을 펌웨어 키 ID로 바꿔 넘긴다.
/// 패드의 B(게임패드 그대로일 때)는 <see cref="INavPage.Handle"/>로 와서 테스트를 끝낸다.
/// </summary>
public interface IKeyCapture
{
    bool IsCapturing { get; }
    void OnCapturedKey(string id, bool down);
}

/// <summary>입력 기록 한 칸.</summary>
public sealed record CapturedKey(string Label, string? Button);

/// <summary>
/// 15초 테스트: 매핑을 컨트롤러에 실제로 적용하고, 나오는 키를 이 화면이 받아서
/// 해당 버튼 줄을 밝히고 아래에 기록한다. 메모장을 따로 열 필요가 없다.
/// </summary>
public sealed partial class MappingEditorViewModel : IKeyCapture
{
    private static readonly TimeSpan TestDuration = TimeSpan.FromSeconds(15);

    [ObservableProperty] private bool _isTesting;
    [ObservableProperty] private int _testSecondsLeft;
    public ObservableCollection<CapturedKey> TestLog { get; } = [];
    public bool HasTestLog => TestLog.Count > 0;

    private CancellationTokenSource? _testCts;
    private DispatcherTimer? _countdown;
    private readonly HashSet<string> _down = [];

    public bool IsCapturing => IsTesting;

    partial void OnIsTestingChanged(bool value) => _shell.UpdateHints();

    private async Task Test()
    {
        if (IsTesting) return;
        if (IsPickerOpen) ClosePicker();
        TestLog.Clear();
        OnPropertyChanged(nameof(HasTestLog));
        _down.Clear();
        _testCts = new CancellationTokenSource();
        IsTesting = true;
        TestSecondsLeft = (int)TestDuration.TotalSeconds;
        _countdown = new DispatcherTimer { Interval = TimeSpan.FromSeconds(1) };
        _countdown.Tick += (_, _) => { if (TestSecondsLeft > 0) TestSecondsLeft--; };
        _countdown.Start();
        try
        {
            await _shell.TestMappingAsync(Profile, TestDuration, _testCts.Token);
        }
        finally
        {
            _countdown.Stop();
            IsTesting = false;
            _down.Clear();
            foreach (var r in AllRows) r.IsLive = false;
            _testCts.Dispose();
            _testCts = null;
            _shell.RequestFocus();
        }
    }

    private void StopTest() => _testCts?.Cancel();

    [CommunityToolkit.Mvvm.Input.RelayCommand]
    private void StartOrStopTest()
    {
        if (IsTesting) StopTest();
        else _ = Test();
    }

    public void OnCapturedKey(string id, bool down)
    {
        if (!IsTesting) return;
        if (down)
        {
            if (!_down.Add(id) && !id.StartsWith("MS_WHEEL", StringComparison.Ordinal)) return; // 키 반복은 한 번만 기록
            // 이 키가 들어간 버튼들 (조합이면 모든 키가 눌렸을 때 그 버튼)
            var owner = AllRows.FirstOrDefault(r => Current(r.Button) is { Count: > 0 } keys && keys.Contains(id) && keys.All(_down.Contains))
                ?? AllRows.FirstOrDefault(r => Current(r.Button).Contains(id));
            TestLog.Insert(0, new CapturedKey(Label(id), owner?.Name));
            while (TestLog.Count > 8) TestLog.RemoveAt(TestLog.Count - 1);
            OnPropertyChanged(nameof(HasTestLog));
        }
        else
        {
            _down.Remove(id);
        }
        foreach (var r in AllRows)
            r.IsLive = Current(r.Button) is { Count: > 0 } keys && keys.Any(_down.Contains);
    }
}
