using Microsoft.Win32;

namespace ClawHubSetup;

/// <summary>
/// HKLM\SOFTWARE\ClawHub\Setup — 설치 프로그램이 실제로 바꾼 것. 단계마다 바로 기록하므로
/// 설치가 중간에 실패해도 제거가 바꾼 것만 정확히 되돌릴 수 있다.
/// </summary>
internal static class SetupState
{
    public const string Version = "Version";
    public const string RunKeySid = "RunKeySid";
    public const string MsiCenterDisabled = "MsiCenterDisabled";
    public const string OemWidgetSet = "OemWidgetSet";
    public const string OemAppSlot = "OemAppSlot";
    public const string ImportedCerts = "ImportedCerts";
    public const string WidgetInstalled = "WidgetInstalled";
    public const string HomeRegistered = "HomeRegistered";

    public static string? GetString(string name)
    {
        using var key = Registry.LocalMachine.OpenSubKey(K.SetupKey);
        return key?.GetValue(name) as string;
    }

    public static bool GetFlag(string name)
    {
        using var key = Registry.LocalMachine.OpenSubKey(K.SetupKey);
        return key?.GetValue(name) is int v && v != 0;
    }

    public static string[] GetList(string name)
    {
        using var key = Registry.LocalMachine.OpenSubKey(K.SetupKey);
        return key?.GetValue(name) as string[] ?? [];
    }

    public static void SetString(string name, string value)
    {
        using var key = Registry.LocalMachine.CreateSubKey(K.SetupKey, writable: true);
        key.SetValue(name, value, RegistryValueKind.String);
    }

    public static void SetFlag(string name)
    {
        using var key = Registry.LocalMachine.CreateSubKey(K.SetupKey, writable: true);
        key.SetValue(name, 1, RegistryValueKind.DWord);
    }

    public static void AddToList(string name, IEnumerable<string> values)
    {
        var merged = GetList(name).Concat(values).Distinct(StringComparer.OrdinalIgnoreCase).ToArray();
        using var key = Registry.LocalMachine.CreateSubKey(K.SetupKey, writable: true);
        key.SetValue(name, merged, RegistryValueKind.MultiString);
    }

    public static void Clear(string name)
    {
        using var key = Registry.LocalMachine.OpenSubKey(K.SetupKey, writable: true);
        key?.DeleteValue(name, throwOnMissingValue: false);
    }

    /// <summary>앱 및 기능에 표시된 설치 버전 (없으면 null).</summary>
    public static string? InstalledVersion()
    {
        using var key = Registry.LocalMachine.OpenSubKey(K.UninstallKey);
        return key?.GetValue("DisplayVersion") as string;
    }

    /// <summary>Setup 키를 지우고, 비어 있으면 Backup·ClawHub 키도 지운다.</summary>
    public static void DeleteAll()
    {
        Registry.LocalMachine.DeleteSubKeyTree(K.SetupKey, throwOnMissingSubKey: false);
        DeleteIfEmpty(K.BackupKey);
        DeleteIfEmpty(K.ClawHubKey);
    }

    private static void DeleteIfEmpty(string path)
    {
        using (var key = Registry.LocalMachine.OpenSubKey(path))
        {
            if (key is null || key.SubKeyCount > 0 || key.ValueCount > 0) return;
        }
        Registry.LocalMachine.DeleteSubKey(path, throwOnMissingSubKey: false);
    }
}
