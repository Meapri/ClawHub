namespace ClawApp.ViewModels;

public sealed partial class SettingsViewModel
{
    /// <summary>Windows 전원 구성표 설정과 게임 중 절전 방지. UI: Views/Sections/PowerPlanSection (배터리 분류).</summary>
    public PowerPlanViewModel PowerPlan => _shell.QuickAccess.PowerPlan;
    public SoundViewModel Sound => _shell.Sound;
    public ConnectivityViewModel Connections => _shell.Connections;
    public NightModeViewModel Night => _shell.Night;
    public SystemInfoViewModel SystemInfo => _shell.SystemInfo;
}
