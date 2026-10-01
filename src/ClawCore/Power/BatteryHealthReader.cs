using System.Management;
using System.Runtime.InteropServices;
using System.Text;
using ClawCore.Ipc;
using Microsoft.Win32.SafeHandles;

namespace ClawCore.Power;

/// <summary>
/// 배터리 수명 정보 (읽기 전용). 아래 순서로 읽고, 없는 항목은 다음 출처로 채운다.
/// 1. root\WMI BatteryStaticData (DesignedCapacity, ManufactureName) — 이 기기에서는 사용자 권한으로 "일반 오류"
/// 2. root\WMI BatteryFullChargedCapacity, BatteryCycleCount
/// 3. Win32_Battery (DesignCapacity, FullChargeCapacity — 이 기기는 비어 있음)
/// 4. 배터리 드라이버 IOCTL_BATTERY_QUERY_INFORMATION (BatteryInformation, BatteryManufactureName) — 사용자 권한으로도 됨
/// </summary>
public static class BatteryHealthReader
{
    public static BatteryHealth Read()
    {
        var h = new BatteryHealth();

        Wmi(@"root\WMI", "SELECT DesignedCapacity, ManufactureName FROM BatteryStaticData", o =>
        {
            h.DesignedCapacityMWh ??= Capacity(o["DesignedCapacity"]);
            h.Manufacturer ??= Text(o["ManufactureName"]);
        });
        Wmi(@"root\WMI", "SELECT FullChargedCapacity FROM BatteryFullChargedCapacity",
            o => h.FullChargeCapacityMWh ??= Capacity(o["FullChargedCapacity"]));
        Wmi(@"root\WMI", "SELECT CycleCount FROM BatteryCycleCount", o => h.CycleCount ??= Cycles(o["CycleCount"]));
        Wmi(@"root\CIMV2", "SELECT DesignCapacity, FullChargeCapacity FROM Win32_Battery", o =>
        {
            h.DesignedCapacityMWh ??= Capacity(o["DesignCapacity"]);
            h.FullChargeCapacityMWh ??= Capacity(o["FullChargeCapacity"]);
        });

        if (h.DesignedCapacityMWh is null || h.FullChargeCapacityMWh is null || h.CycleCount is null || h.Manufacturer is null)
        {
            try { ReadIoctl(h); }
            catch (Exception) { /* 배터리 없음 / 드라이버 미지원 */ }
        }

        if (h is { DesignedCapacityMWh: > 0 and var d, FullChargeCapacityMWh: > 0 and var f })
            h.HealthPercent = (int)Math.Round(f * 100.0 / d);
        return h;
    }

    private static void Wmi(string scope, string query, Action<ManagementBaseObject> read)
    {
        try
        {
            using var s = new ManagementObjectSearcher(scope, query);
            foreach (var o in s.Get())
            {
                using (o)
                {
                    read(o);
                    break; // 첫 번째 배터리만
                }
            }
        }
        catch (Exception) { /* 클래스 없음, 접근 거부, "일반 오류" 등 — 다음 출처로 */ }
    }

    /// <summary>mWh. 0과 0xFFFFFFFF(BATTERY_UNKNOWN_CAPACITY)는 모름.</summary>
    private static int? Capacity(object? v)
    {
        if (v is null) return null;
        try
        {
            var n = Convert.ToInt64(v);
            return n is > 0 and < int.MaxValue ? (int)n : null;
        }
        catch (Exception) { return null; }
    }

    /// <summary>0은 "사이클 카운터 미지원"(BATTERY_INFORMATION 문서) — 모름으로 본다.</summary>
    private static int? Cycles(object? v) => Capacity(v);

    private static string? Text(object? v) => v is string s && s.Trim('\0', ' ') is { Length: > 0 } t ? t : null;

    // ── IOCTL ─────────────────────────────────────────────

    private static readonly Guid BatteryClass = new("72631e54-78a4-11d0-bcf7-00aa00b7b32a"); // GUID_DEVCLASS_BATTERY
    private const uint IoctlBatteryQueryTag = 0x294040;
    private const uint IoctlBatteryQueryInformation = 0x294044;
    private const int BatteryInformationLevel = 0;
    private const int BatteryManufactureNameLevel = 6;

    private static void ReadIoctl(BatteryHealth h)
    {
        if (FirstBatteryPath() is not { } path) return;
        using var file = CreateFile(path, 0x80000000 /* GENERIC_READ */, 3, IntPtr.Zero, 3 /* OPEN_EXISTING */, 0x80, IntPtr.Zero);
        if (file.IsInvalid) return;

        var wait = new byte[4];
        var tag = new byte[4];
        if (!DeviceIoControl(file, IoctlBatteryQueryTag, wait, 4, tag, 4, out _, IntPtr.Zero) || BitConverter.ToUInt32(tag) == 0) return;

        // BATTERY_QUERY_INFORMATION { ULONG BatteryTag; BATTERY_QUERY_INFORMATION_LEVEL InformationLevel; LONG AtRate; }
        var query = new byte[12];
        tag.CopyTo(query, 0);

        // BATTERY_INFORMATION (36바이트): Capabilities, Technology, Reserved[3], Chemistry[4],
        // DesignedCapacity @12, FullChargedCapacity @16, DefaultAlert1, DefaultAlert2, CriticalBias, CycleCount @32
        BitConverter.GetBytes(BatteryInformationLevel).CopyTo(query, 4);
        var info = new byte[36];
        if (DeviceIoControl(file, IoctlBatteryQueryInformation, query, query.Length, info, info.Length, out var n, IntPtr.Zero) && n >= 36)
        {
            h.DesignedCapacityMWh ??= Capacity(BitConverter.ToUInt32(info, 12));
            h.FullChargeCapacityMWh ??= Capacity(BitConverter.ToUInt32(info, 16));
            h.CycleCount ??= Cycles(BitConverter.ToUInt32(info, 32));
        }

        BitConverter.GetBytes(BatteryManufactureNameLevel).CopyTo(query, 4);
        var name = new byte[512];
        if (DeviceIoControl(file, IoctlBatteryQueryInformation, query, query.Length, name, name.Length, out n, IntPtr.Zero) && n > 0)
            h.Manufacturer ??= Text(Encoding.Unicode.GetString(name, 0, n));
    }

    private static string? FirstBatteryPath()
    {
        var cls = BatteryClass;
        var set = SetupDiGetClassDevs(ref cls, IntPtr.Zero, IntPtr.Zero, 0x12 /* DIGCF_PRESENT | DIGCF_DEVICEINTERFACE */);
        if (set == IntPtr.Zero || set == new IntPtr(-1)) return null;
        try
        {
            var data = new SpDeviceInterfaceData { CbSize = Marshal.SizeOf<SpDeviceInterfaceData>() };
            if (!SetupDiEnumDeviceInterfaces(set, IntPtr.Zero, ref cls, 0, ref data)) return null;
            SetupDiGetDeviceInterfaceDetail(set, ref data, IntPtr.Zero, 0, out var size, IntPtr.Zero);
            if (size <= 0) return null;
            var buf = Marshal.AllocHGlobal(size);
            try
            {
                Marshal.WriteInt32(buf, 8); // x64 SP_DEVICE_INTERFACE_DETAIL_DATA_W.cbSize
                if (!SetupDiGetDeviceInterfaceDetail(set, ref data, buf, size, out _, IntPtr.Zero)) return null;
                return Marshal.PtrToStringUni(buf + 4);
            }
            finally
            {
                Marshal.FreeHGlobal(buf);
            }
        }
        finally
        {
            SetupDiDestroyDeviceInfoList(set);
        }
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct SpDeviceInterfaceData
    {
        public int CbSize;
        public Guid InterfaceClassGuid;
        public int Flags;
        public IntPtr Reserved;
    }

    [DllImport("setupapi.dll", SetLastError = true)]
    private static extern IntPtr SetupDiGetClassDevs(ref Guid classGuid, IntPtr enumerator, IntPtr hwndParent, int flags);

    [DllImport("setupapi.dll", SetLastError = true)]
    private static extern bool SetupDiEnumDeviceInterfaces(IntPtr set, IntPtr devInfo, ref Guid classGuid, int index, ref SpDeviceInterfaceData data);

    [DllImport("setupapi.dll", SetLastError = true, CharSet = CharSet.Unicode)]
    private static extern bool SetupDiGetDeviceInterfaceDetail(IntPtr set, ref SpDeviceInterfaceData data, IntPtr detail, int detailSize,
        out int requiredSize, IntPtr devInfo);

    [DllImport("setupapi.dll")]
    private static extern bool SetupDiDestroyDeviceInfoList(IntPtr set);

    [DllImport("kernel32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
    private static extern SafeFileHandle CreateFile(string name, uint access, uint share, IntPtr security, uint creation, uint flags, IntPtr template);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool DeviceIoControl(SafeFileHandle device, uint code, byte[] input, int inputSize, byte[] output, int outputSize,
        out int returned, IntPtr overlapped);
}
