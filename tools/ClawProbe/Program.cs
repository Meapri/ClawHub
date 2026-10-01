using System.Security.Principal;
using ClawCore;
using ClawCore.Controller;
using ClawCore.Ec;

// ClawProbe: 읽기 전용 점검. 하드웨어에 아무것도 쓰지 않는다.
var admin = new WindowsPrincipal(WindowsIdentity.GetCurrent()).IsInRole(WindowsBuiltInRole.Administrator);
Console.WriteLine($"관리자 권한: {admin}");

Console.WriteLine("\n== 컨트롤러 (HID) ==");
try
{
    using var ctl = ClawController.TryOpen();
    if (ctl is null)
    {
        Console.WriteLine("명령 채널(리포트 0x0F)을 가진 HID 장치를 찾지 못함");
    }
    else
    {
        Console.WriteLine($"PID 0x{ctl.ProductId:X4}, 펌웨어 {ctl.FirmwareVersion}");
        Console.WriteLine($"경로 {ctl.DevicePath}");
        var (mode, mk) = ctl.ReadMode();
        Console.WriteLine($"모드 {mode}, M키 {mk}");

        var image = ctl.ReadProfile();
        var dump = Path.Combine(JsonStore.UserDir, $"probe-profile-{DateTime.Now:yyyyMMdd-HHmmss}.bin");
        File.WriteAllBytes(dump, image);
        Console.WriteLine($"프로필 {image.Length}바이트 → {dump}");

        foreach (var b in Enum.GetValues<ClawButton>())
        {
            var slot = ProfileLayout.Get(b);
            var head = Convert.ToHexString(image, slot.Offset, slot.MapOffset - slot.Offset);
            var map = Convert.ToHexString(image, slot.MapOffset, ProfileLayout.MaxKeysPerBinding);
            var codes = ProfileLayout.ReadBinding(image, b);
            var expect = ProfileLayout.NativeCode(b);
            var ok = codes.Length == 1 && codes[0] == expect ? "기본" : "변경됨/확인필요";
            Console.WriteLine($"  {b,-10} @0x{slot.Offset:X3} head={head,-10} map={map}  {KeyCodes.Describe(codes),-14} ({ok})");
        }
    }
}
catch (Exception ex)
{
    Console.WriteLine($"오류: {ex.GetType().Name}: {ex.Message}");
}

Console.WriteLine("\n== EC (WMI MSI_ACPI) ==");
try
{
    var wmi = MsiWmi.Open();
    Console.WriteLine($"WMI v{wmi.WmiMajor}.{wmi.WmiMinor} (쓰기 {(wmi.WritesEnabled ? "지원" : "비활성")}), EC '{wmi.EcVersion}'");
    foreach (var (name, idx) in new[] { ("PL1", EcIndex.Pl1), ("PL2", EcIndex.Pl2), ("CoolerBoost", EcIndex.CoolerBoost),
                 ("Shift", EcIndex.ShiftMode), ("FanCurve", EcIndex.FanCurveMode), ("Battery", EcIndex.BatteryLimit), ("Airplane", EcIndex.AirplaneMode) })
    {
        var v = wmi.GetData(idx);
        Console.WriteLine($"  0x{idx:X2} {name,-12} = 0x{v:X2} ({v})");
    }
    Console.WriteLine($"  Fan(1) CPU  = {Convert.ToHexString(wmi.GetFan(1))}");
    Console.WriteLine($"  Fan(2) GPU  = {Convert.ToHexString(wmi.GetFan(2))}");
    Console.WriteLine($"  Temp(0)     = {Convert.ToHexString(wmi.GetTemperature(0))}");
    Console.WriteLine($"  Temp(1)     = {Convert.ToHexString(wmi.GetTemperature(1))}");
    Console.WriteLine($"  AP(0)       = {Convert.ToHexString(wmi.GetAp(0))}");
    Console.WriteLine($"  AP(1)       = {Convert.ToHexString(wmi.GetAp(1))}");
}
catch (Exception ex)
{
    Console.WriteLine($"오류: {ex.GetType().Name}: {ex.Message}");
    if (!admin) Console.WriteLine("→ EC 점검은 관리자 권한으로 다시 실행하세요.");
}
