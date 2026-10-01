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
    private int? _appliedFps;
    /// <summary>ClawHub가 손대기 전 드라이버의 FPS 제한 (사용자가 Intel 앱에서 정한 값 보존).</summary>
    private readonly int? _fpsBaseline = Gpu.GetFrameLimit();

    /// <summary>빠른 설정에서 기본 FPS 제한을 바꿈 → 저장하고 바로 적용 (게임별 값이 있으면 그게 우선).</summary>
    public async Task SetDefaultFpsAsync(int fps)
    {
        Settings.FpsLimit = fps;
        Settings.Save();
        await SyncControllerAsync();
    }

    public int CurrentFps => _appliedFps ?? _fpsBaseline ?? 0;

    /// <summary>FPS 제한: 게임별 값 > 사용자 기본값 > ClawHub 시작 때 드라이버 값.</summary>
    private async Task SyncFpsAsync()
    {
        var fps = RunningGame?.FpsLimit ?? Settings.FpsLimit ?? _fpsBaseline;
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
    public bool IsAnyPanelOpen => IsQuickAccessOpen;

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
        };
        try { await Service.SendAsync(new Request { Op = Ops.SetAppState, AppState = state }, TimeSpan.FromSeconds(3)); }
        catch (Exception) { /* 연결 끊김 — 재연결 때 다시 보냄 */ }
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
                Session?.Kill();
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
            // 서비스의 실제 상태로 장부를 맞춘다 (앱이 재시작돼도 남은 매핑/게임별 성능을 반드시 정리하도록)
            if (Status?.MappingActive == true) _appliedProfileId = "__unknown__";
            _appliedPerf = Status?.TemporaryPerformance?.Mode;
            await SyncControllerAsync();
        });
        Service.EventReceived += e => Ui(() =>
        {
            if (e.Event == Ops.EventButton && e.Button is { } b) OnMsiButton(b);
            else if (e.Event == Ops.EventStatus && e.Status is { } s) Status = s;
            else if (e.Event == Ops.EventAppCommand && e.Command is { } c) OnWidgetCommand(c);
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
    private void OpenXboxApp() => OpenXboxHome();

    /// <summary>
    /// Xbox 앱 홈을 앞으로. ClawHub 창은 숨기되 게임 창을 다시 앞으로 올리지는 않는다
    /// (Xbox 앱이 앞에 있어야 하므로). Xbox 앱이 앞에 있는 동안은 게임 매핑을 풀어 둔다.
    /// </summary>
    private bool OpenXboxHome()
    {
        if (!XboxIntegration.TryOpenXboxApp(out var error))
        {
            ShowToast($"Xbox 앱을 열 수 없습니다: {error}");
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
        IReadOnlyList<Hint> hints = IsQuickAccessOpen
            ? [new("A", "선택"), new("B", "닫기")]
            : CurrentPage?.Hints ?? [];
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

    private void CloseMenus() => IsQuickAccessOpen = false;

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
        if (s.LaunchedByUs && !IsWindowVisible)
        {
            CloseMenus();
            IsShellVisible = true;
            IsWindowVisible = true; // 실행했던 화면(홈/게임 상세)으로 돌아온다
            AfterPanelChange();
        }
        _ = SyncControllerAsync();
    }

    /// <summary>"게임 종료". 홈 타일에서 실수로 누르지 않도록 3초 안에 한 번 더 눌러야 실행.</summary>
    [RelayCommand]
    private void QuitGame()
    {
        if (Session is null || RunningGame is null) return;
        if (!ConfirmTwice("quit", $"{RunningGame.Title}을(를) 종료합니다")) return;
        Session.Kill();
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
                    : _inDesktop ? GamepadMode.Desktop
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
        if (!await SendAsync(new Request { Op = Ops.ApplyMapping, Bindings = profile.ToCodes().ToDictionary(), Macros = profile.ToMacros() })) return;
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
        _ = PushAppStateAsync(); // 위젯 표시용 상태: 10초마다 갱신 (프로필 이름 변경 등도 반영)
        Clock = DateTime.Now.ToString("tt h:mm");
        var b = SystemControls.GetBattery();
        BatteryText = b.Percent is { } p ? $"{p}%" : "";
        // Segoe Fluent Icons: Battery0~9 = E850~E859, 10 = E83F / 충전 중 0~9 = E85A~E863, 10 = E83E
        BatteryGlyph = b.Percent is not { } pct ? ""
            : pct >= 95 ? (b.Charging ? "" : "")
            : ((char)((b.Charging ? 0xE85A : 0xE850) + pct / 10)).ToString();
    }

    [ObservableProperty] private string _batteryGlyph = "";

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
    partial void OnIsQuickAccessOpenChanged(bool value) => OnPropertyChanged(nameof(IsAnyPanelOpen));
    public void OnPropertyChangedPageTitle() => OnPropertyChanged(nameof(PageTitle));
    public string PageTitle => CurrentPage?.Title ?? "";
}
