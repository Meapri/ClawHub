using System.Runtime.InteropServices;

namespace ClawApp.Services;

public enum CaptureAction { Screenshot, ToggleRecording, SaveClip }

/// <summary>
/// Windows Game Bar 캡처를 단축키 입력으로 실행한다 (서비스 불필요, 사용자 세션에서 SendInput).
/// Windows 기본 단축키: 스크린샷 Win+Alt+PrtSc, 녹화 시작/중지 Win+Alt+R, 방금 일어난 일 기록(최근 30초) Win+Alt+G.
/// 시스템 단축키라 Game Bar가 가로채고 게임에는 전달되지 않는다. 사용자가 Game Bar 설정에서 단축키를 바꿨거나
/// Game Bar가 꺼져 있으면 동작하지 않는다. 캡처 대상은 앞에 있는 창(게임)이므로 호출 전에 런처를 숨겨야 한다.
/// "최근 30초 저장"은 설정 → 게임 → 캡처 → "방금 일어난 일 기록"(백그라운드 녹화)이 켜져 있어야 한다.
/// </summary>
public static class GameCapture
{
    private const ushort VkLWin = 0x5B, VkLMenu = 0xA4, VkSnapshot = 0x2C, VkR = 0x52, VkG = 0x47;

    public static string Label(CaptureAction a) => a switch
    {
        CaptureAction.Screenshot => "스크린샷",
        CaptureAction.ToggleRecording => "녹화 시작/중지",
        _ => "최근 30초 저장",
    };

    /// <summary>단축키를 보낸다. 입력이 모두 들어갔으면 true (UIPI로 막히면 false).</summary>
    public static bool Send(CaptureAction action)
    {
        var key = action switch
        {
            CaptureAction.Screenshot => VkSnapshot,
            CaptureAction.ToggleRecording => VkR,
            _ => VkG,
        };
        ushort[] chord = [VkLWin, VkLMenu, key];

        var inputs = new Input[chord.Length * 2];
        for (var i = 0; i < chord.Length; i++)
        {
            inputs[i] = Key(chord[i], up: false);
            inputs[inputs.Length - 1 - i] = Key(chord[i], up: true); // 거꾸로 뗀다
        }
        return SendInput((uint)inputs.Length, inputs, Marshal.SizeOf<Input>()) == inputs.Length;
    }

    private static Input Key(ushort vk, bool up)
    {
        const uint ExtendedKey = 0x1, KeyUp = 0x2;
        var flags = up ? KeyUp : 0;
        if (vk is VkLWin or VkSnapshot) flags |= ExtendedKey; // 두 키 모두 E0 확장 스캔 코드
        return new Input
        {
            Type = 1, // INPUT_KEYBOARD
            U = new InputUnion { Ki = new KeybdInput { Vk = vk, Scan = (ushort)MapVirtualKey(vk, 0 /* VK_TO_VSC */), Flags = flags } },
        };
    }

    // INPUT 공용체: 가장 큰 멤버(MOUSEINPUT) 크기여야 SendInput이 cbSize를 받아 준다.
#pragma warning disable CS0649 // MOUSEINPUT은 크기 맞춤용으로만 둔다
    [StructLayout(LayoutKind.Sequential)]
    private struct Input
    {
        public uint Type;
        public InputUnion U;
    }

    [StructLayout(LayoutKind.Explicit)]
    private struct InputUnion
    {
        [FieldOffset(0)] public MouseInput Mi;
        [FieldOffset(0)] public KeybdInput Ki;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct MouseInput
    {
        public int Dx, Dy;
        public uint MouseData, Flags, Time;
        public IntPtr ExtraInfo;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct KeybdInput
    {
        public ushort Vk;
        public ushort Scan;
        public uint Flags;
        public uint Time;
        public IntPtr ExtraInfo;
    }
#pragma warning restore CS0649

    [DllImport("user32.dll", SetLastError = true)]
    private static extern uint SendInput(uint count, Input[] inputs, int size);

    [DllImport("user32.dll")]
    private static extern uint MapVirtualKey(uint code, uint mapType);
}
