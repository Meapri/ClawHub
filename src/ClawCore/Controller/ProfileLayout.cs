namespace ClawCore.Controller;

/// <summary>매핑 가능한 물리 버튼 (펌웨어가 키 출력으로 재매핑할 수 있는 것만).</summary>
public enum ClawButton
{
    DpadUp, DpadDown, DpadLeft, DpadRight,
    LB, RB, LT, RT, L3, R3,
    A, B, X, Y,
    M1, M2,
}

/// <summary>1478바이트 프로필 이미지 내 버튼별 슬롯 위치. docs/protocol-controller.md 참고.</summary>
public static class ProfileLayout
{
    public const int ImageLength = 1478;
    public const int MaxKeysPerBinding = 5;
    public const byte MapEnableFlag = 0x04;

    /// <summary>매핑 테이블 전체 범위 (D-pad KeyProfile ~ RT TriggerKeyProfile 끝). 복원 시 이 범위만 다시 쓴다.</summary>
    public const int MappingRangeStart = 0x3A;
    public const int MappingRangeEnd = 0x22A;

    public readonly record struct Slot(int Offset, int MapOffset);

    // KeyProfile: [Index][Flag][Turbo][Map0..4]  → Map은 +3
    // TriggerKeyProfile: [Index][Flag][Turbo][Dead][EdgeDead][Map0..4] → Map은 +5
    public static Slot Get(ClawButton b) => b switch
    {
        ClawButton.DpadUp => Key(0x3A),
        ClawButton.DpadDown => Key(0x42),
        ClawButton.DpadLeft => Key(0x4A),
        ClawButton.DpadRight => Key(0x52),
        ClawButton.LB => Key(0x5A),
        ClawButton.RB => Key(0x62),
        ClawButton.L3 => Key(0x6A),
        ClawButton.R3 => Key(0x72),
        ClawButton.A => Key(0x7A),
        ClawButton.B => Key(0x82),
        ClawButton.X => Key(0x8A),
        ClawButton.Y => Key(0x92),
        ClawButton.M1 => Key(0xBA),
        ClawButton.M2 => Key(0x163),
        ClawButton.LT => new(0x20C, 0x20C + 5),
        ClawButton.RT => new(0x21B, 0x21B + 5),
        _ => throw new ArgumentOutOfRangeException(nameof(b)),
    };

    private static Slot Key(int offset) => new(offset, offset + 3);

    /// <summary>버튼이 원래 내보내는 게임패드 코드 (baseline이 손상된 경우 대비용).</summary>
    public static byte NativeCode(ClawButton b) => b switch
    {
        ClawButton.DpadUp => 1, ClawButton.DpadDown => 2, ClawButton.DpadLeft => 3, ClawButton.DpadRight => 4,
        ClawButton.LB => 5, ClawButton.RB => 6, ClawButton.L3 => 7, ClawButton.R3 => 8,
        ClawButton.A => 9, ClawButton.B => 10, ClawButton.X => 11, ClawButton.Y => 12,
        ClawButton.M1 => 17, ClawButton.M2 => 18, ClawButton.LT => 19, ClawButton.RT => 20,
        _ => 255,
    };

    /// <summary>
    /// baseline 이미지 위에 바인딩을 덮어쓴 새 이미지를 만든다.
    /// Index/Flag/Turbo 등 나머지 바이트는 baseline 값을 그대로 유지해 펌웨어가 기대하는 형식을 깨지 않는다.
    /// </summary>
    public static byte[] Apply(ReadOnlySpan<byte> baseline, IReadOnlyDictionary<ClawButton, byte[]> bindings)
    {
        if (baseline.Length != ImageLength)
            throw new InvalidOperationException($"프로필 길이 {baseline.Length} ≠ {ImageLength} — 지원하지 않는 펌웨어 레이아웃");

        var image = baseline.ToArray();
        foreach (var (button, codes) in bindings)
        {
            if (codes.Length is 0 or > MaxKeysPerBinding)
                throw new ArgumentException($"{button}: 키는 1~{MaxKeysPerBinding}개");
            foreach (var c in codes)
                if (!KeyCodes.IsValidTarget(c)) throw new ArgumentException($"{button}: 알 수 없는 코드 {c}");

            var slot = Get(button);
            image[slot.Offset] = 1; // Index = 매핑 (M1/M2 매크로 해제)
            // 실기 덤프: 기본 버튼 Flag=0x00, 키보드로 매핑된 M1/M2는 0x04 → bit2(MapOrMacroEn)가 재매핑 활성 비트.
            // 원래 코드로 되돌리는 경우는 baseline Flag를 그대로 둔다.
            var native = codes.Length == 1 && codes[0] == NativeCode(button);
            image[slot.Offset + 1] = native ? baseline[slot.Offset + 1] : (byte)(baseline[slot.Offset + 1] | MapEnableFlag);
            var map = image.AsSpan(slot.MapOffset, MaxKeysPerBinding);
            map.Clear();
            codes.CopyTo(map);
        }
        return image;
    }

    public static byte[] ReadBinding(ReadOnlySpan<byte> image, ClawButton b)
    {
        var map = image.Slice(Get(b).MapOffset, MaxKeysPerBinding);
        return map.ToArray().Where(c => c is not 0 and not 255).ToArray();
    }
}
