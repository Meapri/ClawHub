using System.Diagnostics;
using System.Runtime.InteropServices;
using ClawCore.Games;

namespace ClawApp.Services;

/// <summary>
/// Windows Xbox 환경 연동. ClawHub는 게임 위에 자체 오버레이를 띄우지 않고
/// 빠른 설정은 Game Bar 위젯으로, 홈은 ClawHub 런처(또는 Xbox 모드일 땐 Xbox 앱)로 넘긴다.
/// 모든 메서드는 사용자 세션(앱 프로세스)에서 호출한다.
/// </summary>
public static class XboxIntegration
{
    /// <summary>ClawHub 빠른 설정 Game Bar 위젯 패키지 (src/ClawWidget).</summary>
    public const string WidgetFamily = "ClawHub.Widget_tj9ced57bhrww";
    public const string WidgetActivateUri = "ms-gamebar:activate/" + WidgetFamily + "_App_ClawHubQuick";
    /// <summary>핸드헬드용 간결 모드 Game Bar (MSI 도우미와 같은 순서: 위젯 활성화 → compact).</summary>
    public const string CompactModeUri = "ms-gamebar://compactmode?enable=true";

    /// <summary>위젯 패키지가 현재 사용자에게 설치돼 있는지. 매번 조회해서 앱 재시작 없이 설치/제거를 반영.</summary>
    public static bool IsWidgetInstalled()
    {
        try
        {
            uint count = 0, length = 0;
            var rc = GetPackagesByPackageFamily(WidgetFamily, ref count, IntPtr.Zero, ref length, IntPtr.Zero);
            // 버퍼 없이 부르면 설치돼 있을 때 ERROR_INSUFFICIENT_BUFFER(122) + count > 0
            return rc is 0 or 122 && count > 0;
        }
        catch (Exception) { return false; } // API 없음 (구형 Windows)
    }

    /// <summary>ClawHub 위젯을 Game Bar에서 연다. URI 처리기가 없으면(Game Bar 제거됨) false.</summary>
    public static bool TryOpenWidget()
    {
        if (!TryShell(WidgetActivateUri)) return false;
        TryShell(CompactModeUri);
        return true;
    }

    /// <summary>Xbox 앱 홈을 연다 (Xbox 모드에서 홈 화면 역할).</summary>
    public static bool TryOpenXboxApp(out string? error)
    {
        error = null;
        try
        {
            ClawCore.ShellLauncher.Start(AppSettings.XboxAppId);
            return true;
        }
        catch (Exception ex)
        {
            error = ex.Message;
            return false;
        }
    }

    private static bool TryShell(string uri)
    {
        try
        {
            Process.Start(new ProcessStartInfo(uri) { UseShellExecute = true })?.Dispose();
            return true;
        }
        catch (Exception) { return false; }
    }

    /// <summary>
    /// Win+G 입력 (Game Bar 열기/닫기 토글). URI만으로 Game Bar가 안 뜰 때 MSI 도우미가 쓰는 방식.
    /// 시스템 단축키라 게임에는 전달되지 않는다.
    /// </summary>
    public static void PressWinG()
    {
        const byte VkLWin = 0x5B, VkG = 0x47;
        const uint Extended = 0x1, KeyUp = 0x2;
        keybd_event(VkLWin, 0, Extended, UIntPtr.Zero);
        keybd_event(VkG, 0, 0, UIntPtr.Zero);
        keybd_event(VkG, 0, KeyUp, UIntPtr.Zero);
        keybd_event(VkLWin, 0, Extended | KeyUp, UIntPtr.Zero);
    }

    /// <summary>
    /// Win+F11: Windows Xbox 모드(전체 화면 환경) 들어가기/나오기 토글. Xbox 앱의 "XBOX 모드" 버튼·Game Bar의
    /// "전체 화면 환경"과 같은 공식 단축키다 (명령줄로 여는 방법은 따로 없다). 이 기능을 끈 PC에서는 아무 일도 없다.
    /// </summary>
    public static void PressWinF11()
    {
        const byte VkLWin = 0x5B, VkF11 = 0x7A;
        const uint Extended = 0x1, KeyUp = 0x2;
        keybd_event(VkLWin, 0, Extended, UIntPtr.Zero);
        keybd_event(VkF11, 0, 0, UIntPtr.Zero);
        keybd_event(VkF11, 0, KeyUp, UIntPtr.Zero);
        keybd_event(VkLWin, 0, Extended | KeyUp, UIntPtr.Zero);
    }

    /// <summary>
    /// 지금 Windows Xbox 모드(전체 화면 환경)인지. 공개 API가 없어 탐색기가 남기는 흔적으로 본다
    /// (Windows 11 26300에서 확인): 들어가면 탐색기가 GamingFallbackWindow를 띄우고 작업 표시줄(Shell_TrayWnd)을
    /// 가린다(cloak). 나오면 둘 다 원래대로이고, 나가기 확인 창에서 취소하면 그대로다.
    /// </summary>
    public static bool IsXboxModeActive()
    {
        var fallback = FindWindow("GamingFallbackWindow", null);
        if (fallback != IntPtr.Zero && IsWindowVisible(fallback)) return true;
        var tray = FindWindow("Shell_TrayWnd", null);
        return tray != IntPtr.Zero && DwmGetWindowAttribute(tray, DwmwaCloaked, out var cloaked, sizeof(int)) == 0 && cloaked != 0;
    }

    /// <summary>Xbox 모드가 <paramref name="active"/> 상태가 될 때까지 기다린다 (나가기 확인 창이 뜨면 사용자가 고를 때까지).</summary>
    public static async Task<bool> WaitXboxModeAsync(bool active, TimeSpan timeout)
    {
        var until = DateTime.UtcNow + timeout;
        while (DateTime.UtcNow < until)
        {
            await Task.Delay(250);
            if (IsXboxModeActive() == active) return true;
        }
        return false;
    }

    private const int DwmwaCloaked = 14;

    private const string GamingConfigKey = @"HKEY_CURRENT_USER\Software\Microsoft\Windows\CurrentVersion\GamingConfiguration";

    /// <summary>Windows 설정 > 게임 > Xbox 모드 > "시작할 때 들어가기".</summary>
    public static bool StartsInXboxMode =>
        Microsoft.Win32.Registry.GetValue(GamingConfigKey, "StartupToGamingHome", 0) is int v && v != 0;

    /// <summary>ClawHub 홈 신원 패키지의 앱 ID (packaging/home, 설치 프로그램의 "Xbox 모드 홈 앱 등록").</summary>
    public const string HomeAppId = "ClawHub_tj9ced57bhrww!ClawHub";

    /// <summary>
    /// Windows 설정 > 게임 > Xbox 모드 > 홈 앱이 ClawHub인지. 그러면 Xbox 모드에서도 홈은 ClawHub이고
    /// Xbox 앱으로 넘기지 않는다 (ClawHub ↔ 데스크톱만 오간다).
    /// </summary>
    public static bool HomeIsClawHub =>
        string.Equals(Microsoft.Win32.Registry.GetValue(GamingConfigKey, "GamingHomeApp", null) as string, HomeAppId, StringComparison.OrdinalIgnoreCase);

    // ── 지금 앞에 있는 앱 판별 ──────────────────────────────

    public readonly record struct Owner(string Name, string? Path);

    /// <summary>창을 가진 프로세스 (창 없음/이미 종료됨이면 null).</summary>
    public static Owner? OwnerOf(IntPtr hwnd)
    {
        if (hwnd == IntPtr.Zero) return null;
        GetWindowThreadProcessId(hwnd, out var pid);
        if (pid == 0) return null;
        try
        {
            using var p = Process.GetProcessById((int)pid);
            return new Owner(p.ProcessName, GameSession.GetPath(p));
        }
        catch (Exception) { return null; }
    }

    public static Owner? ForegroundOwner() => OwnerOf(GetForegroundWindow());

    /// <summary>Game Bar 프로세스가 떠 있는지 (보이는지는 모른다 — 닫혀도 프로세스는 남는다).</summary>
    public static bool IsGameBarRunning()
    {
        var ps = Process.GetProcessesByName("GameBar");
        foreach (var p in ps) p.Dispose();
        return ps.Length > 0;
    }

    private static readonly HashSet<string> GameBarProcesses = new(StringComparer.OrdinalIgnoreCase)
    {
        "GameBar", "GameBarFTServer", "XboxGameBarWidgets",
        "ClawWidget",     // ClawHub 빠른 설정 위젯
        "Gamebar_Widget", // MSI 빠른 설정 (OEM 위젯)
    };

    /// <summary>Game Bar 오버레이(또는 그 안의 위젯)가 앞에 있는지.</summary>
    public static bool IsGameBar(Owner? o) => o is { } x &&
        (GameBarProcesses.Contains(x.Name) ||
         x.Path?.Contains(@"\Microsoft.XboxGamingOverlay_", StringComparison.OrdinalIgnoreCase) == true ||
         x.Path?.Contains(@"\ClawHub.Widget_", StringComparison.OrdinalIgnoreCase) == true);

    /// <summary>Xbox 앱(홈)이 앞에 있는지. 같은 패키지의 Game Bar 위젯 호스트는 제외.</summary>
    public static bool IsXboxApp(Owner? o) => o is { } x &&
        !GameBarProcesses.Contains(x.Name) &&
        (x.Name.Equals("XboxPcApp", StringComparison.OrdinalIgnoreCase) ||
         x.Path?.Contains(@"\Microsoft.GamingApp_", StringComparison.OrdinalIgnoreCase) == true);

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode)]
    private static extern int GetPackagesByPackageFamily(string packageFamilyName, ref uint count, IntPtr packageFullNames, ref uint bufferLength, IntPtr buffer);

    [DllImport("user32.dll")] private static extern void keybd_event(byte vk, byte scan, uint flags, UIntPtr extraInfo);
    [DllImport("user32.dll")] private static extern IntPtr GetForegroundWindow();
    [DllImport("user32.dll", CharSet = CharSet.Unicode)] private static extern IntPtr FindWindow(string? className, string? title);
    [DllImport("user32.dll")] private static extern bool IsWindowVisible(IntPtr hwnd);
    [DllImport("dwmapi.dll")] private static extern int DwmGetWindowAttribute(IntPtr hwnd, int attribute, out int value, int size);
    [DllImport("user32.dll")] private static extern uint GetWindowThreadProcessId(IntPtr hwnd, out uint pid);
}
