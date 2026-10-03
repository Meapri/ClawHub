using System.Collections.ObjectModel;
using System.Windows.Threading;
using ClawApp.Services;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using NAudio.CoreAudioApi;

namespace ClawApp.ViewModels;

/// <summary>고를 수 있는 오디오 장치 칩.</summary>
public sealed partial class SoundDeviceChoice(string id, string name) : ObservableObject
{
    public string Id { get; } = id;
    [ObservableProperty] private string _name = name;
    [ObservableProperty] private bool _isSelected;
}

/// <summary>볼륨 믹서의 앱 한 줄 (0~100, 음소거).</summary>
public sealed partial class AppVolumeViewModel : ObservableObject
{
    private readonly DispatcherTimer _debounce = new() { Interval = TimeSpan.FromMilliseconds(120) };
    private bool _loading;

    public AppVolumeViewModel(AudioApp app)
    {
        ProcessId = app.ProcessId;
        IsSystemSounds = app.IsSystemSounds;
        _debounce.Tick += (_, _) =>
        {
            _debounce.Stop();
            var (pid, system, v) = (ProcessId, IsSystemSounds, Volume);
            _ = Task.Run(() => AudioControls.SetApp(pid, system, v / 100f, null));
        };
        Update(app);
    }

    public int ProcessId { get; }
    public bool IsSystemSounds { get; }
    [ObservableProperty] private string _name = "";
    [ObservableProperty] private int _volume;
    [ObservableProperty] private bool _muted;

    public void Update(AudioApp app)
    {
        _loading = true;
        Name = app.Name;
        Volume = (int)Math.Round(app.Volume * 100);
        Muted = app.Muted;
        _loading = false;
    }

    partial void OnVolumeChanged(int value)
    {
        if (_loading) return;
        _debounce.Stop();
        _debounce.Start();
    }

    [RelayCommand]
    private void ToggleMuted() => Muted = !Muted;

    partial void OnMutedChanged(bool value)
    {
        if (_loading) return;
        var (pid, system) = (ProcessId, IsSystemSounds);
        _ = Task.Run(() => AudioControls.SetApp(pid, system, null, value));
    }
}

/// <summary>
/// 사운드 설정: 출력·입력 장치, 소리 끄기, 마이크 레벨·켜기, 앱별 볼륨, 통화 중 다른 소리.
/// 설정 화면과 빠른 설정이 같은 인스턴스를 쓴다. 조회는 뒤에서 (오디오 COM 호출은 수십 ms).
/// </summary>
public sealed partial class SoundViewModel : ObservableObject
{
    private readonly ShellViewModel _shell;
    private readonly DispatcherTimer _micDebounce = new() { Interval = TimeSpan.FromMilliseconds(120) };
    private bool _loading;

    public SoundViewModel(ShellViewModel shell)
    {
        _shell = shell;
        _micDebounce.Tick += (_, _) =>
        {
            _micDebounce.Stop();
            var level = MicLevel;
            _ = Task.Run(() => AudioControls.SetMicLevel(level));
        };
    }

    public ObservableCollection<SoundDeviceChoice> Outputs { get; } = [];
    public ObservableCollection<SoundDeviceChoice> Inputs { get; } = [];
    public ObservableCollection<AppVolumeViewModel> Apps { get; } = [];

    public IReadOnlyList<TuningChoice<int>> DuckingChoices { get; } =
        [new("다른 소리 끄기", 0), new("80%로 줄이기", 1), new("50%로 줄이기", 2), new("그대로", 3)];

    [ObservableProperty] private bool _speakerMuted;
    [ObservableProperty] private int _micLevel;
    [ObservableProperty] private bool _micOn = true;
    [ObservableProperty] private bool _hasMic;

    public bool HasManyOutputs => Outputs.Count > 1;
    public bool HasApps => Apps.Count > 0;

    private bool _refreshing;

    /// <summary>화면이 보일 때 다시 읽는다 (장치를 꽂거나 앱이 소리를 내기 시작했을 수 있다).</summary>
    public async Task RefreshAsync()
    {
        if (_refreshing) return;
        _refreshing = true;
        try
        {
            var (outs, ins, apps, muted, mic, micOn, ducking) = await Task.Run(() => (
                AudioControls.Devices(DataFlow.Render), AudioControls.Devices(DataFlow.Capture), AudioControls.Apps(),
                AudioControls.IsSpeakerMuted(), AudioControls.GetMicLevel(), SystemControls.IsMicrophoneOn(), AudioControls.GetDucking()));
            _loading = true;
            try
            {
                Sync(Outputs, outs);
                Sync(Inputs, ins);
                SyncApps(apps);
                SpeakerMuted = muted ?? false;
                HasMic = mic is not null;
                MicLevel = mic ?? 0;
                MicOn = micOn ?? false;
                foreach (var c in DuckingChoices) c.IsSelected = c.Value == ducking;
            }
            finally
            {
                _loading = false;
            }
            OnPropertyChanged(nameof(HasManyOutputs));
            OnPropertyChanged(nameof(HasApps));
        }
        finally
        {
            _refreshing = false;
        }
    }

    /// <summary>목록을 새로 만들지 않고 고치기만 (패드 포커스가 칩에 남게).</summary>
    private static void Sync(ObservableCollection<SoundDeviceChoice> list, List<AudioDevice> devices)
    {
        for (var i = list.Count - 1; i >= 0; i--)
            if (devices.All(d => d.Id != list[i].Id)) list.RemoveAt(i);
        foreach (var d in devices)
        {
            if (list.FirstOrDefault(c => c.Id == d.Id) is not { } c) list.Add(c = new SoundDeviceChoice(d.Id, d.Name));
            c.Name = d.Name;
            c.IsSelected = d.IsDefault;
        }
    }

    private void SyncApps(List<AudioApp> apps)
    {
        for (var i = Apps.Count - 1; i >= 0; i--)
            if (apps.All(a => a.ProcessId != Apps[i].ProcessId || a.IsSystemSounds != Apps[i].IsSystemSounds)) Apps.RemoveAt(i);
        foreach (var a in apps)
        {
            if (Apps.FirstOrDefault(x => x.ProcessId == a.ProcessId && x.IsSystemSounds == a.IsSystemSounds) is { } vm) vm.Update(a);
            else Apps.Add(new AppVolumeViewModel(a));
        }
    }

    [RelayCommand]
    private async Task PickOutput(SoundDeviceChoice choice) => await PickDevice(choice, Outputs, "출력");

    [RelayCommand]
    private async Task PickInput(SoundDeviceChoice choice) => await PickDevice(choice, Inputs, "입력");

    private async Task PickDevice(SoundDeviceChoice choice, ObservableCollection<SoundDeviceChoice> list, string what)
    {
        if (choice.IsSelected) return;
        try
        {
            await Task.Run(() => AudioControls.SetDefault(choice.Id));
            foreach (var c in list) c.IsSelected = c == choice;
            _shell.ShowToast($"{what}: {choice.Name}");
        }
        catch (Exception ex) when (ex is System.Runtime.InteropServices.COMException or InvalidCastException)
        {
            _shell.ShowToast($"{what} 장치를 바꾸지 못했어요");
        }
        await RefreshAsync(); // 볼륨·앱 목록은 장치마다 다르다
        _shell.QuickAccess.Refresh();
    }

    [RelayCommand]
    private void PickDucking(TuningChoice<int> choice)
    {
        try
        {
            AudioControls.SetDucking(choice.Value);
            foreach (var c in DuckingChoices) c.IsSelected = c == choice;
        }
        catch (Exception) { _shell.ShowToast("통화 중 소리 설정을 바꾸지 못했어요"); }
    }

    partial void OnSpeakerMutedChanged(bool value)
    {
        if (_loading) return;
        _ = Task.Run(() => AudioControls.SetSpeakerMuted(value));
    }

    partial void OnMicLevelChanged(int value)
    {
        if (_loading) return;
        _micDebounce.Stop();
        _micDebounce.Start();
    }

    partial void OnMicOnChanged(bool value)
    {
        if (_loading) return;
        _ = Task.Run(() =>
        {
            // 켜짐/꺼짐을 맞춘다 (토글이라 지금 상태가 다르면 한 번만)
            if (SystemControls.IsMicrophoneOn() is { } now && now != value) SystemControls.ToggleMicrophone();
        });
    }
}
