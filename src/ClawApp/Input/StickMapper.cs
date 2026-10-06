using System.Diagnostics;
using System.Runtime.InteropServices;
using ClawCore.Mapping;

namespace ClawApp.Input;

/// <summary>
/// 스틱 하나를 WASD·방향키·마우스로 바꾼다 (가상 패드와 XInput 스틱 변환이 같이 쓴다).
/// 마우스: 데드존 15% 뒤 제곱 곡선 — 작게 기울이면 정밀하게, 끝까지 기울이면 빠르게. 지난번 호출 뒤 흐른 시간만큼 움직인다.
/// </summary>
public sealed class StickMapper
{
    private readonly StickKeys _keys = new();
    private double _remX, _remY;

    /// <summary>바꿨으면(게임에 보낼 스틱은 0) true. <paramref name="elapsedMs"/> = 지난번 호출 뒤 흐른 시간.</summary>
    public bool Convert(StickOutput output, short x, short y, int speed, double elapsedMs)
    {
        switch (output)
        {
            case StickOutput.Wasd: _keys.Update(x, y, "KB_W", "KB_S", "KB_A", "KB_D"); return true;
            case StickOutput.Arrows: _keys.Update(x, y, "KB_UP", "KB_DOWN", "KB_LEFT", "KB_RIGHT"); return true;
            case StickOutput.Mouse:
                _keys.Release();
                double fx = x / 32767.0, fy = -y / 32767.0;
                var mag = Math.Sqrt(fx * fx + fy * fy);
                if (mag < 0.15) { _remX = _remY = 0; return true; }
                // 1ms당 픽셀 (속도 40 → 끝까지 기울이면 초당 약 1600)
                var scaled = Math.Pow((Math.Min(mag, 1) - 0.15) / 0.85, 2) * Math.Clamp(speed, 1, 100) * 0.04 * Math.Clamp(elapsedMs, 0, 50);
                _remX += fx / mag * scaled;
                _remY += fy / mag * scaled;
                int dx = (int)_remX, dy = (int)_remY;
                _remX -= dx; _remY -= dy;
                if (dx != 0 || dy != 0) SoftInput.MoveMouse(dx, dy);
                return true;
            default:
                _keys.Release();
                return false;
        }
    }

    /// <summary>누르고 있던 방향키를 모두 뗀다 (멈출 때).</summary>
    public void Release()
    {
        _keys.Release();
        _remX = _remY = 0;
    }

    /// <summary>스틱 방향 → 키 (8방향, 켜짐 50% / 꺼짐 40%로 떨림 없이).</summary>
    private sealed class StickKeys
    {
        private readonly bool[] _on = new bool[4];
        private readonly string[] _ids = ["", "", "", ""];

        public void Update(short x, short y, string up, string down, string left, string right)
        {
            double fx = x / 32767.0, fy = y / 32767.0;
            Set(0, up, fy);
            Set(1, down, -fy);
            Set(2, left, -fx);
            Set(3, right, fx);
        }

        private void Set(int i, string id, double amount)
        {
            if (_ids[i] != id && _on[i]) { SoftInput.Up([_ids[i]]); _on[i] = false; } // 설정이 바뀌면 이전 키를 뗀다
            _ids[i] = id;
            var want = _on[i] ? amount > 0.4 : amount > 0.5;
            if (want == _on[i]) return;
            _on[i] = want;
            if (want) SoftInput.Down([id]);
            else SoftInput.Up([id]);
        }

        public void Release()
        {
            for (var i = 0; i < 4; i++)
                if (_on[i]) { SoftInput.Up([_ids[i]]); _on[i] = false; }
        }
    }
}

/// <summary>
/// 가상 패드 없이(XInput 모드) 스틱을 마우스·키로: 실제 패드를 읽어 바꾼다. 게임도 실제 스틱을 그대로 보므로
/// (숨기려면 가상 패드가 필요) 스틱을 안 쓰는 게임(메이플 등)에 알맞다. 게임이 앞에 있는 동안만 돌고, 허브가 앞이면 멈춘다.
/// </summary>
public static class StickConverter
{
    private static readonly object Gate = new();
    private static Thread? _thread;
    private static volatile AdvancedInput? _config;
    private static volatile int _generation; // 끌 때·새로 켤 때 올린다 — 이전 스레드는 자기 번호가 아니면 끝난다

    /// <summary>켜거나(설정 바꾸기) 끈다 (<paramref name="config"/> = null).</summary>
    public static void Use(AdvancedInput? config)
    {
        Thread? stopping = null;
        lock (Gate)
        {
            _config = config;
            if (config is null)
            {
                _generation++;
                stopping = _thread; // 스레드는 스스로 끝나며 키를 뗀다
                _thread = null;
            }
        }
        if (config is null)
        {
            if (stopping is not null && stopping != Thread.CurrentThread) stopping.Join(200); // 앱이 끝나기 전에 키를 다 떼게
            return;
        }
        lock (Gate)
        {
            if (_thread is not null) return;
            var generation = ++_generation;
            _thread = new Thread(() => Loop(generation)) { IsBackground = true, Name = "ClawHub.StickConverter", Priority = ThreadPriority.AboveNormal };
            _thread.Start();
        }
    }

    private static void Loop(int generation)
    {
        var left = new StickMapper();
        var right = new StickMapper();
        var clock = Stopwatch.StartNew();
        var last = clock.Elapsed.TotalMilliseconds;
        var slot = -1;
        long nextScan = 0;
        timeBeginPeriod(1); // 4ms 간격으로 고르게 (기본 타이머는 15.6ms라 커서가 뚝뚝 끊긴다)
        try
        {
            while (generation == _generation && _config is { } cfg)
            {
                XInputState st = default;
                if (slot < 0 || slot == VirtualPad.VirtualSlot || XInputGetState(slot, ref st) != 0)
                {
                    slot = -1;
                    if (Environment.TickCount64 >= nextScan)
                    {
                        nextScan = Environment.TickCount64 + 1000;
                        for (var i = 0; i < 4 && slot < 0; i++)
                            if (i != VirtualPad.VirtualSlot && XInputGetState(i, ref st) == 0) slot = i;
                    }
                }
                var now = clock.Elapsed.TotalMilliseconds;
                var ms = now - last;
                last = now;
                if (slot >= 0)
                {
                    left.Convert(cfg.LeftStick, st.Gamepad.ThumbLX, st.Gamepad.ThumbLY, cfg.MouseSpeed, ms);
                    right.Convert(cfg.RightStick, st.Gamepad.ThumbRX, st.Gamepad.ThumbRY, cfg.MouseSpeed, ms);
                }
                else
                {
                    left.Release();
                    right.Release();
                }
                Thread.Sleep(4);
            }
        }
        finally
        {
            left.Release();
            right.Release();
            timeEndPeriod(1);
        }
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

    [DllImport("xinput1_4.dll")] private static extern int XInputGetState(int index, ref XInputState state);
    [DllImport("winmm.dll")] private static extern uint timeBeginPeriod(uint ms);
    [DllImport("winmm.dll")] private static extern uint timeEndPeriod(uint ms);
}
