using System.Runtime.InteropServices;

namespace ClawApp.Input;

/// <summary>
/// 펌웨어 키 ID(KB_·MS_·MD_) → Windows 입력. 고급 입력 엔진이 소프트웨어로 키·마우스를 낼 때 쓴다.
/// 키보드는 스캔 코드로 보낸다 (DirectInput·Raw Input으로 키를 읽는 게임도 받게).
/// </summary>
internal static class SoftInput
{
    private readonly record struct Key(ushort Vk, bool Extended);

    private static readonly Dictionary<string, Key> Keys = Build();

    private static Dictionary<string, Key> Build()
    {
        var d = new Dictionary<string, Key>(StringComparer.OrdinalIgnoreCase);
        void K(string id, int vk, bool ext = false) => d["KB_" + id] = new((ushort)vk, ext);
        K("ESC", 0x1B);
        for (var i = 1; i <= 12; i++) K($"F{i}", 0x70 + i - 1);
        K("F22", 0x85); K("F23", 0x86);
        K("GRAVE", 0xC0);
        for (var i = 0; i <= 9; i++) K($"{i}", 0x30 + i);
        for (var c = 'A'; c <= 'Z'; c++) K(c.ToString(), c);
        K("MINUS", 0xBD); K("EQUAL", 0xBB); K("BACKSPACE", 0x08); K("TAB", 0x09);
        K("LBRACKET", 0xDB); K("RBRACKET", 0xDD); K("BACKSLASH", 0xDC); K("CAPSLOCK", 0x14);
        K("SEMICOLON", 0xBA); K("QUOTE", 0xDE); K("ENTER", 0x0D); K("LSHIFT", 0xA0);
        K("COMMA", 0xBC); K("PERIOD", 0xBE); K("SLASH", 0xBF); K("RSHIFT", 0xA1);
        K("LCTRL", 0xA2); K("WIN", 0x5B, true); K("LALT", 0xA4); K("SPACE", 0x20);
        K("RALT", 0x15, true); K("RCTRL", 0x19, true); // 한국어 키보드: 오른쪽 Alt = 한/영, 오른쪽 Ctrl = 한자
        K("INSERT", 0x2D, true); K("HOME", 0x24, true); K("PAGEUP", 0x21, true);
        K("DELETE", 0x2E, true); K("END", 0x23, true); K("PAGEDOWN", 0x22, true);
        K("UP", 0x26, true); K("DOWN", 0x28, true); K("LEFT", 0x25, true); K("RIGHT", 0x27, true);
        K("NUMLOCK", 0x90); K("NUM_DIV", 0x6F, true); K("NUM_MUL", 0x6A); K("NUM_SUB", 0x6D); K("NUM_ADD", 0x6B);
        K("NUM_DOT", 0x6E); K("NUM_ENTER", 0x0D, true);
        for (var i = 0; i <= 9; i++) K($"NUM{i}", 0x60 + i);
        void M(string id, int vk) => d[id] = new((ushort)vk, true);
        M("MD_PLAY", 0xB3); M("MD_STOP", 0xB2); M("MD_NEXT", 0xB0); M("MD_PREV", 0xB1);
        M("MD_VOLUP", 0xAF); M("MD_VOLDOWN", 0xAE); M("MD_MUTE", 0xAD);
        return d;
    }

    /// <summary>소프트웨어로 낼 수 있는 키인지 (게임패드 키는 3단계 가상 패드가 켜져 있을 때만).</summary>
    public static bool CanSend(string id) => Keys.ContainsKey(id) || id.StartsWith("MS_", StringComparison.OrdinalIgnoreCase)
        || (VirtualPad.Active is not null && VirtualPad.CanSend(id));

    /// <summary>마우스를 상대 이동 (가상 패드의 스틱 → 마우스).</summary>
    public static void MoveMouse(int dx, int dy)
    {
        var m = new Input { Type = 0, U = new InputUnion { Mi = new MouseInput { Dx = dx, Dy = dy, Flags = 0x0001 /* MOVE */ } } };
        SendInput(1, [m], Marshal.SizeOf<Input>());
    }

    /// <summary>키들을 차례로 누른다 (조합키: Ctrl → Q 순서).</summary>
    public static void Down(IReadOnlyList<string> ids)
    {
        foreach (var id in ids) Send(id, down: true);
    }

    /// <summary>거꾸로 뗀다 (Q → Ctrl).</summary>
    public static void Up(IReadOnlyList<string> ids)
    {
        for (var i = ids.Count - 1; i >= 0; i--) Send(ids[i], down: false);
    }

    /// <summary>가상 키 하나를 뗀다 (운반 글자가 새어 나갔을 때 되돌리기).</summary>
    public static void UpVk(ushort vk)
    {
        var input = new Input { Type = 1, U = new InputUnion { Ki = new KeyboardInput { Vk = vk, Flags = KeyeventfKeyup } } };
        SendInput(1, [input], Marshal.SizeOf<Input>());
    }

    private static void Send(string id, bool down)
    {
        if (id.StartsWith("PAD_", StringComparison.Ordinal))
        {
            VirtualPad.Active?.Press(id, down); // 3단계: 가상 패드 버튼
            return;
        }
        if (Keys.TryGetValue(id, out var k))
        {
            var scan = (ushort)MapVirtualKey(k.Vk, 0);
            var flags = (scan != 0 ? KeyeventfScancode : 0u) | (k.Extended ? KeyeventfExtendedkey : 0) | (down ? 0 : KeyeventfKeyup);
            var input = new Input { Type = 1, U = new InputUnion { Ki = new KeyboardInput { Vk = scan != 0 ? (ushort)0 : k.Vk, Scan = scan, Flags = flags } } };
            SendInput(1, [input], Marshal.SizeOf<Input>());
            return;
        }
        uint mouse = id.ToUpperInvariant() switch
        {
            "MS_LEFT" => down ? 0x0002u : 0x0004u,
            "MS_RIGHT" => down ? 0x0008u : 0x0010u,
            "MS_MIDDLE" => down ? 0x0020u : 0x0040u,
            "MS_B4" or "MS_B5" => down ? 0x0080u : 0x0100u,
            "MS_WHEELUP" or "MS_WHEELDOWN" => down ? 0x0800u : 0u, // 휠은 누를 때 한 칸
            _ => 0u,
        };
        if (mouse == 0) return;
        var data = id.ToUpperInvariant() switch
        {
            "MS_WHEELUP" => 120,
            "MS_WHEELDOWN" => -120,
            "MS_B4" => 1, // XBUTTON1
            "MS_B5" => 2, // XBUTTON2
            _ => 0,
        };
        var m = new Input { Type = 0, U = new InputUnion { Mi = new MouseInput { MouseData = data, Flags = mouse } } };
        SendInput(1, [m], Marshal.SizeOf<Input>());
    }

    private const uint KeyeventfExtendedkey = 0x0001, KeyeventfKeyup = 0x0002, KeyeventfScancode = 0x0008;

    [StructLayout(LayoutKind.Sequential)]
    private struct Input
    {
        public uint Type;
        public InputUnion U;
    }

    // 공용체 크기 = 가장 큰 멤버(MOUSEINPUT) — SendInput이 cbSize를 맞게 받는다
    [StructLayout(LayoutKind.Explicit)]
    private struct InputUnion
    {
        [FieldOffset(0)] public MouseInput Mi;
        [FieldOffset(0)] public KeyboardInput Ki;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct MouseInput
    {
        public int Dx, Dy;
        public int MouseData;
        public uint Flags, Time;
        public IntPtr ExtraInfo;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct KeyboardInput
    {
        public ushort Vk, Scan;
        public uint Flags, Time;
        public IntPtr ExtraInfo;
    }

    [DllImport("user32.dll", SetLastError = true)] private static extern uint SendInput(uint count, Input[] inputs, int size);
    [DllImport("user32.dll")] private static extern uint MapVirtualKey(uint code, uint mapType);
}
