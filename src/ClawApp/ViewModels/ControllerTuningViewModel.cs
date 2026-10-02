using ClawCore.Controller;
using ClawCore.Mapping;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;

namespace ClawApp.ViewModels;

/// <summary>고를 수 있는 값 하나 (칩).</summary>
public sealed partial class TuningChoice<T>(string label, T value) : ObservableObject
{
    public string Label { get; } = label;
    public T Value { get; } = value;
    [ObservableProperty] private bool _isSelected;
}

/// <summary>
/// 매핑 프로필의 "스틱·트리거·자이로" 패널 — 펌웨어가 처리하는 조정 (<see cref="ControllerTuning"/>).
/// 값을 바꾸면 바로 프로필에 저장되고(되돌리기 가능), 게임으로 돌아가면 적용된다.
/// 데드존은 이 프로필에서 손대지 않았으면 기기·전역 값을 보여 준다.
/// </summary>
public sealed partial class ControllerTuningViewModel : ObservableObject
{
    private readonly MappingProfile _profile;
    private readonly Action<string> _beforeChange;
    private readonly Action _afterChange;
    private readonly ControllerSettings? _global;
    private bool _loading;

    /// <param name="beforeChange">바꾸기 직전 (되돌리기용 기억, 같은 슬라이더를 끄는 동안은 한 번만).</param>
    public ControllerTuningViewModel(MappingProfile profile, ControllerSettings? global, Action<string> beforeChange, Action afterChange)
    {
        _profile = profile;
        _global = global;
        _beforeChange = beforeChange;
        _afterChange = afterChange;
        Reload();
    }

    private ControllerTuning T => _profile.Tuning ??= new ControllerTuning();

    // ── 자이로 ──────────────────────────────────────────────

    public IReadOnlyList<TuningChoice<GyroOutput>> GyroOutputs { get; } =
    [
        new("끔", GyroOutput.Off), new("오른쪽 스틱", GyroOutput.RightStick), new("왼쪽 스틱", GyroOutput.LeftStick), new("마우스", GyroOutput.Mouse),
    ];

    /// <summary>켜는 버튼: 늘 켜짐 또는 버튼 하나 (조준할 때 LT를 누르는 동안 등).</summary>
    public IReadOnlyList<TuningChoice<ClawButton?>> GyroButtons { get; } =
    [
        new("늘 켜짐", null), new("LT", ClawButton.LT), new("RT", ClawButton.RT), new("LB", ClawButton.LB), new("RB", ClawButton.RB),
        new("왼쪽 스틱 누름", ClawButton.L3), new("오른쪽 스틱 누름", ClawButton.R3), new("M1", ClawButton.M1), new("M2", ClawButton.M2),
    ];

    public IReadOnlyList<TuningChoice<GyroActivation>> GyroActivations { get; } =
    [
        new("누르는 동안 켜짐", GyroActivation.HoldToEnable), new("누를 때마다 켜고 끄기", GyroActivation.Toggle), new("누르는 동안 꺼짐", GyroActivation.HoldToDisable),
    ];

    public bool GyroOn => _profile.Tuning?.Gyro is { Output: not GyroOutput.Off };
    public bool GyroHasButton => GyroOn && _profile.Tuning?.Gyro?.Button is not null;

    [ObservableProperty] private int _gyroSensitivity = 20;
    [ObservableProperty] private int _gyroDeadzone = 10;

    [RelayCommand]
    private void PickGyroOutput(TuningChoice<GyroOutput> c) => Change("gyro-output", () =>
    {
        if (c.Value == GyroOutput.Off) T.Gyro = null;
        else (T.Gyro ??= new GyroTuning()).Output = c.Value;
    });

    [RelayCommand]
    private void PickGyroButton(TuningChoice<ClawButton?> c) => Change("gyro-button", () => { if (T.Gyro is { } g) g.Button = c.Value; });

    [RelayCommand]
    private void PickGyroActivation(TuningChoice<GyroActivation> c) => Change("gyro-activation", () => { if (T.Gyro is { } g) g.Activation = c.Value; });

    partial void OnGyroSensitivityChanged(int value) => ChangeSlider("gyro-sens", () => { if (T.Gyro is { } g) g.Sensitivity = value; });
    partial void OnGyroDeadzoneChanged(int value) => ChangeSlider("gyro-dz", () => { if (T.Gyro is { } g) g.Deadzone = value; });

    // ── 스틱 ────────────────────────────────────────────────

    [ObservableProperty] private int _leftInner;
    [ObservableProperty] private int _leftOuter;
    [ObservableProperty] private int _rightInner;
    [ObservableProperty] private int _rightOuter;
    [ObservableProperty] private bool _swapSticks;

    partial void OnLeftInnerChanged(int value) => ChangeSlider("ls-in", () => (T.LeftStick ??= new()).InnerDeadzone = value);
    partial void OnLeftOuterChanged(int value) => ChangeSlider("ls-out", () => (T.LeftStick ??= new()).OuterDeadzone = value);
    partial void OnRightInnerChanged(int value) => ChangeSlider("rs-in", () => (T.RightStick ??= new()).InnerDeadzone = value);
    partial void OnRightOuterChanged(int value) => ChangeSlider("rs-out", () => (T.RightStick ??= new()).OuterDeadzone = value);
    partial void OnSwapSticksChanged(bool value) => Change("swap", () => T.SwapSticks = value);

    // ── 트리거 ──────────────────────────────────────────────

    [ObservableProperty] private int _ltInner;
    [ObservableProperty] private int _ltOuter;
    [ObservableProperty] private int _rtInner;
    [ObservableProperty] private int _rtOuter;

    partial void OnLtInnerChanged(int value) => ChangeSlider("lt-in", () => (T.LeftTrigger ??= new()).InnerDeadzone = value);
    partial void OnLtOuterChanged(int value) => ChangeSlider("lt-out", () => (T.LeftTrigger ??= new()).OuterDeadzone = value);
    partial void OnRtInnerChanged(int value) => ChangeSlider("rt-in", () => (T.RightTrigger ??= new()).InnerDeadzone = value);
    partial void OnRtOuterChanged(int value) => ChangeSlider("rt-out", () => (T.RightTrigger ??= new()).OuterDeadzone = value);

    /// <summary>스틱·트리거를 이 프로필에서 손대기 전으로 (전역 설정·기기 기본값).</summary>
    [RelayCommand]
    private void ResetSticks() => Change("reset", () =>
    {
        T.LeftStick = T.RightStick = null;
        T.LeftTrigger = T.RightTrigger = null;
        T.SwapSticks = false;
    });

    // ── 요약 (프로필 줄의 버튼) ─────────────────────────────

    public string Summary
    {
        get
        {
            var parts = new List<string>();
            if (_profile.Tuning?.Gyro is { Output: not GyroOutput.Off } g)
            {
                var to = g.Output switch { GyroOutput.Mouse => "마우스", GyroOutput.LeftStick => "왼쪽 스틱", _ => "오른쪽 스틱" };
                parts.Add(g.Button is { } b ? $"자이로 → {to} ({Short(b)})" : $"자이로 → {to}");
            }
            if (_profile.Tuning is { } t && (t.LeftStick ?? t.RightStick) is not null || _profile.Tuning?.LeftTrigger is not null || _profile.Tuning?.RightTrigger is not null)
                parts.Add("데드존 조정");
            if (_profile.Tuning?.SwapSticks == true) parts.Add("스틱 바꿈");
            return parts.Count == 0 ? "기본값" : string.Join(" · ", parts);
        }
    }

    private static string Short(ClawButton b) => b switch { ClawButton.L3 => "L3", ClawButton.R3 => "R3", _ => b.ToString() };

    // ── 공통 ────────────────────────────────────────────────

    /// <summary>프로필 값으로 화면을 맞춘다 (처음·되돌리기 뒤).</summary>
    public void Reload()
    {
        _loading = true;
        try
        {
            var t = _profile.Tuning;
            var g = t?.Gyro;
            foreach (var c in GyroOutputs) c.IsSelected = c.Value == (g?.Output ?? GyroOutput.Off);
            foreach (var c in GyroButtons) c.IsSelected = g is not null && c.Value == g.Button;
            foreach (var c in GyroActivations) c.IsSelected = g is not null && c.Value == g.Activation;
            GyroSensitivity = g?.Sensitivity ?? 20;
            GyroDeadzone = g?.Deadzone ?? 10;

            LeftInner = t?.LeftStick?.InnerDeadzone ?? _global?.LeftStickDeadzone ?? 10;
            LeftOuter = t?.LeftStick?.OuterDeadzone ?? 100;
            RightInner = t?.RightStick?.InnerDeadzone ?? _global?.RightStickDeadzone ?? 10;
            RightOuter = t?.RightStick?.OuterDeadzone ?? 100;
            LtInner = t?.LeftTrigger?.InnerDeadzone ?? _global?.TriggerDeadzone ?? 5;
            LtOuter = t?.LeftTrigger?.OuterDeadzone ?? 95;
            RtInner = t?.RightTrigger?.InnerDeadzone ?? _global?.TriggerDeadzone ?? 5;
            RtOuter = t?.RightTrigger?.OuterDeadzone ?? 95;
            SwapSticks = t?.SwapSticks ?? false;
        }
        finally
        {
            _loading = false;
        }
        OnPropertyChanged(nameof(GyroOn));
        OnPropertyChanged(nameof(GyroHasButton));
        OnPropertyChanged(nameof(Summary));
    }

    private void Change(string key, Action apply)
    {
        if (_loading) return;
        _beforeChange(key);
        apply();
        if (_profile.Tuning is { IsEmpty: true }) _profile.Tuning = null; // 다 기본값이면 저장 파일에 남기지 않는다
        _afterChange();
        Reload();
    }

    /// <summary>슬라이더: 끄는 동안 계속 불린다 — 되돌리기 기억은 한 번만(같은 키), 화면 다시 맞추기는 하지 않는다.</summary>
    private void ChangeSlider(string key, Action apply)
    {
        if (_loading) return;
        _beforeChange(key);
        apply();
        _afterChange();
        OnPropertyChanged(nameof(Summary));
    }
}
