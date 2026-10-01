using System.Management;

namespace ClawCore.Ec;

/// <summary>
/// root\WMI\MSI_ACPI 래퍼. 관리자(서비스) 권한 필요. 출처: docs/protocol-ec.md
/// 쓰기는 검증된 인덱스만 허용하고, 모든 호출은 단일 락으로 직렬화한다.
/// </summary>
public sealed class MsiWmi
{
    private const int PackageSize = 32;

    private static readonly HashSet<byte> WritableDataIndices = [EcIndex.Pl1, EcIndex.Pl2, EcIndex.CoolerBoost, EcIndex.ShiftMode, EcIndex.FanCurveMode, EcIndex.BatteryLimit];
    private static readonly HashSet<byte> WritableFanIndices = [1, 2];

    private static readonly Dictionary<(string Method, byte Index), int> PayloadLength = new()
    {
        [("Data", 0)] = 1, // 모든 Data 인덱스는 1바이트 (아래 Len()에서 처리)
        [("Fan", 0)] = 8, [("Fan", 1)] = 8, [("Fan", 2)] = 8, [("Fan", 3)] = 8,
        [("Temperature", 0)] = 3, [("Temperature", 1)] = 8, [("Temperature", 2)] = 8,
        [("AP", 0)] = 6, [("AP", 1)] = 3, [("AP", 2)] = 7, [("AP", 3)] = 8,
        [("MasterBattery", 0)] = 2,
    };

    private readonly ManagementScope _scope;
    private readonly ManagementObject _acpi;
    private readonly object _lock = new();

    public int WmiMajor { get; }
    public int WmiMinor { get; }
    public string EcVersion { get; }

    /// <summary>WMI v2만 레이아웃이 검증됨. 그 외 버전이면 쓰기를 막는다.</summary>
    public bool WritesEnabled => WmiMajor == 2;

    private MsiWmi(ManagementScope scope, ManagementObject acpi)
    {
        _scope = scope;
        _acpi = acpi;

        var wmi = InvokeRaw("Get_WMI", null);
        (WmiMajor, WmiMinor) = (wmi[1], wmi[2]);
        var ec = InvokeRaw("Get_EC", null);
        EcVersion = System.Text.Encoding.ASCII.GetString(ec, 2, PackageSize - 2).TrimEnd('\0', ' ');
    }

    public static MsiWmi Open()
    {
        var scope = new ManagementScope(@"\\.\root\WMI");
        scope.Connect();
        using var searcher = new ManagementObjectSearcher(scope, new ObjectQuery("SELECT * FROM MSI_ACPI"));
        var acpi = searcher.Get().Cast<ManagementObject>().FirstOrDefault()
            ?? throw new InvalidOperationException("MSI_ACPI 인스턴스가 없습니다 (관리자 권한 또는 MSI 드라이버 확인)");
        return new MsiWmi(scope, acpi);
    }

    public byte GetData(byte index) => Get("Data", index)[0];

    public void SetData(byte index, byte value)
    {
        if (!WritesEnabled) throw new InvalidOperationException($"WMI v{WmiMajor}는 쓰기 미지원");
        if (!WritableDataIndices.Contains(index)) throw new InvalidOperationException($"쓰기 금지된 EC 인덱스 0x{index:X2}");
        Set("Data", index, [value]);
    }

    public byte[] GetFan(byte index) => Get("Fan", index);

    public void SetFan(byte index, byte[] payload)
    {
        if (!WritesEnabled) throw new InvalidOperationException($"WMI v{WmiMajor}는 쓰기 미지원");
        if (!WritableFanIndices.Contains(index)) throw new InvalidOperationException($"쓰기 금지된 팬 인덱스 {index}");
        Set("Fan", index, payload);
    }

    public byte[] GetTemperature(byte index) => Get("Temperature", index);
    public byte[] GetAp(byte index) => Get("AP", index);

    private static int Len(string method, byte index) =>
        method == "Data" ? 1 : PayloadLength.TryGetValue((method, index), out var n) ? n
            : throw new ArgumentException($"알 수 없는 {method}({index})");

    private byte[] Get(string method, byte index)
    {
        var input = new byte[PackageSize];
        input[0] = index;
        var output = InvokeRaw("Get_" + method, input);
        if (output[0] != 1) throw new IOException($"Get_{method}({index}) 실패 flag={output[0]}");
        return output.AsSpan(1, Len(method, index)).ToArray();
    }

    private void Set(string method, byte index, byte[] payload)
    {
        if (payload.Length != Len(method, index))
            throw new ArgumentException($"Set_{method}({index}) 페이로드는 {Len(method, index)}바이트여야 함");
        var input = new byte[PackageSize];
        input[0] = index;
        payload.CopyTo(input, 1);
        var output = InvokeRaw("Set_" + method, input);
        if (output[0] != 1) throw new IOException($"Set_{method}({index}) 실패 flag={output[0]}");
    }

    private byte[] InvokeRaw(string method, byte[]? input)
    {
        lock (_lock)
        {
            ManagementBaseObject? inParams = null;
            if (input is not null)
            {
                inParams = _acpi.GetMethodParameters(method);
                using var pkgClass = new ManagementClass(_scope, new ManagementPath("Package_32"), null);
                var pkg = pkgClass.CreateInstance();
                pkg["Bytes"] = input;
                inParams["Data"] = pkg;
            }
            using var outParams = _acpi.InvokeMethod(method, inParams, null);
            var data = (ManagementBaseObject)outParams["Data"];
            return (byte[])data["Bytes"];
        }
    }
}

public static class EcIndex
{
    public const byte Pl1 = 0x50;
    public const byte Pl2 = 0x51;
    public const byte CoolerBoost = 0x98;
    public const byte ShiftMode = 0xD2;
    public const byte FanCurveMode = 0xD4;
    public const byte BatteryLimit = 0xD7;
    public const byte AirplaneMode = 0xD9;
}
