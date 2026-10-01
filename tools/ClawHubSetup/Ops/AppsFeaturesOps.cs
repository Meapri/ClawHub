using Microsoft.Win32;

namespace ClawHubSetup.Ops;

/// <summary>설정 > 앱 > 설치된 앱(앱 및 기능) 항목. 제거는 설치 폴더의 ClawHubSetup.exe /uninstall.</summary>
internal static class AppsFeaturesOps
{
    public static void Register(string version, SetupLog log)
    {
        using var key = Registry.LocalMachine.CreateSubKey(K.UninstallKey, writable: true);
        key.SetValue("DisplayName", "ClawHub");
        key.SetValue("DisplayVersion", version);
        key.SetValue("Publisher", "ClawHub");
        key.SetValue("DisplayIcon", K.IconPath);
        key.SetValue("InstallLocation", K.InstallDir);
        key.SetValue("UninstallString", $"\"{K.SetupExe}\" /uninstall");
        key.SetValue("InstallDate", DateTime.Now.ToString("yyyyMMdd"));
        key.SetValue("EstimatedSize", (int)Math.Min(int.MaxValue, FileOps.DirectorySizeKb(K.InstallDir)), RegistryValueKind.DWord);
        key.SetValue("NoModify", 1, RegistryValueKind.DWord);
        key.SetValue("NoRepair", 1, RegistryValueKind.DWord);
        log.Ok($"앱 및 기능: ClawHub {version}");
    }

    public static void Unregister(SetupLog log)
    {
        Registry.LocalMachine.DeleteSubKeyTree(K.UninstallKey, throwOnMissingSubKey: false);
        log.Ok("앱 및 기능 항목 삭제");
    }
}
