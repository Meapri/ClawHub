using System.Diagnostics;
using System.Runtime.InteropServices;
using Microsoft.Win32;

namespace ClawService;

/// <summary>
/// 3단계 가상 패드용: 실제 Claw 패드를 게임에서 숨긴다 (HidHide). ClawHub 앱만 실제 패드를 보고,
/// 게임은 앱이 만든 가상 Xbox 패드(ViGEm)만 본다.
/// <para>
/// 숨긴 뒤 패드를 다시 시작(pnputil /restart-device)해야 이미 열려 있던 게임의 XInput 핸들이 닫히고 다시 찾는다.
/// 앱 연결이 모두 끊기거나(크래시 포함) 서비스가 시작·종료될 때 늘 숨김을 푼다 — 패드가 사라진 채 남지 않게.
/// </para>
/// </summary>
public sealed class PadHider(ILogger<PadHider> log)
{
    private const string Cli = @"C:\Program Files\Nefarius Software Solutions\HidHide\x64\HidHideCLI.exe";
    // Claw 8 AI+ 내장 패드: XInput(XUSB) 장치와 그 HID 쪽 (DirectInput·WGI가 쓰는 쪽)
    private const string XusbKey = @"SYSTEM\CurrentControlSet\Enum\USB\VID_0DB0&PID_1901&MI_00";
    private const string HidKey = @"SYSTEM\CurrentControlSet\Enum\HID\VID_0DB0&PID_1901&IG_01";

    private readonly object _gate = new();

    public bool Hidden { get; private set; }

    /// <summary>드라이버가 설치돼 있는지 (HidHide + ViGEmBus).</summary>
    public static bool DriversInstalled =>
        File.Exists(Cli) && ServiceExists("HidHide") && ServiceExists("ViGEmBus");

    private static bool ServiceExists(string name) =>
        Registry.LocalMachine.OpenSubKey($@"SYSTEM\CurrentControlSet\Services\{name}") is { } k && k.GetValue("Start") is not null;

    /// <summary>숨기기: 앱(<paramref name="appPath"/>)만 실제 패드를 보게 하고 패드를 다시 시작한다.</summary>
    public void Hide(string appPath)
    {
        if (!DriversInstalled) throw new InvalidOperationException("가상 패드 드라이버(ViGEmBus·HidHide)가 설치돼 있지 않아요");
        if (!File.Exists(appPath)) throw new ArgumentException("앱 경로를 찾을 수 없어요");
        lock (_gate)
        {
            var devices = Devices();
            if (devices.Count == 0) throw new InvalidOperationException("Claw 패드를 찾지 못했어요");
            var args = new List<string> { "--app-reg", appPath };
            foreach (var d in devices) args.AddRange(["--dev-hide", d]);
            args.Add("--cloak-on");
            Run(args);
            Hidden = true;
            Restart(devices);
            log.LogInformation("실제 패드 숨김 ({Count}개, 허용 앱 {App})", devices.Count, appPath);
        }
    }

    /// <summary>숨김 풀기 (숨긴 적 없어도 안전 — 서비스 시작 시 크래시 복구용으로도 부른다).</summary>
    public void Unhide(bool force = false)
    {
        if (!File.Exists(Cli)) return;
        lock (_gate)
        {
            if (!Hidden && !force) return;
            try
            {
                Run(["--cloak-off"]);
                var wasHidden = Hidden;
                Hidden = false;
                if (wasHidden || force) Restart(Devices());
                log.LogInformation("실제 패드 숨김 해제");
            }
            catch (Exception ex)
            {
                log.LogError(ex, "패드 숨김 해제 실패");
            }
        }
    }

    /// <summary>
    /// 서비스 시작 시: 지난번에 숨긴 채로 끝났으면(서비스 크래시·전원 끊김) 되돌린다.
    /// 다른 프로그램이 HidHide를 쓰는 경우를 생각해 우리 패드가 숨김 목록에 있을 때만.
    /// </summary>
    public void RecoverAtStartup()
    {
        if (!File.Exists(Cli)) return;
        try
        {
            var state = Run(["--cloak-state"]);
            if (!state.Contains("--cloak-on", StringComparison.OrdinalIgnoreCase)) return;
            var hidden = Run(["--dev-list"]);
            if (!hidden.Contains("VID_0DB0&PID_1901", StringComparison.OrdinalIgnoreCase)) return;
            log.LogWarning("지난번 숨김이 남아 있어 실제 패드를 되돌립니다");
            Hidden = true;
            Unhide();
        }
        catch (Exception ex)
        {
            log.LogError(ex, "패드 숨김 상태 확인 실패");
        }
    }

    /// <summary>지금 연결된 Claw 패드 장치 경로 (레지스트리에는 예전 포트 기록도 남아 있어 연결된 것만).</summary>
    private static List<string> Devices()
    {
        var list = new List<string>();
        foreach (var key in new[] { XusbKey, HidKey })
        {
            using var k = Registry.LocalMachine.OpenSubKey(key);
            if (k is null) continue;
            var prefix = key[(key.IndexOf(@"Enum\", StringComparison.Ordinal) + 5)..];
            foreach (var sub in k.GetSubKeyNames())
            {
                var id = $@"{prefix}\{sub}";
                if (CM_Locate_DevNodeW(out _, id, 0 /* NORMAL = 연결된 것만 */) == 0) list.Add(id);
            }
        }
        return list;
    }

    /// <summary>XUSB 장치를 다시 시작한다 (HID 쪽은 그 자식이라 함께 다시 시작된다).</summary>
    private void Restart(List<string> devices)
    {
        foreach (var d in devices.Where(d => d.StartsWith("USB", StringComparison.OrdinalIgnoreCase)))
        {
            var psi = new ProcessStartInfo("pnputil.exe", $"/restart-device \"{d}\"")
            {
                UseShellExecute = false, CreateNoWindow = true, RedirectStandardOutput = true, RedirectStandardError = true,
            };
            using var p = Process.Start(psi)!;
            if (!p.WaitForExit(10_000)) { try { p.Kill(); } catch (InvalidOperationException) { } }
            log.LogInformation("패드 다시 시작 {Device}: {Code}", d, p.HasExited ? p.ExitCode : -1);
        }
    }

    private static string Run(IReadOnlyList<string> args)
    {
        var psi = new ProcessStartInfo(Cli) { UseShellExecute = false, CreateNoWindow = true, RedirectStandardOutput = true, RedirectStandardError = true };
        foreach (var a in args) psi.ArgumentList.Add(a);
        using var p = Process.Start(psi)!;
        var errTask = p.StandardError.ReadToEndAsync();
        var output = p.StandardOutput.ReadToEnd();
        if (!p.WaitForExit(10_000)) { try { p.Kill(); } catch (InvalidOperationException) { } throw new TimeoutException("HidHide 응답 없음"); }
        if (p.ExitCode != 0) throw new InvalidOperationException($"HidHide 실패 ({p.ExitCode}) {errTask.Result.Trim()}");
        return output;
    }

    [DllImport("cfgmgr32.dll", CharSet = CharSet.Unicode)]
    private static extern int CM_Locate_DevNodeW(out uint devInst, string deviceId, uint flags);
}
