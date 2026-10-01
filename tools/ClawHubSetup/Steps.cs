namespace ClawHubSetup;

internal sealed class SetupOptions
{
    public bool AutoStart { get; set; } = true;
    public bool Widget { get; set; } = true;
    public bool Home { get; set; } = true;
    public bool OemWidget { get; set; }
    public bool DisableMsiCenter { get; set; }
    public bool PurgeUserData { get; set; }

    /// <summary>드라이런: 선택 단계까지 전부 보여 준다.</summary>
    public static SetupOptions All() => new() { OemWidget = true, DisableMsiCenter = true, PurgeUserData = true };
}

/// <summary>
/// 설치/제거의 한 단계. Plan은 읽기 전용으로 지금 상태에서 무엇을 할지 설명하고(드라이런), Run이 실제로 한다.
/// Critical 단계가 실패하면 중단하고, 나머지는 경고만 남기고 계속한다.
/// </summary>
internal sealed record SetupStep(string Title, Func<string> Plan, Action<SetupLog> Run, bool Critical = false, bool Optional = false);

/// <summary>단계를 건너뛸 때 (실패가 아님).</summary>
internal sealed class SkipStepException(string reason) : Exception(reason);

internal enum RunResult { Success, Warnings, Failed }

internal static class StepRunner
{
    public static RunResult Run(IReadOnlyList<SetupStep> steps, SetupLog log, Action<int, int, string> progress, Action<SetupLog>? onAbort)
    {
        var warnings = 0;
        for (var i = 0; i < steps.Count; i++)
        {
            var step = steps[i];
            progress(i, steps.Count, step.Title);
            log.Step(step.Title);
            try
            {
                step.Run(log);
            }
            catch (SkipStepException skip)
            {
                log.Info($"건너뜀: {skip.Message}");
            }
            catch (Exception ex)
            {
                var message = ex is SetupException ? ex.Message : $"{ex.GetType().Name}: {ex.Message}";
                if (step.Critical)
                {
                    log.Error(message);
                    log.Error("필수 단계가 실패해서 중단했어요.");
                    try { onAbort?.Invoke(log); }
                    catch (Exception abortEx) { log.Error($"복구 실패: {abortEx.Message}"); }
                    progress(i, steps.Count, step.Title);
                    return RunResult.Failed;
                }
                log.Warn($"{message} (계속 진행)");
                warnings++;
            }
        }
        progress(steps.Count, steps.Count, "완료");
        return warnings == 0 ? RunResult.Success : RunResult.Warnings;
    }

    /// <summary>드라이런 출력: 단계마다 지금 상태 기준의 계획.</summary>
    public static IEnumerable<string> Describe(IReadOnlyList<SetupStep> steps)
    {
        for (var i = 0; i < steps.Count; i++)
        {
            var s = steps[i];
            string plan;
            try { plan = s.Plan(); }
            catch (Exception ex) { plan = $"(상태 확인 실패: {ex.Message})"; }
            var tags = (s.Critical ? " [필수]" : "") + (s.Optional ? " [선택: 체크했을 때]" : "");
            yield return $"{i + 1,2}. {s.Title}{tags}";
            foreach (var line in plan.Split('\n')) yield return $"      {line}";
        }
    }
}
