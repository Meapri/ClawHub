using System.Runtime.InteropServices;
using System.Windows.Threading;

namespace ClawApp.Input;

public enum NavAction
{
    Up, Down, Left, Right,
    Accept, Back, Secondary, Options,
    TabPrev, TabNext,
    MainMenu, QuickAccess,
    /// <summary>LT / RT. 셸은 페이지에 그대로 넘긴다 (라이브러리: 찾기·정렬 등 페이지별 보조 동작).</summary>
    TriggerLeft, TriggerRight,
}

/// <summary>
/// XInput 폴링 → 내비게이션 액션. 방향은 누르고 있으면 반복된다.
/// <see cref="Enabled"/>가 false면(게임 실행 중 등) 입력을 무시한다.
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

    public event Action<NavAction>? Action;
    public bool Enabled { get; set; } = true;
    public bool Connected { get; private set; }

    public GamepadInput()
    {
        _timer = new DispatcherTimer(DispatcherPriority.Input) { Interval = TimeSpan.FromMilliseconds(16) };
        _timer.Tick += (_, _) => Poll();
    }

    public void Start() => _timer.Start();

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
            _ltDown = _rtDown = true; // 게임에서 누른 채로 돌아와도 바로 발동하지 않게 (한 번 놓아야 함)
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

        // 트리거는 아날로그 — 깊게 누를 때 한 번, 거의 다 놓아야 다시 (손가락을 얹어 두기만 해도 반복되지 않게)
        Trigger(state.Gamepad.LeftTrigger, ref _ltDown, NavAction.TriggerLeft);
        Trigger(state.Gamepad.RightTrigger, ref _rtDown, NavAction.TriggerRight);

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

    private bool _ltDown, _rtDown;

    private void Trigger(byte value, ref bool down, NavAction action)
    {
        if (!down && value > 160)
        {
            down = true;
            Action?.Invoke(action);
        }
        else if (down && value < 60)
        {
            down = false;
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
