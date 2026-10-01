using System.Diagnostics;
using System.ServiceProcess;
using Microsoft.Win32;

namespace ClawService;

/// <summary>
/// MSI Center M 켜기/끄기 (제거는 하지 않음 — EC용 MSI_ACPI WMI 클래스가 MSI 소프트웨어 소속).
/// scripts/msi-center.ps1과 같은 동작을 서비스(SYSTEM 권한)가 대신 한다.
/// </summary>
public sealed class MsiCenterManager(ILogger<MsiCenterManager> log)
{
    private static readonly string[] Tasks = ["MSI_Center_M_Server", "MSI_Center_M_Updater"];
    private const string FoundationService = "MSI Foundation Service";

    public bool? IsEnabled
    {
        get
        {
            try
            {
                using var sc = new ServiceController(FoundationService);
                using var key = Registry.LocalMachine.OpenSubKey($@"SYSTEM\CurrentControlSet\Services\{FoundationService}");
                return key?.GetValue("Start") is int start ? start == 2 /* 자동 */ : sc.Status == ServiceControllerStatus.Running;
            }
            catch (InvalidOperationException) { return null; } // 설치 안 됨
        }
    }

    public void Set(bool enabled)
    {
        foreach (var task in Tasks)
            Run("schtasks.exe", $"/Change /TN \"{task}\" {(enabled ? "/ENABLE" : "/DISABLE")}");

        using var sc = new ServiceController(FoundationService);
        Run("sc.exe", $"config \"{FoundationService}\" start= {(enabled ? "auto" : "demand")}");

        if (enabled)
        {
            if (sc.Status != ServiceControllerStatus.Running) sc.Start();
            Run("schtasks.exe", $"/Run /TN \"{Tasks[0]}\"");
        }
        else
        {
            foreach (var p in Process.GetProcesses().Where(p => p.ProcessName.StartsWith("MSI_Center_M", StringComparison.OrdinalIgnoreCase)
                         || p.ProcessName is "MSI Center M" or "Command Center"))
            {
                try { p.Kill(); } catch (Exception) { /* 이미 종료 */ }
            }
            if (sc.Status == ServiceControllerStatus.Running) sc.Stop();
        }
        log.LogInformation("MSI Center M {State}", enabled ? "켬" : "끔");
    }

    private void Run(string exe, string args)
    {
        using var p = Process.Start(new ProcessStartInfo(exe, args) { CreateNoWindow = true, UseShellExecute = false })!;
        p.WaitForExit(10000);
        if (p.ExitCode != 0) log.LogWarning("{Exe} {Args} → {Code}", exe, args, p.ExitCode);
    }
}
