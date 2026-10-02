using System.ComponentModel;
using System.IO.Pipes;
using System.Runtime.InteropServices;
using System.Security.Principal;
using System.Text;
using Microsoft.Win32.SafeHandles;

namespace ClawService;

/// <summary>
/// 서비스 파이프 생성과 클라이언트 확인.
/// - 대화형 사용자(데스크톱 앱)와 Game Bar 위젯(AppContainer, 낮은 무결성)이 접속할 수 있게 DACL + Low 레이블을 생성 시점에 넣는다.
/// - 접속한 프로세스가 패키지 앱이면 ClawHub 패키지(런처 신원 ClawHub_*, 위젯 ClawHub.Widget_*)만 허용한다.
///   ("ALL APPLICATION PACKAGES" 허용만으로는 모든 스토어 앱이 TDP를 바꿀 수 있게 되므로)
/// </summary>
internal static class PipeAccess
{
    private static readonly string[] AllowedPackagePrefixes = ["ClawHub_", "ClawHub.Widget_"];

    /// <summary>
    /// 읽기 + 쓰기(FILE_CREATE_PIPE_INSTANCE 0x4 제외). 인스턴스 생성 권한을 주면 클라이언트가 서버를 사칭할 수 있다.
    /// FILE_GENERIC_READ 0x120089 | (FILE_GENERIC_WRITE 0x120116 &amp; ~0x4) = 0x12019B
    /// </summary>
    private const string ClientRights = "0x12019b";

    private static string BuildSddl()
    {
        var self = WindowsIdentity.GetCurrent().User?.Value ?? "SY";
        return "D:" +
               "(A;;GA;;;SY)" +                 // LocalSystem
               "(A;;GA;;;BA)" +                 // Administrators
               $"(A;;GA;;;{self})" +            // 파이프를 만드는 계정 (서비스면 SYSTEM, 개발용 콘솔이면 현재 사용자)
               $"(A;;{ClientRights};;;IU)" +    // 대화형 사용자 (ClawHub 앱)
               $"(A;;{ClientRights};;;AC)" +    // ALL APPLICATION PACKAGES (Game Bar 위젯) — 패키지는 연결마다 확인
               "(D;;GA;;;NU)" +                 // 네트워크 로그온 거부
               "S:(ML;;NW;;;LW)";               // 낮은 무결성 프로세스도 쓰기 가능 (MSI 위젯 파이프와 같은 방식)
    }

    public static NamedPipeServerStream Create(string name)
    {
        if (!ConvertStringSecurityDescriptorToSecurityDescriptor(BuildSddl(), 1, out var sd, out _))
            throw new Win32Exception();
        try
        {
            var sa = new SecurityAttributes { Length = Marshal.SizeOf<SecurityAttributes>(), Descriptor = sd };
            var handle = CreateNamedPipe(@"\\.\pipe\" + name,
                0x00000003 | 0x40000000, // PIPE_ACCESS_DUPLEX | FILE_FLAG_OVERLAPPED
                0,                       // PIPE_TYPE_BYTE | PIPE_READMODE_BYTE | PIPE_WAIT
                255, 0, 0, 0, ref sa);   // PIPE_UNLIMITED_INSTANCES
            if (handle.IsInvalid) throw new Win32Exception();
            return new NamedPipeServerStream(PipeDirection.InOut, isAsync: true, isConnected: false, handle);
        }
        finally
        {
            LocalFree(sd);
        }
    }

    /// <summary>일반 데스크톱 프로세스는 DACL로 이미 걸러졌으므로 허용, 패키지 앱은 ClawHub 패키지만.</summary>
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
            var pfn = sb.ToString();
            who = $"pid {pid} ({pfn})";
            return AllowedPackagePrefixes.Any(p => pfn.StartsWith(p, StringComparison.OrdinalIgnoreCase));
        }
        finally
        {
            CloseHandle(h);
        }
    }

    /// <summary>클라이언트 프로세스의 실행 파일 경로 (가상 패드: HidHide에 "실제 패드를 볼 수 있는 앱"으로 등록).</summary>
    public static string? ClientImagePath(NamedPipeServerStream pipe)
    {
        if (!GetNamedPipeClientProcessId(pipe.SafePipeHandle, out var pid)) return null;
        var h = OpenProcess(0x1000 /* QUERY_LIMITED_INFORMATION */, false, pid);
        if (h == IntPtr.Zero) return null;
        try
        {
            var sb = new StringBuilder(1024);
            var len = (uint)sb.Capacity;
            return QueryFullProcessImageName(h, 0, sb, ref len) ? sb.ToString() : null;
        }
        finally
        {
            CloseHandle(h);
        }
    }

    [DllImport("kernel32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
    private static extern bool QueryFullProcessImageName(IntPtr process, uint flags, StringBuilder name, ref uint size);

    /// <summary>지금 화면(콘솔)을 가진 세션. 전환 중이면 0xFFFFFFFF.</summary>
    public static uint ActiveConsoleSession => WTSGetActiveConsoleSessionId();

    /// <summary>클라이언트 프로세스의 로그온 세션 (실패하면 null → 읽기 전용으로 취급).</summary>
    public static uint? ClientSession(NamedPipeServerStream pipe) =>
        GetNamedPipeClientSessionId(pipe.SafePipeHandle, out var session) ? session : null;

    /// <summary>
    /// 클라이언트가 (UAC 승격된) 관리자인지. 파이프에서 데이터를 읽은 뒤에만 가장(impersonation)이 가능하므로
    /// 첫 요청을 받은 다음에 부른다. 필터링된(비승격) 관리자 토큰은 false.
    /// </summary>
    public static bool IsClientAdmin(NamedPipeServerStream pipe)
    {
        var admin = false;
        try
        {
            pipe.RunAsClient(() =>
            {
                // ifImpersonating: 가장에 실패했으면 null — 서비스 자신(SYSTEM)의 토큰으로 판단하는 일이 없도록
                using var id = WindowsIdentity.GetCurrent(ifImpersonating: true);
                admin = id is not null && new WindowsPrincipal(id).IsInRole(WindowsBuiltInRole.Administrator);
            });
        }
        catch (Exception) { admin = false; }
        return admin;
    }

    [DllImport("kernel32.dll")] private static extern uint WTSGetActiveConsoleSessionId();
    [DllImport("kernel32.dll", SetLastError = true)] private static extern bool GetNamedPipeClientSessionId(SafePipeHandle pipe, out uint session);

    [StructLayout(LayoutKind.Sequential)]
    private struct SecurityAttributes
    {
        public int Length;
        public IntPtr Descriptor;
        public int Inherit;
    }

    [DllImport("kernel32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
    private static extern SafePipeHandle CreateNamedPipe(string name, uint openMode, uint pipeMode, uint maxInstances,
        uint outBuffer, uint inBuffer, uint timeout, ref SecurityAttributes sa);

    [DllImport("advapi32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
    private static extern bool ConvertStringSecurityDescriptorToSecurityDescriptor(string sddl, uint revision, out IntPtr sd, out uint size);

    [DllImport("kernel32.dll")] private static extern IntPtr LocalFree(IntPtr mem);
    [DllImport("kernel32.dll", SetLastError = true)] private static extern bool GetNamedPipeClientProcessId(SafePipeHandle pipe, out uint pid);
    [DllImport("kernel32.dll", SetLastError = true)] private static extern IntPtr OpenProcess(uint access, bool inherit, uint pid);
    [DllImport("kernel32.dll")] private static extern bool CloseHandle(IntPtr h);
    [DllImport("kernel32.dll", CharSet = CharSet.Unicode)] private static extern int GetPackageFamilyName(IntPtr process, ref uint length, StringBuilder? name);
}
