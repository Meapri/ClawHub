namespace ClawCore.Controller;

/// <summary>자이로를 어디로 내보낼지 (펌웨어 SensorModule).</summary>
public enum GyroOutput { Off, Mouse, LeftStick, RightStick }

/// <summary>자이로를 켜는 방식. 버튼이 없으면(<see cref="GyroTuning.Button"/> = null) 늘 켜짐.</summary>
public enum GyroActivation { HoldToEnable, Toggle, HoldToDisable }

/// <summary>
/// 자이로 조준 (펌웨어가 처리 — 드라이버·소프트웨어 입력 없음). MSI Center M과 같은 방식으로 쓴다:
/// 감도 방식 4 + 데드존/감도 % × 254/100.
/// </summary>
public sealed class GyroTuning
{
    public GyroOutput Output { get; set; } = GyroOutput.RightStick;
    /// <summary>이 버튼을 누르는 동안 등 (null = 늘 켜짐). 그 버튼의 원래 동작은 그대로다.</summary>
    public ClawButton? Button { get; set; } = ClawButton.LT;
    public GyroActivation Activation { get; set; } = GyroActivation.HoldToEnable;
    /// <summary>0–100 % (MSI 기본 10).</summary>
    public int Deadzone { get; set; } = 10;
    /// <summary>1–100 % (MSI 기본 20).</summary>
    public int Sensitivity { get; set; } = 20;
}

/// <summary>스틱 하나. null 값 = 손대지 않음 (전역 설정·기기 기본값 그대로).</summary>
public sealed class StickTuning
{
    /// <summary>안쪽 데드존 0–30 (기기 기본 10).</summary>
    public int? InnerDeadzone { get; set; }
    /// <summary>바깥 데드존 70–100: 이만큼 기울이면 끝까지 기울인 것으로 (기기 기본 100 = 끔).</summary>
    public int? OuterDeadzone { get; set; }
}

/// <summary>트리거 하나. null 값 = 손대지 않음.</summary>
public sealed class TriggerTuning
{
    /// <summary>안쪽 데드존 0–40 (기기 기본 5).</summary>
    public int? InnerDeadzone { get; set; }
    /// <summary>바깥 데드존 60–100: 이만큼 당기면 끝까지 당긴 것으로 (기기 기본 95).</summary>
    public int? OuterDeadzone { get; set; }
}

/// <summary>
/// 매핑 프로필별 컨트롤러 펌웨어 조정 (Xbox Ally의 Armoury Crate처럼 펌웨어가 직접 처리 — 안티치트·지연 없음).
/// 전역 컨트롤러 설정 위에 덮어쓰고, 프로필이 풀리면 전역 설정으로 돌아간다.
/// </summary>
public sealed class ControllerTuning
{
    public GyroTuning? Gyro { get; set; }
    public StickTuning? LeftStick { get; set; }
    public StickTuning? RightStick { get; set; }
    public TriggerTuning? LeftTrigger { get; set; }
    public TriggerTuning? RightTrigger { get; set; }
    /// <summary>왼쪽·오른쪽 스틱 바꾸기 (MSI "스틱 바꾸기").</summary>
    public bool SwapSticks { get; set; }

    [System.Text.Json.Serialization.JsonIgnore]
    public bool IsEmpty => Gyro is null && LeftStick is null && RightStick is null && LeftTrigger is null && RightTrigger is null && !SwapSticks;

    public ControllerTuning Clone() => new()
    {
        Gyro = Gyro is { } g ? new GyroTuning { Output = g.Output, Button = g.Button, Activation = g.Activation, Deadzone = g.Deadzone, Sensitivity = g.Sensitivity } : null,
        LeftStick = LeftStick is { } l ? new StickTuning { InnerDeadzone = l.InnerDeadzone, OuterDeadzone = l.OuterDeadzone } : null,
        RightStick = RightStick is { } r ? new StickTuning { InnerDeadzone = r.InnerDeadzone, OuterDeadzone = r.OuterDeadzone } : null,
        LeftTrigger = LeftTrigger is { } lt ? new TriggerTuning { InnerDeadzone = lt.InnerDeadzone, OuterDeadzone = lt.OuterDeadzone } : null,
        RightTrigger = RightTrigger is { } rt ? new TriggerTuning { InnerDeadzone = rt.InnerDeadzone, OuterDeadzone = rt.OuterDeadzone } : null,
        SwapSticks = SwapSticks,
    };
}

/// <summary>
/// 프로필 이미지에 <see cref="ControllerTuning"/>을 쓴다 (docs/protocol-controller.md, MSI API_ControlMode 디컴파일 기준).
/// MSI Center M이 실제로 쓰는 필드만 쓴다 — 의미가 확인되지 않은 바이트(스틱 Flip·Target·Linear, 터보, 빠른 트리거)는 건드리지 않는다.
/// </summary>
public static class TuningLayout
{
    // SensorModule (10바이트): [플래그][항상 켜짐][켜는 버튼][감도 방식][안티 데드존][데드존][감도][원본2][목표2][스틱 선택]
    private const int Sensor = 0x29;
    private const byte MotionEnable = 0x01, PressToEnable = 0x02, OutputMouse = 0x04, PressToToggle = 0x80;
    // StickKeyProfile (16바이트): +0 사용, +1/+2 데드존 사용/값, +3 바꾸기 사용, +6 바꾸기 대상, +9/+10 바깥 데드존 사용/값
    private const int LStick = 0x22A, RStick = 0x23A;
    // TriggerKeyProfile (15바이트): +0 Index, +1 플래그(bit1 데드존 사용), +3 안쪽, +4 바깥쪽
    private const int LTrigger = 0x20C, RTrigger = 0x21B;
    private const byte KeyDeadEn = 0x02;

    public static void Apply(byte[] image, ControllerTuning t)
    {
        if (t.Gyro is { } g) WriteGyro(image, g);

        Stick(image, LStick, t.LeftStick, swapTo: t.SwapSticks ? 2 : null);
        Stick(image, RStick, t.RightStick, swapTo: t.SwapSticks ? 1 : null);
        Trigger(image, LTrigger, t.LeftTrigger);
        Trigger(image, RTrigger, t.RightTrigger);
    }

    private static void WriteGyro(byte[] image, GyroTuning g)
    {
        var s = image.AsSpan(Sensor, 10);
        if (g.Output == GyroOutput.Off)
        {
            s[0] &= unchecked((byte)~MotionEnable); // 나머지 설정은 그대로 두고 끄기만
            return;
        }
        byte flag = MotionEnable;
        if (g.Output == GyroOutput.Mouse) flag |= OutputMouse;
        if (g.Button is not null)
            flag |= g.Activation switch
            {
                GyroActivation.Toggle => PressToToggle,
                GyroActivation.HoldToDisable => (byte)(PressToEnable | PressToToggle), // MSI: 두 비트 = 누르는 동안 꺼짐
                _ => PressToEnable,
            };
        s[0] = flag;
        s[1] = (byte)(g.Button is null ? 1 : 0);                        // 늘 켜짐
        s[2] = g.Button is { } b ? ProfileLayout.NativeCode(b) : (byte)0; // 켜는 버튼 (게임패드 코드)
        s[3] = 4;                                                       // 감도 방식: 데드존·감도 % (MSI와 같음)
        s[5] = (byte)(Math.Clamp(g.Deadzone, 0, 100) * 254 / 100);
        s[6] = (byte)(Math.Clamp(g.Sensitivity, 1, 100) * 254 / 100);
        s[7] = 255;
        s[8] = 255;
        s[9] = (byte)(g.Output == GyroOutput.LeftStick ? 0 : g.Output == GyroOutput.RightStick ? 1 : 0);
    }

    private static void Stick(byte[] image, int off, StickTuning? t, int? swapTo)
    {
        if (t?.InnerDeadzone is { } dz)
        {
            image[off + 1] = 1;
            image[off + 2] = (byte)Math.Clamp(dz, 0, 30);
        }
        if (t?.OuterDeadzone is { } edge)
        {
            image[off + 9] = 1;
            image[off + 10] = (byte)Math.Clamp(edge, 70, 100);
        }
        if (swapTo is { } to)
        {
            image[off + 0] = 1;
            image[off + 3] = 1;
            image[off + 6] = (byte)to;
        }
    }

    private static void Trigger(byte[] image, int off, TriggerTuning? t)
    {
        if (t is null || (t.InnerDeadzone is null && t.OuterDeadzone is null)) return;
        image[off + 1] |= KeyDeadEn;
        if (t.InnerDeadzone is { } i) image[off + 3] = (byte)Math.Clamp(i, 0, 40);
        if (t.OuterDeadzone is { } o) image[off + 4] = (byte)Math.Clamp(o, 60, 100);
    }
}
