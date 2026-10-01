using ClawCore;
using Microsoft.Win32;

namespace ClawApp.Services;

public enum StartupMode { Auto, Shell, Companion }

public sealed class AppSettings
{
    private static string FilePath => Path.Combine(JsonStore.UserDir, "settings.json");
    private const string RunKey = @"Software\Microsoft\Windows\CurrentVersion\Run";

    /// <summary>셸이 보일 때 컨트롤러를 XInput 모드로, "데스크톱으로" 갈 때 Desktop 모드로 자동 전환.</summary>
    public bool AutoControllerMode { get; set; } = true;

    /// <summary>
    /// 시작 방식. Companion = Windows Xbox 모드(전체 화면 환경)처럼 다른 홈 화면이 있을 때
    /// ClawHub는 숨어서 버튼/자동 매핑만 담당한다.
    /// </summary>
    public StartupMode StartupMode { get; set; } = StartupMode.Auto;

    public const string XboxAppId = @"shell:AppsFolder\Microsoft.GamingApp_8wekyb3d8bbwe!Microsoft.Xbox.App";

    /// <summary>Auto일 때: 시작 시점에 Xbox 앱이 이미 떠 있으면 Xbox 모드로 부팅한 것으로 본다.</summary>
    public bool ResolveCompanion() => (App.StartupOverride ?? StartupMode) switch
    {
        StartupMode.Companion => true,
        StartupMode.Shell => false,
        _ => System.Diagnostics.Process.GetProcesses()
            .Where(p => p.ProcessName.Contains("Xbox", StringComparison.OrdinalIgnoreCase))
            .Any(IsXboxAppProcess),
    };

    private static bool IsXboxAppProcess(System.Diagnostics.Process p)
    {
        try { return p.MainModule?.FileName.Contains(@"\Microsoft.GamingApp_", StringComparison.OrdinalIgnoreCase) == true; }
        catch (Exception) { return false; } // 접근 불가 프로세스
    }

    public static AppSettings Load() => JsonStore.Load<AppSettings>(FilePath);
    public void Save() => JsonStore.Save(FilePath, this);

    public static bool StartWithWindows
    {
        get
        {
            using var key = Registry.CurrentUser.OpenSubKey(RunKey);
            return key?.GetValue("ClawHub") is not null;
        }
        set
        {
            using var key = Registry.CurrentUser.CreateSubKey(RunKey);
            if (value) key.SetValue("ClawHub", $"\"{Environment.ProcessPath}\"");
            else key.DeleteValue("ClawHub", throwOnMissingValue: false);
        }
    }
}
