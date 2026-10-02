using System.Runtime.InteropServices;
using ClawCore.Mapping;
using Nefarius.ViGEm.Client;
using Nefarius.ViGEm.Client.Targets;

namespace ClawApp.Input;

/// <summary>
/// 3단계 가상 Xbox 패드 (ViGEmBus). 실제 패드는 서비스가 HidHide로 게임에서 숨기고(ClawHub만 볼 수 있다),
/// 이 클래스가 실제 패드를 1ms마다 읽어 가공한 뒤 가상 패드로 낸다:
/// 고급 입력 엔진의 게임패드 버튼 출력을 겹치고, 스틱을 WASD·방향키·마우스로 바꾸고, 진동은 실제 패드로 돌려준다.
/// <para>허브가 앞에 있는 동안은 <see cref="Paused"/> — 게임에는 손을 뗀 패드로 보인다.</para>
/// </summary>
public sealed class VirtualPad : IDisposable
{
    /// <summary>지금 쓰는 가상 패드 (없으면 null). 엔진이 게임패드 키를 여기로 낸다.</summary>
    public static VirtualPad? Active { get; private set; }

    /// <summary>가상 패드가 차지한 XInput 번호 — 허브·원형 메뉴는 이 번호를 읽지 않는다 (-1 = 없음).</summary>
    public static int VirtualSlot => Active?._virtual ?? -1;

    private readonly ViGEmClient _client;
    private readonly IXbox360Controller _pad;
    private readonly Thread _loop;
    private volatile bool _run = true;
    private volatile AdvancedInput _config;
    private int _virtual = -1;
    private int _physical = -1;
    private long _nextScan;

    /// <summary>true = 게임에는 손 뗀 패드 (허브가 앞, Game Bar 등). 스틱 → 키도 멈춘다.</summary>
    public volatile bool Paused;

    // 엔진이 누르고 있는 게임패드 버튼 (같은 버튼을 여러 곳에서 누를 수 있어 개수로 센다)
    private readonly int[] _held = new int[16];
    private int _ltHeld, _rtHeld;
    private readonly object _heldGate = new();

    private VirtualPad(AdvancedInput config)
    {
        _config = config;
        var before = ConnectedSlots(); // 가상 패드를 붙이기 전 = 실제 패드
        _client = new ViGEmClient();
        _pad = _client.CreateXbox360Controller();
        _pad.AutoSubmitReport = false;
        _pad.FeedbackReceived += (_, e) =>
        {
            // 게임이 가상 패드에 보낸 진동 → 실제 패드
            if (_physical < 0) return;
            var v = new XInputVibration { Left = (ushort)(e.LargeMotor * 257), Right = (ushort)(e.SmallMotor * 257) };
            XInputSetState(_physical, ref v);
        };
        _pad.Connect();
        for (var i = 0; i < 50 && _virtual < 0; i++)
        {
            try { _virtual = _pad.UserIndex; }
            catch (Exception) { Thread.Sleep(20); } // 번호가 정해질 때까지
        }
        _physical = before.FirstOrDefault(s => s != _virtual, -1);
        _loop = new Thread(Loop) { IsBackground = true, Name = "ClawHub.VirtualPad", Priority = ThreadPriority.Highest };
        _loop.Start();
    }

    /// <summary>가상 패드를 만들거나(없으면) 설정만 바꾼다.</summary>
    public static VirtualPad Start(AdvancedInput config)
    {
        if (Active is { } a) { a._config = config; return a; }
        return Active = new VirtualPad(config);
    }

    public static void Stop()
    {
        var a = Active;
        Active = null;
        a?.Dispose();
    }

    // ── 엔진 출력 (게임패드 키) ──────────────────────────────

    /// <summary>엔진이 낼 수 있는 게임패드 키인지 (M1·M2·Camera는 Xbox 패드에 없다).</summary>
    public static bool CanSend(string id) => Bit(id) >= 0 || id is "PAD_LT" or "PAD_RT";

    public void Press(string id, bool down)
    {
        lock (_heldGate)
        {
            var d = down ? 1 : -1;
            if (id == "PAD_LT") _ltHeld = Math.Max(0, _ltHeld + d);
            else if (id == "PAD_RT") _rtHeld = Math.Max(0, _rtHeld + d);
            else if (Bit(id) is var b and >= 0) _held[b] = Math.Max(0, _held[b] + d);
        }
    }

    private static int Bit(string id) => id switch
    {
        "PAD_UP" => 0, "PAD_DOWN" => 1, "PAD_LEFT" => 2, "PAD_RIGHT" => 3,
        "PAD_MENU" => 4, "PAD_VIEW" => 5, "PAD_L3" => 6, "PAD_R3" => 7,
        "PAD_LB" => 8, "PAD_RB" => 9, "PAD_HOME" => 10,
        "PAD_A" => 12, "PAD_B" => 13, "PAD_X" => 14, "PAD_Y" => 15,
        _ => -1,
    };

    // ── 1ms 루프 ────────────────────────────────────────────

    private readonly StickKeys _leftKeys = new(), _rightKeys = new();
    private double _mouseRemX, _mouseRemY;

    private void Loop()
    {
        timeBeginPeriod(1);
        uint lastPacket = uint.MaxValue;
        ushort lastOverlay = 0;
        bool lastLt = false, lastRt = false, lastPaused = false;
        while (_run)
        {
            XInputState st = default;
            if (_physical < 0 || XInputGetStateEx(_physical, ref st) != 0)
            {
                Rescan();
                st = default;
            }

            ushort overlay;
            bool lt, rt;
            lock (_heldGate)
            {
                overlay = 0;
                for (var i = 0; i < 16; i++) if (_held[i] > 0) overlay |= (ushort)(1 << i);
                lt = _ltHeld > 0;
                rt = _rtHeld > 0;
            }
            var paused = Paused;
            var cfg = _config;
            var g = st.Gamepad;

            // 스틱 → 키·마우스 (멈춰 있으면 모두 뗀다)
            var lx = g.ThumbLX; var ly = g.ThumbLY; var rx = g.ThumbRX; var ry = g.ThumbRY;
            if (!paused && Convert(cfg.LeftStick, _leftKeys, lx, ly, cfg.MouseSpeed)) lx = ly = 0;
            else if (paused) _leftKeys.Release();
            if (!paused && Convert(cfg.RightStick, _rightKeys, rx, ry, cfg.MouseSpeed)) rx = ry = 0;
            else if (paused) _rightKeys.Release();

            var changed = st.PacketNumber != lastPacket || overlay != lastOverlay || lt != lastLt || rt != lastRt || paused != lastPaused
                          || cfg.LeftStick != StickOutput.Stick || cfg.RightStick != StickOutput.Stick;
            if (changed)
            {
                if (paused) { g = default; overlay = 0; lt = rt = false; lx = ly = rx = ry = 0; } // 게임에는 손을 뗀 패드
                {
                    _pad.SetButtonsFull((ushort)(g.Buttons | overlay));
                    _pad.SetSliderValue(Nefarius.ViGEm.Client.Targets.Xbox360.Xbox360Slider.LeftTrigger, lt ? (byte)255 : g.LeftTrigger);
                    _pad.SetSliderValue(Nefarius.ViGEm.Client.Targets.Xbox360.Xbox360Slider.RightTrigger, rt ? (byte)255 : g.RightTrigger);
                    _pad.SetAxisValue(Nefarius.ViGEm.Client.Targets.Xbox360.Xbox360Axis.LeftThumbX, lx);
                    _pad.SetAxisValue(Nefarius.ViGEm.Client.Targets.Xbox360.Xbox360Axis.LeftThumbY, ly);
                    _pad.SetAxisValue(Nefarius.ViGEm.Client.Targets.Xbox360.Xbox360Axis.RightThumbX, rx);
                    _pad.SetAxisValue(Nefarius.ViGEm.Client.Targets.Xbox360.Xbox360Axis.RightThumbY, ry);
                }
                try { _pad.SubmitReport(); }
                catch (Exception) { /* 버스가 잠깐 바쁘면 다음 틱에 */ }
                lastPacket = st.PacketNumber; lastOverlay = overlay; lastLt = lt; lastRt = rt; lastPaused = paused;
            }
            Thread.Sleep(1);
        }
        _leftKeys.Release();
        _rightKeys.Release();
    }

    /// <summary>실제 패드 번호를 다시 찾는다 (패드 다시 시작 후 번호가 바뀔 수 있다). 가상 패드 번호는 절대 읽지 않는다.</summary>
    private void Rescan()
    {
        var now = Environment.TickCount64;
        if (now < _nextScan) { _physical = -1; return; }
        _nextScan = now + 500;
        _physical = ConnectedSlots().FirstOrDefault(s => s != _virtual, -1);
    }

    private static List<int> ConnectedSlots()
    {
        var list = new List<int>();
        XInputState st = default;
        for (var i = 0; i < 4; i++) if (XInputGetStateEx(i, ref st) == 0) list.Add(i);
        return list;
    }

    /// <summary>스틱을 키·마우스로. 바꿨으면(게임에 스틱은 0) true.</summary>
    private bool Convert(StickOutput output, StickKeys keys, short x, short y, int speed)
    {
        switch (output)
        {
            case StickOutput.Wasd: keys.Update(x, y, "KB_W", "KB_S", "KB_A", "KB_D"); return true;
            case StickOutput.Arrows: keys.Update(x, y, "KB_UP", "KB_DOWN", "KB_LEFT", "KB_RIGHT"); return true;
            case StickOutput.Mouse:
                keys.Release();
                // 데드존 15% 뒤 제곱 곡선 — 작게 기울이면 정밀하게, 끝까지 기울이면 빠르게
                double fx = x / 32767.0, fy = -y / 32767.0;
                var mag = Math.Sqrt(fx * fx + fy * fy);
                if (mag < 0.15) return true;
                var scaled = Math.Pow((Math.Min(mag, 1) - 0.15) / 0.85, 2) * Math.Clamp(speed, 1, 100) * 0.04; // 1ms당 픽셀 (40 → 끝까지 기울이면 초당 약 1600)
                _mouseRemX += fx / mag * scaled;
                _mouseRemY += fy / mag * scaled;
                int dx = (int)_mouseRemX, dy = (int)_mouseRemY;
                _mouseRemX -= dx; _mouseRemY -= dy;
                if (dx != 0 || dy != 0) SoftInput.MoveMouse(dx, dy);
                return true;
            default:
                keys.Release();
                return false;
        }
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

    public void Dispose()
    {
        _run = false;
        _loop.Join(500);
        try { _pad.Disconnect(); } catch (Exception) { /* 이미 끊김 */ }
        _client.Dispose();
    }

    // ── XInput ─────────────────────────────────────────────

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

    [StructLayout(LayoutKind.Sequential)]
    private struct XInputVibration
    {
        public ushort Left, Right;
    }

    // 100번 = XInputGetStateEx (가이드 버튼도 읽는다)
    [DllImport("xinput1_4.dll", EntryPoint = "#100")] private static extern int XInputGetStateEx(int index, ref XInputState state);
    [DllImport("xinput1_4.dll")] private static extern int XInputSetState(int index, ref XInputVibration vibration);
    [DllImport("winmm.dll")] private static extern uint timeBeginPeriod(uint ms);
}
