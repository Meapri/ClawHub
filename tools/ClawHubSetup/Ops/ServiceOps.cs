using System.Diagnostics;
using System.ServiceProcess;

namespace ClawHubSetup.Ops;

/// <summary>
/// ClawHub 서비스와 ClawApp 프로세스.
/// 안전 원칙: 서비스는 SCM으로만 멈춘다(강제 종료 금지). 서비스가 멈출 때 컨트롤러 기준 매핑을 복원하므로,
/// 멈추지 않으면 파일을 건드리지 않고 중단한다.
/// </summary>
internal static class ServiceOps
{
    private static readonly TimeSpan Timeout = TimeSpan.FromSeconds(45);

    public static ServiceControllerStatus? Status(string name = K.ServiceName)
    {
        try
        {
            using var sc = new ServiceController(name);
            return sc.Status;
        }
        catch (InvalidOperationException) { return null; } // 없음
    }

    public static string Describe(ServiceControllerStatus? status) => status switch
    {
        null => "없음",
        ServiceControllerStatus.Running => "실행 중",
        ServiceControllerStatus.Stopped => "중지됨",
        var s => s.ToString()!,
    };

    public static int AppProcessCount()
    {
        var procs = Process.GetProcessesByName("ClawApp");
        foreach (var p in procs) p.Dispose();
        return procs.Length;
    }

    public static void KillApp(SetupLog log)
    {
        foreach (var p in Process.GetProcessesByName("ClawApp"))
        {
            using (p)
            {
                try
                {
                    p.Kill();
                    p.WaitForExit(5000);
                    log.Ok($"ClawApp (PID {p.Id}) 종료");
                }
                catch (Exception ex) when (ex is InvalidOperationException or System.ComponentModel.Win32Exception)
                {
                    log.Warn($"ClawApp (PID {p.Id}) 종료 실패: {ex.Message}");
                }
            }
        }
    }

    /// <summary>SCM 정지 요청 후 멈출 때까지 기다린다. 멈추지 않으면 예외 (강제 종료하지 않음).</summary>
    public static void Stop(SetupLog log)
    {
        using var sc = new ServiceController(K.ServiceName);
        if (sc.Status == ServiceControllerStatus.Stopped) { log.Info("이미 중지됨"); return; }
        if (sc.Status != ServiceControllerStatus.StopPending)
        {
            if (sc.Status is ServiceControllerStatus.StartPending)
                sc.WaitForStatus(ServiceControllerStatus.Running, Timeout);
            sc.Stop();
        }
        try { sc.WaitForStatus(ServiceControllerStatus.Stopped, Timeout); }
        catch (System.ServiceProcess.TimeoutException)
        {
            throw new SetupException("ClawHub 서비스가 멈추지 않았어요. 컨트롤러 매핑 복원이 끝나지 않았을 수 있어 " +
                                     "강제로 끝내지 않고 중단합니다. 잠시 후 다시 시도하세요.");
        }
        log.Ok("서비스 중지 (컨트롤러 기준 매핑 복원)");
    }

    public static void Start(SetupLog log)
    {
        using var sc = new ServiceController(K.ServiceName);
        if (sc.Status == ServiceControllerStatus.Running) { log.Info("이미 실행 중"); return; }
        if (sc.Status != ServiceControllerStatus.StartPending) sc.Start();
        try { sc.WaitForStatus(ServiceControllerStatus.Running, Timeout); }
        catch (System.ServiceProcess.TimeoutException)
        {
            throw new SetupException("ClawHub 서비스가 시작되지 않았어요. 이벤트 뷰어(응용 프로그램, 원본 ClawHub)를 확인하세요.");
        }
        log.Ok("서비스 실행 중");
    }

    /// <summary>
    /// 새로 만들거나(LocalSystem, 자동 시작) 기존 서비스의 경로·시작 유형을 갱신. 설명과 실패 시 재시작(5초, 5초, 30초)도 매번 적용.
    /// </summary>
    public static void CreateOrUpdate(SetupLog log)
    {
        var bin = $"binPath= \"\\\"{K.ServiceExe}\\\"\" start= auto DisplayName= \"ClawHub\"";
        if (Status() is null)
        {
            Sc($"create {K.ServiceName} {bin}");
            log.Ok("서비스 등록 (LocalSystem, 자동 시작)");
        }
        else
        {
            Sc($"config {K.ServiceName} {bin}");
            log.Ok("서비스 설정 갱신 (경로, 자동 시작)");
        }
        Sc($"description {K.ServiceName} \"{K.ServiceDescription}\"");
        Sc($"failure {K.ServiceName} reset= 86400 actions= restart/5000/restart/5000/restart/30000");
        log.Ok("실패 시 재시작: 5초, 5초, 30초 (하루 뒤 초기화)");
    }

    public static void Delete(SetupLog log)
    {
        if (Status() is null) { log.Info("서비스 없음"); return; }
        Sc($"delete {K.ServiceName}");
        log.Ok("서비스 삭제");
    }

    private static void Sc(string args)
    {
        var (code, output) = ProcessRunner.Run("sc.exe", args);
        if (code != 0) throw new SetupException($"sc.exe {args.Split(' ')[0]} 실패 ({code}): {output}");
    }
}
