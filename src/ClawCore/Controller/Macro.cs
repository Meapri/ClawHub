namespace ClawCore.Controller;

/// <summary>매크로 한 단계: 키를 HoldMs 동안 누르고, 뗀 뒤 GapMs 기다린다.</summary>
public sealed record MacroStep(byte Code, ushort HoldMs, ushort GapMs);

/// <summary>
/// M1/M2 펌웨어 매크로. 레이아웃(MSI 코드 정적 분석):
/// KeyProfile Index=2, Map0 = FL1(17)/FR1(18) → 매크로 블록 [단계 수] + 32 × [키][누름H][누름L][지연H][지연L] (BE, ms).
/// 지연은 "누른 시점부터 다음 단계까지" = 누름 + 간격 (MSI와 같은 방식).
/// </summary>
public static class MacroLayout
{
    public const int MaxSteps = 32;
    private const int StepSize = 5;

    private static int BlockOffset(ClawButton b) => b switch
    {
        ClawButton.M1 => 0xC2,
        ClawButton.M2 => 0x16B,
        _ => throw new ArgumentException("매크로는 M1/M2만 가능", nameof(b)),
    };

    public static void Apply(byte[] image, ClawButton button, IReadOnlyList<MacroStep> steps)
    {
        if (steps.Count is 0 or > MaxSteps) throw new ArgumentException($"매크로는 1~{MaxSteps}단계");
        foreach (var s in steps)
            if (!KeyCodes.IsValidTarget(s.Code)) throw new ArgumentException($"알 수 없는 키 코드 {s.Code}");

        var slot = ProfileLayout.Get(button);
        image[slot.Offset] = 2;                                          // Index = 매크로
        image[slot.Offset + 1] |= ProfileLayout.MapEnableFlag;
        image.AsSpan(slot.MapOffset, ProfileLayout.MaxKeysPerBinding).Clear();
        image[slot.MapOffset] = ProfileLayout.NativeCode(button);       // MSI와 동일: Map0 = FL1/FR1

        var block = BlockOffset(button);
        image.AsSpan(block, 1 + MaxSteps * StepSize).Clear();
        image[block] = (byte)steps.Count;
        for (var i = 0; i < steps.Count; i++)
        {
            var at = block + 1 + i * StepSize;
            var hold = Math.Clamp((int)steps[i].HoldMs, 10, 10000);
            var delay = Math.Clamp(hold + steps[i].GapMs, hold, ushort.MaxValue);
            image[at] = steps[i].Code;
            image[at + 1] = (byte)(hold >> 8);
            image[at + 2] = (byte)hold;
            image[at + 3] = (byte)(delay >> 8);
            image[at + 4] = (byte)delay;
        }
    }
}
