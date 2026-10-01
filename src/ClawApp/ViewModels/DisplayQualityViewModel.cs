using System.ComponentModel;
using System.Windows.Threading;
using ClawCore.Ipc;
using CommunityToolkit.Mvvm.ComponentModel;

namespace ClawApp.ViewModels;

/// <summary>
/// 설정 → 디스플레이의 "화면 품질" 묶음 (Intel 드라이버): 레트로 스케일링, 선명도, 저지연, 가변 주사율(읽기 전용).
/// 값은 서비스 상태(ServiceStatus.DisplayQuality)에서 읽고, 바꿀 때는 SetDisplayQuality로 보낸다.
/// </summary>
public sealed partial class DisplayQualityViewModel : ObservableObject
{
    private readonly ShellViewModel _shell;
    private readonly DispatcherTimer _levelDebounce;
    private bool _loading;

    private static DisplayQualityViewModel? _instance;

    /// <summary>셸당 하나 (셸 이벤트를 구독하므로 설정 화면을 열 때마다 새로 만들지 않는다).</summary>
    public static DisplayQualityViewModel For(ShellViewModel shell) => _instance ??= new DisplayQualityViewModel(shell);

    private DisplayQualityViewModel(ShellViewModel shell)
    {
        _shell = shell;
        _levelDebounce = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(400) };
        _levelDebounce.Tick += async (_, _) =>
        {
            _levelDebounce.Stop();
            await SendAsync(new DisplayQualityChange { Sharpening = true, SharpeningLevel = SharpeningLevel });
        };
        shell.PropertyChanged += OnShellChanged;
        Load();
    }

    private void OnShellChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(ShellViewModel.Status)) Load();
    }

    /// <summary>설정 화면을 열 때 최신 값으로.</summary>
    public async Task RefreshAsync()
    {
        await _shell.RefreshStatusAsync();
        Load();
    }

    private DisplayQualityState? State => _shell.Status?.DisplayQuality;

    // ── 표시 ───────────────────────────────────────────

    public bool Available => State?.Available == true;
    public string? UnavailableText => !_shell.ServiceConnected ? "ClawHub 서비스에 연결되지 않았어요"
        : State is null ? "서비스가 화면 품질 설정을 지원하지 않아요 (서비스 업데이트 필요)"
        : !State.Available ? $"Intel 그래픽 설정을 사용할 수 없어요{(State.Error is { } e ? $" — {e}" : "")}"
        : null;

    public bool RetroSupported => State?.RetroScaling is not null;
    public bool RetroModeChoice => RetroSupported && RetroScaling && State!.RetroScalingModes.Count > 1;
    public bool SharpeningSupported => State?.Sharpening is not null;
    public int SharpeningMin => State?.SharpeningMin ?? 0;
    public int SharpeningMax => State?.SharpeningMax ?? 100;
    public bool LowLatencySupported => State?.LowLatency is not null;
    public bool BoostSupported => State?.LowLatencyModes.Contains(LowLatencyMode.Boost) == true;
    public bool VrrSupported => State?.AdaptiveSyncSupported == true;

    public string VrrText => State switch
    {
        null => "",
        { AdaptiveSyncSupported: not true } => "이 화면은 가변 주사율을 지원하지 않아요",
        { AdaptiveSync: true } s => $"켜짐 · {Range(s)}{(s.VrrProfile is { } p ? $" · Intel Arc Sync {ProfileName(p)}" : "")}",
        var s => $"꺼짐{(s.VrrProfile is { } p ? $" · Intel Arc Sync {ProfileName(p)}" : "")}",
    };

    private static string Range(DisplayQualityState s) =>
        s.VrrMinHz is { } lo && s.VrrMaxHz is { } hi ? $"{lo:0}–{hi:0}Hz" : "";

    private static string ProfileName(string p) => p switch
    {
        "Recommended" => "권장",
        "Excellent" => "최상",
        "Good" => "좋음",
        "Compatible" => "호환",
        "Off" => "끔",
        "Vesa" => "VESA",
        "Custom" => "사용자 지정",
        _ => p,
    };

    // ── 바꿀 수 있는 값 ─────────────────────────────────

    [ObservableProperty] private bool _retroScaling;
    [ObservableProperty] private bool _retroNearest;
    [ObservableProperty] private bool _sharpening;
    [ObservableProperty] private int _sharpeningLevel;
    [ObservableProperty] private LowLatencyMode _lowLatency;

    public bool RetroInteger { get => !RetroNearest; set { if (value) RetroNearest = false; } }

    public bool LowLatencyOff { get => LowLatency == LowLatencyMode.Off; set { if (value) LowLatency = LowLatencyMode.Off; } }
    public bool LowLatencyOn { get => LowLatency == LowLatencyMode.On; set { if (value) LowLatency = LowLatencyMode.On; } }
    public bool LowLatencyBoost { get => LowLatency == LowLatencyMode.Boost; set { if (value) LowLatency = LowLatencyMode.Boost; } }

    private void Load()
    {
        if (_levelDebounce.IsEnabled) return; // 슬라이더를 움직이는 중이면 덮어쓰지 않는다
        _loading = true;
        try
        {
            var s = State;
            RetroScaling = s?.RetroScaling == true;
            RetroNearest = s?.RetroScalingMode == RetroScalingMode.NearestNeighbour;
            Sharpening = s?.Sharpening == true;
            SharpeningLevel = Math.Clamp(s?.SharpeningLevel ?? 0, SharpeningMin, SharpeningMax);
            LowLatency = s?.LowLatency ?? LowLatencyMode.Off;
        }
        finally
        {
            _loading = false;
        }
        OnPropertyChanged(string.Empty);
    }

    partial void OnRetroScalingChanged(bool value)
    {
        OnPropertyChanged(nameof(RetroModeChoice));
        if (!_loading) _ = SendAsync(new DisplayQualityChange { RetroScaling = value }, "화면이 잠깐 깜빡일 수 있어요");
    }

    partial void OnRetroNearestChanged(bool value)
    {
        OnPropertyChanged(nameof(RetroInteger));
        if (!_loading)
            _ = SendAsync(new DisplayQualityChange
            {
                RetroScaling = RetroScaling,
                RetroScalingMode = value ? RetroScalingMode.NearestNeighbour : RetroScalingMode.Integer,
            });
    }

    partial void OnSharpeningChanged(bool value)
    {
        if (!_loading) _ = SendAsync(new DisplayQualityChange { Sharpening = value });
    }

    partial void OnSharpeningLevelChanged(int value)
    {
        if (_loading) return;
        if (!Sharpening)
        {
            _loading = true;
            Sharpening = true; // 강도를 움직이면 선명도를 켠다 (디바운스 전송에 포함)
            _loading = false;
        }
        _levelDebounce.Stop();
        _levelDebounce.Start();
    }

    partial void OnLowLatencyChanged(LowLatencyMode value)
    {
        OnPropertyChanged(nameof(LowLatencyOff));
        OnPropertyChanged(nameof(LowLatencyOn));
        OnPropertyChanged(nameof(LowLatencyBoost));
        if (!_loading) _ = SendAsync(new DisplayQualityChange { LowLatency = value });
    }

    private async Task SendAsync(DisplayQualityChange change, string? note = null)
    {
        if (await _shell.SendAsync(new Request { Op = Ops.SetDisplayQuality, DisplayQuality = change }))
        {
            if (note is not null) _shell.ShowToast(note);
        }
        else
        {
            Load(); // 실패하면 실제 값으로 되돌린다 (SendAsync가 응답 Status로 갱신해 둠)
        }
    }
}
