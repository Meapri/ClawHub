using System.Runtime.InteropServices;

namespace ClawHubSetup;

internal static class Native
{
    public const int AttachParentProcess = -1;

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool AttachConsole(int processId);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern IntPtr GetStdHandle(int stdHandle);

    /// <summary>
    /// GUI 앱의 Console 출력이 보이게: stdout이 리디렉션돼 있으면(파이프·파일) 그대로 쓰고, 아니면 부모 콘솔에 붙는다.
    /// </summary>
    public static void AttachParentConsoleIfNeeded()
    {
        var stdout = GetStdHandle(-11); // STD_OUTPUT_HANDLE
        if (stdout == IntPtr.Zero || stdout == new IntPtr(-1)) AttachConsole(AttachParentProcess);
    }

    private const int MoveFileDelayUntilReboot = 0x4;

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern bool MoveFileEx(string existing, string? replacement, int flags);

    /// <summary>재부팅 때 지우도록 예약 (관리자 필요). 폴더는 비어 있어야 지워지므로 안쪽부터 예약한다.</summary>
    public static bool DeleteAtReboot(string path) => MoveFileEx(path, null, MoveFileDelayUntilReboot);

    private const int WtsCurrentSession = -1;
    private const int WtsUserName = 5;
    private const int WtsDomainName = 7;

    [DllImport("wtsapi32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern bool WTSQuerySessionInformationW(IntPtr server, int sessionId, int infoClass, out IntPtr buffer, out int bytes);

    [DllImport("wtsapi32.dll")]
    private static extern void WTSFreeMemory(IntPtr memory);

    /// <summary>이 프로세스가 속한 세션에 로그인한 사용자 (DOMAIN\user). UAC 자격 증명 입력으로 다른 계정이 올렸어도 원래 사용자.</summary>
    public static string? SessionUser()
    {
        var user = QuerySession(WtsUserName);
        if (string.IsNullOrEmpty(user)) return null;
        var domain = QuerySession(WtsDomainName);
        return string.IsNullOrEmpty(domain) ? user : $@"{domain}\{user}";
    }

    private static string? QuerySession(int infoClass)
    {
        if (!WTSQuerySessionInformationW(IntPtr.Zero, WtsCurrentSession, infoClass, out var buffer, out _)) return null;
        try { return Marshal.PtrToStringUni(buffer); }
        finally { WTSFreeMemory(buffer); }
    }
}
