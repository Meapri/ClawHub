using System.Diagnostics;
using System.Runtime.InteropServices;

namespace ClawCore;

/// <summary>
/// 게임·앱·설정 페이지 실행. ClawHub 본체는 관리자 권한 게임 위에서도 입력과 오버레이가 동작하도록 UIAccess(높은 무결성)로 도는데,
/// 그 상태로 바로 실행하면 게임·런처가 높은 무결성을 물려받는다 (끌어 놓기 안 됨, 다른 앱과 통신 안 됨 등).
/// 그래서 높은 무결성이면 Windows 탐색기(바탕 화면 셸)에게 대신 실행을 맡긴다 — 사용자가 직접 연 것과 같은 일반 권한
/// (Raymond Chen, "How can I launch an unelevated process from my elevated process"). 실패하면 직접 실행.
/// </summary>
public static class ShellLauncher
{
    /// <summary>이 프로세스가 일반(Medium)보다 높은 무결성으로 도는지.</summary>
    public static bool IsRaised { get; } = IntegrityLevel() > 0x2000;

    public static void Start(string file, string? args = null, string? workingDirectory = null)
    {
        if (IsRaised && TryDesktopShell(file, args ?? "", workingDirectory ?? "")) return;
        using var _ = Process.Start(new ProcessStartInfo(file, args ?? "")
        {
            UseShellExecute = true,
            WorkingDirectory = workingDirectory ?? "",
        });
    }

    private static bool TryDesktopShell(string file, string args, string dir)
    {
        try
        {
            dynamic windows = Activator.CreateInstance(Type.GetTypeFromCLSID(new Guid("9BA05972-F6A8-11CF-A442-00A0C90A8F39"))!)!; // ShellWindows
            object location = 0; // CSIDL_DESKTOP
            object root = Type.Missing;
            var hwnd = 0;
            object desktop = windows.FindWindowSW(ref location, ref root, 8 /* SWC_DESKTOP */, ref hwnd, 1 /* SWFO_NEEDDISPATCH */);
            var provider = (IServiceProvider)desktop;
            var sidTopLevelBrowser = new Guid("4C96BE40-915C-11CF-99D3-00AA004AE837");
            var iidShellBrowser = typeof(IShellBrowser).GUID;
            var browser = (IShellBrowser)provider.QueryService(ref sidTopLevelBrowser, ref iidShellBrowser);
            var view = browser.QueryActiveShellView();
            var iidDispatch = new Guid("00020400-0000-0000-C000-000000000046");
            dynamic folderView = view.GetItemObject(0 /* SVGIO_BACKGROUND */, ref iidDispatch);
            dynamic shell = folderView.Application;
            shell.ShellExecute(file, args, dir, "", 1 /* SW_SHOWNORMAL */);
            return true;
        }
        catch (Exception)
        {
            return false;
        }
    }

    private static int IntegrityLevel()
    {
        try
        {
            if (!OpenProcessToken(Process.GetCurrentProcess().Handle, 8 /* TOKEN_QUERY */, out var token)) return 0;
            var buf = Marshal.AllocHGlobal(256);
            try
            {
                if (!GetTokenInformation(token, 25 /* TokenIntegrityLevel */, buf, 256, out _)) return 0;
                var sid = Marshal.ReadIntPtr(buf);
                var count = Marshal.ReadByte(GetSidSubAuthorityCount(sid));
                return Marshal.ReadInt32(GetSidSubAuthority(sid, (uint)(count - 1)));
            }
            finally
            {
                Marshal.FreeHGlobal(buf);
                CloseHandle(token);
            }
        }
        catch (Exception) { return 0; }
    }

    [ComImport, Guid("6D5140C1-7436-11CE-8034-00AA006009FA"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IServiceProvider
    {
        [return: MarshalAs(UnmanagedType.IUnknown)]
        object QueryService(ref Guid service, ref Guid riid);
    }

    [ComImport, Guid("000214E2-0000-0000-C000-000000000046"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IShellBrowser
    {
        void GetWindow();             // IOleWindow
        void ContextSensitiveHelp();
        void InsertMenusSB();
        void SetMenuSB();
        void RemoveMenusSB();
        void SetStatusTextSB();
        void EnableModelessSB();
        void TranslateAcceleratorSB();
        void BrowseObject();
        void GetViewStateStream();
        void GetControlWindow();
        void SendControlMsg();
        IShellView QueryActiveShellView();
    }

    [ComImport, Guid("000214E3-0000-0000-C000-000000000046"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IShellView
    {
        void GetWindow();             // IOleWindow
        void ContextSensitiveHelp();
        void TranslateAccelerator();
        void EnableModeless();
        void UIActivate();
        void Refresh();
        void CreateViewWindow();
        void DestroyViewWindow();
        void GetCurrentInfo();
        void AddPropertySheetPages();
        void SaveViewState();
        void SelectItem();
        [return: MarshalAs(UnmanagedType.IDispatch)]
        object GetItemObject(uint item, ref Guid riid);
    }

    [DllImport("advapi32.dll")] private static extern bool OpenProcessToken(IntPtr process, uint access, out IntPtr token);
    [DllImport("advapi32.dll")] private static extern bool GetTokenInformation(IntPtr token, int cls, IntPtr buf, int len, out int ret);
    [DllImport("advapi32.dll")] private static extern IntPtr GetSidSubAuthority(IntPtr sid, uint index);
    [DllImport("advapi32.dll")] private static extern IntPtr GetSidSubAuthorityCount(IntPtr sid);
    [DllImport("kernel32.dll")] private static extern bool CloseHandle(IntPtr handle);
}
