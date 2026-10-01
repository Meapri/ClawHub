using Microsoft.Win32;

namespace ClawHubSetup.Ops;

/// <summary>GamingConfiguration (HKLM): Game Bar OEM 위젯, Xbox 앱 "내 앱" OEM 슬롯. register-home.ps1과 같은 동작.</summary>
internal static class GamingOps
{
    public static string? OemWidget()
    {
        using var key = Registry.LocalMachine.OpenSubKey(K.GamingKey);
        return key?.GetValue(K.OemWidgetValue) as string;
    }

    private static string? Backup()
    {
        using var key = Registry.LocalMachine.OpenSubKey(K.BackupKey);
        return key?.GetValue(K.OemWidgetValue) as string;
    }

    public static bool HasBackup => Backup() is not null;

    /// <summary>OEMGameBarWidget = ClawHub 위젯. 원래 값(MSI 퀵 설정)은 한 번만 백업한다.</summary>
    public static void SetOemWidget(SetupLog log)
    {
        if (PackageOps.Find(K.WidgetName, K.Publisher)?.Id.FamilyName != K.WidgetFamily)
            throw new SetupException($"위젯 패키지 {K.WidgetFamily}가 이 사용자에게 설치돼 있지 않아요.");
        var current = OemWidget();
        if (current == K.WidgetFamily) { log.Info($"이미 {K.WidgetFamily}"); return; }

        // 백업은 한 번만: 원래 값(예: MSI 퀵 설정)을 나중 값으로 덮지 않는다
        if (Backup() is null)
        {
            using var backup = Registry.LocalMachine.CreateSubKey(K.BackupKey, writable: true);
            var saved = current ?? K.NoValue;
            backup.SetValue(K.OemWidgetValue, saved, RegistryValueKind.String);
            log.Info($"원래 값 백업: {saved}");
        }
        using (var gaming = Registry.LocalMachine.CreateSubKey(K.GamingKey, writable: true))
            gaming.SetValue(K.OemWidgetValue, K.WidgetFamily, RegistryValueKind.String);
        log.Ok($"OEMGameBarWidget = {K.WidgetFamily} (Game Bar를 다시 시작하거나 로그아웃/로그인하면 적용)");
    }

    /// <summary>백업이 있으면 복원 (값이 아직 우리 것일 때만 — 그사이 다른 프로그램이 바꿨으면 그대로 둔다).</summary>
    public static void RestoreOemWidget(SetupLog log)
    {
        var saved = Backup();
        if (saved is null) { log.Info("백업 없음 — 그대로 둠"); return; }
        if (OemWidget() == K.WidgetFamily)
        {
            using var gaming = Registry.LocalMachine.OpenSubKey(K.GamingKey, writable: true);
            if (saved == K.NoValue) gaming?.DeleteValue(K.OemWidgetValue, throwOnMissingValue: false);
            else gaming?.SetValue(K.OemWidgetValue, saved, RegistryValueKind.String);
            log.Ok($"OEMGameBarWidget 복원: {saved}");
        }
        else
        {
            log.Info($"현재 값이 ClawHub가 아니라 그대로 둠: {OemWidget() ?? "(없음)"}");
        }
        using var backup = Registry.LocalMachine.OpenSubKey(K.BackupKey, writable: true);
        backup?.DeleteValue(K.OemWidgetValue, throwOnMissingValue: false);
    }

    /// <summary>ClawHub가 이미 쓰는 슬롯, 없으면 빈 슬롯 (1..3). 둘 다 없으면 null.</summary>
    public static (string Key, bool Mine)? FindOemSlot()
    {
        for (var i = 1; i <= 3; i++)
        {
            var path = $@"{K.OemAppsKey}\OEMAppPathsAndName{i}";
            using var key = Registry.LocalMachine.OpenSubKey(path);
            if (key?.GetValue("OemAppName") as string == "ClawHub") return (path, true);
        }
        for (var i = 1; i <= 3; i++)
        {
            var path = $@"{K.OemAppsKey}\OEMAppPathsAndName{i}";
            using var key = Registry.LocalMachine.OpenSubKey(path);
            if (string.IsNullOrEmpty(key?.GetValue("OemAppName") as string)) return (path, false);
        }
        return null;
    }

    /// <summary>Xbox 앱 "내 앱"에 ClawHub 추가. 반환: 쓴 키 경로 (빈 슬롯이 없으면 null).</summary>
    public static string? SetOemSlot(SetupLog log)
    {
        if (FindOemSlot() is not { } slot)
        {
            log.Warn("Xbox 앱의 OEM 슬롯 3개가 모두 사용 중이라 건너뜀");
            return null;
        }
        using var key = Registry.LocalMachine.CreateSubKey(slot.Key, writable: true);
        key.SetValue("OemAppName", "ClawHub", RegistryValueKind.String);
        key.SetValue("OemAppExePath", K.AppExe, RegistryValueKind.String);
        key.SetValue("OemAppIconPath", System.IO.Path.Combine(K.AppDir, "Assets", "Square150x150Logo.png"), RegistryValueKind.String);
        log.Ok($"Xbox 앱 내 앱: {slot.Key}");
        return slot.Key;
    }

    public static void ClearOemSlot(SetupLog log)
    {
        if (FindOemSlot() is not { Mine: true } slot) { log.Info("Xbox 앱 내 앱에 ClawHub 없음"); return; }
        using var key = Registry.LocalMachine.OpenSubKey(slot.Key, writable: true);
        foreach (var name in new[] { "OemAppName", "OemAppExePath", "OemAppIconPath" })
            key?.DeleteValue(name, throwOnMissingValue: false);
        log.Ok($"Xbox 앱 내 앱에서 제거: {slot.Key}");
    }
}
