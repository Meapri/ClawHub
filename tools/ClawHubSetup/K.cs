using System.IO;

namespace ClawHubSetup;

/// <summary>설치 경로·레지스트리·패키지 상수. 값은 scripts/*.ps1(레거시)과 같다.</summary>
internal static class K
{
    public const string ServiceName = "ClawHub";
    public const string ServiceDescription = "MSI Claw hardware control for ClawHub (TDP, fan, battery limit, controller mapping)";

    public static readonly string InstallDir =
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles), "ClawHub");
    public static string ServiceDir => Path.Combine(InstallDir, "service");
    public static string ServiceExe => Path.Combine(ServiceDir, "ClawService.exe");
    public static string AppDir => Path.Combine(InstallDir, "app");
    public static string AppExe => Path.Combine(AppDir, "ClawApp.exe");
    public static string SetupExe => Path.Combine(InstallDir, "ClawHubSetup.exe");
    public static string IconPath => Path.Combine(InstallDir, "ClawHub.ico");

    /// <summary>서비스 데이터(컨트롤러 기준 매핑, EC 상태).</summary>
    public static readonly string DataDir =
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData), "ClawHub");
    /// <summary>
    /// install.ps1의 icacls와 같은 뜻: 상속 끊기, SYSTEM·Administrators 모든 권한, Users 읽기/실행 (하위 폴더·파일 상속).
    /// </summary>
    public const string DataDirSddl = "D:PAI(A;OICI;FA;;;SY)(A;OICI;FA;;;BA)(A;OICI;0x1200a9;;;BU)";

    public const string RunKey = @"Software\Microsoft\Windows\CurrentVersion\Run";
    public const string RunValue = "ClawHub";

    public const string UninstallKey = @"SOFTWARE\Microsoft\Windows\CurrentVersion\Uninstall\ClawHub";
    public const string ClawHubKey = @"SOFTWARE\ClawHub";
    /// <summary>설치 프로그램이 바꾼 것을 기록 (제거 때 이것만 되돌린다).</summary>
    public const string SetupKey = @"SOFTWARE\ClawHub\Setup";
    /// <summary>register-home.ps1과 같은 백업 위치 (스크립트로 바꾼 것도 제거 때 복원된다).</summary>
    public const string BackupKey = @"SOFTWARE\ClawHub\Backup";

    public const string GamingKey = @"SOFTWARE\Microsoft\Windows\CurrentVersion\GamingConfiguration";
    public const string OemAppsKey = GamingKey + @"\OEMApps";
    public const string OemWidgetValue = "OEMGameBarWidget";
    public const string DevModeKey = @"SOFTWARE\Microsoft\Windows\CurrentVersion\AppModelUnlock";

    public const string Publisher = "CN=ClawHub Dev";
    public const string WidgetName = "ClawHub.Widget";
    public const string WidgetFamily = "ClawHub.Widget_tj9ced57bhrww";
    public const string HomeName = "ClawHub";
    /// <summary>백업 당시 OEMGameBarWidget 값이 아예 없었음 (복원 = 값 삭제).</summary>
    public const string NoValue = "<none>";

    public const string MsiFoundationService = "MSI Foundation Service";
    public static readonly string[] MsiTasks = ["MSI_Center_M_Server", "MSI_Center_M_Updater"];

    public const string PayloadResource = "ClawHub.payload.zip";
}
