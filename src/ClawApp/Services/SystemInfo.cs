using System.Management;
using System.Runtime.InteropServices;
using Microsoft.Win32;

namespace ClawApp.Services;

/// <summary>드라이브 하나 (고정 디스크).</summary>
public sealed record DriveUsage(string Name, string Label, long Total, long Free);

/// <summary>
/// 저장소·시스템 정보 (읽기 전용): 드라이브 사용량, 게임 폴더 크기, 기기·CPU·메모리·GPU 드라이버·Windows 버전·마지막 업데이트.
/// 모두 느릴 수 있어 뒤에서 부른다 (WMI·폴더 크기 계산).
/// </summary>
public static class SystemInfo
{
    public static List<DriveUsage> Drives()
    {
        var list = new List<DriveUsage>();
        foreach (var d in DriveInfo.GetDrives())
        {
            try
            {
                if (d.DriveType != DriveType.Fixed || !d.IsReady) continue;
                list.Add(new DriveUsage(d.Name.TrimEnd('\\'), d.VolumeLabel, d.TotalSize, d.AvailableFreeSpace));
            }
            catch (IOException) { /* 그사이 빠짐 */ }
        }
        return list;
    }

    /// <summary>폴더 크기 (바이트). 접근할 수 없는 하위 폴더는 건너뛴다. 취소되면 그때까지의 합.</summary>
    public static long FolderSize(string path, CancellationToken ct)
    {
        long total = 0;
        try
        {
            var opts = new System.IO.EnumerationOptions { RecurseSubdirectories = true, IgnoreInaccessible = true, AttributesToSkip = FileAttributes.ReparsePoint };
            foreach (var f in new DirectoryInfo(path).EnumerateFiles("*", opts))
            {
                if (ct.IsCancellationRequested) break;
                total += f.Length;
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { }
        return total;
    }

    /// <summary>(이름, 값) 목록: 기기 모델, 프로세서, 메모리, 그래픽 드라이버, Windows, 마지막 업데이트.</summary>
    public static List<(string Name, string Value)> Details()
    {
        var rows = new List<(string, string)>();
        string? Wmi(string query, string prop)
        {
            try
            {
                using var s = new ManagementObjectSearcher(query);
                using var r = s.Get();
                foreach (ManagementObject o in r) using (o) return o[prop]?.ToString()?.Trim();
            }
            catch (Exception) { }
            return null;
        }

        var maker = Wmi("SELECT Manufacturer FROM Win32_ComputerSystem", "Manufacturer");
        var model = Wmi("SELECT Model FROM Win32_ComputerSystem", "Model");
        if (model is not null) rows.Add(("기기", maker is null ? model : $"{Pretty(maker)} {model}"));

        var cpu = Registry.GetValue(@"HKEY_LOCAL_MACHINE\HARDWARE\DESCRIPTION\System\CentralProcessor\0", "ProcessorNameString", null) as string;
        if (cpu is not null) rows.Add(("프로세서", System.Text.RegularExpressions.Regex.Replace(cpu.Trim(), @"\s{2,}", " ")));

        var mem = new MemoryStatusEx { Length = (uint)Marshal.SizeOf<MemoryStatusEx>() };
        if (GlobalMemoryStatusEx(ref mem)) rows.Add(("메모리", $"{Math.Round(mem.TotalPhys / 1073741824.0)} GB"));

        var gpu = Wmi("SELECT Name FROM Win32_VideoController", "Name");
        var driver = Wmi("SELECT DriverVersion FROM Win32_VideoController", "DriverVersion");
        if (gpu is not null) rows.Add(("그래픽", driver is null ? gpu : $"{gpu} · 드라이버 {driver}"));

        const string nt = @"HKEY_LOCAL_MACHINE\SOFTWARE\Microsoft\Windows NT\CurrentVersion";
        var display = Registry.GetValue(nt, "DisplayVersion", null) as string;
        var build = Registry.GetValue(nt, "CurrentBuild", null) as string;
        var ubr = Registry.GetValue(nt, "UBR", null) is int u ? $".{u}" : "";
        var edition = (Registry.GetValue(nt, "EditionID", null) as string) switch { "Core" => "Home", "Professional" => "Pro", var e => e };
        // 빌드 22000 이상은 Windows 11 (ProductName은 여전히 "Windows 10"이라 쓰지 않는다)
        var name = int.TryParse(build, out var b) && b >= 22000 ? "Windows 11" : "Windows 10";
        rows.Add(("Windows", $"{name} {edition} {display} (빌드 {build}{ubr})".Replace("  ", " ")));

        if (LastUpdate() is { } last) rows.Add(("마지막 업데이트", last.ToString("yyyy-MM-dd")));
        return rows;
    }

    private static string Pretty(string maker) =>
        maker.Contains("Micro-Star", StringComparison.OrdinalIgnoreCase) ? "MSI" : maker;

    /// <summary>마지막으로 설치된 Windows 업데이트 날짜 (Windows Update 기록, 일반 사용자도 읽기 가능).</summary>
    private static DateTime? LastUpdate()
    {
        try
        {
            var type = Type.GetTypeFromProgID("Microsoft.Update.Session");
            if (type is null) return null;
            dynamic session = Activator.CreateInstance(type)!;
            dynamic searcher = session.CreateUpdateSearcher();
            int count = searcher.GetTotalHistoryCount();
            if (count == 0) return null;
            dynamic history = searcher.QueryHistory(0, Math.Min(count, 20));
            DateTime? latest = null;
            foreach (dynamic h in history)
            {
                // ResultCode 2 = 성공, Operation 1 = 설치
                if ((int)h.ResultCode == 2 && (int)h.Operation == 1)
                {
                    DateTime d = h.Date;
                    if (latest is null || d > latest) latest = d;
                }
            }
            return latest?.ToLocalTime();
        }
        catch (Exception) { return null; }
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct MemoryStatusEx
    {
        public uint Length, MemoryLoad;
        public ulong TotalPhys, AvailPhys, TotalPageFile, AvailPageFile, TotalVirtual, AvailVirtual, AvailExtendedVirtual;
    }

    [DllImport("kernel32.dll")] private static extern bool GlobalMemoryStatusEx(ref MemoryStatusEx status);

    // ── 잠금 ─────────────────────────────────────────

    private const string DesktopKey = @"HKEY_CURRENT_USER\Control Panel\Desktop";

    /// <summary>
    /// 자리를 비운 뒤 다시 로그인을 요구할 때까지 (초). 0 = 깨어날 때마다, -1 = 안 함 (Windows 설정 → 계정 → 로그인 옵션과 같은 값).
    /// </summary>
    public static int GetRelockDelay() => Registry.GetValue(DesktopKey, "DelayLockInterval", 0) is int v ? v : 0;

    public static void SetRelockDelay(int seconds) =>
        Registry.SetValue(DesktopKey, "DelayLockInterval", seconds, RegistryValueKind.DWord);

    [DllImport("user32.dll")] public static extern bool LockWorkStation();
}
