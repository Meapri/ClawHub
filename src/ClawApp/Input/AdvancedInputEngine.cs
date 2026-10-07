using System.Collections.Concurrent;
using System.Diagnostics;
using System.Runtime.InteropServices;
using ClawCore.Controller;
using ClawCore.Mapping;

namespace ClawApp.Input;

/// <summary>
/// 고급 입력 엔진 (2단계, 드라이버 없음). 펌웨어가 엔진 버튼을 "F23 + 글자"로 보내면(<see cref="AdvancedInput.CarrierLetter"/>)
/// 저수준 키보드 훅이 게임보다 먼저 받아 삼키고, 작업 스레드가 길게·두 번 누르기·연사·토글·레이어·원형 메뉴로 바꿔
/// SendInput으로 낸다. 우리가 낸 입력(injected)은 훅이 다시 받지 않는다.
/// <para>
/// 스레드: 훅 스레드(메시지 루프 — 콜백은 줄에 넣기만 하고 바로 돌아간다, 늦으면 Windows가 훅을 떼어 낸다) +
/// 작업 스레드(2ms 간격으로 줄·타이머 처리). 원형 메뉴 화면은 UI 스레드가 그린다(<see cref="RadialMenu"/>).
/// </para>
/// </summary>
public sealed class AdvancedInputEngine : IDisposable
{
    public static AdvancedInputEngine Instance { get; } = new();

    /// <summary>원형 메뉴를 보여 줄 곳 (UI 스레드에서 그린다). 엔진 스레드에서 부르므로 받는 쪽이 Dispatcher로 넘긴다.</summary>
    public interface IRadialView
    {
        /// <summary><paramref name="size"/>: 0 작게 · 1 보통 · 2 크게.</summary>
        void Show(IReadOnlyList<RadialSlice> slices, int size);
        void Select(int index);
        /// <summary><paramref name="activated"/>: 칸을 골라 실행했으면 true (반짝이며 닫힘), 취소면 false.</summary>
        void Hide(bool activated);
        /// <summary>
        /// 원형 메뉴가 있는 프로필이 적용 중인지. true면 창을 투명하게 미리 띄워 둔다 — 전체 화면 게임 위에 창이 새로 생기고
        /// 없어질 때마다 Windows가 게임 화면 내보내는 방식을 바꾸며 깜빡이므로, 열고 닫을 때는 투명도만 바꾼다.
        /// </summary>
        void Ready(bool ready);
    }

    public IRadialView? RadialView { get; set; }

    private sealed record Config(MappingProfile Profile, Dictionary<ClawButton, AdvancedButton> Buttons, Dictionary<int, ClawButton> ByVk);

    /// <summary>작업 스레드를 깨우기만 하는 가짜 이벤트 (어느 버튼에도 해당하지 않는다).</summary>
    private const ClawButton Wake = (ClawButton)(-1);

    private volatile Config? _config;
    private MappingProfile? _source;
    private readonly BlockingCollection<(ClawButton Button, bool Down)> _events = new();
    private Thread? _hookThread, _worker;
    private uint _hookThreadId;
    private IntPtr _hook;
    private HookProc? _proc; // GC가 콜백을 치우지 않게 붙잡아 둔다
    private bool _f23Down;
    private readonly HashSet<int> _carrierDown = []; // 훅 스레드 전용

    /// <summary>엔진이 지금 버튼을 맡고 있는지.</summary>
    public bool IsActive => _config is not null;

    /// <summary>
    /// 이 프로필로 시작/교체 (null = 멈춤 — 누르고 있던 키를 모두 뗀다). 펌웨어에는 이미 운반 키 매핑이 들어가 있어야 한다.
    /// </summary>
    public void Use(MappingProfile? profile)
    {
        if (profile is not null && ReferenceEquals(profile, _source)) return; // 같은 프로필 다시 동기화 — 누르고 있던 것 유지
        _source = profile;
        if (EngineLog.On) EngineLog.Write($"Use {profile?.Id ?? "(없음)"} adv={profile?.Advanced?.Enabled} buttons={string.Join(",", profile?.EngineButtons() ?? [])}");
        profile = profile?.Clone(); // 엔진 스레드가 읽는 동안 편집기가 바꿔도 안전하게
        foreach (var a in profile?.Advanced?.Buttons.Values.AsEnumerable() ?? [])
            if (a.Radial is { Count: 8 } r && new[] { 1, 3, 5, 7 }.All(i => r[i].Keys.Count == 0))
                a.Radial = [r[0], r[2], r[4], r[6]]; // 대각선 칸이 비었으면 상하좌우 4칸 메뉴 (고르기 쉽게)
        RadialView?.Ready(profile?.Advanced is { Enabled: true } withRadial && withRadial.Buttons.Values.Any(b => b.Radial is { Count: > 0 }));
        // 고급 동작 버튼 + 시스템 동작(마이크 등) 버튼. 고급 입력이 꺼져 있으면 시스템 동작만 (누르면 실행)
        var buttons = profile?.EngineButtons().ToList() ?? [];
        if (profile is null || buttons.Count == 0)
        {
            if (_config is null) return;
            _config = null;
            _events.Add((Wake, false)); // 작업 스레드가 깨어나 정리하게
            return;
        }
        var adv = profile.Advanced is { Enabled: true } enabled ? enabled : null;
        _config = new Config(profile, buttons.ToDictionary(b => b, b => adv?.Buttons.GetValueOrDefault(b) ?? new AdvancedButton()),
            buttons.ToDictionary(b => (int)AdvancedInput.CarrierLetter[b], b => b));
        _events.Add((Wake, false)); // 자고 있던 작업 스레드가 새 프로필로 바로 정리하게
        EnsureThreads();
    }

    private Timer? _diag;

    private void EnsureThreads()
    {
        // 진단: 1초마다 훅이 받은 키 개수와 패드(XInput) 버튼 비트 — 게임이 앞에 있을 때 무엇이 들어오는지
        if (EngineLog.On && _diag is null)
            _diag = new Timer(_ =>
            {
                XInputState st = default;
                var slot = -1;
                for (var i = 0; i < 4 && slot < 0; i++)
                    if (i != VirtualPad.VirtualSlot && XInputGetState(i, ref st) == 0) slot = i;
                EngineLog.Write($"tick hook={_hookCount} pad{slot} buttons=0x{st.Gamepad.Buttons:X4} config={_config?.Profile.Id} fg={GetForegroundWindow():X}");
            }, null, 1000, 1000);
        if (_worker is null)
        {
            _worker = new Thread(WorkerLoop) { IsBackground = true, Name = "ClawHub.AdvancedInput", Priority = ThreadPriority.AboveNormal };
            _worker.Start();
        }
        if (_hookThread is null)
        {
            _hookThread = new Thread(HookLoop) { IsBackground = true, Name = "ClawHub.KeyHook", Priority = ThreadPriority.Highest };
            _hookThread.Start();
        }
    }

    // ── 훅 ─────────────────────────────────────────────────

    private void HookLoop()
    {
        _hookThreadId = GetCurrentThreadId();
        _proc = HookCallback;
        _hook = SetWindowsHookEx(WhKeyboardLl, _proc, GetModuleHandle(null), 0);
        while (GetMessage(out var msg, IntPtr.Zero, 0, 0) > 0)
        {
            TranslateMessage(ref msg);
            DispatchMessage(ref msg);
        }
        if (_hook != IntPtr.Zero) UnhookWindowsHookEx(_hook);
    }

    private const int VkF23 = 0x86;

    private IntPtr HookCallback(int code, IntPtr wParam, IntPtr lParam)
    {
        if (EngineLog.On && code >= 0)
        {
            // 진단: 키 내용은 적지 않고 받은 개수만 (보안 프로그램이 훅을 막는지), F13~F24만 이름으로
            var kk = Marshal.PtrToStructure<KbdLlHookStruct>(lParam);
            _hookCount++;
            if (kk.VkCode is >= 0x7C and <= 0x87) EngineLog.Write($"hook F{kk.VkCode - 0x7C + 13} msg={(int)wParam:X} injected={(kk.Flags & 0x10) != 0}");
            if (Environment.TickCount64 - _hookLogAt > 3000)
            {
                _hookLogAt = Environment.TickCount64;
                EngineLog.Write($"hook alive: {_hookCount} key events so far · foreground {GetForegroundWindow():X}");
            }
        }
        if (code >= 0 && _config is { } cfg)
        {
            var k = Marshal.PtrToStructure<KbdLlHookStruct>(lParam);
            var injected = (k.Flags & 0x10) != 0;
            if (!injected)
            {
                var msg = (int)wParam;
                var down = msg is WmKeydown or WmSyskeydown;
                if (k.VkCode == VkF23)
                {
                    if (EngineLog.On) EngineLog.Write($"hook F23 {(down ? "down" : "up")}");
                    if (down && !_f23Down) Rescue(cfg, k.Time);
                    _f23Down = down;
                    return 1; // 운반 키의 머리는 게임에 보이지 않게
                }
                if (cfg.ByVk.TryGetValue(k.VkCode, out var button) && (_f23Down || _carrierDown.Contains(k.VkCode)))
                {
                    if (EngineLog.On) EngineLog.Write($"hook {button} {(down ? "down" : "up")}");
                    if (down)
                    {
                        if (_carrierDown.Add(k.VkCode)) _events.Add((button, true)); // 키 반복은 무시
                    }
                    else if (_carrierDown.Remove(k.VkCode)) _events.Add((button, false));
                    return 1;
                }
                if (down && cfg.ByVk.ContainsKey(k.VkCode)) { _strayVk = k.VkCode; _strayAt = k.Time; }
            }
        }
        else if (_config is null)
        {
            _f23Down = false;
            _carrierDown.Clear();
        }
        return CallNextHookEx(_hook, code, wParam, lParam);
    }

    private long _hookCount, _hookLogAt;
    private int _strayVk;
    private int _strayAt;

    /// <summary>
    /// 펌웨어가 글자를 F23보다 먼저 보낸 경우: 방금(20ms 안) 지나간 운반 글자를 이 버튼으로 되돌린다.
    /// 이미 게임에 간 글자 눌림은 바로 떼어 준다 (아주 짧은 한 번 — 순서가 늘 F23 먼저면 일어나지 않는다).
    /// </summary>
    private void Rescue(Config cfg, int time)
    {
        if (_strayVk == 0 || unchecked(time - _strayAt) > 20 || !cfg.ByVk.TryGetValue(_strayVk, out var button)) return;
        var vk = _strayVk;
        _strayVk = 0;
        if (!_carrierDown.Add(vk)) return;
        SoftInput.UpVk((ushort)vk);
        _events.Add((button, true));
    }

    // ── 작업 스레드: 동작 판정 ──────────────────────────────

    private enum Phase { Idle, Holding, Waiting, LongHolding, PendingDouble, Turbo, Radial, Layer, Sequence }

    private sealed class State
    {
        public Phase Phase;
        public long Since;            // 이 단계에 들어간 시각 (ms)
        public long Next;             // 연사 다음 시각 · 두 번 누르기 마감
        public IReadOnlyList<string> Keys = [];
        public bool Toggled;
        public IReadOnlyList<string> ToggleKeys = [];
        public int Index;             // 차례로 누르기: 지금 누르고 있는 키 번호
        public int[] Gaps = [];       // 차례로 누르기: 키마다 누르고 있을 시간 (ms)
        public long RepeatAt;         // 차례로 누르기: 지금 키의 다음 자동 반복 시각
        public SequenceMode Mode;     // 차례로 누르기: 한 번 끝까지 / 누르는 동안 반복 / 누르는 동안만
    }

    private readonly Dictionary<ClawButton, State> _states = [];
    private readonly List<(long At, IReadOnlyList<string> Keys)> _pendingUps = []; // 짧게 누름(탭)의 뗌 예약
    private readonly Stopwatch _clock = Stopwatch.StartNew();
    private bool _layer;
    private ClawButton? _radialButton;
    private int _radialPick = -1;
    private Config? _lastConfig;

    private long Now => _clock.ElapsedMilliseconds;
    private const int TapMs = 30;

    /// <summary>
    /// 버튼 이벤트가 오거나 시간이 걸린 동작(연사·길게·두 번·탭 떼기·원형 메뉴)의 다음 시각이 될 때만 깨어난다.
    /// 예전에는 엔진이 켜져 있는 내내 2ms마다 깨어나고(초당 500번) Windows 타이머를 1ms로 붙잡아 두어
    /// 게임하는 동안 배터리를 더 썼다. 이제 정밀 타이머는 시간이 걸린 동작이 남아 있는 동안만 켠다.
    /// </summary>
    private void WorkerLoop()
    {
        while (true)
        {
            var cfg = _config;
            if (!ReferenceEquals(cfg, _lastConfig))
            {
                ReleaseAll(); // 프로필이 바뀌거나 멈춤 → 누르던 것 정리
                Volatile.Write(ref _lastConfig, cfg);
            }
            var due = cfg is null ? null : NextDue(cfg);
            FineTimer(due is not null);
            var wait = due is { } d ? (int)Math.Clamp(d - Now, 0, 1000) : Timeout.Infinite;
            if (_events.TryTake(out var e, wait) && cfg is not null && cfg.Buttons.ContainsKey(e.Button))
            {
                if (e.Down) OnDown(cfg, e.Button);
                else OnUp(cfg, e.Button);
            }
            if (cfg is not null) Tick(cfg);
        }
    }

    private const int RadialPollMs = 8; // 원형 메뉴가 떠 있는 동안 오른쪽 스틱 읽기 (≈120Hz)

    /// <summary>다음에 처리할 시각 (없으면 null = 버튼 이벤트가 올 때까지 잔다).</summary>
    private long? NextDue(Config cfg)
    {
        long? due = null;
        void At(long t) => due = due is { } d ? Math.Min(d, t) : t;
        foreach (var (at, _) in _pendingUps) At(at);
        foreach (var (b, s) in _states)
        {
            if (!cfg.Buttons.TryGetValue(b, out var a)) continue;
            switch (s.Phase)
            {
                case Phase.Waiting when a.Long.Count > 0: At(s.Since + Math.Clamp(a.LongMs, 150, 2000)); break;
                case Phase.PendingDouble:
                case Phase.Turbo: At(s.Next); break;
                case Phase.Sequence: At(Math.Min(s.Next, s.RepeatAt)); break;
            }
        }
        if (_radialButton is not null) At(Now + RadialPollMs);
        if (_rumbleOffAt is { } off) At(off);
        return due;
    }

    private bool _fine;

    /// <summary>1ms 정밀 타이머는 시간이 걸린 동작이 있을 때만 (연사 간격이 정확하게).</summary>
    private void FineTimer(bool on)
    {
        if (on == _fine) return;
        _fine = on;
        if (on) timeBeginPeriod(1);
        else timeEndPeriod(1);
    }

    private State S(ClawButton b) => _states.TryGetValue(b, out var s) ? s : _states[b] = new State();

    private static IReadOnlyList<string> Sendable(IEnumerable<string> keys) => [.. keys.Where(SoftInput.CanSend)];

    private IReadOnlyList<string> PressKeys(Config cfg, ClawButton b) =>
        Sendable(cfg.Profile.Bindings.TryGetValue(b, out var l) ? l : []);

    private void OnDown(Config cfg, ClawButton b)
    {
        var a = cfg.Buttons[b];
        var s = S(b);
        var now = Now;
        if (EngineLog.On) EngineLog.Write($"down {b} phase={s.Phase} layer={_layer} toggle={a.Toggle} turbo={a.TurboMs} long={a.Long.Count} double={a.Double.Count} press=[{string.Join(" ", PressKeys(cfg, b))}] seq=[{string.Join(",", cfg.Profile.SequenceGaps(b) ?? [])}]");

        if (a.LayerShift)
        {
            _layer = true;
            s.Phase = Phase.Layer;
            return;
        }
        if (a.Radial is { Count: > 0 } slices)
        {
            // 스틱을 눌러 연 메뉴가 열려 있는데 다시 누름 → 고른 칸 실행 (안 골랐으면 취소)
            if (s.Phase == Phase.Radial && _radialButton == b && _radialClick)
            {
                FinishRadial(a, s);
                return;
            }
            _radialClick = a.RadialClickToOpen(b);
            _radialReleased = false;
            _radialTilted = false;
            _radialOpenedAt = now;
            _radialButton = b;
            _radialPick = -1;
            s.Phase = Phase.Radial;
            if (EngineLog.On) EngineLog.Write($"radial show {slices.Count} view={RadialView?.GetType().Name}");
            RadialView?.Show(slices, a.RadialSize);
            return;
        }
        // 레이어 버튼을 누르고 있으면 "레이어 중" 키 (단순히 누르는 동안)
        if (_layer && a.Layer.Count > 0)
        {
            var layerKeys = Sendable(a.Layer);
            if (TryStartSequence(s, layerKeys, a.SequenceGaps("layer", a.Layer.Count), a.SequenceModeOf("layer", b), now)) return;
            s.Keys = layerKeys;
            SoftInput.Down(s.Keys);
            s.Phase = Phase.Holding;
            return;
        }
        var press = PressKeys(cfg, b);
        // 여러 키를 차례로: 첫 키를 누른 채 간격만큼 → 떼고 다음 키 … (다시 누르면 처음부터).
        // 토글·연사보다 우선 (함께 켜 두면 5개 키가 한꺼번에 눌린 채 고정되던 문제). 길게·두 번이 있으면 짧게 뗄 때 차례로 나간다.
        if (a.Long.Count == 0 && a.Double.Count == 0 && TryStartSequence(s, press, cfg.Profile.SequenceGaps(b), cfg.Profile.SequenceModeOf(b), now)) return;
        if (a.Toggle)
        {
            if (s.Toggled) SoftInput.Up(s.ToggleKeys);
            else SoftInput.Down(s.ToggleKeys = press);
            s.Toggled = !s.Toggled;
            return;
        }
        if (s.Phase == Phase.PendingDouble && a.Double.Count > 0)
        {
            s.Phase = Phase.Idle; // 두 번째 누름 → 두 번 누르기
            var doubleKeys = Sendable(a.Double);
            if (!TryStartSequence(s, doubleKeys, a.SequenceGaps("double", a.Double.Count), a.SequenceModeOf("double", b), now)) Tap(doubleKeys);
            return;
        }
        if (a.Long.Count > 0 || a.Double.Count > 0)
        {
            s.Phase = Phase.Waiting; // 길게인지·두 번인지 아직 모른다
            s.Since = now;
            return;
        }
        if (a.TurboMs > 0)
        {
            s.Phase = Phase.Turbo;
            s.Keys = press;
            s.Next = now; // 바로 한 번
            return;
        }
        s.Keys = press;
        SoftInput.Down(press);
        s.Phase = Phase.Holding;
    }

    private void OnUp(Config cfg, ClawButton b)
    {
        var a = cfg.Buttons[b];
        var s = S(b);
        if (EngineLog.On) EngineLog.Write($"up {b} phase={s.Phase}");
        switch (s.Phase)
        {
            case Phase.Sequence when s.Mode != SequenceMode.Once:
                // 누르는 동안 반복·누르는 동안만: 떼면 그 자리에서 멈춘다 (누르던 키도 뗀다)
                if (s.Index < s.Keys.Count) SoftInput.Up([s.Keys[s.Index]]);
                if (EngineLog.On) EngineLog.Write($"seq stop (released) at {s.Index}");
                s.Phase = Phase.Idle;
                break;
            case Phase.Layer:
                _layer = false;
                s.Phase = Phase.Idle;
                break;
            case Phase.Radial:
                // 스틱을 눌러 연 메뉴: 누른 채 이미 골랐으면 바로 실행, 아니면 열린 채로 (기울였다 놓으면 실행)
                if (_radialClick && _radialPick < 0)
                {
                    _radialReleased = true;
                    break;
                }
                FinishRadial(a, s);
                break;
            case Phase.Holding:
                SoftInput.Up(s.Keys);
                s.Phase = Phase.Idle;
                break;
            case Phase.LongHolding:
                SoftInput.Up(s.Keys);
                s.Phase = Phase.Idle;
                break;
            case Phase.Turbo:
                s.Phase = Phase.Idle;
                break;
            case Phase.Waiting:
                // 길게가 되기 전에 뗐다 → 짧게 (두 번 누르기가 있으면 잠깐 기다려 본다)
                if (a.Double.Count > 0)
                {
                    s.Phase = Phase.PendingDouble;
                    s.Next = Now + Math.Clamp(a.DoubleMs, 100, 600);
                }
                else
                {
                    s.Phase = Phase.Idle;
                    var shortKeys = PressKeys(cfg, b);
                    if (!TryStartSequence(s, shortKeys, cfg.Profile.SequenceGaps(b), SequenceMode.Once, Now)) Tap(shortKeys);
                }
                break;
        }
    }

    private void Tick(Config cfg)
    {
        var now = Now;
        for (var i = _pendingUps.Count - 1; i >= 0; i--)
            if (now >= _pendingUps[i].At)
            {
                SoftInput.Up(_pendingUps[i].Keys);
                _pendingUps.RemoveAt(i);
            }

        foreach (var (b, s) in _states)
        {
            if (!cfg.Buttons.TryGetValue(b, out var a)) continue;
            switch (s.Phase)
            {
                case Phase.Waiting when a.Long.Count > 0 && now - s.Since >= Math.Clamp(a.LongMs, 150, 2000):
                    var longKeys = Sendable(a.Long);
                    if (TryStartSequence(s, longKeys, a.SequenceGaps("long", a.Long.Count), a.SequenceModeOf("long", b), now)) break;
                    s.Keys = longKeys;
                    SoftInput.Down(s.Keys); // 길게: 누르고 있는 동안 계속 눌림
                    s.Phase = Phase.LongHolding;
                    break;
                case Phase.PendingDouble when now >= s.Next:
                    s.Phase = Phase.Idle; // 두 번째가 안 왔다 → 짧게 한 번
                    var pressKeys = PressKeys(cfg, b);
                    if (!TryStartSequence(s, pressKeys, cfg.Profile.SequenceGaps(b), SequenceMode.Once, now)) Tap(pressKeys);
                    break;
                case Phase.Turbo when now >= s.Next:
                    var hold = Math.Max(10, Math.Min(TapMs, a.TurboMs / 2));
                    SoftInput.Down(s.Keys);
                    _pendingUps.Add((now + hold, s.Keys));
                    s.Next = now + Math.Clamp(a.TurboMs, 20, 2000);
                    break;
                case Phase.Sequence when now < s.Next:
                    if (now >= s.RepeatAt)
                    {
                        SoftInput.Repeat(s.Keys[s.Index]); // 진짜 키처럼 누르고 있는 동안 '눌림'을 되풀이
                        s.RepeatAt = now + KeyRepeatMs;
                    }
                    break;
                case Phase.Sequence:
                    if (EngineLog.On) EngineLog.Write($"seq up {s.Keys[s.Index]}");
                    SoftInput.Up([s.Keys[s.Index]]);
                    if (++s.Index < s.Keys.Count)
                    {
                        if (EngineLog.On) EngineLog.Write($"seq down {s.Keys[s.Index]} for {StepMs(s, s.Index)}ms");
                        SoftInput.Down([s.Keys[s.Index]]);
                        s.Next = now + StepMs(s, s.Index);
                        s.RepeatAt = now + KeyRepeatMs;
                    }
                    else if (s.Mode == SequenceMode.Repeat)
                    {
                        // 누르고 있는 동안 반복: 처음부터 다시 (떼면 OnUp에서 멈춘다)
                        s.Index = 0;
                        if (EngineLog.On) EngineLog.Write($"seq again {s.Keys[0]}");
                        SoftInput.Down([s.Keys[0]]);
                        s.Next = now + StepMs(s, 0);
                        s.RepeatAt = now + KeyRepeatMs;
                    }
                    else s.Phase = Phase.Idle;
                    break;
            }
        }

        if (_rumbleOffAt is { } rumbleOff && now >= rumbleOff) Rumble(0);

        if (_radialButton is { } rb && cfg.Buttons.TryGetValue(rb, out var ra) && ra.Radial is { Count: > 0 } sl)
        {
            var pick = RadialPick(sl.Count, ra.UsesLeftStick(rb));
            if (pick >= 0 && pick != _radialPick)
            {
                _radialPick = pick;
                RadialView?.Select(pick);
                if (ra.RadialHaptics) HapticTick(now);
            }
            if (_radialClick && _radialReleased)
            {
                // 버튼을 뗀 뒤: 기울였다가 가운데로 돌아오면 고른 칸 실행 · 아무것도 안 고르고 5초면 닫기
                if (pick >= 0) _radialTilted = true;
                else if (_radialTilted && _radialPick >= 0) FinishRadial(ra, S(rb));
                else if (_radialPick < 0 && now - _radialOpenedAt > RadialIdleCloseMs) FinishRadial(ra, S(rb));
            }
        }
    }

    private const int RadialIdleCloseMs = 5000;
    private bool _radialClick, _radialReleased, _radialTilted;
    private long _radialOpenedAt;

    /// <summary>원형 메뉴를 닫고, 고른 칸이 있으면 그 키를 낸다.</summary>
    private void FinishRadial(AdvancedButton a, State s)
    {
        var chosen = _radialPick >= 0 && a.Radial is { } slices && _radialPick < slices.Count ? slices[_radialPick] : null;
        RadialView?.Hide(activated: chosen is { Keys.Count: > 0 });
        if (chosen is not null) Tap(Sendable(chosen.Keys));
        _radialButton = null;
        _radialPick = -1;
        _radialClick = false;
        s.Phase = Phase.Idle;
    }

    /// <summary>
    /// 그 칸이 "차례로"(간격 > 0)이고 키가 둘 이상이면: 첫 키를 누른 채 간격만큼 → 떼고 다음 키 … (버튼을 떼도 끝까지, 다시 누르면 처음부터).
    /// 아니면 false — 호출한 쪽이 평소처럼 (동시에) 낸다.
    /// </summary>
    private bool TryStartSequence(State s, IReadOnlyList<string> keys, int[]? gaps, SequenceMode mode, long now)
    {
        if (gaps is not { Length: > 0 } || keys.Count < 2) return false;
        if (s.Phase == Phase.Sequence && s.Index < s.Keys.Count) SoftInput.Up([s.Keys[s.Index]]);
        s.Phase = Phase.Sequence;
        s.Keys = keys;
        s.Index = 0;
        s.Gaps = gaps;
        s.Mode = mode;
        s.Next = now + StepMs(s, 0);
        s.RepeatAt = now + KeyRepeatMs;
        if (EngineLog.On) EngineLog.Write($"seq start [{string.Join(" ", keys)}] gaps [{string.Join(",", gaps)}] · down {keys[0]}");
        SoftInput.Down([keys[0]]);
        return true;
    }

    /// <summary>
    /// 차례로 누르는 키의 자동 반복 간격. 진짜 키보드·컨트롤러 펌웨어처럼 누르고 있는 동안 '눌림'을 되풀이한다 —
    /// 눌림 신호가 올 때만 받는 게임(메이플 등)이 앞 스킬 동작 중에 들어온 첫 신호를 버려도 동작이 끝나면 다음 신호로 받는다.
    /// </summary>
    private const int KeyRepeatMs = 33;

    /// <summary>차례로 누르기에서 i번째 키를 누르고 있을 시간 (키 수가 시간 수보다 많으면 마지막 값).</summary>
    private static int StepMs(State s, int i) => Math.Clamp(s.Gaps[Math.Min(i, s.Gaps.Length - 1)], 30, 5000);

    private void Tap(IReadOnlyList<string> keys)
    {
        if (keys.Count == 0) return;
        SoftInput.Down(keys);
        _pendingUps.Add((Now + TapMs, keys));
    }

    /// <summary>누르고 있던 것을 모두 뗀다 (멈출 때·프로필 바뀔 때 — 키가 눌린 채 남지 않게).</summary>
    private void ReleaseAll()
    {
        foreach (var s in _states.Values)
        {
            if (s.Phase is Phase.Holding or Phase.LongHolding) SoftInput.Up(s.Keys);
            if (s.Phase == Phase.Sequence && s.Index < s.Keys.Count) SoftInput.Up([s.Keys[s.Index]]);
            if (s.Toggled) SoftInput.Up(s.ToggleKeys);
        }
        foreach (var (_, keys) in _pendingUps) SoftInput.Up(keys);
        _pendingUps.Clear();
        _states.Clear();
        _layer = false;
        if (_radialButton is not null) RadialView?.Hide(activated: false);
        _radialClick = false;
        _radialButton = null;
        if (_rumbleOffAt is not null) Rumble(0);
    }

    // ── 원형 메뉴: 오른쪽 스틱 방향으로 고른다 (XInput — 스틱은 펌웨어 매핑과 상관없이 늘 나온다) ──

    private int _padSlot = -1;

    // ── 진동 틱: 고른 칸이 바뀔 때 아주 짧게 (Steam의 햅틱 틱처럼 — 거슬리지 않게 약하게 18ms) ──

    private long? _rumbleOffAt;

    private void HapticTick(long now)
    {
        Rumble(9000);
        _rumbleOffAt = now + 18;
    }

    private void Rumble(ushort strength)
    {
        if (strength == 0) _rumbleOffAt = null;
        if (_padSlot < 0) return;
        var v = new XInputVibration { Left = 0, Right = strength };
        XInputSetState(_padSlot, ref v);
    }

    private int RadialPick(int count, bool leftStick)
    {
        XInputState st = default;
        if (_padSlot < 0 || _padSlot == VirtualPad.VirtualSlot || XInputGetState(_padSlot, ref st) != 0)
        {
            _padSlot = -1;
            for (var i = 0; i < 4; i++)
                if (i != VirtualPad.VirtualSlot && XInputGetState(i, ref st) == 0) { _padSlot = i; break; }
            if (_padSlot < 0) return -1;
        }
        double x = leftStick ? st.Gamepad.ThumbLX : st.Gamepad.ThumbRX, y = leftStick ? st.Gamepad.ThumbLY : st.Gamepad.ThumbRY;
        if (Math.Sqrt(x * x + y * y) < 16000) return -1; // 가운데 = 지금 고른 칸 그대로
        // 위(0)부터 시계 방향
        var angle = Math.Atan2(x, y); // 위 = 0, 오른쪽 = +π/2
        if (angle < 0) angle += 2 * Math.PI;
        var sector = 2 * Math.PI / count;
        // 경계에서 왔다 갔다 하지 않게: 지금 칸에서 경계를 6° 넘어야 옮긴다
        if (_radialPick >= 0 && _radialPick < count)
        {
            var diff = Math.Abs(((angle - _radialPick * sector) % (2 * Math.PI) + 3 * Math.PI) % (2 * Math.PI) - Math.PI);
            if (diff < sector / 2 + 6 * Math.PI / 180) return _radialPick;
        }
        return (int)Math.Floor((angle + sector / 2) / sector) % count;
    }

    public void Dispose()
    {
        Use(null);
        // 작업 스레드가 누르던 키를 떼어 놓을 때까지 잠깐 기다린다 (앱이 꺼지며 키가 눌린 채 남지 않게)
        var until = Environment.TickCount64 + 200;
        while (_worker is not null && Volatile.Read(ref _lastConfig) is not null && Environment.TickCount64 < until) Thread.Sleep(5);
        if (_hookThreadId != 0) PostThreadMessage(_hookThreadId, 0x0012 /* WM_QUIT */, IntPtr.Zero, IntPtr.Zero);
    }

    // ── Win32 ──────────────────────────────────────────────

    private const int WhKeyboardLl = 13, WmKeydown = 0x0100, WmSyskeydown = 0x0104;

    private delegate IntPtr HookProc(int code, IntPtr wParam, IntPtr lParam);

    [StructLayout(LayoutKind.Sequential)]
    private struct KbdLlHookStruct
    {
        public int VkCode, ScanCode, Flags, Time;
        public IntPtr ExtraInfo;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct Msg
    {
        public IntPtr Hwnd;
        public uint Message;
        public IntPtr WParam, LParam;
        public uint Time;
        public int X, Y;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct XInputGamepad
    {
        public ushort Buttons;
        public byte LeftTrigger, RightTrigger;
        public short ThumbLX, ThumbLY, ThumbRX, ThumbRY;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct XInputVibration
    {
        public ushort Left, Right;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct XInputState
    {
        public uint PacketNumber;
        public XInputGamepad Gamepad;
    }

    [DllImport("user32.dll")] private static extern IntPtr GetForegroundWindow();
    [DllImport("user32.dll", SetLastError = true)] private static extern IntPtr SetWindowsHookEx(int id, HookProc proc, IntPtr mod, uint threadId);
    [DllImport("user32.dll")] private static extern bool UnhookWindowsHookEx(IntPtr hook);
    [DllImport("user32.dll")] private static extern IntPtr CallNextHookEx(IntPtr hook, int code, IntPtr wParam, IntPtr lParam);
    [DllImport("user32.dll")] private static extern int GetMessage(out Msg msg, IntPtr hwnd, uint min, uint max);
    [DllImport("user32.dll")] private static extern bool TranslateMessage(ref Msg msg);
    [DllImport("user32.dll")] private static extern IntPtr DispatchMessage(ref Msg msg);
    [DllImport("user32.dll")] private static extern bool PostThreadMessage(uint threadId, uint msg, IntPtr wParam, IntPtr lParam);
    [DllImport("kernel32.dll")] private static extern IntPtr GetModuleHandle(string? name);
    [DllImport("kernel32.dll")] private static extern uint GetCurrentThreadId();
    [DllImport("winmm.dll")] private static extern uint timeBeginPeriod(uint ms);
    [DllImport("winmm.dll")] private static extern uint timeEndPeriod(uint ms);
    [DllImport("xinput1_4.dll")] private static extern int XInputGetState(int index, ref XInputState state);
    [DllImport("xinput1_4.dll")] private static extern int XInputSetState(int index, ref XInputVibration vibration);
}
