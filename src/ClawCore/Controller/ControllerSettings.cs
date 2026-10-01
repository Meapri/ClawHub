namespace ClawCore.Controller;

public enum LightMode { Default, Static, Off, Breath, Rainbow, Wave }

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
    /// <summary>단색·숨쉬기 색 (RGB).</summary>
    public byte[] LightColor { get; set; } = [0x4C, 0xC2, 0xFF];
    /// <summary>효과 속도 0 느림, 1 보통, 2 빠름.</summary>
    public int LightSpeed { get; set; } = 1;
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
                WriteAnimation(image, s, [Fill(s.LightColor)], uiSpeed: 10);
                break;
            case LightMode.Breath: // 색 ↔ 꺼짐 (MSI 프리셋: 속도 11/14/17)
                WriteAnimation(image, s, [Fill(s.LightColor), Fill([0, 0, 0])], Pick(s.LightSpeed, 11, 14, 17));
                break;
            case LightMode.Rainbow: // 색상환 6단계 (MSI ColorCycle: 13/15/17)
                WriteAnimation(image, s, [.. Enumerable.Range(0, 6).Select(i => Fill(Hue(i * 60)))], Pick(s.LightSpeed, 13, 15, 17));
                break;
            case LightMode.Wave: // 링을 따라 색이 돈다 (MSI Wave: 14/17/20)
                WriteAnimation(image, s, [.. Enumerable.Range(0, 4).Select(WaveFrame)], Pick(s.LightSpeed, 14, 17, 20));
                break;
            default:
                if (s.LightBrightness is { } b) SetBrightness(image, image[LightBase], b);
                break;
        }

        if (s.AbxyBrightness is { } ab)
            for (var i = 0; i < 4; i++) image[AbxyBright + i] = (byte)Math.Clamp(ab, 0, 100);
    }

    // ── 조명 키프레임 ──
    // 애니메이션 = [키프레임 수][효과=9][속도(원시값 = 20 - UI속도, 작을수록 빠름)][밝기] + 8 키프레임 × 9 LED × RGB
    // LED 순서: 0..3 왼쪽 링(아래왼·아래오·위오·위왼), 4..7 오른쪽 링(위오·위왼·아래왼·아래오), 8 ABXY
    private const int MaxKeyframes = 8;
    private static readonly int[] LeftRingClockwise = [3, 2, 1, 0];   // 위왼 → 위오 → 아래오 → 아래왼
    private static readonly int[] RightRingClockwise = [5, 4, 7, 6];

    private static void WriteAnimation(byte[] image, ControllerSettings s, byte[][] frames, int uiSpeed)
    {
        var anim = LightBase + 1 + CustomAnim * AnimSize;
        image[LightBase] = CustomAnim;
        image[anim] = (byte)Math.Min(frames.Length, MaxKeyframes);
        image[anim + 1] = 9;
        image[anim + 2] = (byte)Math.Clamp(20 - uiSpeed, 0, 20);
        image[anim + 3] = (byte)Math.Clamp(s.LightBrightness ?? 100, 0, 100);
        image.AsSpan(anim + AnimHeader, AnimSize - AnimHeader).Clear();
        for (var f = 0; f < Math.Min(frames.Length, MaxKeyframes); f++)
            frames[f].CopyTo(image.AsSpan(anim + AnimHeader + f * Leds * 3));
    }

    private static byte[] Fill(byte[] rgb)
    {
        var frame = new byte[Leds * 3];
        for (var led = 0; led < Leds; led++) rgb.AsSpan(0, 3).CopyTo(frame.AsSpan(led * 3));
        return frame;
    }

    private static byte[] WaveFrame(int step)
    {
        byte[][] palette = [Hue(0), Hue(90), Hue(180), Hue(270)];
        var frame = new byte[Leds * 3];
        for (var pos = 0; pos < 4; pos++)
        {
            var color = palette[(pos + step) % 4];
            color.CopyTo(frame.AsSpan(LeftRingClockwise[pos] * 3));
            color.CopyTo(frame.AsSpan(RightRingClockwise[pos] * 3));
        }
        palette[step % 4].CopyTo(frame.AsSpan(8 * 3));
        return frame;
    }

    private static int Pick(int speed, int slow, int normal, int fast) => speed switch { 0 => slow, 2 => fast, _ => normal };

    /// <summary>채도·명도 최대인 색상(0–359°).</summary>
    private static byte[] Hue(int h)
    {
        double x = 1 - Math.Abs(h / 60.0 % 2 - 1);
        var (r, g, b) = (h % 360) switch
        {
            < 60 => (1d, x, 0d), < 120 => (x, 1d, 0d), < 180 => (0d, 1d, x),
            < 240 => (0d, x, 1d), < 300 => (x, 0d, 1d), _ => (1d, 0d, x),
        };
        return [(byte)(r * 255), (byte)(g * 255), (byte)(b * 255)];
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
