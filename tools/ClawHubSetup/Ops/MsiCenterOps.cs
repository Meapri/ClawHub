using System.Diagnostics;
using System.ServiceProcess;
using Microsoft.Win32;

namespace ClawHubSetup.Ops;

/// <summary>
/// MSI Center M 끄기/켜기 (msi-center.ps1 -Disable / -Enable과 같음). 제거는 하지 않는다 —
/// ClawHub가 EC에 쓰는 MSI_ACPI WMI 클래스가 MSI 소프트웨어 소속이다.
/// </summary>
internal static class MsiCenterOps
{
    public static bool Installed => ServiceOps.Status(K.MsiFoundationService) is not null;

    /// <summary>MSI Foundation Service가 자동 시작이면 켜진 것으로 본다. 설치 안 됐으면 null.</summary>
    public static bool? Enabled
    {
        get
        {
            if (!Installed) return null;
            using var key = Registry.LocalMachine.OpenSubKey($@"SYSTEM\CurrentControlSet\Services\{K.MsiFoundationService}");
            return key?.GetValue("Start") is int start ? start == 2 : null;
        }
    }

    public static void Disable(SetupLog log)
    {
        foreach (var task in K.MsiTasks) Schtasks($"/Change /TN \"{task}\" /DISABLE", log);
        foreach (var p in Process.GetProcesses())
        {
            using (p)
            {
                if (!p.ProcessName.StartsWith("MSI_Center_M", StringComparison.OrdinalIgnoreCase)
                    && p.ProcessName is not ("MSI Center M" or "Command Center")) continue;
                try { p.Kill(); log.Info($"{p.ProcessName} 종료"); }
                catch (Exception) { /* 이미 종료 */ }
            }
        }
        Sc($"config \"{K.MsiFoundationService}\" start= demand");
        using (var sc = new ServiceController(K.MsiFoundationService))
        {
            if (sc.Status == ServiceControllerStatus.Running)
            {
                sc.Stop();
                try { sc.WaitForStatus(ServiceControllerStatus.Stopped, TimeSpan.FromSeconds(30)); }
                catch (System.ServiceProcess.TimeoutException) { log.Warn("MSI Foundation Service가 아직 멈추는 중"); }
            }
        }
        log.Ok("MSI Center M 끔 (작업 2개 사용 안 함, MSI Foundation Service 수동)");
    }

    public static void Enable(SetupLog log)
    {
        foreach (var task in K.MsiTasks) Schtasks($"/Change /TN \"{task}\" /ENABLE", log);
        Sc($"config \"{K.MsiFoundationService}\" start= auto");
        using (var sc = new ServiceController(K.MsiFoundationService))
        {
            if (sc.Status != ServiceControllerStatus.Running) sc.Start();
        }
        Schtasks($"/Run /TN \"{K.MsiTasks[0]}\"", log);
        log.Ok("MSI Center M 다시 켬");
    }

    private static void Schtasks(string args, SetupLog log)
    {
        var (code, output) = ProcessRunner.Run("schtasks.exe", args);
        if (code != 0) log.Warn($"schtasks {args} → {code} {output}"); // 작업이 없으면 넘어감 (스크립트의 SilentlyContinue)
    }

    private static void Sc(string args)
    {
        var (code, output) = ProcessRunner.Run("sc.exe", args);
        if (code != 0) throw new SetupException($"sc.exe {args} 실패 ({code}): {output}");
    }
}
