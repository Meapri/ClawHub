using System.Runtime.InteropServices;

namespace ClawApp.Services;

/// <summary>주 디스플레이 해상도/주사율 (사용자 권한으로 가능한 ChangeDisplaySettings).</summary>
public static class DisplayControls
{
    public readonly record struct DisplayMode(int Width, int Height, int Hz)
    {
        public override string ToString() => $"{Width}×{Height}";
    }

    public static DisplayMode? Current()
    {
        var dm = NewDevMode();
        return EnumDisplaySettings(null, EnumCurrentSettings, ref dm) ? new(dm.dmPelsWidth, dm.dmPelsHeight, dm.dmDisplayFrequency) : null;
    }

    /// <summary>지원 모드 (32bit 색만, 중복 제거, 큰 해상도 순).</summary>
    public static IReadOnlyList<DisplayMode> Modes()
    {
        var list = new HashSet<DisplayMode>();
        var dm = NewDevMode();
        for (var i = 0; EnumDisplaySettings(null, i, ref dm); i++)
            if (dm.dmBitsPerPel == 32 && dm.dmPelsWidth >= 800) list.Add(new(dm.dmPelsWidth, dm.dmPelsHeight, dm.dmDisplayFrequency));
        return list.OrderByDescending(m => m.Width * m.Height).ThenByDescending(m => m.Hz).ToList();
    }

    /// <returns>오류 메시지 (성공이면 null).</returns>
    public static string? Apply(DisplayMode mode)
    {
        var dm = NewDevMode();
        if (!EnumDisplaySettings(null, EnumCurrentSettings, ref dm)) return "현재 디스플레이 설정을 읽을 수 없어요";
        dm.dmPelsWidth = mode.Width;
        dm.dmPelsHeight = mode.Height;
        dm.dmDisplayFrequency = mode.Hz;
        dm.dmFields = DmPelsWidth | DmPelsHeight | DmDisplayFrequency;
        var r = ChangeDisplaySettings(ref dm, CdsUpdateRegistry);
        return r == 0 ? null : $"디스플레이 변경 실패 (코드 {r})";
    }

    private const int EnumCurrentSettings = -1;
    private const int CdsUpdateRegistry = 0x01;
    private const int DmPelsWidth = 0x80000, DmPelsHeight = 0x100000, DmDisplayFrequency = 0x400000;

    private static DEVMODE NewDevMode() => new() { dmSize = (short)Marshal.SizeOf<DEVMODE>() };

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct DEVMODE
    {
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 32)] public string dmDeviceName;
        public short dmSpecVersion, dmDriverVersion, dmSize, dmDriverExtra;
        public int dmFields;
        public int dmPositionX, dmPositionY, dmDisplayOrientation, dmDisplayFixedOutput;
        public short dmColor, dmDuplex, dmYResolution, dmTTOption, dmCollate;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 32)] public string dmFormName;
        public short dmLogPixels;
        public int dmBitsPerPel, dmPelsWidth, dmPelsHeight, dmDisplayFlags, dmDisplayFrequency;
        public int dmICMMethod, dmICMIntent, dmMediaType, dmDitherType, dmReserved1, dmReserved2, dmPanningWidth, dmPanningHeight;
    }

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    private static extern bool EnumDisplaySettings(string? device, int mode, ref DEVMODE dm);

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    private static extern int ChangeDisplaySettings(ref DEVMODE dm, int flags);
}
