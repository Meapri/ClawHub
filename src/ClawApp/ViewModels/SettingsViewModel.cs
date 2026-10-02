using System.Collections.ObjectModel;
using System.Windows.Media;
using System.Windows.Threading;
using ClawApp.Input;
using ClawApp.Services;
using ClawCore.Controller;
using ClawCore.Ipc;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Microsoft.Win32;

namespace ClawApp.ViewModels;

public sealed partial class SettingsCategory(string key, string icon, string label) : ObservableObject
{
    public string Key { get; } = key;
    public string Icon { get; } = icon;
    public string Label { get; } = label;
    [ObservableProperty] private bool _isSelected;
}

public sealed partial class ChoiceItem(string label, object value) : ObservableObject
{
    public string Label { get; } = label;
    public object Value { get; } = value;
    public Brush? Swatch { get; init; }
    [ObservableProperty] private bool _isSelected;
}

/// <summary>
/// SteamOS식 설정 허브: 왼쪽 분류, 오른쪽 내용. 런처 밖으로 나가지 않고 모든 설정을 패드로 한다.
/// 성능·밝기·볼륨·충전 한도는 퀵 설정과 같은 VM(<see cref="QuickAccessViewModel"/>)을 공유한다.
/// </summary>
public sealed partial class SettingsViewModel : ObservableObject, INavPage
{
    private readonly ShellViewModel _shell;
    private readonly DispatcherTimer _controllerDebounce;
    private bool _loading;

    public SettingsViewModel(ShellViewModel shell)
    {
        _shell = shell;
        Categories =
        [
            new("general", "", "일반"),
            new("home", "", "홈 화면"),
            new("controller", "", "컨트롤러"),
            new("light", "", "조명"),
            new("performance", "", "성능"),
            new("display", "", "디스플레이·사운드"),
            new("battery", "", "배터리"),
            new("windows", "", "Windows 설정"),
            new("library", "", "라이브러리"),
            new("about", "", "정보"),
        ];
        Selected = Categories[0];
        Selected.IsSelected = true;

        _controllerDebounce = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(400) };
        _controllerDebounce.Tick += async (_, _) => { _controllerDebounce.Stop(); await SendControllerSettingsAsync(); };

        LightColors = new ObservableCollection<ChoiceItem>(new (string, byte, byte, byte)[]
        {
            ("하늘", 0x4C, 0xC2, 0xFF), ("흰색", 0xFF, 0xFF, 0xFF), ("빨강", 0xFF, 0x20, 0x20), ("주황", 0xFF, 0x80, 0x00),
            ("노랑", 0xFF, 0xE0, 0x00), ("초록", 0x20, 0xFF, 0x40), ("파랑", 0x20, 0x40, 0xFF), ("보라", 0xA0, 0x40, 0xFF),
            ("분홍", 0xFF, 0x50, 0xB0),
        }.Select(c => new ChoiceItem(c.Item1, new[] { c.Item2, c.Item3, c.Item4 })
        {
            Swatch = Frozen(new SolidColorBrush(Color.FromRgb(c.Item2, c.Item3, c.Item4))),
        }));
    }

    private static Brush Frozen(Brush b) { b.Freeze(); return b; }

    public string Title => "설정";
    public IReadOnlyList<Hint> Hints => [new("A", "선택"), new("B", "뒤로"), new("LB", "이전 분류"), new("RB", "다음 분류")];

    public ShellViewModel Shell => _shell;
    public QuickAccessViewModel Quick => _shell.QuickAccess;
    /// <summary>Endurance Gaming·배터리 전용 기본값·배터리 수명 (Views/Sections의 전원 관련 섹션이 바인딩).</summary>
    public PowerSettingsViewModel Power => _shell.QuickAccess.Power;

    public IReadOnlyList<SettingsCategory> Categories { get; }
    [ObservableProperty] private SettingsCategory _selected;

    partial void OnSelectedChanged(SettingsCategory? oldValue, SettingsCategory newValue)
    {
        if (oldValue is not null) oldValue.IsSelected = false;
        newValue.IsSelected = true;
        if (newValue.Key == "display")
        {
            LoadDisplayModes();
            _ = DisplayQuality.RefreshAsync();
        }
    }

    /// <summary>디스플레이 → 화면 품질 (Intel 드라이버). Views/Sections/DisplayQualitySection이 바인딩.</summary>
    public DisplayQualityViewModel DisplayQuality => DisplayQualityViewModel.For(_shell);

    [RelayCommand] private void Select(SettingsCategory c) => Selected = c;

    public bool Handle(NavAction action)
    {
        var i = Categories.ToList().IndexOf(Selected);
        switch (action)
        {
            case NavAction.TabPrev: Selected = Categories[(i + Categories.Count - 1) % Categories.Count]; break;
            case NavAction.TabNext: Selected = Categories[(i + 1) % Categories.Count]; break;
            default: return false;
        }
        _shell.RequestFocus();
        return true;
    }

    public void OnShown()
    {
        Quick.Refresh();
        RefreshHomeSection(); // 배경 고르기에서 돌아왔을 때 등
        _ = _shell.RefreshStatusAsync().ContinueWith(_ => LoadFromStatus(), TaskScheduler.FromCurrentSynchronizationContext());
    }

    // ── 일반 ───────────────────────────────────────────────

    public bool StartWithWindows
    {
        get => AppSettings.StartWithWindows;
        set { AppSettings.StartWithWindows = value; OnPropertyChanged(); }
    }

    public bool AutoControllerMode
    {
        get => _shell.Settings.AutoControllerMode;
        set { _shell.Settings.AutoControllerMode = value; _shell.Settings.Save(); OnPropertyChanged(); }
    }

    public bool StartupAuto { get => _shell.Settings.StartupMode == StartupMode.Auto; set { if (value) SetStartup(StartupMode.Auto); } }
    public bool StartupShell { get => _shell.Settings.StartupMode == StartupMode.Shell; set { if (value) SetStartup(StartupMode.Shell); } }
    public bool StartupCompanion { get => _shell.Settings.StartupMode == StartupMode.Companion; set { if (value) SetStartup(StartupMode.Companion); } }

    private void SetStartup(StartupMode mode)
    {
        _shell.Settings.StartupMode = mode;
        _shell.Settings.Save();
        _shell.IsCompanion = mode == StartupMode.Companion || (mode == StartupMode.Auto && _shell.Settings.ResolveCompanion());
        OnPropertyChanged(nameof(StartupAuto));
        OnPropertyChanged(nameof(StartupShell));
        OnPropertyChanged(nameof(StartupCompanion));
        OnPropertyChanged(nameof(CompanionText));
    }

    public string CompanionText => _shell.IsCompanion
        ? "지금: Xbox 모드 동반 — 홈 화면은 Xbox 앱, ClawHub는 버튼·자동 매핑 담당"
        : "지금: ClawHub 전체 화면";

    public bool MsiCenterKnown => _shell.Status?.MsiCenterEnabled is not null;

    public bool MsiCenterEnabled
    {
        get => _shell.Status?.MsiCenterEnabled == true;
        set => _ = SetMsiCenterAsync(value);
    }

    private async Task SetMsiCenterAsync(bool enabled)
    {
        _shell.BusyText = enabled ? "MSI Center M 켜는 중…" : "MSI Center M 끄는 중…";
        try
        {
            if (await _shell.SendAsync(new Request { Op = Ops.SetMsiCenter, Enabled = enabled }))
                _shell.ShowToast(enabled ? "MSI Center M을 켰어요" : "MSI Center M을 껐어요 — 이제 본체 버튼은 ClawHub만 반응해요");
        }
        finally
        {
            _shell.BusyText = null;
            OnPropertyChanged(nameof(MsiCenterEnabled));
        }
    }

    // ── 컨트롤러 / 조명 ────────────────────────────────────

    [ObservableProperty] private int _vibration = 100;
    [ObservableProperty] private int _leftDeadzone = 10;
    [ObservableProperty] private int _rightDeadzone = 10;
    [ObservableProperty] private int _triggerDeadzone = 5;
    [ObservableProperty] private bool _swapAbxy;
    [ObservableProperty] private bool _diagonalLock;
    [ObservableProperty] private LightMode _light = LightMode.Default;
    [ObservableProperty] private int _lightBrightness = 100;
    [ObservableProperty] private int _abxyBrightness = 50;
    public ObservableCollection<ChoiceItem> LightColors { get; }

    public bool ControllerAvailable => _shell.Status?.ControllerConnected == true;
    public bool LightDefault { get => Light == LightMode.Default; set { if (value) Light = LightMode.Default; } }
    public bool LightStatic { get => Light == LightMode.Static; set { if (value) Light = LightMode.Static; } }
    public bool LightOff { get => Light == LightMode.Off; set { if (value) Light = LightMode.Off; } }
    public bool LightBreath { get => Light == LightMode.Breath; set { if (value) Light = LightMode.Breath; } }
    public bool LightRainbow { get => Light == LightMode.Rainbow; set { if (value) Light = LightMode.Rainbow; } }
    public bool LightWave { get => Light == LightMode.Wave; set { if (value) Light = LightMode.Wave; } }
    public bool HasSpeed => Light is LightMode.Breath or LightMode.Rainbow or LightMode.Wave;
    public bool UsesColor => Light is LightMode.Static or LightMode.Breath;

    [ObservableProperty] private int _lightSpeed = 1;
    public bool SpeedSlow { get => LightSpeed == 0; set { if (value) LightSpeed = 0; } }
    public bool SpeedNormal { get => LightSpeed == 1; set { if (value) LightSpeed = 1; } }
    public bool SpeedFast { get => LightSpeed == 2; set { if (value) LightSpeed = 2; } }

    partial void OnLightSpeedChanged(int value)
    {
        OnPropertyChanged(nameof(SpeedSlow));
        OnPropertyChanged(nameof(SpeedNormal));
        OnPropertyChanged(nameof(SpeedFast));
        ControllerChanged();
    }

    private void LoadFromStatus()
    {
        _loading = true;
        try
        {
            var s = _shell.Status?.ControllerSettings ?? new ControllerSettings();
            var d = _shell.Status?.ControllerDefaults ?? new ControllerSettings();
            Vibration = s.Vibration ?? d.Vibration ?? 100;
            LeftDeadzone = s.LeftStickDeadzone ?? d.LeftStickDeadzone ?? 10;
            RightDeadzone = s.RightStickDeadzone ?? d.RightStickDeadzone ?? 10;
            TriggerDeadzone = s.TriggerDeadzone ?? d.TriggerDeadzone ?? 5;
            SwapAbxy = s.SwapAbxy ?? d.SwapAbxy ?? false;
            DiagonalLock = s.DpadDiagonalLock ?? d.DpadDiagonalLock ?? false;
            Light = s.Light;
            LightSpeed = s.LightSpeed;
            LightBrightness = s.LightBrightness ?? d.LightBrightness ?? 100;
            AbxyBrightness = s.AbxyBrightness ?? d.AbxyBrightness ?? 50;
            foreach (var c in LightColors) c.IsSelected = ((byte[])c.Value).SequenceEqual(s.LightColor);
        }
        finally
        {
            _loading = false;
        }
        OnPropertyChanged(string.Empty);
    }

    partial void OnVibrationChanged(int value) => ControllerChanged();
    partial void OnLeftDeadzoneChanged(int value) => ControllerChanged();
    partial void OnRightDeadzoneChanged(int value) => ControllerChanged();
    partial void OnTriggerDeadzoneChanged(int value) => ControllerChanged();
    partial void OnSwapAbxyChanged(bool value) => ControllerChanged();
    partial void OnDiagonalLockChanged(bool value) => ControllerChanged();
    partial void OnLightBrightnessChanged(int value) => ControllerChanged();
    partial void OnAbxyBrightnessChanged(int value) => ControllerChanged();

    partial void OnLightChanged(LightMode value)
    {
        OnPropertyChanged(nameof(LightDefault));
        OnPropertyChanged(nameof(LightStatic));
        OnPropertyChanged(nameof(LightOff));
        OnPropertyChanged(nameof(LightBreath));
        OnPropertyChanged(nameof(LightRainbow));
        OnPropertyChanged(nameof(LightWave));
        OnPropertyChanged(nameof(HasSpeed));
        OnPropertyChanged(nameof(UsesColor));
        ControllerChanged();
    }

    [RelayCommand]
    private void PickColor(ChoiceItem item)
    {
        foreach (var c in LightColors) c.IsSelected = c == item;
        if (!UsesColor) Light = LightMode.Static; // 숨쉬기 중이면 숨쉬기 색만 바꾼다
        ControllerChanged();
    }

    private void ControllerChanged()
    {
        if (!_loading) _controllerDebounce.Stop();
        if (!_loading) _controllerDebounce.Start();
    }

    private async Task SendControllerSettingsAsync()
    {
        var color = LightColors.FirstOrDefault(c => c.IsSelected)?.Value as byte[] ?? (byte[])LightColors[0].Value;
        await _shell.SendAsync(new Request
        {
            Op = Ops.SetControllerSettings,
            ControllerSettings = new ControllerSettings
            {
                Vibration = Vibration,
                LeftStickDeadzone = LeftDeadzone,
                RightStickDeadzone = RightDeadzone,
                TriggerDeadzone = TriggerDeadzone,
                SwapAbxy = SwapAbxy,
                DpadDiagonalLock = DiagonalLock,
                Light = Light,
                LightBrightness = LightBrightness,
                LightColor = color,
                LightSpeed = LightSpeed,
                AbxyBrightness = AbxyBrightness,
            },
        });
    }

    [RelayCommand]
    private async Task ResetController()
    {
        if (await _shell.SendAsync(new Request { Op = Ops.SetControllerSettings, ControllerSettings = new ControllerSettings() }))
        {
            LoadFromStatus();
            _shell.ShowToast("컨트롤러 설정을 원래대로 돌렸어요");
        }
    }

    [RelayCommand] private void OpenProfiles() => _shell.Navigate(new ProfilesViewModel(_shell));

    // ── 디스플레이 ─────────────────────────────────────────

    [ObservableProperty] private ObservableCollection<ChoiceItem> _resolutions = [];
    [ObservableProperty] private ObservableCollection<ChoiceItem> _refreshRates = [];

    /// <summary>
    /// 지원 모드 목록 (EnumDisplaySettings 90번 남짓 ≈ 12–28ms 실측). 설정을 열 때 뒤에서 미리 읽어 두고 이 화면 동안 다시 쓴다 —
    /// LB/RB로 분류를 넘기다 디스플레이를 지날 때마다 UI 스레드가 멈칫하지 않게. 지금 모드(Current)는 매번 새로 읽는다.
    /// </summary>
    private readonly Task<IReadOnlyList<DisplayControls.DisplayMode>> _modes = Task.Run(DisplayControls.Modes);

    private void LoadDisplayModes()
    {
        var modes = _modes.GetAwaiter().GetResult();
        var cur = DisplayControls.Current();
        Resolutions = new(modes.DistinctBy(m => (m.Width, m.Height)).Take(6)
            .Select(m => new ChoiceItem(m.ToString(), m) { IsSelected = cur is { } c && c.Width == m.Width && c.Height == m.Height }));
        RefreshRates = new(modes.Where(m => cur is { } c && m.Width == c.Width && m.Height == c.Height)
            .DistinctBy(m => m.Hz).OrderBy(m => m.Hz)
            .Select(m => new ChoiceItem($"{m.Hz}Hz", m) { IsSelected = cur?.Hz == m.Hz }));
    }

    [RelayCommand]
    private void ApplyDisplay(ChoiceItem item)
    {
        var mode = (DisplayControls.DisplayMode)item.Value;
        // 해상도를 고르면 그 해상도의 가장 높은 주사율로
        if (Resolutions.Contains(item))
            mode = _modes.GetAwaiter().GetResult().Where(m => m.Width == mode.Width && m.Height == mode.Height).MaxBy(m => m.Hz);
        if (DisplayControls.Apply(mode) is { } err) _shell.ShowToast(err);
        LoadDisplayModes();
        _shell.RequestFocus();
    }

    // ── Windows 설정 (데스크톱 모드로 나가서 연다) ─────────

    [RelayCommand]
    private void OpenWindows(string page)
    {
        SystemControls.OpenWindowsSettings(page);
        _shell.GoToDesktopCommand.Execute(null);
        _shell.ShowToast("왼쪽 MSI 버튼을 누르면 ClawHub로 돌아와요");
    }

    // ── 라이브러리 ─────────────────────────────────────────

    [RelayCommand]
    private void AddGame() => AppPickerViewModel.AddFromFile(_shell);

    [RelayCommand]
    private void OpenAppPicker() => _shell.OpenAppPickerCommand.Execute(null);

    [RelayCommand]
    private void Rescan()
    {
        _shell.Library.Refresh();
        _shell.Home.Reload();
        _shell.ShowToast($"게임 {_shell.Library.Visible.Count()}개를 찾았어요");
    }

    [RelayCommand]
    private void UnhideAll()
    {
        var n = _shell.Library.UnhideAll();
        _shell.Home.Reload();
        _shell.ShowToast(n > 0 ? $"숨긴 게임 {n}개를 다시 표시해요" : "숨긴 게임이 없어요");
    }

    // ── 정보 ───────────────────────────────────────────────

    public string AppVersion => typeof(SettingsViewModel).Assembly.GetName().Version?.ToString(3) ?? "?";
    public string ServiceText => _shell.ServiceConnected ? "실행 중" : "연결 안 됨 — scripts\\install.ps1로 설치하세요";

    public string ControllerText => _shell.Status is { ControllerConnected: true } s
        ? $"펌웨어 {s.ControllerFirmware} · {s.ControllerMode} 모드{(s.MappingActive ? " · 게임 매핑 적용 중" : "")}"
        : "연결 안 됨";

    public string EcText => _shell.Status is { EcAvailable: true } s
        ? $"{s.EcVersion} · {(s.EcWritable ? "제어 가능" : "읽기 전용")}"
        : "사용 불가";

    public string BiosText => _shell.Status?.BiosVersion ?? "?";
    public string Warnings => _shell.Status?.Warnings is { Count: > 0 } w ? string.Join("\n", w) : "";
}
