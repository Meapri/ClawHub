using System.Windows.Threading;
using ClawApp.Services;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;

namespace ClawApp.ViewModels;

/// <summary>
/// 야간 모드 (SteamOS의 야간 모드처럼): 지금 켜기, 강도(색 온도), 예약(몇 시부터 몇 시까지).
/// 15초마다 예약 시간을 보고, 켜져 있으면 다른 프로그램·화면 모드 변경이 감마를 되돌렸는지 확인해 다시 적용한다.
/// 앱이 끝나면 원래 색으로 되돌린다 (<see cref="Shutdown"/>).
/// </summary>
public sealed partial class NightModeViewModel : ObservableObject
{
    private readonly ShellViewModel _shell;
    private readonly DispatcherTimer _tick = new() { Interval = TimeSpan.FromSeconds(15) };
    private readonly DispatcherTimer _debounce = new() { Interval = TimeSpan.FromMilliseconds(60) };
    private NightModeSettings S => _shell.Settings.NightMode;
    private int? _appliedKelvin; // null = 원래 색
    private bool _loading;

    public NightModeViewModel(ShellViewModel shell)
    {
        _shell = shell;
        _loading = true;
        On = S.On;
        Strength = ToStrength(S.Kelvin);
        Scheduled = S.Scheduled;
        _loading = false;
        StartChoices = [.. new[] { 18, 19, 20, 21, 22, 23 }.Select(h => new TuningChoice<int>($"{h}시", h))];
        EndChoices = [.. new[] { 5, 6, 7, 8, 9 }.Select(h => new TuningChoice<int>($"{h}시", h))];
        MarkHours();
        _tick.Tick += (_, _) => Update();
        _debounce.Tick += (_, _) => { _debounce.Stop(); Update(force: true); };
        Update();
    }

    /// <summary>지금 켬 (예약과 상관없이).</summary>
    [ObservableProperty] private bool _on;
    /// <summary>강도 0–100 (0 = 6500K, 100 = 2700K).</summary>
    [ObservableProperty] private int _strength;
    [ObservableProperty] private bool _scheduled;
    [ObservableProperty] private bool _active;

    public IReadOnlyList<TuningChoice<int>> StartChoices { get; }
    public IReadOnlyList<TuningChoice<int>> EndChoices { get; }

    public string StatusText => Active
        ? (On ? "켜짐" : $"예약으로 켜짐 · {S.EndHour}시에 꺼져요") + (Limited ? " · Windows 한도까지만 적용" : "")
        : Scheduled ? $"{S.StartHour}시에 켜져요" : "꺼짐";

    /// <summary>원하는 강도가 Windows 감마 한도를 넘어 약하게 적용됐는지.</summary>
    [ObservableProperty] private bool _limited;

    private static int ToKelvin(int strength) => NightMode.MaxKelvin - (int)Math.Round(Math.Clamp(strength, 0, 100) / 100.0 * (NightMode.MaxKelvin - NightMode.MinKelvin));
    private static int ToStrength(int kelvin) => (int)Math.Round((NightMode.MaxKelvin - Math.Clamp(kelvin, NightMode.MinKelvin, NightMode.MaxKelvin)) * 100.0 / (NightMode.MaxKelvin - NightMode.MinKelvin));

    partial void OnOnChanged(bool value)
    {
        if (_loading) return;
        S.On = value;
        _shell.Settings.Save();
        Update(force: true);
    }

    partial void OnStrengthChanged(int value)
    {
        if (_loading) return;
        S.Kelvin = ToKelvin(value);
        _shell.Settings.Save();
        _debounce.Stop();
        _debounce.Start(); // 슬라이더를 끄는 동안 너무 자주 바꾸지 않게
    }

    partial void OnScheduledChanged(bool value)
    {
        if (_loading) return;
        S.Scheduled = value;
        _shell.Settings.Save();
        Update(force: true);
    }

    partial void OnActiveChanged(bool value) => OnPropertyChanged(nameof(StatusText));
    partial void OnLimitedChanged(bool value) => OnPropertyChanged(nameof(StatusText));

    [RelayCommand]
    private void PickStart(TuningChoice<int> c) { S.StartHour = c.Value; _shell.Settings.Save(); MarkHours(); Update(force: true); }

    [RelayCommand]
    private void PickEnd(TuningChoice<int> c) { S.EndHour = c.Value; _shell.Settings.Save(); MarkHours(); Update(force: true); }

    [RelayCommand]
    private void Toggle() => On = !On;

    private void MarkHours()
    {
        foreach (var c in StartChoices) c.IsSelected = c.Value == S.StartHour;
        foreach (var c in EndChoices) c.IsSelected = c.Value == S.EndHour;
        OnPropertyChanged(nameof(StatusText));
    }

    private bool InSchedule()
    {
        if (!S.Scheduled) return false;
        var h = DateTime.Now.Hour;
        return S.StartHour > S.EndHour ? h >= S.StartHour || h < S.EndHour : h >= S.StartHour && h < S.EndHour;
    }

    /// <summary>켜야 하는지 보고 화면에 적용 (force = 값이 바뀌었으니 무조건 다시).</summary>
    private void Update(bool force = false)
    {
        var want = On || InSchedule();
        Active = want;
        if (want)
        {
            var kelvin = ToKelvin(Strength);
            if (force || _appliedKelvin is null || !NightMode.IsApplied(_appliedKelvin.Value))
            {
                _appliedKelvin = NightMode.Apply(kelvin);
                MarkApplied(true);
                Limited = _appliedKelvin is { } k && k > kelvin;
            }
            if (!_tick.IsEnabled) _tick.Start();
        }
        else
        {
            if (_appliedKelvin is not null || force || S.Applied) NightMode.Reset(); // S.Applied = 지난번에 비정상 종료돼 색이 남았다
            MarkApplied(false);
            _appliedKelvin = null;
            Limited = false;
            if (!S.Scheduled) _tick.Stop(); // 예약이 없으면 확인할 것이 없다
            else if (!_tick.IsEnabled) _tick.Start();
        }
        OnPropertyChanged(nameof(StatusText));
    }

    private void MarkApplied(bool applied)
    {
        if (S.Applied == applied) return;
        S.Applied = applied;
        _shell.Settings.Save();
    }

    /// <summary>앱 끝: 원래 색으로.</summary>
    public void Shutdown()
    {
        _tick.Stop();
        if (_appliedKelvin is not null) NightMode.Reset();
        _appliedKelvin = null;
        MarkApplied(false);
    }
}
