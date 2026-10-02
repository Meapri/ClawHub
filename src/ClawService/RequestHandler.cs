using ClawCore.Controller;
using ClawCore.Ipc;
using Microsoft.Win32;

namespace ClawService;

public sealed class RequestHandler(ILogger<RequestHandler> log, ControllerManager controller, EcManager ec, MsiCenterManager msi,
    SystemManager system, EnduranceManager endurance, PowerManager power, DisplayQualityManager displayQuality,
    PowerPlanManager powerPlan, PadHider padHider)
{
    /// <summary>위젯의 AppCommand를 런처 앱(구독자)에게 보내는 통로 — PipeServer가 연결한다.</summary>
    public Action<string>? RelayAppCommand { get; set; }

    private static readonly HashSet<string> KnownCommands =
        [AppCommands.CycleProfile, AppCommands.QuitGame, AppCommands.ShowClawHub,
         AppCommands.Screenshot, AppCommands.ToggleRecording, AppCommands.SaveClip,
         AppCommands.WidgetShown, AppCommands.WidgetHidden, AppCommands.EditMapping];

    /// <summary>"SetVolume:0"–"SetVolume:100"만 통과시킨다.</summary>
    private static bool IsVolumeCommand(string cmd) =>
        cmd.StartsWith(AppCommands.SetVolumePrefix, StringComparison.Ordinal) &&
        int.TryParse(cmd.AsSpan(AppCommands.SetVolumePrefix.Length), System.Globalization.NumberStyles.None, null, out var v) && v is >= 0 and <= 100;

    private static readonly string? Bios =
        Registry.GetValue(@"HKEY_LOCAL_MACHINE\HARDWARE\DESCRIPTION\System\BIOS", "BIOSVersion", null) as string;

    private static void Validate(Request req)
    {
        if (req.ChargeLimit is { } cl && !Enum.IsDefined(cl)) throw new ArgumentException("잘못된 충전 한도");
        if (req.Performance is { } p && (!Enum.IsDefined(p.Mode) || !Enum.IsDefined(p.Fan)))
            throw new ArgumentException("잘못된 성능 설정");
        if (req.ControllerMode is { } m && m is not (GamepadMode.XInput or GamepadMode.Desktop))
            throw new ArgumentException("허용되지 않은 컨트롤러 모드");
        if (req.Brightness is < 0 or > 100) throw new ArgumentException("밝기는 0~100");
        if (req.FpsLimit is < 0 or > 240) throw new ArgumentException("FPS 제한은 0~240");
        if (req.FrameGeneration is < 0 or > 3) throw new ArgumentException("프레임 생성은 0~3");
        if (req.AppState is { Profiles.Count: > 100 }) throw new ArgumentException("프로필이 너무 많아요");
        if (req.EnduranceControl is { } egc && !Enum.IsDefined(egc)) throw new ArgumentException("잘못된 Endurance Gaming 설정");
        if (req.EnduranceMode is { } egm && !Enum.IsDefined(egm)) throw new ArgumentException("잘못된 Endurance Gaming 모드");
        if (req.PowerSource is { } src && !Enum.IsDefined(src)) throw new ArgumentException("잘못된 전원 구분");
        if (req.PowerSetting is { } ps && !Enum.IsDefined(ps)) throw new ArgumentException("잘못된 전원 설정 항목");
    }

    public Response Handle(Request req)
    {
        try
        {
            Validate(req);
            switch (req.Op)
            {
                case Ops.GetStatus:
                case Ops.Subscribe:
                    break;
                case Ops.SetPerformance:
                    if (req.Performance is null && req.PowerSource == PowerSourceKind.Battery) ec.ClearPerformanceOnBattery();
                    else ec.SetPerformance(req.Performance ?? throw new ArgumentException("Performance 누락"), req.PowerSource);
                    break;
                case Ops.SetEnduranceGaming:
                    endurance.Set(req.EnduranceControl ?? throw new ArgumentException("EnduranceControl 누락"), req.EnduranceMode);
                    break;
                case Ops.GetPowerPlan:
                    powerPlan.Refresh();
                    break;
                case Ops.SetPowerPlanSetting:
                    powerPlan.Set(req.PowerSetting ?? throw new ArgumentException("PowerSetting 누락"), req.PowerSource,
                        req.Value ?? throw new ArgumentException("Value 누락"));
                    break;
                case Ops.ApplyTemporaryPerformance:
                    ec.ApplyTemporary(req.Performance ?? throw new ArgumentException("Performance 누락"));
                    break;
                case Ops.RestorePerformance:
                    ec.RestoreDefault();
                    break;
                case Ops.SetChargeLimit:
                    ec.SetChargeLimit(req.ChargeLimit ?? throw new ArgumentException("ChargeLimit 누락"));
                    break;
                case Ops.ApplyMapping:
                    controller.ApplyMapping(req.Bindings ?? throw new ArgumentException("Bindings 누락"), req.Macros, req.Tuning);
                    break;
                case Ops.RestoreMapping:
                    controller.RestoreBaseline();
                    break;
                case Ops.SetControllerMode:
                    controller.SetMode(req.ControllerMode ?? GamepadMode.XInput);
                    break;
                case Ops.SetControllerSettings:
                    controller.SetSettings(req.ControllerSettings ?? throw new ArgumentException("ControllerSettings 누락"));
                    break;
                case Ops.GetTelemetry:
                    return new Response { Telemetry = system.GetTelemetry() }; // 자주 불리므로 상태는 생략
                case Ops.SetGraphics:
                    system.SetGraphics(req.FpsLimit, req.FrameGeneration);
                    break;
                case Ops.SetBrightness:
                    system.SetBrightness(req.Brightness ?? throw new ArgumentException("Brightness 누락"));
                    break;
                case Ops.SetDisplayQuality:
                    displayQuality.Apply(req.DisplayQuality ?? throw new ArgumentException("DisplayQuality 누락"));
                    break;
                case Ops.SetAppState:
                    system.AppState = req.AppState;
                    break;
                case Ops.AppCommand:
                    var cmd = req.Command ?? throw new ArgumentException("Command 누락");
                    if (!KnownCommands.Contains(cmd) && !cmd.StartsWith(AppCommands.SetProfilePrefix, StringComparison.Ordinal) && !IsVolumeCommand(cmd))
                        throw new ArgumentException($"알 수 없는 명령 {cmd}");
                    if (RelayAppCommand is null || system.AppState is null) throw new InvalidOperationException("ClawHub 앱이 실행 중이 아니에요");
                    RelayAppCommand(cmd);
                    break;
                case Ops.SetPadHidden:
                    if (req.Enabled == true) padHider.Hide(req.ClientPath ?? throw new InvalidOperationException("요청한 앱을 확인할 수 없어요"));
                    else padHider.Unhide();
                    break;
                case Ops.SetMsiCenter:
                    msi.Set(req.Enabled ?? throw new ArgumentException("Enabled 누락"));
                    break;
                default:
                    return new Response { Ok = false, Error = $"알 수 없는 요청 {req.Op}" };
            }
            return new Response { Status = BuildStatus() };
        }
        catch (Exception ex)
        {
            log.LogError(ex, "{Op} 실패", req.Op);
            return new Response { Ok = false, Error = ex.Message, Status = BuildStatus() };
        }
    }

    public ServiceStatus BuildStatus()
    {
        var s = new ServiceStatus
        {
            ControllerConnected = controller.Connected,
            ControllerFirmware = controller.Firmware,
            ControllerMode = controller.Mode,
            MappingActive = controller.MappingActive,
            ControllerSettings = controller.Settings,
            ControllerDefaults = controller.Defaults,
            EcAvailable = ec.Available,
            EcWritable = ec.Writable,
            EcVersion = ec.EcVersion,
            Performance = ec.Performance,
            TemporaryPerformance = ec.Temporary,
            ChargeLimit = ec.ChargeLimit,
            MsiCenterEnabled = msi.IsEnabled,
            VirtualPadDrivers = PadHider.DriversInstalled,
            PadHidden = padHider.Hidden,
            BiosVersion = Bios,
            Graphics = system.Graphics,
            DisplayQuality = displayQuality.State,
            Brightness = system.Brightness,
            AppState = system.AppState,
            OnBattery = power.OnBattery,
            PerformanceOnAc = ec.PerformanceOnAc,
            PerformanceOnBattery = ec.PerformanceOnBattery,
            BatteryHealth = power.BatteryHealth,
            PowerPlan = powerPlan.State,
        };
        if (s.Graphics is { } g) g.Endurance = endurance.State;
        if (ec.Error is not null) s.Warnings.Add($"EC: {ec.Error}");
        if (!controller.Connected) s.Warnings.Add("컨트롤러가 연결되지 않았어요");
        return s;
    }
}
