using ClawCore.Controller;
using ClawCore.Ipc;
using Microsoft.Win32;

namespace ClawService;

public sealed class RequestHandler(ILogger<RequestHandler> log, ControllerManager controller, EcManager ec, MsiCenterManager msi)
{
    private static readonly string? Bios =
        Registry.GetValue(@"HKEY_LOCAL_MACHINE\HARDWARE\DESCRIPTION\System\BIOS", "BIOSVersion", null) as string;

    private static void Validate(Request req)
    {
        if (req.ChargeLimit is { } cl && !Enum.IsDefined(cl)) throw new ArgumentException("잘못된 충전 한도");
        if (req.Performance is { } p && (!Enum.IsDefined(p.Mode) || !Enum.IsDefined(p.Fan)))
            throw new ArgumentException("잘못된 성능 설정");
        if (req.ControllerMode is { } m && m is not (GamepadMode.XInput or GamepadMode.Desktop))
            throw new ArgumentException("허용되지 않은 컨트롤러 모드");
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
                    ec.SetPerformance(req.Performance ?? throw new ArgumentException("Performance 누락"));
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
                    controller.ApplyMapping(req.Bindings ?? throw new ArgumentException("Bindings 누락"));
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
            BiosVersion = Bios,
        };
        if (ec.Error is not null) s.Warnings.Add($"EC: {ec.Error}");
        if (!controller.Connected) s.Warnings.Add("컨트롤러가 연결되지 않았습니다");
        return s;
    }
}
