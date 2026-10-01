using System.Collections.ObjectModel;
using System.Windows;
using System.Windows.Threading;
using ClawApp.Services;
using ClawCore.Controller;
using ClawCore.Ec;
using ClawCore.Games;
using ClawCore.Ipc;
using ClawCore.Mapping;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;

namespace ClawApp.ViewModels;

/// <summary>
/// 셸 전체 상태: 페이지 이동, 메뉴/퀵 설정 패널, 창 표시, 게임 세션, 컨트롤러 상태 동기화.
/// </summary>
public sealed partial class ShellViewModel : ObservableObject
{
    public static ShellViewModel Instance { get; private set; } = null!;

    public ServiceClient Service { get; } = new();
    public GameLibrary Library { get; } = new();
    public ProfileStore Profiles { get; } = new();
    public AppSettings Settings { get; } = AppSettings.Load();
    public QuickAccessViewModel QuickAccess { get; }
    public HomeViewModel Home { get; }

    private readonly Stack<INavPage> _back = new();
    private readonly SemaphoreSlim _syncLock = new(1, 1);
    private readonly DispatcherTimer _clockTimer;
    private DispatcherTimer? _toastTimer;
    private IntPtr _returnWindow;
    private bool _inDesktop;
    private string? _appliedProfileId;
    private GamepadMode? _appliedMode;
    private PerformanceMode? _appliedPerf;

    [ObservableProperty] private INavPage? _currentPage;
    [ObservableProperty] private bool _isMainMenuOpen;
    [ObservableProperty] private bool _isQuickAccessOpen;
    /// <summary>창 자체가 보이는지 (게임/데스크톱 중이면 false).</summary>
    [ObservableProperty] private bool _isWindowVisible = true;
    /// <summary>셸 화면(배경+페이지)이 보이는지. false면 게임 위에 패널만 띄운 오버레이 상태.</summary>
    [ObservableProperty] private bool _isShellVisible = true;
    [ObservableProperty] private bool _serviceConnected;
    [ObservableProperty] private ServiceStatus? _status;
    [ObservableProperty] private string _clock = "";
    [ObservableProperty] private string _batteryText = "";
    [ObservableProperty] private string? _toast;
    [ObservableProperty] private string? _busyText;
    [ObservableProperty] private GameEntry? _runningGame;
    [ObservableProperty] private ObservableCollection<Hint> _hints = [];

    /// <summary>뷰에 "지금 포커스를 다시 잡아라"를 알림 (페이지 변경, 패널 열림 등).</summary>
    public event Action? FocusRequested;

    public void RequestFocus() => FocusRequested?.Invoke();

    public GameSession? Session { get; private set; }
    public bool IsAnyPanelOpen => IsMainMenuOpen || IsQuickAccessOpen;

    public ShellViewModel()
    {
        Instance = this;
        Library.Refresh();
        QuickAccess = new QuickAccessViewModel(this);
        Home = new HomeViewModel(this);
        CurrentPage = Home;

        Service.ConnectionChanged += c => Ui(async () =>
        {
            ServiceConnected = c;
            if (!c) return;
            _appliedProfileId = null;
            _appliedMode = null;
            _appliedPerf = null;
            await RefreshStatusAsync();
            await SyncControllerAsync();
        });
        Service.EventReceived += e => Ui(() =>
        {
            if (e.Event == Ops.EventButton && e.Button is { } b) OnMsiButton(b);
            else if (e.Event == Ops.EventStatus && e.Status is { } s) Status = s;
        });
        Service.Start();

        _clockTimer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(10) };
        _clockTimer.Tick += (_, _) => UpdateClock();
        _clockTimer.Start();
        UpdateClock();
        UpdateHints();

        // Xbox 모드(동반 모드): 홈 화면은 Xbox 앱에 맡기고 숨어서 시작
        IsCompanion = Settings.ResolveCompanion();
        if (IsCompanion)
        {
            IsWindowVisible = false;
            IsShellVisible = false;
        }

        // 어디서 실행했든(Xbox 앱, 넥슨 런처, 바탕화면) 라이브러리 게임이 뜨면 붙어서 매핑을 적용
        _watchTimer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(2) };
        _watchTimer.Tick += async (_, _) => await WatchExternalGamesAsync();
        _watchTimer.Start();
    }

    /// <summary>Xbox 모드 등 다른 홈 화면 위에서 동작 중인지.</summary>
    [ObservableProperty] private bool _isCompanion;

    private readonly DispatcherTimer _watchTimer;
    private bool _watching;

    private async Task WatchExternalGamesAsync()
    {
        if (_watching || RunningGame is not null) return;
        _watching = true;
        try
        {
            var candidates = Library.Visible.ToList();
            var game = await Task.Run(() => GameSession.FindRunning(candidates));
            if (game is null || RunningGame is not null) return;

            var session = GameSession.Attach(game);
            session.Ended += s => Ui(() => OnGameEnded(s));
            Session = session;
            RunningGame = game;
            if (Profiles.Find(game.MappingProfileId) is { } p && !IsWindowVisible)
                ShowToast($"{game.Title} 감지 — '{p.Name}' 매핑 적용");
            await SyncControllerAsync();
        }
        finally
        {
            _watching = false;
        }
    }

    [RelayCommand]
    private void OpenXboxApp()
    {
        try
        {
            System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo("explorer.exe", AppSettings.XboxAppId));
        }
        catch (Exception ex)
        {
            ShowToast($"Xbox 앱을 열 수 없습니다: {ex.Message}");
            return;
        }
        _inDesktop = false;
        _returnWindow = IntPtr.Zero;
        ReturnToBackground();
    }

    private static void Ui(Action a) => Application.Current.Dispatcher.BeginInvoke(a);
    private static void Ui(Func<Task> a) => Application.Current.Dispatcher.BeginInvoke(a);

    // ── 페이지 이동 ─────────────────────────────────────────

    public void Navigate(INavPage page)
    {
        if (CurrentPage is { } cur) { cur.OnHidden(); _back.Push(cur); }
        CurrentPage = page;
        page.OnShown();
        CloseMenus();
        UpdateHints();
        FocusRequested?.Invoke();
    }

    public bool GoBack()
    {
        if (_back.Count == 0) return false;
        CurrentPage?.OnHidden();
        CurrentPage = _back.Pop();
        CurrentPage.OnShown();
        UpdateHints();
        FocusRequested?.Invoke();
        return true;
    }

    [RelayCommand]
    public void GoHome()
    {
        CurrentPage?.OnHidden();
        _back.Clear();
        CurrentPage = Home;
        Home.OnShown();
        IsShellVisible = true;
        CloseMenus();
        UpdateHints();
        _ = SyncControllerAsync();
        FocusRequested?.Invoke();
    }

    [RelayCommand] private void OpenLibrary() { IsShellVisible = true; Navigate(new LibraryViewModel(this)); }
    [RelayCommand] private void OpenProfiles() { IsShellVisible = true; Navigate(new ProfilesViewModel(this)); }
    [RelayCommand] private void OpenSettings() { IsShellVisible = true; Navigate(new SettingsViewModel(this)); }

    /// <summary>퀵 설정(게임 위 오버레이 포함)에서 전체 설정으로. 셸을 띄우고 매핑은 풀린다.</summary>
    public void OpenSettingsEverywhere()
    {
        IsShellVisible = true;
        IsWindowVisible = true;
        OpenSettings();
        _ = SyncControllerAsync();
    }

    public void UpdateHints()
    {
        IReadOnlyList<Hint> hints = IsQuickAccessOpen || IsMainMenuOpen
            ? [new("A", "선택"), new("B", "닫기")]
            : CurrentPage?.Hints ?? [];
        Hints = new ObservableCollection<Hint>(hints);
    }

    // ── 패널 ───────────────────────────────────────────────

    public void ToggleMainMenu()
    {
        IsQuickAccessOpen = false;
        IsMainMenuOpen = !IsMainMenuOpen;
        AfterPanelChange();
    }

    public void ToggleQuickAccess()
    {
        IsMainMenuOpen = false;
        IsQuickAccessOpen = !IsQuickAccessOpen;
        if (IsQuickAccessOpen) QuickAccess.Refresh();
        AfterPanelChange();
    }

    /// <summary>패널을 닫는다. 게임/데스크톱 위 오버레이였으면 창을 숨기고 원래 창으로 돌려보낸다.</summary>
    [RelayCommand]
    public void ClosePanels()
    {
        CloseMenus();
        if (!IsShellVisible) ReturnToBackground();
        else AfterPanelChange();
    }

    private void CloseMenus()
    {
        IsMainMenuOpen = false;
        IsQuickAccessOpen = false;
    }

    private void AfterPanelChange()
    {
        OnPropertyChanged(nameof(IsAnyPanelOpen));
        UpdateHints();
        FocusRequested?.Invoke();
    }

    // ── 본체 전용 버튼 ──────────────────────────────────────

    // ── 아무 앱에나 매핑 ────────────────────────────────────

    private static readonly HashSet<string> NotApps = new(StringComparer.OrdinalIgnoreCase)
    {
        "ClawApp", "explorer", "XboxPcApp", "ApplicationFrameHost", "ShellExperienceHost", "SearchHost",
        "StartMenuExperienceHost", "TextInputHost", "LockApp", "GameBar", "Idle",
    };

    /// <summary>오버레이를 띄우기 직전에 앞에 있던 앱 (라이브러리 밖 게임에 프로필을 붙일 때 사용).</summary>
    public (string Name, string? Path)? ForegroundApp { get; private set; }

    private void CaptureForegroundApp(IntPtr hwnd)
    {
        ForegroundApp = null;
        if (hwnd == IntPtr.Zero) return;
        GetWindowThreadProcessId(hwnd, out var pid);
        try
        {
            using var p = System.Diagnostics.Process.GetProcessById((int)pid);
            if (NotApps.Contains(p.ProcessName)) return;
            var path = GameSession.GetPath(p);
            if (path?.Contains(@"\Microsoft.GamingApp_", StringComparison.OrdinalIgnoreCase) == true) return;
            ForegroundApp = (p.ProcessName, path);
        }
        catch (Exception) { /* 이미 종료됨 */ }
    }

    /// <summary>퀵 설정에서 A를 누를 때마다: 실행 중 게임(또는 앞에 있던 앱)의 프로필을 다음 것으로.</summary>
    public void CycleTargetProfile()
    {
        var all = Profiles.All;
        string? Next(string? current)
        {
            var i = all.ToList().FindIndex(p => p.Id == current);
            return i + 1 < all.Count ? all[i + 1].Id : null;
        }

        if (RunningGame is { } game)
        {
            Library.SetProfile(game, Next(game.MappingProfileId));
        }
        else if (ForegroundApp is { } app)
        {
            var bound = Library.BindProcess(app.Name, app.Path, Next(null));
            var session = GameSession.Attach(bound);
            session.Ended += s => Ui(() => OnGameEnded(s));
            Session = session;
            RunningGame = bound;
            ForegroundApp = null;
            Home.Reload();
        }
        QuickAccess.Refresh(); // 패널을 닫으면 SyncControllerAsync가 새 프로필을 적용
    }

    [System.Runtime.InteropServices.DllImport("user32.dll")]
    private static extern uint GetWindowThreadProcessId(IntPtr hwnd, out uint pid);

    public void OnMsiButton(MsiButton button)
    {
        if (!IsWindowVisible)
        {
            _returnWindow = SystemControls.ForegroundWindow();
            CaptureForegroundApp(_returnWindow);
            var fullShell = RunningGame is null && button == MsiButton.MainMenu;
            IsShellVisible = fullShell;
            _inDesktop = false;
            IsWindowVisible = true;
            CloseMenus();
            if (!fullShell)
            {
                if (button == MsiButton.MainMenu) IsMainMenuOpen = true;
                else { IsQuickAccessOpen = true; QuickAccess.Refresh(); }
            }
            AfterPanelChange();
            _ = SyncControllerAsync();
            return;
        }

        if (button == MsiButton.MainMenu) ToggleMainMenu(); else ToggleQuickAccess();
        if (!IsAnyPanelOpen && !IsShellVisible) ReturnToBackground();
    }

    /// <summary>창을 숨기고 게임(또는 데스크톱)으로 돌아간다.</summary>
    [RelayCommand]
    public void ReturnToBackground()
    {
        CloseMenus();
        OnPropertyChanged(nameof(IsAnyPanelOpen));
        IsWindowVisible = false;
        IsShellVisible = false;
        var target = _returnWindow;
        if (RunningGame is not null && Session?.FindProcesses().FirstOrDefault(p => p.MainWindowHandle != IntPtr.Zero) is { } p)
            target = p.MainWindowHandle;
        SystemControls.Activate(target);
        _ = SyncControllerAsync();
    }

    [RelayCommand]
    private void GoToDesktop()
    {
        _inDesktop = true;
        _returnWindow = IntPtr.Zero;
        ReturnToBackground();
    }

    // ── 게임 실행 ──────────────────────────────────────────

    public async Task LaunchAsync(GameEntry game)
    {
        if (RunningGame is not null)
        {
            ShowToast($"{RunningGame.Title}이(가) 이미 실행 중입니다");
            return;
        }

        BusyText = $"{game.Title} 시작 중…";
        try
        {
            Session = GameSession.Launch(game);
        }
        catch (Exception ex)
        {
            BusyText = null;
            ShowToast($"실행 실패: {ex.Message}");
            return;
        }

        RunningGame = game;
        Library.MarkPlayed(game);
        Home.Reload();
        Session.Ended += s => Ui(() => OnGameEnded(s));

        await Task.Delay(800);
        BusyText = null;
        _returnWindow = IntPtr.Zero;
        ReturnToBackground(); // 숨기면 SyncControllerAsync가 매핑을 적용
    }

    private void OnGameEnded(GameSession s)
    {
        if (Session != s) return;
        s.Dispose();
        Session = null;
        RunningGame = null;

        // ClawHub에서 실행한 게임이면 셸로 돌아오고, 외부(Xbox 앱 등)에서 실행한 게임이면 그쪽 홈에 맡긴다
        if (s.LaunchedByUs)
        {
            IsShellVisible = true;
            IsWindowVisible = true;
            CloseMenus();
            AfterPanelChange();
        }
        else if (!IsShellVisible)
        {
            CloseMenus();
            IsWindowVisible = false;
            OnPropertyChanged(nameof(IsAnyPanelOpen));
        }
        _ = SyncControllerAsync();
    }

    [RelayCommand]
    private void QuitGame()
    {
        Session?.Kill();
        CloseMenus();
        ShowToast("게임을 종료하는 중…");
    }

    // ── 컨트롤러 상태 동기화 ────────────────────────────────

    /// <summary>
    /// 현재 화면 상태에서 컨트롤러가 있어야 할 상태를 계산해 맞춘다.
    /// 게임 중이고 셸이 숨겨져 있을 때만 게임 매핑, 그 외에는 원래 상태(패드).
    /// </summary>
    public async Task SyncControllerAsync()
    {
        if (!ServiceConnected) return;
        await _syncLock.WaitAsync();
        try
        {
            MappingProfile? profile = RunningGame is not null && !IsWindowVisible ? Profiles.Find(RunningGame.MappingProfileId) : null;

            // Xbox 모드(동반)는 항상 패드 조작이므로 XInput, "데스크톱으로"를 고른 경우만 Desktop
            GamepadMode? mode = profile?.Mode
                ?? (!Settings.AutoControllerMode ? null
                    : IsWindowVisible || RunningGame is not null ? GamepadMode.XInput
                    : _inDesktop ? GamepadMode.Desktop
                    : IsCompanion ? GamepadMode.XInput : null);

            if (profile?.Id != _appliedProfileId)
            {
                var resp = profile is null
                    ? await Service.SendAsync(new Request { Op = Ops.RestoreMapping })
                    : await Service.SendAsync(new Request { Op = Ops.ApplyMapping, Bindings = profile.ToCodes().ToDictionary() });
                if (!resp.Ok) { ShowToast($"매핑 적용 실패: {resp.Error}"); return; }
                _appliedProfileId = profile?.Id;
                if (resp.Status is { } st) Status = st;
            }

            if (mode is { } m && m != (_appliedMode ?? Status?.ControllerMode))
            {
                var resp = await Service.SendAsync(new Request { Op = Ops.SetControllerMode, ControllerMode = m });
                if (!resp.Ok) { ShowToast($"컨트롤러 모드 전환 실패: {resp.Error}"); return; }
                _appliedMode = m;
            }

            // 게임별 성능 모드: 게임이 도는 동안만 적용, 끝나면 사용자 기본값으로
            var perf = RunningGame?.PerformanceMode;
            if (perf != _appliedPerf && Status?.EcWritable == true)
            {
                var resp = perf is { } pm
                    ? await Service.SendAsync(new Request
                    {
                        Op = Ops.ApplyTemporaryPerformance,
                        Performance = new PerformanceState(pm, PerformanceManager.Preset(pm).Pl1, PerformanceManager.Preset(pm).Pl2,
                            Status.Performance?.Fan ?? FanMode.Auto),
                    })
                    : await Service.SendAsync(new Request { Op = Ops.RestorePerformance });
                if (resp.Ok) _appliedPerf = perf;
                if (resp.Status is { } st) Status = st;
            }
        }
        catch (Exception ex)
        {
            ShowToast($"서비스 통신 오류: {ex.Message}");
        }
        finally
        {
            _syncLock.Release();
        }
    }

    /// <summary>편집 중인 매핑을 잠깐 실제로 적용해 본다. 시간이 지나면 자동으로 원래대로.</summary>
    public async Task TestMappingAsync(MappingProfile profile, TimeSpan duration)
    {
        if (!await SendAsync(new Request { Op = Ops.ApplyMapping, Bindings = profile.ToCodes().ToDictionary() })) return;
        _appliedProfileId = "__test__"; // 다음 동기화 때 반드시 복원되도록
        ShowToast($"{duration.TotalSeconds:0}초 동안 테스트 중 — 메모장 등에서 눌러 보세요");
        await Task.Delay(duration);
        await SyncControllerAsync();
        ShowToast("테스트 끝 — 원래 버튼으로 돌아왔습니다");
    }

    public async Task RefreshStatusAsync()
    {
        if (!ServiceConnected) return;
        try
        {
            var resp = await Service.SendAsync(new Request { Op = Ops.GetStatus });
            if (resp.Status is { } s) Status = s;
        }
        catch (Exception) { /* 연결 끊김은 ConnectionChanged가 처리 */ }
    }

    public async Task<bool> SendAsync(Request request)
    {
        if (!ServiceConnected) { ShowToast("ClawHub 서비스가 실행되고 있지 않습니다"); return false; }
        try
        {
            var resp = await Service.SendAsync(request);
            if (resp.Status is { } s) Status = s;
            if (!resp.Ok) ShowToast(resp.Error ?? "실패");
            return resp.Ok;
        }
        catch (Exception ex)
        {
            ShowToast(ex.Message);
            return false;
        }
    }

    // ── 기타 ───────────────────────────────────────────────

    public void ShowToast(string message)
    {
        Toast = message;
        _toastTimer?.Stop();
        _toastTimer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(3) };
        _toastTimer.Tick += (_, _) => { Toast = null; _toastTimer?.Stop(); };
        _toastTimer.Start();
    }

    private void UpdateClock()
    {
        Clock = DateTime.Now.ToString("tt h:mm");
        var b = SystemControls.GetBattery();
        BatteryText = b.Percent is { } p ? $"{(b.Charging ? "⚡" : "")}{p}%" : "";
    }

    private string? _pendingPower;
    private DateTime _pendingPowerAt;

    /// <summary>실수로 A를 눌러 꺼지지 않도록 3초 안에 한 번 더 눌러야 실행.</summary>
    private bool ConfirmTwice(string action, string label)
    {
        if (_pendingPower == action && DateTime.UtcNow - _pendingPowerAt < TimeSpan.FromSeconds(3))
        {
            _pendingPower = null;
            return true;
        }
        _pendingPower = action;
        _pendingPowerAt = DateTime.UtcNow;
        ShowToast($"한 번 더 누르면 {label}");
        return false;
    }

    [RelayCommand] private static void Sleep() => SystemControls.Sleep();
    [RelayCommand] private void Restart() { if (ConfirmTwice("restart", "다시 시작합니다")) SystemControls.Restart(); }
    [RelayCommand] private void Shutdown() { if (ConfirmTwice("shutdown", "시스템을 종료합니다")) SystemControls.Shutdown(); }

    [RelayCommand]
    private async Task ExitApp()
    {
        if (!ConfirmTwice("exit", "ClawHub를 종료합니다")) return;
        if (ServiceConnected)
        {
            try { await Service.SendAsync(new Request { Op = Ops.RestoreMapping }, TimeSpan.FromSeconds(3)); }
            catch (Exception) { /* 종료 중 */ }
        }
        Application.Current.Shutdown();
    }

    partial void OnCurrentPageChanged(INavPage? value) => OnPropertyChanged(nameof(PageTitle));
    partial void OnIsMainMenuOpenChanged(bool value) => OnPropertyChanged(nameof(IsAnyPanelOpen));
    partial void OnIsQuickAccessOpenChanged(bool value) => OnPropertyChanged(nameof(IsAnyPanelOpen));
    public void OnPropertyChangedPageTitle() => OnPropertyChanged(nameof(PageTitle));
    public string PageTitle => CurrentPage?.Title ?? "";
}
