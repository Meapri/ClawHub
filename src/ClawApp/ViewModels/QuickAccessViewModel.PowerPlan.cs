namespace ClawApp.ViewModels;

public sealed partial class QuickAccessViewModel
{
    private PowerPlanViewModel? _powerPlan;

    /// <summary>Windows 전원 구성표(화면 끄기·절전 등)와 게임 중 절전 방지 (설정 화면과 공유). UI: Views/Sections/SleepQuickSection.</summary>
    public PowerPlanViewModel PowerPlan => _powerPlan ??= new PowerPlanViewModel(_shell);
}
