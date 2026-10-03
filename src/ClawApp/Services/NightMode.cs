using System.Runtime.InteropServices;

namespace ClawApp.Services;

/// <summary>
/// 야간 모드: 화면 감마 램프로 파란빛을 줄인다 (f.lux와 같은 방식 — 드라이버 출력 단계라 전체 화면 게임에도 적용).
/// Windows 야간 모드는 Windows 11 최신 버전에서 켜짐 상태를 외부에서 바꿀 방법이 없어 직접 그린다.
/// Windows는 기본으로 감마를 원래 값에서 너무 멀리 못 바꾸게 막으므로(약 3400K 아래 거부), 거부되면 되는 데까지 약하게 한다.
/// 화면 모드 바꾸기·절전 복귀·게임의 감마 설정이 램프를 되돌릴 수 있어, 켜져 있는 동안 주기적으로 확인해 다시 적용한다.
/// </summary>
public static class NightMode
{
    public const int MinKelvin = 2700;
    public const int MaxKelvin = 6500;

    /// <summary>
    /// 연결된 모든 화면에 색 온도를 적용한다 (6500 = 원래대로). 실제로 적용된 색 온도 (거부되면 그보다 높다), 실패하면 null.
    /// </summary>
    public static int? Apply(int kelvin)
    {
        kelvin = Math.Clamp(kelvin, MinKelvin, MaxKelvin);
        int? applied = null;
        foreach (var device in Displays())
        {
            var dc = CreateDC("DISPLAY", device, null, IntPtr.Zero);
            if (dc == IntPtr.Zero) continue;
            try
            {
                // 거부되면 200K씩 원래 쪽으로
                for (var k = kelvin; k <= MaxKelvin; k += 200)
                {
                    if (!SetDeviceGammaRamp(dc, Ramp(k))) continue;
                    applied = applied is { } a ? Math.Max(a, k) : k;
                    break;
                }
            }
            finally { DeleteDC(dc); }
        }
        return applied;
    }

    public static void Reset() => Apply(MaxKelvin);

    /// <summary>지금 화면 램프가 이 색 온도 그대로인지 (다른 프로그램·모드 변경이 되돌렸으면 false).</summary>
    public static bool IsApplied(int kelvin)
    {
        var want = Ramp(kelvin);
        foreach (var device in Displays())
        {
            var dc = CreateDC("DISPLAY", device, null, IntPtr.Zero);
            if (dc == IntPtr.Zero) continue;
            try
            {
                var now = new ushort[768];
                if (!GetDeviceGammaRamp(dc, now)) continue;
                // 파랑 끝값만 비교 (드라이버가 몇 단위 반올림할 수 있다)
                if (Math.Abs(now[767] - want[767]) > 512 || Math.Abs(now[511] - want[511]) > 512) return false;
            }
            finally { DeleteDC(dc); }
        }
        return true;
    }

    /// <summary>색 온도 → 감마 램프 (빨강·초록·파랑 각 256단계). Tanner Helland 근사, 6500K = 원래대로.</summary>
    private static ushort[] Ramp(int kelvin)
    {
        var (r, g, b) = Multipliers(kelvin);
        var ramp = new ushort[768];
        for (var i = 0; i < 256; i++)
        {
            ramp[i] = (ushort)Math.Min(65535, i * 257 * r);
            ramp[256 + i] = (ushort)Math.Min(65535, i * 257 * g);
            ramp[512 + i] = (ushort)Math.Min(65535, i * 257 * b);
        }
        return ramp;
    }

    private static (double R, double G, double B) Multipliers(int kelvin)
    {
        static (double, double, double) Rgb(double k)
        {
            var t = k / 100;
            var r = t <= 66 ? 255 : 329.698727446 * Math.Pow(t - 60, -0.1332047592);
            var g = t <= 66 ? 99.4708025861 * Math.Log(t) - 161.1195681661 : 288.1221695283 * Math.Pow(t - 60, -0.0755148492);
            var b = t >= 66 ? 255 : t <= 19 ? 0 : 138.5177312231 * Math.Log(t - 10) - 305.0447927307;
            return (Math.Clamp(r, 0, 255), Math.Clamp(g, 0, 255), Math.Clamp(b, 0, 255));
        }
        var (r0, g0, b0) = Rgb(MaxKelvin);
        var (r1, g1, b1) = Rgb(kelvin);
        // 6500K에서 1이 되도록 정규화하고, 가장 큰 채널이 1을 넘지 않게
        double r = r1 / r0, g = g1 / g0, b = b1 / b0;
        var max = Math.Max(r, Math.Max(g, b));
        return (r / max, g / max, b / max);
    }

    /// <summary>바탕화면에 붙어 있는 화면 장치 이름 (\\.\DISPLAY1 …).</summary>
    private static List<string> Displays()
    {
        var list = new List<string>();
        var d = new DisplayDevice { cb = Marshal.SizeOf<DisplayDevice>() };
        for (uint i = 0; EnumDisplayDevices(null, i, ref d, 0); i++)
        {
            if ((d.StateFlags & 1) != 0) list.Add(d.DeviceName); // ATTACHED_TO_DESKTOP
            d.cb = Marshal.SizeOf<DisplayDevice>();
        }
        return list;
    }

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct DisplayDevice
    {
        public int cb;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 32)] public string DeviceName;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 128)] public string DeviceString;
        public int StateFlags;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 128)] public string DeviceID;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 128)] public string DeviceKey;
    }

    [DllImport("user32.dll", CharSet = CharSet.Unicode)] private static extern bool EnumDisplayDevices(string? device, uint index, ref DisplayDevice dd, uint flags);
    [DllImport("gdi32.dll", CharSet = CharSet.Unicode)] private static extern IntPtr CreateDC(string? driver, string device, string? output, IntPtr init);
    [DllImport("gdi32.dll")] private static extern bool DeleteDC(IntPtr dc);
    [DllImport("gdi32.dll")] private static extern bool SetDeviceGammaRamp(IntPtr dc, ushort[] ramp);
    [DllImport("gdi32.dll")] private static extern bool GetDeviceGammaRamp(IntPtr dc, ushort[] ramp);
}
