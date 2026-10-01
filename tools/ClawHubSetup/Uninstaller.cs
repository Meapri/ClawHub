using System.IO;
using ClawHubSetup.Ops;

namespace ClawHubSetup;

/// <summary>
/// 제거: 설치의 역순. 앱 종료 → 서비스 정지(컨트롤러 기준 매핑 복원)를 가장 먼저, 서비스가 안 멈추면 아무것도 지우지 않고 중단.
/// 설치 프로그램이 바꾼 것(HKLM\SOFTWARE\ClawHub\Setup에 기록)만 되돌린다. 사용자 데이터는 선택했을 때만 지운다.
/// </summary>
internal sealed class Uninstaller(SetupOptions options, UserContext user)
{
    public IReadOnlyList<SetupStep> BuildSteps()
    {
        var steps = new List<SetupStep>
        {
            new("ClawHub 앱 종료",
                () => ServiceOps.AppProcessCount() is var n && n > 0 ? $"실행 중인 ClawApp {n}개 종료" : "실행 중인 ClawApp 없음",
                ServiceOps.KillApp),

            new("ClawHub 서비스 중지 (컨트롤러 매핑 복원)",
                () => ServiceOps.Status() is { } s
                    ? $"{ServiceOps.Describe(s)} → SCM으로 정지하고 멈출 때까지 대기 (강제 종료 안 함, 안 멈추면 제거 중단)"
                    : "서비스 없음",
                log => { if (ServiceOps.Status() is not null) ServiceOps.Stop(log); else log.Info("서비스 없음"); },
                Critical: true),

            new("ClawHub 서비스 삭제",
                () => ServiceOps.Status() is null ? "서비스 없음" : "sc delete ClawHub",
                ServiceOps.Delete,
                Critical: true),

            new("Game Bar OEM 위젯 복원",
                () => GamingOps.HasBackup
                    ? $"백업된 원래 값으로 복원 (현재 값 {GamingOps.OemWidget() ?? "(없음)"}가 ClawHub일 때만)"
                    : "백업 없음 → 그대로",
                GamingOps.RestoreOemWidget),

            new("Game Bar 위젯 제거",
                () => PlanPackage(K.WidgetName) + "\n의존 프레임워크(VCLibs, UI.Xaml, .NET Native)는 다른 앱이 쓰므로 남김",
                log => RemovePackage(K.WidgetName, log)),

            new("Xbox 모드 홈 앱 등록 해제",
                () => PlanPackage(K.HomeName) + "\nXbox 앱 내 앱: " +
                      (GamingOps.FindOemSlot() is { Mine: true } s ? $"{s.Key}에서 ClawHub 제거" : "ClawHub 없음"),
                log =>
                {
                    RemovePackage(K.HomeName, log);
                    GamingOps.ClearOemSlot(log);
                }),

            new("서명 인증서 신뢰 해제",
                () => SetupState.GetList(SetupState.ImportedCerts) is { Length: > 0 } certs
                    ? $"설치 프로그램이 추가한 것만 LocalMachine\\TrustedPeople에서 제거: {string.Join(", ", certs)}"
                    : "설치 프로그램이 추가한 인증서 없음 → 그대로 (스크립트로 넣은 인증서는 남김)",
                log => CertOps.Remove(SetupState.GetList(SetupState.ImportedCerts), log)),

            new("MSI Center M 다시 켜기",
                () => !SetupState.GetFlag(SetupState.MsiCenterDisabled)
                    ? "설치 프로그램이 끈 적 없음 → 그대로"
                    : MsiCenterOps.Installed ? "작업 2개 사용, MSI Foundation Service 자동·시작" : "MSI Center M 없음 → 건너뜀",
                log =>
                {
                    if (!SetupState.GetFlag(SetupState.MsiCenterDisabled)) { log.Info("설치 프로그램이 끈 적 없음"); return; }
                    if (!MsiCenterOps.Installed) throw new SkipStepException("MSI Center M이 설치돼 있지 않음");
                    MsiCenterOps.Enable(log);
                }),

            new("로그인 시 자동 실행 해제",
                () => $"{string.Join(", ", RunKeySids().Select(s => s ?? "HKCU"))}의 Run\\{K.RunValue} 삭제",
                log =>
                {
                    var removed = false;
                    foreach (var sid in RunKeySids())
                    {
                        try { removed |= UserContext.DeleteRunValue(sid); }
                        catch (Exception ex) { log.Warn($"{sid ?? "HKCU"}: {ex.Message}"); }
                    }
                    if (removed) log.Ok("자동 실행 해제"); else log.Info("Run 값 없음");
                }),

            new("설치 폴더 삭제",
                () => Directory.Exists(K.InstallDir) ? $"{K.InstallDir} 삭제 (사용 중인 파일은 재부팅 때)" : "설치 폴더 없음",
                log => FileOps.DeleteDir(K.InstallDir, log)),
        };

        if (options.PurgeUserData)
        {
            steps.Add(new("사용자 데이터 삭제",
                () => $"{SafeUserDataDir()} (설정·게임 라이브러리·매핑 프로필)\n{K.DataDir} (서비스 상태·컨트롤러 기준 매핑 백업)",
                log =>
                {
                    FileOps.DeleteDir(user.UserDataDir, log);
                    FileOps.DeleteDir(K.DataDir, log);
                },
                Optional: true));
        }
        else
        {
            steps.Add(new("사용자 데이터 유지", () => $"{SafeUserDataDir()}, {K.DataDir} 남김", log => log.Info("사용자 데이터는 남겨 둠")));
        }

        steps.Add(new("앱 및 기능 항목·설치 기록 삭제",
            () => $"HKLM\\{K.UninstallKey}, HKLM\\{K.SetupKey} 삭제",
            log =>
            {
                AppsFeaturesOps.Unregister(log);
                SetupState.DeleteAll();
                log.Ok("설치 기록 삭제");
            }));
        return steps;
    }

    private string SafeUserDataDir()
    {
        try { return user.UserDataDir; }
        catch (SetupException ex) { return ex.Message; }
    }

    /// <summary>Run 값을 지울 곳: 기록된 SID, 로그인 사용자, 현재(관리자) 사용자. null = HKCU.</summary>
    private IEnumerable<string?> RunKeySids()
    {
        var sids = new List<string?> { null };
        foreach (var sid in new[] { SetupState.GetString(SetupState.RunKeySid), user.TargetSid.Value })
        {
            if (sid is null || sid == user.ElevatedSid.Value || sids.Contains(sid)) continue;
            sids.Add(sid);
        }
        return sids;
    }

    private string PlanPackage(string name)
    {
        var v = PackageOps.InstalledVersion(name, K.Publisher);
        var who = user.SameUser ? "" : $" ({user.ElevatedName} 기준 — 로그인 사용자 {user.TargetName}의 패키지는 그 계정에서 제거해야 함)";
        return (v is null ? $"{name} 설치 안 됨" : $"{name} {v} 제거") + who;
    }

    private static void RemovePackage(string name, SetupLog log)
    {
        if (PackageOps.Find(name, K.Publisher) is not { } pkg) { log.Info($"{name} 설치 안 됨"); return; }
        PackageOps.Remove(pkg);
        log.Ok($"제거: {pkg.Id.FullName}");
    }
}
