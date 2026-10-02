using System.Runtime.InteropServices;
using System.Windows.Threading;

namespace ClawApp.Input;

public enum NavAction
{
    Up, Down, Left, Right,
    Accept, Back, Secondary, Options,
    TabPrev, TabNext,
    MainMenu, QuickAccess,
}

/// <summary>
/// XInput 폴링 → 내비게이션 액션. 방향은 누르고 있으면 반복된다.
/// <see cref="Enabled"/>가 false면(게임 실행 중 등) 폴링 자체를 멈춘다 — 창이 숨어 있는 동안
/// 16ms 타이머가 UI 스레드를 초당 60번 깨우지 않게 (배터리).
/// </summary>
public sealed class GamepadInput
{
    private static readonly TimeSpan RepeatDelay = TimeSpan.FromMilliseconds(380);
    private static readonly TimeSpan RepeatRate = TimeSpan.FromMilliseconds(90);
    private const short StickThreshold = 16000;

    private readonly DispatcherTimer _timer;
    private ushort _prevButtons;
    private NavAction? _heldDir;
    private DateTime _nextRepeat;
    private int? _slot;
    private DateTime _nextScan;
    private bool _started;
    private bool _enabled = true;

    public event Action<NavAction>? Action;

    /// <summary>false면 입력을 무시하고 폴링 타이머도 멈춘다 (창이 비활성일 때).</summary>
    public bool Enabled
    {
        get => _enabled;
        set
        {
            if (_enabled == value) return;
            _enabled = value;
            ApplyTimer();
        }
    }

    public bool Connected { get; private set; }

    public GamepadInput()
    {
        _timer = new DispatcherTimer(DispatcherPriority.Input) { Interval = TimeSpan.FromMilliseconds(16) };
        _timer.Tick += (_, _) => Poll();
    }

    public void Start()
    {
        _started = true;
        ApplyTimer();
    }

    private void ApplyTimer()
    {
        var run = _started && _enabled;
        if (run == _timer.IsEnabled) return;
        // 멈춰 있던 동안의 상태는 버린다 (예전처럼: 다시 켜진 첫 틱에 눌려 있는 버튼은 새로 누른 것으로 본다)
        _prevButtons = 0;
        _heldDir = null;
        if (run)
        {
            _nextScan = DateTime.MinValue; // 멈춘 사이 연결이 바뀌었을 수 있으니 바로 다시 찾는다
            _timer.Start();
        }
        else
        {
            _timer.Stop();
        }
    }

    private void Poll()
    {
        XInputState state = default;
        // 빈 슬롯 조회는 느리므로, 연결된 슬롯만 매 틱 읽고 끊겼을 때만 1초 간격으로 재탐색
        if (_slot is { } s && XInputGetState(s, ref state) != 0) _slot = null;
        if (_slot is null && DateTime.UtcNow >= _nextScan)
        {
            _nextScan = DateTime.UtcNow.AddSeconds(1);
            for (var i = 0; i < 4 && _slot is null; i++)
                if (XInputGetState(i, ref state) == 0) _slot = i;
        }

        Connected = _slot is not null;
        if (!Connected || !Enabled)
        {
            _prevButtons = 0;
            _heldDir = null;
            return;
        }

        var b = state.Gamepad.Buttons;
        var pressed = (ushort)(b & ~_prevButtons);
        _prevButtons = b;

        void Fire(ushort mask, NavAction a)
        {
            if ((pressed & mask) != 0) Action?.Invoke(a);
        }
        Fire(0x1000, NavAction.Accept);    // A
        Fire(0x2000, NavAction.Back);      // B
        Fire(0x4000, NavAction.Secondary); // X
        Fire(0x8000, NavAction.Options);   // Y
        Fire(0x0100, NavAction.TabPrev);   // LB
        Fire(0x0200, NavAction.TabNext);   // RB
        Fire(0x0010, NavAction.MainMenu);  // Menu(Start)
        Fire(0x0020, NavAction.QuickAccess); // View — 전용 버튼을 못 받을 때의 대체

        var dir = Direction(state.Gamepad);
        var now = DateTime.UtcNow;
        if (dir != _heldDir)
        {
            _heldDir = dir;
            if (dir is { } d) { Action?.Invoke(d); _nextRepeat = now + RepeatDelay; }
        }
        else if (dir is { } d && now >= _nextRepeat)
        {
            Action?.Invoke(d);
            _nextRepeat = now + RepeatRate;
        }
    }

    private static NavAction? Direction(XInputGamepad g)
    {
        var b = g.Buttons;
        if ((b & 0x0001) != 0) return NavAction.Up;
        if ((b & 0x0002) != 0) return NavAction.Down;
        if ((b & 0x0004) != 0) return NavAction.Left;
        if ((b & 0x0008) != 0) return NavAction.Right;

        int x = g.ThumbLX, y = g.ThumbLY;
        if (Math.Max(Math.Abs(x), Math.Abs(y)) < StickThreshold) return null;
        return Math.Abs(x) > Math.Abs(y)
            ? (x > 0 ? NavAction.Right : NavAction.Left)
            : (y > 0 ? NavAction.Up : NavAction.Down);
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct XInputGamepad
    {
        public ushort Buttons;
        public byte LeftTrigger, RightTrigger;
        public short ThumbLX, ThumbLY, ThumbRX, ThumbRY;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct XInputState
    {
        public uint PacketNumber;
        public XInputGamepad Gamepad;
    }

    [DllImport("xinput1_4.dll")]
    private static extern int XInputGetState(int userIndex, ref XInputState state);
}
