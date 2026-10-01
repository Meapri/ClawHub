namespace ClawCore.Controller;

/// <summary>Claw 컨트롤러 HID 프로토콜 상수. 출처: docs/protocol-controller.md</summary>
public static class ControllerProtocol
{
    public const int VendorId = 0x0DB0;
    public static readonly int[] ProductIds = [0x1901, 0x1902];

    public const byte ReportId = 0x0F;
    public const int ReportLength = 64;
    public const int ChunkSize = 55;

    public const byte CmdReadProfile = 0x04;
    public const byte CmdReadProfileAck = 0x05;
    public const byte CmdWriteProfileRam = 0x21;
    public const byte CmdSwitchMode = 0x24;
    public const byte CmdReadMode = 0x26;
    public const byte CmdModeAck = 0x27;
    /// <summary>RAM의 조명 프로필을 LED에 반영 (MSI가 조명 변경 후 보냄, 응답 없음).</summary>
    public const byte CmdSyncRgb = 0x09;
    /// <summary>자이로/가속도 실시간 스트림 켜기(1)/끄기(0). 응답은 0x30 입력 리포트.</summary>
    public const byte CmdMotionStream = 0x2F;
    public const byte CmdMotionData = 0x30;

    /// <summary>ClawHub가 보낼 수 있는 명령. 나머지(F0 DFU, 28 Reset, 22 Flash 등)는 전송 자체를 막는다.</summary>
    public static readonly HashSet<byte> AllowedCommands =
        [CmdReadProfile, CmdWriteProfileRam, CmdSwitchMode, CmdReadMode, CmdSyncRgb, CmdMotionStream];

    /// <summary>응답이 명령 ack로 취급되면 안 되는 비동기 리포트.</summary>
    public static readonly HashSet<byte> UnsolicitedReports = [0xF4, 0x0A, 0x27, 0x30];

    public static byte[] BuildPacket(byte cmd, ReadOnlySpan<byte> args)
    {
        if (!AllowedCommands.Contains(cmd))
            throw new InvalidOperationException($"차단된 컨트롤러 명령 0x{cmd:X2}");
        if (args.Length > ReportLength - 5)
            throw new ArgumentException("인자가 너무 깁니다", nameof(args));

        var buf = new byte[ReportLength];
        buf[0] = ReportId;
        buf[3] = 0x3C;
        buf[4] = cmd;
        args.CopyTo(buf.AsSpan(5));
        return buf;
    }

    public static byte[] BuildRead(int offset, int length) =>
        BuildPacket(CmdReadProfile, [0x00, (byte)(offset >> 8), (byte)offset, (byte)length]);

    public static byte[] BuildWriteRam(int offset, ReadOnlySpan<byte> data)
    {
        if (data.Length is 0 or > ChunkSize) throw new ArgumentOutOfRangeException(nameof(data));
        Span<byte> args = stackalloc byte[4 + data.Length];
        args[0] = 0x01;
        args[1] = (byte)(offset >> 8);
        args[2] = (byte)offset;
        args[3] = (byte)data.Length;
        data.CopyTo(args[4..]);
        return BuildPacket(CmdWriteProfileRam, args);
    }

    public static byte[] BuildSwitchMode(GamepadMode mode, MKeysFunction mk)
    {
        if (mode is not (GamepadMode.XInput or GamepadMode.Desktop))
            throw new InvalidOperationException($"허용되지 않은 모드 {mode}");
        return BuildPacket(CmdSwitchMode, [(byte)mode, (byte)mk]);
    }
}

public enum GamepadMode : byte
{
    Offline = 0, XInput = 1, DInput = 2, Msi = 3, Desktop = 4, Bios = 5, Testing = 6,
}

public enum MKeysFunction : byte { Macro = 0, Combination = 1, Disabled = 2 }
