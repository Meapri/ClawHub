using System.ComponentModel;
using System.IO.Pipes;
using System.Runtime.InteropServices;
using System.Text;
using Microsoft.Win32.SafeHandles;

namespace ClawService;

/// <summary>
/// Game Bar 위젯(AppContainer, 낮은 무결성)도 파이프에 접속할 수 있게 하되, 샌드박스 앱 중에서는 ClawHub 위젯만 허용한다.
/// (DACL의 "ALL APPLICATION PACKAGES" 허용만으로는 모든 스토어 앱이 TDP를 바꿀 수 있게 되므로 연결마다 확인)
/// </summary>
internal static class PipeAccess
{
    public const string AllAppPackagesSid = "S-1-15-2-1";
    public const string WidgetPackagePrefix = "ClawHub.Widget_";

    /// <summary>낮은 무결성(Low IL) 클라이언트가 쓸 수 있도록 파이프에 Low 레이블을 붙인다. MSI 위젯 파이프와 같은 방식.</summary>
    public static void AllowLowIntegrity(SafePipeHandle pipe)
    {
        if (!ConvertStringSecurityDescriptorToSecurityDescriptor("S:(ML;;NW;;;LW)", 1, out var sd, out _))
            throw new Win32Exception();
        try
        {
            if (!GetSecurityDescriptorSacl(sd, out _, out var sacl, out _)) throw new Win32Exception();
            var err = SetSecurityInfo(pipe, 6 /* SE_KERNEL_OBJECT */, 0x10 /* LABEL_SECURITY_INFORMATION */,
                IntPtr.Zero, IntPtr.Zero, IntPtr.Zero, sacl);
            if (err != 0) throw new Win32Exception(err);
        }
        finally
        {
            LocalFree(sd);
        }
    }

    /// <summary>
    /// 접속한 클라이언트 확인. 일반(데스크톱) 프로세스는 DACL로 이미 걸러졌으므로 허용,
    /// 패키지 앱(AppContainer)은 ClawHub 위젯 패키지일 때만 허용.
    /// </summary>
    public static bool IsAllowedClient(NamedPipeServerStream pipe, out string who)
    {
        who = "?";
        if (!GetNamedPipeClientProcessId(pipe.SafePipeHandle, out var pid)) return false;
        var h = OpenProcess(0x1000 /* QUERY_LIMITED_INFORMATION */, false, pid);
        if (h == IntPtr.Zero) { who = $"pid {pid}"; return false; }
        try
        {
            var len = 0u;
            var rc = GetPackageFamilyName(h, ref len, null);
            if (rc == 15700 /* APPMODEL_ERROR_NO_PACKAGE */) { who = $"pid {pid} (데스크톱)"; return true; }
            var sb = new StringBuilder((int)len);
            if (GetPackageFamilyName(h, ref len, sb) != 0) return false;
            who = $"pid {pid} ({sb})";
            return sb.ToString().StartsWith(WidgetPackagePrefix, StringComparison.OrdinalIgnoreCase);
        }
        finally
        {
            CloseHandle(h);
        }
    }

    [DllImport("advapi32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
    private static extern bool ConvertStringSecurityDescriptorToSecurityDescriptor(string sddl, uint revision, out IntPtr sd, out uint size);

    [DllImport("advapi32.dll", SetLastError = true)]
    private static extern bool GetSecurityDescriptorSacl(IntPtr sd, out bool present, out IntPtr sacl, out bool defaulted);

    [DllImport("advapi32.dll")]
    private static extern int SetSecurityInfo(SafeHandle handle, int objectType, uint info, IntPtr owner, IntPtr group, IntPtr dacl, IntPtr sacl);

    [DllImport("kernel32.dll")] private static extern IntPtr LocalFree(IntPtr mem);
    [DllImport("kernel32.dll", SetLastError = true)] private static extern bool GetNamedPipeClientProcessId(SafePipeHandle pipe, out uint pid);
    [DllImport("kernel32.dll", SetLastError = true)] private static extern IntPtr OpenProcess(uint access, bool inherit, uint pid);
    [DllImport("kernel32.dll")] private static extern bool CloseHandle(IntPtr h);
    [DllImport("kernel32.dll", CharSet = CharSet.Unicode)] private static extern int GetPackageFamilyName(IntPtr process, ref uint length, StringBuilder? name);
}
