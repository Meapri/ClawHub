namespace ClawCore.Controller;

public enum LightMode { Default, Static, Off }

/// <summary>
/// 컨트롤러 펌웨어 설정 중 ClawHub가 바꾸는 것. null이면 "원래 값(baseline) 유지".
/// 레이아웃은 펌웨어 4.19 실기 덤프로 확인 (docs/protocol-controller.md).
/// </summary>
public sealed class ControllerSettings
{
    /// <summary>진동 세기 0–100 (좌우 공통). 원래 100.</summary>
    public int? Vibration { get; set; }
    /// <summary>스틱 안쪽 데드존 % 0–30. 원래 10.</summary>
    public int? LeftStickDeadzone { get; set; }
    public int? RightStickDeadzone { get; set; }
    /// <summary>트리거 안쪽 데드존 % 0–40. 원래 5.</summary>
    public int? TriggerDeadzone { get; set; }
    /// <summary>A/B, X/Y 위치 바꾸기 (닌텐도 배치).</summary>
    public bool? SwapAbxy { get; set; }
    /// <summary>D-pad 대각선 입력 막기.</summary>
    public bool? DpadDiagonalLock { get; set; }

    public LightMode Light { get; set; } = LightMode.Default;
    /// <summary>조명 밝기 0–100.</summary>
    public int? LightBrightness { get; set; }
    /// <summary>단색 모드 색 (RGB).</summary>
    public byte[] LightColor { get; set; } = [0x4C, 0xC2, 0xFF];
    /// <summary>ABXY 버튼 백라이트 밝기 0–100. 원래 50.</summary>
    public int? AbxyBrightness { get; set; }
}

public static class SettingsLayout
{
    private const int MotorL = 0x22, MotorR = 0x23;
    private const int DiagLock = 0x37, SwapAbxy = 0x38;
    private const int LtProfile = 0x20C, RtProfile = 0x21B, TriggerDeadOffset = 3;
    private const int LStick = 0x22A, RStick = 0x23A, StickDzEnable = 1, StickDz = 2;
    private const int LightBase = 0x24A, AnimSize = 220, AnimHeader = 4, Leds = 9;
    private const int CustomAnim = 3; // 4번째 애니메이션 슬롯을 ClawHub 단색용으로 사용
    private const int AbxyBright = 0x5BD;

    /// <summary>조명 관련 바이트 범위 (변경 시 SyncRGB 필요).</summary>
    public static bool TouchesLight(int first, int last) => last >= LightBase && first < LightBase + 1 + 4 * AnimSize + 2
        || last >= AbxyBright && first < AbxyBright + 4;

    public static void Apply(byte[] image, ControllerSettings s)
    {
        if (s.Vibration is { } v) image[MotorL] = image[MotorR] = (byte)Math.Clamp(v, 0, 100);
        if (s.SwapAbxy is { } swap) image[SwapAbxy] = (byte)(swap ? 1 : 0);
        if (s.DpadDiagonalLock is { } diag) image[DiagLock] = (byte)(diag ? 1 : 0);

        if (s.TriggerDeadzone is { } td)
            foreach (var t in new[] { LtProfile, RtProfile })
                image[t + TriggerDeadOffset] = (byte)Math.Clamp(td, 0, 40);

        void Stick(int off, int? dz)
        {
            if (dz is not { } d) return;
            image[off + StickDzEnable] = 1;
            image[off + StickDz] = (byte)Math.Clamp(d, 0, 30);
        }
        Stick(LStick, s.LeftStickDeadzone);
        Stick(RStick, s.RightStickDeadzone);

        switch (s.Light)
        {
            case LightMode.Off:
                SetBrightness(image, image[LightBase], 0);
                break;
            case LightMode.Static:
                var anim = LightBase + 1 + CustomAnim * AnimSize;
                image[LightBase] = CustomAnim;
                image[anim] = 1;       // keyframe 1개
                image[anim + 1] = 9;   // effect (MSI는 항상 9)
                image[anim + 2] = 10;  // speed
                image[anim + 3] = (byte)Math.Clamp(s.LightBrightness ?? 100, 0, 100);
                for (var led = 0; led < Leds; led++)
                    s.LightColor.AsSpan(0, 3).CopyTo(image.AsSpan(anim + AnimHeader + led * 3));
                break;
            default:
                if (s.LightBrightness is { } b) SetBrightness(image, image[LightBase], b);
                break;
        }

        if (s.AbxyBrightness is { } ab)
            for (var i = 0; i < 4; i++) image[AbxyBright + i] = (byte)Math.Clamp(ab, 0, 100);
    }

    private static void SetBrightness(byte[] image, int animIndex, int value)
    {
        if (animIndex is < 0 or > 3) return;
        image[LightBase + 1 + animIndex * AnimSize + 3] = (byte)Math.Clamp(value, 0, 100);
    }

    /// <summary>baseline에서 현재 값 읽기 (설정 화면 초기값 표시용).</summary>
    public static ControllerSettings ReadDefaults(ReadOnlySpan<byte> image) => new()
    {
        Vibration = image[MotorL],
        LeftStickDeadzone = image[LStick + StickDz],
        RightStickDeadzone = image[RStick + StickDz],
        TriggerDeadzone = image[LtProfile + TriggerDeadOffset],
        SwapAbxy = image[SwapAbxy] != 0,
        DpadDiagonalLock = image[DiagLock] != 0,
        LightBrightness = image[LightBase] is <= 3 and var a ? image[LightBase + 1 + a * AnimSize + 3] : 100,
        AbxyBrightness = image[AbxyBright],
    };
}
