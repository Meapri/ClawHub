using System.Runtime.InteropServices;

namespace ClawApp.Services;

/// <summary>성능 오버레이용 시스템 값 한 번.</summary>
public readonly record struct MetricsSample(double? CpuPercent, int? CpuMhz, double? GpuPercent, double? RamUsedGb, double? RamTotalGb);

/// <summary>
/// CPU 사용률·클럭, GPU(3D 엔진) 사용률, 메모리 — 작업 관리자와 같은 성능 카운터(PDH)를 앱에서 직접 읽는다 (서비스 필요 없음).
/// 속도 카운터라 두 번째 <see cref="Sample"/>부터 값이 나온다. 스레드 하나에서만 부른다 (오버레이 갱신 작업).
/// GPU 엔진 카운터는 프로세스마다 인스턴스가 있어 읽는 데 몇 ms가 걸리므로 필요할 때만 (<paramref name="gpu"/>).
/// </summary>
public sealed class SystemMetrics : IDisposable
{
    private IntPtr _query, _cpuUtil, _cpuPerf, _cpuFreq, _gpu3d;
    private bool _gpuAdded;

    public SystemMetrics()
    {
        if (PdhOpenQuery(null, IntPtr.Zero, out _query) != 0) { _query = IntPtr.Zero; return; }
        // Processor Utility = 작업 관리자의 CPU % (터보로 100을 넘을 수 있어 잘라 쓴다)
        Add(@"\Processor Information(_Total)\% Processor Utility", out _cpuUtil);
        Add(@"\Processor Information(_Total)\% Processor Performance", out _cpuPerf);
        Add(@"\Processor Information(_Total)\Processor Frequency", out _cpuFreq);
    }

    private void Add(string path, out IntPtr counter)
    {
        if (PdhAddEnglishCounter(_query, path, IntPtr.Zero, out counter) != 0) counter = IntPtr.Zero;
    }

    public MetricsSample Sample(bool gpu)
    {
        if (_query == IntPtr.Zero) return new(null, null, null, null, null);
        if (gpu && !_gpuAdded)
        {
            // 와일드카드 인스턴스는 모을 때마다 다시 펼쳐진다 (게임이 새로 떠도 잡힌다)
            Add(@"\GPU Engine(*engtype_3D)\Utilization Percentage", out _gpu3d);
            _gpuAdded = true;
        }
        PdhCollectQueryData(_query);

        double? cpu = Value(_cpuUtil) is { } u ? Math.Clamp(u, 0, 100) : null;
        int? mhz = Value(_cpuFreq) is { } f && Value(_cpuPerf) is { } p && f > 0 ? (int)Math.Round(f * p / 100) : null;
        double? gpuPct = gpu && _gpu3d != IntPtr.Zero ? SumArray(_gpu3d) is { } g ? Math.Clamp(g, 0, 100) : null : null;

        double? used = null, total = null;
        var mem = new MemoryStatusEx { Length = (uint)Marshal.SizeOf<MemoryStatusEx>() };
        if (GlobalMemoryStatusEx(ref mem))
        {
            total = mem.TotalPhys / 1073741824.0;
            used = (mem.TotalPhys - mem.AvailPhys) / 1073741824.0;
        }
        return new(cpu, mhz, gpuPct, used, total);
    }

    private static double? Value(IntPtr counter)
    {
        if (counter == IntPtr.Zero) return null;
        return PdhGetFormattedCounterValue(counter, FmtDouble | FmtNoCap100, out _, out var v) == 0 && v.Status is 0 or 1 ? v.Value : null; // 1 = NEW_DATA도 유효
    }

    /// <summary>와일드카드 카운터의 모든 인스턴스 합 (프로세스마다 나뉜 3D 엔진 사용률).</summary>
    private static double? SumArray(IntPtr counter)
    {
        uint size = 0;
        var rc = PdhGetFormattedCounterArray(counter, FmtDouble | FmtNoCap100, ref size, out _, IntPtr.Zero);
        if (rc != MoreData || size == 0) return null;
        var buf = Marshal.AllocHGlobal((int)size);
        try
        {
            if (PdhGetFormattedCounterArray(counter, FmtDouble | FmtNoCap100, ref size, out var count, buf) != 0) return null;
            double sum = 0;
            var itemSize = IntPtr.Size + Marshal.SizeOf<FmtValue>();
            for (var i = 0; i < count; i++)
            {
                var v = Marshal.PtrToStructure<FmtValue>(buf + i * itemSize + IntPtr.Size);
                if (v.Status is 0 or 1) sum += v.Value;
            }
            return sum;
        }
        finally { Marshal.FreeHGlobal(buf); }
    }

    public void Dispose()
    {
        if (_query != IntPtr.Zero) PdhCloseQuery(_query);
        _query = IntPtr.Zero;
    }

    private const uint FmtDouble = 0x200, FmtNoCap100 = 0x8000;
    private const int MoreData = unchecked((int)0x800007D2);

    [StructLayout(LayoutKind.Explicit)]
    private struct FmtValue
    {
        [FieldOffset(0)] public uint Status;
        [FieldOffset(8)] public double Value;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct MemoryStatusEx
    {
        public uint Length, MemoryLoad;
        public ulong TotalPhys, AvailPhys, TotalPageFile, AvailPageFile, TotalVirtual, AvailVirtual, AvailExtendedVirtual;
    }

    [DllImport("pdh.dll", CharSet = CharSet.Unicode)] private static extern int PdhOpenQuery(string? source, IntPtr user, out IntPtr query);
    [DllImport("pdh.dll", CharSet = CharSet.Unicode)] private static extern int PdhAddEnglishCounter(IntPtr query, string path, IntPtr user, out IntPtr counter);
    [DllImport("pdh.dll")] private static extern int PdhCollectQueryData(IntPtr query);
    [DllImport("pdh.dll")] private static extern int PdhGetFormattedCounterValue(IntPtr counter, uint format, out uint type, out FmtValue value);
    [DllImport("pdh.dll", CharSet = CharSet.Unicode, EntryPoint = "PdhGetFormattedCounterArrayW")]
    private static extern int PdhGetFormattedCounterArray(IntPtr counter, uint format, ref uint size, out uint count, IntPtr items);
    [DllImport("pdh.dll")] private static extern int PdhCloseQuery(IntPtr query);
    [DllImport("kernel32.dll")] private static extern bool GlobalMemoryStatusEx(ref MemoryStatusEx status);
}
