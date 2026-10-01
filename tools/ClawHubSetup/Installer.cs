using System.IO;
using ClawHubSetup.Ops;

namespace ClawHubSetup;

/// <summary>
/// 설치/업데이트 단계 (scripts/install.ps1 + register-home.ps1 + 위젯 설치 + msi-center.ps1 -Disable).
/// 순서 원칙: 앱 종료 → 서비스 정지(컨트롤러 기준 매핑 복원) → 파일 교체 → 서비스 시작. 서비스가 멈추지 않으면 중단.
/// </summary>
internal sealed class Installer(Payload? payload, SetupOptions options, UserContext user) : IDisposable
{
    private readonly string _temp = Path.Combine(Path.GetTempPath(), $"ClawHubSetup-{Guid.NewGuid():N}");

    private Payload P => payload ?? throw new SetupException("이 ClawHubSetup.exe에는 설치할 파일(페이로드)이 없어요. tools\\ClawHubSetup\\build.ps1로 빌드하세요.");

    public IReadOnlyList<SetupStep> BuildSteps()
    {
        var steps = new List<SetupStep>
        {
            new("ClawHub 앱 종료",
                () => ServiceOps.AppProcessCount() is var n && n > 0 ? $"실행 중인 ClawApp {n}개 종료" : "실행 중인 ClawApp 없음",
                ServiceOps.KillApp),

            new("ClawHub 서비스 중지 (컨트롤러 매핑 복원)",
                () => ServiceOps.Status() switch
                {
                    null => "서비스 없음 → 건너뜀 (새 설치)",
                    System.ServiceProcess.ServiceControllerStatus.Stopped => "이미 중지됨",
                    var s => $"{ServiceOps.Describe(s)} → SCM으로 정지하고 멈출 때까지 대기 (강제 종료 안 함, 안 멈추면 설치 중단)",
                },
                log => { if (ServiceOps.Status() is not null) ServiceOps.Stop(log); else log.Info("서비스 없음 (새 설치)"); },
                Critical: true),

            new("파일 복사",
                () => payload is null
                    ? "페이로드 없음 → 이 빌드로는 설치할 수 없음 (build.ps1로 빌드한 exe 필요)"
                    : $"{payload.Describe()}\n→ {K.ServiceDir}, {K.AppDir} 교체 (임시 폴더에 먼저 풀고 바꿔 끼움)\n→ {K.SetupExe} (제거·복구용), {K.IconPath}",
                log => FileOps.InstallFiles(P, log),
                Critical: true),

            new("서비스 데이터 폴더 권한",
                () => $"{K.DataDir}: 상속 끊기, SYSTEM·Administrators 모든 권한, Users 읽기/실행 ({K.DataDirSddl})",
                FileOps.SecureDataDir,
                Critical: true),

            new("ClawHub 서비스 등록",
                () => (ServiceOps.Status() is null ? "새로 등록 (LocalSystem, 자동 시작)" : "기존 서비스 경로·자동 시작 갱신")
                      + $"\n경로 \"{K.ServiceExe}\", 실패 시 재시작 5초/5초/30초",
                ServiceOps.CreateOrUpdate,
                Critical: true),

            new("ClawHub 서비스 시작", () => "시작하고 실행 중이 될 때까지 대기", ServiceOps.Start, Critical: true),
        };

        steps.Add(options.AutoStart
            ? new SetupStep("로그인 시 ClawApp 자동 실행",
                () => $"{RunKeyLocation()}\\{K.RunValue} = \"{K.AppExe}\"",
                log =>
                {
                    user.SetRunValue($"\"{K.AppExe}\"");
                    SetupState.SetString(SetupState.RunKeySid, user.TargetSid.Value);
                    log.Ok($"{user.TargetName} 로그인 시 자동 실행");
                },
                Optional: true)
            : new SetupStep("로그인 시 자동 실행 끄기",
                () => user.GetRunValue() is null ? "Run 값 없음" : $"{RunKeyLocation()}\\{K.RunValue} 삭제",
                log =>
                {
                    if (user.SameUser ? UserContext.DeleteRunValue(null) : UserContext.DeleteRunValue(user.TargetSid.Value)) log.Ok("자동 실행 해제");
                    else log.Info("Run 값 없음");
                }));

        if ((options.Widget || options.Home) && payload is not { Certs.Count: 0 })
            steps.Add(new("서명 인증서 신뢰 (위젯·홈 패키지용)", PlanCerts, RunCerts));

        if (options.Home) steps.Add(new("Xbox 모드 홈 앱 등록", PlanHome, RunHome, Optional: true));
        if (options.Widget) steps.Add(new("Game Bar 위젯 설치", PlanWidget, RunWidget, Optional: true));
        if (options.OemWidget) steps.Add(new("Game Bar OEM 위젯을 ClawHub로", PlanOemWidget, RunOemWidget, Optional: true));
        if (options.DisableMsiCenter) steps.Add(new("MSI Center M 끄기", PlanMsiCenter, RunMsiCenter, Optional: true));

        steps.Add(new("앱 및 기능에 등록",
            () => $"HKLM\\{K.UninstallKey}: ClawHub {payload?.Version ?? "?"}, 아이콘 {K.IconPath}\n제거 명령 \"{K.SetupExe}\" /uninstall",
            log =>
            {
                AppsFeaturesOps.Register(P.Version, log);
                SetupState.SetString(SetupState.Version, P.Version);
            }));
        return steps;
    }

    private string RunKeyLocation() => user.SameUser
        ? $"HKCU\\{K.RunKey} ({user.TargetName})"
        : $"HKEY_USERS\\{user.TargetSid.Value}\\{K.RunKey} ({user.TargetName}, 관리자 계정과 다른 로그인 사용자)";

    /// <summary>설치가 중간에 멈추면: 서비스 파일이 남아 있으면 다시 시작해 둔다 (멈춘 상태도 컨트롤러는 기준 매핑이라 안전).</summary>
    public void OnAbort(SetupLog log)
    {
        if (ServiceOps.Status() is System.ServiceProcess.ServiceControllerStatus.Stopped && File.Exists(K.ServiceExe))
        {
            log.Info("기존 서비스를 다시 시작합니다.");
            ServiceOps.Start(log);
        }
    }

    // ── 인증서 ─────────────────────────────────────────────

    private string PlanCerts()
    {
        if (payload is null) return "페이로드 없음";
        return string.Join("\n", CertOps.Load(payload).Select(c =>
            $"{c.Subject} {c.Thumbprint}: " + (CertOps.IsTrusted(c.Thumbprint) ? "이미 신뢰됨" : "LocalMachine\\TrustedPeople에 추가")));
    }

    private void RunCerts(SetupLog log)
    {
        var added = CertOps.Import(CertOps.Load(P), log);
        if (added.Count > 0) SetupState.AddToList(SetupState.ImportedCerts, added);
    }

    // ── Xbox 모드 홈 (외부 위치 신원 패키지) ─────────────────

    private string? HomeBlocker()
    {
        if (payload?.HomeIdentity is null) return "이 설치 파일에 홈 신원 패키지가 없음 (빌드할 때 Windows SDK 또는 서명 인증서가 없었음)";
        if (!PackageOps.DeveloperModeOn()) return "개발자 모드가 꺼져 있음 (gamingHome 사용자 지정 기능의 서명 안 된 SCCD는 개발자 모드에서만 허용) — 설정 > 시스템 > 개발자용에서 켜고 다시 설치";
        if (!user.SameUser) return $"관리자 계정({user.ElevatedName})과 로그인 사용자({user.TargetName})가 달라 사용자별 패키지를 등록할 수 없음 — {user.TargetName} 계정에서 관리자 권한으로 다시 실행";
        return null;
    }

    private string PlanHome()
    {
        if (HomeBlocker() is { } why) return $"건너뜀: {why}";
        var installed = PackageOps.InstalledVersion(K.HomeName, K.Publisher);
        var pkg = installed is not null && installed >= payload!.HomeIdentity!.Version
            ? $"이미 {installed} 등록됨 → 그대로 둠"
            : (installed is null ? "" : $"기존 {installed} 제거 후 ") + $"{payload!.HomeIdentity} 등록 (외부 위치 = {K.AppDir})";
        var slot = GamingOps.FindOemSlot() is { } s ? $"{s.Key}{(s.Mine ? " (이미 ClawHub)" : " (빈 슬롯)")}" : "빈 슬롯 없음 → 건너뜀";
        return $"{pkg}\nXbox 앱 내 앱 OEM 슬롯: {slot}";
    }

    private void RunHome(SetupLog log)
    {
        if (HomeBlocker() is { } why) throw new SkipStepException(why);
        var id = P.HomeIdentity!;
        var installed = PackageOps.Find(K.HomeName, K.Publisher);
        if (installed is not null && PackageOps.ToVersion(installed.Id.Version) >= id.Version)
        {
            log.Info($"이미 등록됨: {installed.Id.FullName}");
        }
        else
        {
            if (installed is not null)
            {
                PackageOps.Remove(installed);
                log.Info($"이전 등록 제거: {installed.Id.FullName}");
            }
            var msix = P.ExtractFile(P.HomeMsix!, _temp);
            PackageOps.Add(msix, [], K.AppDir);
            log.Ok($"등록: {id} (외부 위치 {K.AppDir})");
        }
        SetupState.SetFlag(SetupState.HomeRegistered);
        if (GamingOps.SetOemSlot(log) is { } slot) SetupState.SetString(SetupState.OemAppSlot, slot);
        log.Info("다음: 설정 > 게임 > Xbox 모드 > 홈 앱 선택 > ClawHub");
    }

    // ── Game Bar 위젯 ─────────────────────────────────────

    private string? WidgetBlocker()
    {
        if (payload?.WidgetIdentity is null) return "이 설치 파일에 위젯 MSIX가 없음 (빌드할 때 src\\ClawWidget\\AppPackages가 비어 있었음)";
        if (!user.SameUser) return $"관리자 계정({user.ElevatedName})과 로그인 사용자({user.TargetName})가 달라 사용자별 패키지를 설치할 수 없음 — {user.TargetName} 계정에서 관리자 권한으로 다시 실행";
        return null;
    }

    private string PlanWidget()
    {
        if (WidgetBlocker() is { } why) return $"건너뜀: {why}";
        var id = payload!.WidgetIdentity!;
        var installed = PackageOps.InstalledVersion(K.WidgetName, K.Publisher);
        if (installed is not null && installed >= id.Version) return $"이미 {installed} 설치됨 → 건너뜀 (같은 버전은 다시 설치하지 않음 — 위젯을 바꾸면 버전을 올릴 것)";
        var lines = new List<string> { (installed is null ? "설치: " : $"{installed} → ") + id.ToString() };
        foreach (var dep in payload.WidgetDeps)
        {
            var depId = ReadDep(dep);
            lines.Add($"의존 {depId}: " + (PackageOps.IsSatisfied(depId) ? "같거나 새 버전 있음 → 넘기지 않음" : "함께 설치"));
        }
        return string.Join("\n", lines);
    }

    private AppxIdentity ReadDep(string entry)
    {
        using var ms = new MemoryStream(P.ReadBytes(entry));
        return AppxIdentity.Read(ms);
    }

    private void RunWidget(SetupLog log)
    {
        if (WidgetBlocker() is { } why) throw new SkipStepException(why);
        var id = P.WidgetIdentity!;
        var installed = PackageOps.InstalledVersion(K.WidgetName, K.Publisher);
        if (installed is not null && installed >= id.Version)
        {
            log.Info($"이미 {installed} 설치됨 (같은 버전은 다시 설치하지 않음)");
            SetupState.SetFlag(SetupState.WidgetInstalled);
            return;
        }
        var deps = new List<string>();
        foreach (var dep in P.WidgetDeps)
        {
            var depId = ReadDep(dep);
            if (PackageOps.IsSatisfied(depId)) { log.Info($"의존 패키지 이미 있음: {depId}"); continue; }
            deps.Add(P.ExtractFile(dep, Path.Combine(_temp, "deps")));
            log.Info($"의존 패키지 함께 설치: {depId}");
        }
        var msix = P.ExtractFile(P.WidgetMsix!, _temp);
        PackageOps.Add(msix, deps, null);
        SetupState.SetFlag(SetupState.WidgetInstalled);
        log.Ok($"위젯 설치: {id} — Win+G > 위젯 메뉴 > ClawHub");
    }

    // ── OEM 위젯 / MSI Center ─────────────────────────────

    private string PlanOemWidget()
    {
        if (!user.SameUser) return "건너뜀: 위젯이 로그인 사용자에게 설치돼 있어야 함 (관리자 계정이 다름)";
        var current = GamingOps.OemWidget() ?? "(없음)";
        if (current == K.WidgetFamily) return "이미 ClawHub 위젯";
        return $"현재 {current} → {K.WidgetFamily}" + (GamingOps.HasBackup ? " (백업 이미 있음, 덮지 않음)" : $" (원래 값은 HKLM\\{K.BackupKey}에 백업)");
    }

    private void RunOemWidget(SetupLog log)
    {
        if (!user.SameUser) throw new SkipStepException("관리자 계정과 로그인 사용자가 달라 위젯 설치를 확인할 수 없음");
        GamingOps.SetOemWidget(log);
        SetupState.SetFlag(SetupState.OemWidgetSet);
    }

    private static string PlanMsiCenter() => MsiCenterOps.Enabled switch
    {
        null => "MSI Center M 없음 → 건너뜀",
        false => "이미 꺼져 있음 → 그대로 (제거 때 다시 켜지 않음)",
        true => $"작업 {string.Join(", ", K.MsiTasks)} 사용 안 함, 프로세스 종료, {K.MsiFoundationService} 수동·정지 (제거 때 다시 켬)",
    };

    private static void RunMsiCenter(SetupLog log)
    {
        switch (MsiCenterOps.Enabled)
        {
            case null: throw new SkipStepException("MSI Center M이 설치돼 있지 않음");
            case false: log.Info("이미 꺼져 있음"); return;
            default:
                MsiCenterOps.Disable(log);
                SetupState.SetFlag(SetupState.MsiCenterDisabled);
                return;
        }
    }

    public void Dispose()
    {
        try { if (Directory.Exists(_temp)) Directory.Delete(_temp, recursive: true); }
        catch (Exception) { /* 임시 폴더 — 남아도 무방 */ }
    }
}
