using System.Collections.ObjectModel;
using System.Windows;
using System.Windows.Threading;
using ClawApp.Services;
using ClawCore.Controller;
using Gpu = ClawCore.Gpu.IntelGraphics;
using ClawCore.Ec;
using ClawCore.Games;
using ClawCore.Ipc;
using ClawCore.Mapping;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;

namespace ClawApp.ViewModels;

/// <summary>
/// 셸 전체 상태: 페이지 이동, 창 표시, 게임 세션, 컨트롤러 상태 동기화.
/// 본체 버튼은 Windows Xbox 환경으로 넘긴다 — 왼쪽 = 홈(ClawHub 런처, Xbox 모드면 Xbox 앱),
/// 오른쪽 = Game Bar의 ClawHub 위젯. 게임 위에 자체 오버레이는 띄우지 않는다.
/// </summary>
public sealed partial class ShellViewModel : ObservableObject
{
    public static ShellViewModel Instance { get; private set; } = null!;

    public ServiceClient Service { get; } = new();
    /// <summary>게임 라이브러리. 시작할 때는 App.OnStartup이 뒤에서 미리 검색해 둔 것을 받는다.</summary>
    public GameLibrary Library { get; } = StartupPrewarm.TakeLibrary();
    public ProfileStore Profiles { get; } = new();
    public AppSettings Settings { get; } = AppSettings.Load();
    public QuickAccessViewModel QuickAccess { get; }
    public HomeViewModel Home { get; }
    /// <summary>시작 화면 배치 (고정·순서·크기·폴더).</summary>
    public StartScreen Start { get; }

    private readonly Stack<INavPage> _back = new();
    private readonly SemaphoreSlim _syncLock = new(1, 1);
    private readonly DispatcherTimer _clockTimer;
    private DispatcherTimer? _toastTimer;
    private IntPtr _returnWindow;
    private bool _inDesktop;
    /// <summary>"데스크톱으로" 중에도 컨트롤러를 게임패드(XInput)로 둔다 (스틱 = 마우스 대신).</summary>
    private bool _desktopGamepad;
    private string? _appliedProfileId;
    private GamepadMode? _appliedMode;
    private PerformanceMode? _appliedPerf;
    private int? _appliedFps;
    /// <summary>
    /// ClawHub가 손대기 전 드라이버의 FPS 제한 (사용자가 Intel 앱에서 정한 값 보존).
    /// 드라이버 호출(DLL 로드 + 초기화 ~15–30ms)은 시작을 막지 않게 뒤에서 읽고, FPS를 바꾸기 전에 반드시 기다린다.
    /// </summary>
    private readonly Task<int?> _fpsBaselineRead = Task.Run(Gpu.GetFrameLimit);

    private int? FpsBaseline => _fpsBaselineRead.GetAwaiter().GetResult();

    /// <summary>빠른 설정에서 기본 FPS 제한을 바꿈 → 저장하고 바로 적용 (게임별 값이 있으면 그게 우선).</summary>
    public async Task SetDefaultFpsAsync(int fps)
    {
        // 배터리 사용 중이고 배터리 전용 기본값이 있으면 그쪽을 바꾼다 (지금 적용 중인 값)
        if (OnBattery && Settings.FpsLimitOnBattery is not null) Settings.FpsLimitOnBattery = fps;
        else Settings.FpsLimit = fps;
        Settings.Save();
        await SyncControllerAsync();
    }

    /// <summary>설정상 FPS 제한 (런처가 보이는 동안 잠깐 풀어 둔 값이 아니라 게임에 걸리는 값).</summary>
    public int CurrentFps => EffectiveFps() ?? FpsBaseline ?? 0;

    /// <summary>FPS 제한: 게임별 값 > 사용자 기본값 > ClawHub 시작 때 드라이버 값 (배터리 전용 값은 EffectiveFps 참고).</summary>
    private async Task SyncFpsAsync()
    {
        // 드라이버 FPS 제한은 모든 앱에 걸린다 — 런처가 앞에 있는 동안은 풀어서 ClawHub 화면이 끊기지 않게,
        // 게임(또는 데스크톱)으로 돌아가면 다시 건다
        await _fpsBaselineRead; // 원래 값을 읽기 전에 드라이버 값을 바꾸지 않는다
        var fps = IsWindowVisible ? 0 : EffectiveFps() ?? FpsBaseline;
        if (fps is not { } f || f == _appliedFps || !Gpu.Available) return;
        if (await Task.Run(() => Gpu.SetFrameLimit(f))) _appliedFps = f;
    }

    [ObservableProperty] private INavPage? _currentPage;
    /// <summary>
    /// 앱 안 빠른 설정 패널. Game Bar 위젯이 없을 때만, 런처가 보이는 동안에만 쓰는 대체 경로.
    /// </summary>
    [ObservableProperty] private bool _isQuickAccessOpen;
    /// <summary>창 자체가 보이는지 (게임/데스크톱/Xbox 앱 중이면 false).</summary>
    [ObservableProperty] private bool _isWindowVisible = true;
    /// <summary>셸 화면(배경+페이지)이 보이는지. 이제 게임 위 오버레이가 없으므로 창이 보이면 항상 true.</summary>
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
    public bool IsAnyPanelOpen => IsQuickAccessOpen || StatusPanel is not null;

    /// <summary>
    /// 왼쪽 메인 메뉴 패널은 없어졌다 (홈 화면이 그 역할). 예전 XAML 바인딩이 남아 있어도
    /// 바인딩 실패로 패널이 보이는 일이 없도록 항상 false를 돌려준다.
    /// </summary>
    public bool IsMainMenuOpen => false;

    // ── 홈의 "지금 플레이 중" 타일용 ───────────────────────────

    public bool IsGameRunning => RunningGame is not null;
    public string? RunningGameTitle => RunningGame?.Title;
    /// <summary>실행 중 게임의 아트(커버·히어로·로고). 게임이 없으면 null.</summary>
    [ObservableProperty] private GameTileViewModel? _runningTile;

    partial void OnRunningGameChanged(GameEntry? value)
    {
        RunningTile = value is null ? null : new GameTileViewModel(this, value);
        OnPropertyChanged(nameof(IsGameRunning));
        OnPropertyChanged(nameof(RunningGameTitle));
        UpdateHints(); // 홈 힌트에 "B 게임으로"가 붙었다 떨어진다
        _ = PushAppStateAsync();
    }

    // ── Game Bar 위젯 연동 (서비스가 중계) ─────────────────

    /// <summary>
    /// 서비스에 마지막으로 맡긴 상태 (JSON). 같으면 다시 보내지 않는다 — 요청마다 서비스가 전체 상태(컨트롤러 HID·EC WMI·
    /// 드라이버 조회)를 만들어 돌려주므로, 게임 중 10초마다 같은 값을 보내면 그만큼 배터리를 쓴다.
    /// 재연결·서비스가 상태를 비웠을 때(세션 전환 등)는 null로 돌려 다시 보낸다.
    /// </summary>
    private string? _sentAppState;

    /// <summary>위젯이 보여 줄 현재 게임·매핑 정보를 서비스에 맡긴다.</summary>
    public async Task PushAppStateAsync()
    {
        if (!ServiceConnected) return;
        var game = RunningGame;
        var profile = Profiles.Find(game?.MappingProfileId);
        var state = new AppState
        {
            RunningGameTitle = game?.Title ?? ForegroundApp?.Name,
            ProfileId = profile?.Id,
            ProfileName = profile?.Name,
            Profiles = [.. Profiles.All.Select(p => new ProfileItem { Id = p.Id, Name = p.Name })],
            GamePerformanceMode = game?.PerformanceMode?.ToString(),
            GameFpsLimit = game?.FpsLimit,
            GamePerformanceModeOnBattery = game?.PerformanceModeOnBattery?.ToString(),
            GameFpsLimitOnBattery = game?.FpsLimitOnBattery,
            OnBattery = OnBattery,
            RunningGamePid = game is null ? null : await Task.Run(() => GameProcessPicker.Pick(Session)), // FPS 측정 대상
        };
        var json = System.Text.Json.JsonSerializer.Serialize(state, ClawCore.JsonStore.Options);
        if (json == _sentAppState) return; // 바뀐 것 없음
        try
        {
            var resp = await Service.SendAsync(new Request { Op = Ops.SetAppState, AppState = state }, TimeSpan.FromSeconds(3));
            if (resp.Ok) _sentAppState = json;
        }
        catch (Exception) { /* 연결 끊김 — 재연결 때 다시 보냄 */ }
    }

    /// <summary>서비스가 앱 상태를 잃었으면(서비스 재시작·세션 전환·앱 연결 끊김 처리) 다음에 반드시 다시 보낸다.</summary>
    private void CheckServiceAppState(ServiceStatus? status)
    {
        if (status is not { AppState: null } || _sentAppState is null || !ServiceConnected) return;
        _sentAppState = null;
        _ = PushAppStateAsync();
    }

    /// <summary>위젯에서 온 명령. 위젯이 이미 확인을 받았으므로 여기서는 바로 실행한다.</summary>
    private void OnWidgetCommand(string command)
    {
        switch (command)
        {
            case AppCommands.CycleProfile:
                CycleTargetProfile();
                break;
            case AppCommands.QuitGame:
                if (Session is { } quitting) KillInBackground(quitting);
                ShowToast("게임을 종료하는 중…");
                break;
            case AppCommands.ShowClawHub:
                ShowFromOutside();
                break;
            case AppCommands.Screenshot:
                _ = CaptureAsync(CaptureAction.Screenshot);
                break;
            case AppCommands.ToggleRecording:
                _ = CaptureAsync(CaptureAction.ToggleRecording);
                break;
            case AppCommands.SaveClip:
                _ = CaptureAsync(CaptureAction.SaveClip);
                break;
            case var c when c.StartsWith(AppCommands.SetProfilePrefix, StringComparison.Ordinal):
                var id = c[AppCommands.SetProfilePrefix.Length..];
                var profileId = string.IsNullOrEmpty(id) ? null : Profiles.Find(id)?.Id;
                if (RunningGame is { } g) Library.SetProfile(g, profileId);
                else if (ForegroundApp is { } app && profileId is not null) BindForegroundApp(app, profileId);
                break;
        }
        _ = PushAppStateAsync();
        _ = SyncControllerAsync(); // 프로필이 바뀌었으면 매핑 다시 적용
    }

    public ShellViewModel()
    {
        Instance = this;
        // Library는 이미 검색돼 있다 (StartupPrewarm.TakeLibrary)
        QuickAccess = new QuickAccessViewModel(this);
        Start = new StartScreen(Settings);
        Home = new HomeViewModel(this);
        InitBackdrop();
        CurrentPage = Home;

        Service.ConnectionChanged += c => Ui(async () =>
        {
            ServiceConnected = c;
            _sentAppState = null; // 새 연결(서비스 재시작 포함)에는 상태를 다시 맡긴다
            if (!c) return;
            _appliedProfileId = null;
            _appliedMode = null;
            _appliedPerf = null;
            await RefreshStatusAsync();
            // 서비스의 실제 상태로 장부를 맞춘다 (앱이 재시작돼도 남은 매핑/게임별 성능을 반드시 정리하도록)
            if (Status?.MappingActive == true) _appliedProfileId = "__unknown__";
            _appliedPerf = Status?.TemporaryPerformance?.Mode;
            await SyncControllerAsync();
            await PushAppStateAsync(); // 위젯 상태 (예전에는 다음 10초 틱까지 비어 있었다)
        });
        Service.EventReceived += e => Ui(() =>
        {
            if (e.Event == Ops.EventButton && e.Button is { } b) OnMsiButton(b);
            else if (e.Event == Ops.EventStatus && e.Status is { } s) { Status = s; ReconcileWithStatus(s); }
            else if (e.Event == Ops.EventAppCommand && e.Command is { } c) OnWidgetCommand(c);
        });
        Service.Start();
        InitPower();

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

        _ = FetchArtAsync();

        // 어디서 실행했든(Xbox 앱, 넥슨 런처, 바탕화면) 라이브러리 게임이 뜨면 붙어서 매핑을 적용
        _watchTimer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(2) };
        _watchTimer.Tick += async (_, _) => await WatchExternalGamesAsync();
        _watchTimer.Start();

        _handoffTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(400) };
        _handoffTimer.Tick += (_, _) => OnHandoffTick();
    }

    /// <summary>커버·배경·로고가 없는 게임의 아트를 받아 오고, 받았으면 화면을 새로 그린다.</summary>
    public async Task FetchArtAsync()
    {
        var got = await Task.Run(() => Library.FetchArtAsync());
        if (got > 0)
        {
            Home.Reload();
            if (CurrentPage is LibraryViewModel lib) lib.Refresh();
        }
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
            var candidates = Library.Visible.Where(g => !g.IsApp).ToList(); // 앱은 "실행 중 게임"으로 잡지 않는다
            // 앞에 떠 있는 창의 프로세스만 본다: 트레이에 상주하는 프로그램(메신저 등)을 라이브러리에 넣어도 "실행 중 게임"이 되지 않게
            if (SystemControls.ForegroundProcessId() is not { } fg || fg == Environment.ProcessId) return;
            var game = await Task.Run(() => GameSession.FindRunning(candidates, fg));
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
    private void OpenXboxApp() => OpenXboxHome();

    /// <summary>
    /// Xbox 앱 홈을 앞으로. ClawHub 창은 숨기되 게임 창을 다시 앞으로 올리지는 않는다
    /// (Xbox 앱이 앞에 있어야 하므로). Xbox 앱이 앞에 있는 동안은 게임 매핑을 풀어 둔다.
    /// </summary>
    private bool OpenXboxHome()
    {
        if (!XboxIntegration.TryOpenXboxApp(out var error))
        {
            ShowToast($"Xbox 앱을 열 수 없어요:{error}");
            return false;
        }
        _inDesktop = false;
        _returnWindow = IntPtr.Zero;
        HideWindow();
        BeginHandoff(Handoff.XboxApp); // 매핑 동기화 포함
        return true;
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
    /// <summary>라이브러리에 프로그램 추가 (시작 메뉴 목록).</summary>
    [RelayCommand] private void OpenAppPicker() { IsShellVisible = true; Navigate(new AppPickerViewModel(this)); }

    /// <summary>앱 안 빠른 설정 패널에서 전체 설정으로. 셸을 띄우고 매핑은 풀린다.</summary>
    public void OpenSettingsEverywhere()
    {
        IsShellVisible = true;
        IsWindowVisible = true;
        OpenSettings();
        _ = SyncControllerAsync();
    }

    public void UpdateHints()
    {
        IReadOnlyList<Hint> hints = IsConfirmOpen ? [new("A", "선택"), new("B", "취소")]
            : IsAnyPanelOpen ? [new("A", "선택"), new("B", "닫기")]
            : CurrentPage?.Hints ?? [];
        // 같은 힌트면 그대로 둔다 — 새 목록을 넣으면 하단 힌트(패드 그림 + 글자)를 전부 다시 만든다 (홈 재구성마다 불림)
        if (Hints.SequenceEqual(hints)) return;
        Hints = new ObservableCollection<Hint>(hints);
    }

    // ── 앱 안 빠른 설정 패널 (위젯이 없을 때의 대체 경로) ─────────

    /// <summary>패널을 닫는다. 패널은 런처 위에서만 열리므로 창은 그대로 둔다.</summary>
    [RelayCommand]
    public void ClosePanels()
    {
        CloseMenus();
        AfterPanelChange();
    }

    private void CloseMenus()
    {
        IsQuickAccessOpen = false;
        StatusPanel = null;
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
        // 항상 떠 있는 런처·메신저 — 게임 대신 이것에 프로필이 붙으면 게임처럼 추적·종료된다
        "steam", "steamwebhelper", "Discord", "EpicGamesLauncher", "EpicWebHelper", "Battle.net", "NexonPlay", "NGM", "NGM64",
        "LeagueClient", "LeagueClientUx", "RiotClientServices", "RiotClientUx", "EADesktop", "upc", "UbisoftConnect",
        "GalaxyClient", "Overwolf", "Taskmgr", "SystemSettings",
    };

    /// <summary>본체 버튼으로 런처를 띄우기 직전에 앞에 있던 앱 (라이브러리 밖 게임에 프로필을 붙일 때 사용).</summary>
    public (string Name, string? Path)? ForegroundApp { get; private set; }

    private void CaptureForegroundApp(IntPtr hwnd)
    {
        ForegroundApp = null;
        if (hwnd == IntPtr.Zero) return;
        GetWindowThreadProcessId(hwnd, out var pid);
        try
        {
            using var p = System.Diagnostics.Process.GetProcessById((int)pid);
            if (NotApps.Contains(p.ProcessName) || ProcessScanner.IsHelper(p.ProcessName)) return;
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
            BindForegroundApp(app, Next(null));
        }
        QuickAccess.Refresh(); // 패널을 닫으면 SyncControllerAsync가 새 프로필을 적용
    }

    /// <summary>라이브러리 밖 앱(Xbox 앱 게임 등)에 프로필을 붙이고 실행 중 게임으로 추적한다.</summary>
    private void BindForegroundApp((string Name, string? Path) app, string? profileId)
    {
        var bound = Library.BindProcess(app.Name, app.Path, profileId);
        var session = GameSession.Attach(bound);
        session.Ended += s => Ui(() => OnGameEnded(s));
        Session = session;
        RunningGame = bound;
        ForegroundApp = null;
        Home.Reload();
    }

    [System.Runtime.InteropServices.DllImport("user32.dll")]
    private static extern uint GetWindowThreadProcessId(IntPtr hwnd, out uint pid);

    /// <summary>다른 곳에서 ClawHub를 다시 실행했을 때 (Xbox 모드 홈, clawhub: 링크, 바로가기) — 셸을 앞으로.</summary>
    public void ShowFromOutside()
    {
        _inDesktop = false;
        CloseMenus();
        IsShellVisible = true;
        if (IsWindowVisible) RequestFocus();
        else IsWindowVisible = true;
        AfterPanelChange();
        _ = SyncControllerAsync();
    }

    /// <summary>화면의 빠른 설정 버튼(터치/마우스용) — 오른쪽 본체 버튼과 같다.</summary>
    [RelayCommand]
    private void OpenQuickSettings() => OnQuickSettingsButton();

    /// <summary>본체 버튼 (서비스 이벤트) 또는 패드 Menu/View, 키보드 F1/F2. 항상 UI 스레드에서 호출된다.</summary>
    public void OnMsiButton(MsiButton button)
    {
        CancelConfirm(); // 확인 대화 상자는 본체 버튼으로 빠져나간다
        if (button == MsiButton.MainMenu) OnHomeButton();
        else OnQuickSettingsButton();
    }

    /// <summary>
    /// 왼쪽 버튼 = Xbox 홈 버튼처럼 "홈으로".
    /// Xbox 모드(동반)면 Xbox 앱 홈, 아니면 ClawHub 런처 홈. 게임은 계속 돌고, 런처가 보이는 동안
    /// SyncControllerAsync가 매핑을 풀어 둔다. 게임 중 이미 홈이면 한 번 더 눌러 게임으로 돌아간다.
    /// </summary>
    private void OnHomeButton()
    {
        if (IsCompanion && OpenXboxHome()) return; // 실패하면 ClawHub 홈으로 대신

        if (!IsWindowVisible)
        {
            _returnWindow = SystemControls.ForegroundWindow();
            CaptureForegroundApp(_returnWindow);
            _inDesktop = false;
            CloseMenus();
            IsShellVisible = true;  // 창을 띄우기 전에 셸 상태로 (백드롭이 Mica로 시작)
            IsWindowVisible = true; // 여기서 창이 앞으로 (MainWindow.ApplyWindowVisibility)
            GoHome();               // 매핑 해제 포함
            return;
        }

        var atHome = CurrentPage == Home && _back.Count == 0;
        if (atHome && !IsQuickAccessOpen && RunningGame is not null)
        {
            ReturnToBackground();
            return;
        }
        GoHome();
    }

    /// <summary>
    /// 오른쪽 버튼 = Game Bar의 ClawHub 빠른 설정 위젯.
    /// 위젯이 없으면 런처가 보일 때만 앱 안 패널로, 런처가 숨어 있으면 Game Bar 자체(OEM 위젯 등)를 연다.
    /// </summary>
    private void OnQuickSettingsButton()
    {
        // 앱 안 패널이 열려 있으면 같은 버튼으로 닫기
        if (IsWindowVisible && IsQuickAccessOpen)
        {
            ClosePanels();
            return;
        }

        // 우리가 연 Game Bar가 앞에 떠 있으면 같은 버튼으로 닫기 (Win+G 토글)
        if (_handoff == Handoff.GameBar && _handoffSeen && XboxIntegration.IsGameBar(XboxIntegration.ForegroundOwner()))
        {
            XboxIntegration.PressWinG(); // 닫히면 OnHandoffTick이 매핑을 되돌린다
            return;
        }

        // "데스크톱으로" 중(스틱 = 마우스): 기본은 터치 키보드 토글 (설정에서 Game Bar로 바꿀 수 있음)
        if (TryDesktopRightButton()) return;

        if (XboxIntegration.IsWidgetInstalled() && XboxIntegration.TryOpenWidget())
        {
            BeginHandoff(Handoff.GameBar, retryWinG: true);
            _panelFallback = IsWindowVisible; // Game Bar가 꺼져 있어 끝내 안 뜨면 런처 위 패널로
            return;
        }

        if (IsWindowVisible)
        {
            OpenQuickPanel();
            return;
        }

        // 위젯 없음 + 게임/데스크톱 중: 우리 오버레이 대신 Windows Game Bar를 연다
        XboxIntegration.PressWinG();
        BeginHandoff(Handoff.GameBar, retryWinG: false);
    }

    private void OpenQuickPanel()
    {
        IsQuickAccessOpen = true;
        QuickAccess.Refresh();
        AfterPanelChange();
    }

    // ── Xbox 연동: 화면을 Game Bar / Xbox 앱에 넘긴 동안 ───────────

    private enum Handoff { None, GameBar, XboxApp }

    /// <summary>위젯을 런처 위에서 열었는지 — Game Bar가 끝내 안 뜨면 앱 안 패널로 대신한다.</summary>
    private bool _panelFallback;

    private readonly DispatcherTimer _handoffTimer;
    private Handoff _handoff;
    private DateTime _handoffAt;
    /// <summary>넘긴 화면이 실제로 앞에 뜬 걸 한 번이라도 봤는지 (그 뒤 사라지면 닫힌 것).</summary>
    private bool _handoffSeen;
    private bool _handoffRetry;

    /// <summary>
    /// 화면을 Game Bar/Xbox 앱에 넘긴다. 그쪽이 앞에 있는 동안은 게임 매핑을 풀고 컨트롤러를 XInput으로
    /// 두어 패드로 조작할 수 있게 하고, 닫히면(다른 창이 앞으로 오면) 다시 동기화한다.
    /// </summary>
    private void BeginHandoff(Handoff kind, bool retryWinG = false)
    {
        _handoff = kind;
        _handoffAt = DateTime.UtcNow;
        _handoffSeen = false;
        _handoffRetry = retryWinG;
        _panelFallback = false;
        _handoffTimer.Start();
        _ = SyncControllerAsync();
    }

    private void EndHandoff()
    {
        if (_handoff == Handoff.None) return;
        _handoff = Handoff.None;
        _handoffTimer.Stop();
        _ = SyncControllerAsync();
    }

    private void OnHandoffTick()
    {
        var front = XboxIntegration.ForegroundOwner();
        var holding = _handoff switch
        {
            Handoff.GameBar => XboxIntegration.IsGameBar(front),
            Handoff.XboxApp => XboxIntegration.IsXboxApp(front),
            _ => false,
        };
        if (holding)
        {
            _handoffSeen = true;
            return;
        }
        if (_handoffSeen)
        {
            // 닫혔거나 다른 창(게임 등)으로 넘어감. 앞 창을 모르면(전환 중) 다음 틱에 판단
            if (front is not null) EndHandoff();
            return;
        }

        var elapsed = DateTime.UtcNow - _handoffAt;
        // URI만으로 Game Bar가 안 뜨면 MSI 도우미처럼 Win+G로 열고 위젯을 한 번 더 활성화
        if (_handoff == Handoff.GameBar && _handoffRetry && front is not null && elapsed > TimeSpan.FromSeconds(1.5))
        {
            _handoffRetry = false;
            _ = RetryGameBarAsync();
            return;
        }
        // 끝내 안 뜸 → 원래 상태로 (Xbox 앱은 첫 실행이 느려서 더 기다린다)
        if (elapsed > (_handoff == Handoff.XboxApp ? TimeSpan.FromSeconds(15) : TimeSpan.FromSeconds(5)))
        {
            var fallback = _handoff == Handoff.GameBar && _panelFallback && IsWindowVisible && !IsQuickAccessOpen;
            EndHandoff();
            if (fallback) OpenQuickPanel(); // Game Bar가 꺼져 있음 — 런처가 보이는 동안만 앱 안 패널
        }
    }

    private async Task RetryGameBarAsync()
    {
        XboxIntegration.PressWinG();
        await Task.Delay(600); // UI 스레드로 돌아옴
        if (_handoff == Handoff.GameBar && !_handoffSeen) XboxIntegration.TryOpenWidget();
    }

    /// <summary>창만 숨긴다 (다른 창을 앞으로 올리지 않음).</summary>
    private void HideWindow()
    {
        CloseMenus();
        OnPropertyChanged(nameof(IsAnyPanelOpen));
        IsWindowVisible = false;
        IsShellVisible = false;
    }

    /// <summary>창을 숨기고 게임(또는 런처를 띄우기 전 앱)으로 돌아간다. "게임으로 돌아가기".</summary>
    [RelayCommand]
    public void ReturnToBackground()
    {
        HideWindow();
        _ = ActivateReturnTargetAsync(RunningGame is not null ? Session : null, _returnWindow);
        _ = SyncControllerAsync();
    }

    /// <summary>게임 창을 찾아(프로세스 목록 훑기 — UI 스레드 밖에서) 앞으로 올린다. 없으면 런처를 띄우기 전 창.</summary>
    private static async Task ActivateReturnTargetAsync(GameSession? session, IntPtr fallback)
    {
        var target = fallback;
        if (session is not null)
        {
            try
            {
                var hwnd = await Task.Run(session.FindMainWindow);
                if (hwnd != IntPtr.Zero) target = hwnd;
            }
            catch (Exception) { /* 게임이 막 종료됨 */ }
        }
        SystemControls.Activate(target);
    }

    /// <summary>게임 종료는 프로세스 목록을 훑으므로 UI 스레드 밖에서.</summary>
    private static void KillInBackground(GameSession session) =>
        _ = Task.Run(() =>
        {
            try { session.Kill(); }
            catch (Exception) { /* 권한 없음/이미 종료 */ }
        });

    [RelayCommand]
    private void GoToDesktop() => EnterDesktop(Settings.DesktopController == DesktopController.Gamepad);

    /// <summary>데스크톱으로 — 컨트롤러는 게임패드 그대로.</summary>
    [RelayCommand]
    private void GoToDesktopGamepad() => EnterDesktop(gamepad: true);

    private void EnterDesktop(bool gamepad)
    {
        _inDesktop = true;
        _desktopGamepad = gamepad;
        _returnWindow = IntPtr.Zero;
        ReturnToBackground();
    }

    // ── 게임 실행 ──────────────────────────────────────────

    /// <summary>
    /// 앱(게임이 아닌 프로그램) 실행: 게임 세션(매핑·성능·실행 감지) 없이 띄우고, "데스크톱으로"처럼
    /// 런처를 숨기고 스틱을 마우스로 바꾼다. 왼쪽 버튼으로 돌아온다.
    /// </summary>
    public void LaunchApp(GameEntry app)
    {
        try
        {
            using var _ = System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo(app.LaunchTarget, app.LaunchArgs ?? "")
            {
                UseShellExecute = true,
                WorkingDirectory = File.Exists(app.LaunchTarget) ? Path.GetDirectoryName(app.LaunchTarget)! : "",
            });
        }
        catch (Exception ex)
        {
            ShowToast($"실행 실패: {ex.Message}");
            return;
        }
        Library.MarkPlayed(app);
        Home.Reload();
        _inDesktop = true;
        _desktopGamepad = Settings.DesktopController == DesktopController.Gamepad;
        _returnWindow = IntPtr.Zero;
        HideWindow(); // 방금 띄운 앱이 앞으로 온다
        _ = SyncControllerAsync();
    }

    public async Task LaunchAsync(GameEntry game)
    {
        if (game.IsApp)
        {
            LaunchApp(game);
            return;
        }
        if (RunningGame is not null)
        {
            ShowToast($"{RunningGame.Title}이(가) 이미 실행 중이에요");
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
        if (s.LaunchedByUs && !IsWindowVisible)
        {
            CloseMenus();
            IsShellVisible = true;
            IsWindowVisible = true; // 실행했던 화면(홈/게임 상세)으로 돌아온다
            AfterPanelChange();
        }
        _ = SyncControllerAsync();
    }

    /// <summary>"게임 종료". 홈 타일에서 실수로 누르지 않도록 확인 대화 상자를 거친다.</summary>
    [RelayCommand(AllowConcurrentExecutions = true)]
    private async Task QuitGame()
    {
        if (Session is null || RunningGame is null) return;
        if (!await ConfirmAsync($"{RunningGame.Title}을(를) 종료할까요?", "저장하지 않은 진행 상황은 사라질 수 있어요.", "게임 종료", danger: true)) return;
        if (Session is null) return; // 기다리는 사이 게임이 이미 끝남
        KillInBackground(Session);
        CloseMenus();
        ShowToast("게임을 종료하는 중…");
    }

    // ── 컨트롤러 상태 동기화 ────────────────────────────────

    /// <summary>
    /// 현재 화면 상태에서 컨트롤러가 있어야 할 상태를 계산해 맞춘다.
    /// 게임 중이고 셸이 숨겨져 있으며 Game Bar/Xbox 앱에 화면을 넘기지 않았을 때만 게임 매핑,
    /// 그 외에는 원래 상태(패드).
    /// </summary>
    public async Task SyncControllerAsync()
    {
        await SyncFpsAsync(); // 드라이버 설정이라 서비스 없이도 적용
        if (!ServiceConnected) return;
        await _syncLock.WaitAsync();
        try
        {
            var handedOff = _handoff != Handoff.None;
            MappingProfile? profile = RunningGame is not null && !IsWindowVisible && !handedOff ? Profiles.Find(RunningGame.MappingProfileId) : null;

            // Xbox 모드(동반)는 항상 패드 조작이므로 XInput, "데스크톱으로"를 고른 경우만 Desktop
            // Game Bar 위젯/Xbox 앱이 앞에 있는 동안도 패드로 조작해야 하므로 XInput
            GamepadMode? mode = profile?.Mode
                ?? (!Settings.AutoControllerMode ? null
                    : IsWindowVisible || handedOff || RunningGame is not null ? GamepadMode.XInput
                    : _inDesktop ? (_desktopGamepad ? GamepadMode.XInput : GamepadMode.Desktop)
                    : IsCompanion ? GamepadMode.XInput : null);

            if (profile?.Id != _appliedProfileId)
            {
                var resp = profile is null
                    ? await Service.SendAsync(new Request { Op = Ops.RestoreMapping })
                    : await Service.SendAsync(new Request { Op = Ops.ApplyMapping, Bindings = profile.ToCodes().ToDictionary(), Macros = profile.ToMacros() });
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

            // 게임별 성능 모드: 게임이 도는 동안만 적용, 끝나면 사용자 기본값으로 (배터리면 배터리 전용 값 우선)
            var perf = RunningGame?.EffectivePerformanceMode(OnBattery);
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

    private bool _resyncQueued;
    private DateTime _lastAutoResync;

    /// <summary>
    /// 서비스가 알려 준 실제 상태가 앱의 "적용함" 장부와 다르면 (절전 복귀로 모드 초기화, 사용자 전환으로 매핑 해제,
    /// 모드 전환 확인 실패 등) 장부를 버리고 다시 맞춘다. 실패가 반복돼도 3초에 한 번만.
    /// </summary>
    private void ReconcileWithStatus(ServiceStatus s)
    {
        var stale = false;
        if (_appliedMode is { } m && s.ControllerMode is { } actual && actual != m) { _appliedMode = null; stale = true; }
        if (_appliedProfileId is not null && !s.MappingActive) { _appliedProfileId = null; stale = true; }
        if (_appliedPerf is not null && s.TemporaryPerformance is null) { _appliedPerf = null; stale = true; }
        if (!stale || _resyncQueued) return;
        _resyncQueued = true;
        var wait = _lastAutoResync + TimeSpan.FromSeconds(3) - DateTime.UtcNow;
        Ui(async () =>
        {
            if (wait > TimeSpan.Zero) await Task.Delay(wait);
            _resyncQueued = false;
            _lastAutoResync = DateTime.UtcNow;
            await SyncControllerAsync();
        });
    }

    /// <summary>편집 중인 매핑을 잠깐 실제로 적용해 본다. 시간이 지나면 자동으로 원래대로.</summary>
    /// <summary>매핑을 잠깐 실제로 적용한다. 취소하거나 시간이 지나면 원래대로 동기화.</summary>
    public async Task TestMappingAsync(MappingProfile profile, TimeSpan duration, CancellationToken cancel = default)
    {
        if (!await SendAsync(new Request { Op = Ops.ApplyMapping, Bindings = profile.ToCodes().ToDictionary(), Macros = profile.ToMacros() })) return;
        _appliedProfileId = "__test__"; // 다음 동기화 때 반드시 복원되도록
        try { await Task.Delay(duration, cancel); }
        catch (OperationCanceledException) { /* 사용자가 끝냄 */ }
        await SyncControllerAsync();
        ShowToast("테스트 끝 — 원래 버튼으로 돌아왔어요");
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
        if (!ServiceConnected) { ShowToast("ClawHub 서비스가 실행되고 있지 않아요"); return false; }
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
        _ = PushAppStateAsync(); // 위젯 표시용 상태: 10초마다 확인해 바뀌었을 때만 보낸다 (프로필 이름 변경·게임 PID 등)
        Clock = DateTime.Now.ToString("tt h:mm");
        _ = RefreshStatusBarAsync();
    }

    // 실수로 A를 눌러 꺼지지 않도록 확인 대화 상자를 거친다 (처음 포커스는 취소)
    [RelayCommand] private static void Sleep() => SystemControls.Sleep();

    [RelayCommand(AllowConcurrentExecutions = true)]
    private async Task Restart()
    {
        if (await ConfirmAsync("다시 시작할까요?", "열려 있는 게임과 프로그램이 모두 닫혀요.", "다시 시작", danger: true)) SystemControls.Restart();
    }

    [RelayCommand(AllowConcurrentExecutions = true)]
    private async Task Shutdown()
    {
        if (await ConfirmAsync("시스템을 종료할까요?", "열려 있는 게임과 프로그램이 모두 닫혀요.", "종료", danger: true)) SystemControls.Shutdown();
    }

    [RelayCommand(AllowConcurrentExecutions = true)]
    private async Task ExitApp()
    {
        if (!await ConfirmAsync("ClawHub를 끌까요?", "게임 매핑이 풀리고 컨트롤러가 원래 버튼으로 돌아가요.", "끄기", danger: true)) return;
        if (ServiceConnected)
        {
            try { await Service.SendAsync(new Request { Op = Ops.RestoreMapping }, TimeSpan.FromSeconds(3)); }
            catch (Exception) { /* 종료 중 */ }
        }
        Application.Current.Shutdown();
    }

    partial void OnCurrentPageChanged(INavPage? value) => OnPropertyChanged(nameof(PageTitle));
    partial void OnIsQuickAccessOpenChanged(bool value) => OnPropertyChanged(nameof(IsAnyPanelOpen));
    public void OnPropertyChangedPageTitle() => OnPropertyChanged(nameof(PageTitle));
    public string PageTitle => CurrentPage?.Title ?? "";
}
