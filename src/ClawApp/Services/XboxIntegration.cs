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
            Process.Start(new ProcessStartInfo("explorer.exe", AppSettings.XboxAppId))?.Dispose();
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
    [DllImport("user32.dll")] private static extern uint GetWindowThreadProcessId(IntPtr hwnd, out uint pid);
}
