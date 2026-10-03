using System.Runtime.InteropServices;
using Windows.Devices.Bluetooth;
using Windows.Devices.Enumeration;
using Windows.Devices.Radios;
using Windows.Networking.Connectivity;

namespace ClawApp.Services;

/// <summary>페어링된 블루투스 기기 하나. <see cref="Kind"/> = 아이콘 이름 (Headphones, Gamepad2, Mouse …).</summary>
public sealed record BluetoothDeviceInfo(string Id, string Name, bool Connected, string Kind);

/// <summary>지금 상태 한 번 (없는 장치는 null).</summary>
public sealed record ConnectivityState(bool? WifiOn, bool? BluetoothOn, bool? AirplaneOn, string? Network, bool NetworkIsWifi, int? SignalBars);

/// <summary>
/// Wi-Fi·블루투스 켜기/끄기(Windows.Devices.Radios — 데스크톱 앱은 권한 창 없이 허용), 비행기 모드(Windows 빠른 설정과 같은
/// RadioManagement COM, 문서화되지 않음), 지금 연결된 네트워크 이름, 페어링된 블루투스 기기와 연결 상태.
/// 주변 Wi-Fi 목록은 Windows 11에서 위치 권한 창을 띄우므로 직접 찾지 않고 Windows의 네트워크 목록을 연다.
/// </summary>
public static class Connectivity
{
    public static async Task<ConnectivityState> GetAsync()
    {
        bool? wifi = null, bt = null;
        foreach (var r in await RadiosAsync())
        {
            if (r.Kind == RadioKind.WiFi) wifi = (wifi ?? false) || r.State == RadioState.On;
            else if (r.Kind == RadioKind.Bluetooth) bt = (bt ?? false) || r.State == RadioState.On;
        }
        string? network = null;
        var isWifi = false;
        int? bars = null;
        try
        {
            if (NetworkInformation.GetInternetConnectionProfile() is { } p)
            {
                network = p.ProfileName;
                isWifi = p.IsWlanConnectionProfile;
                bars = p.GetSignalBars() is { } b ? b : null;
            }
        }
        catch (Exception) { /* 네트워크 서비스 없음 */ }
        return new ConnectivityState(wifi, bt, IsAirplaneOn(), network, isWifi, bars);
    }

    private static async Task<IReadOnlyList<Radio>> RadiosAsync()
    {
        try
        {
            if (await Radio.RequestAccessAsync() != RadioAccessStatus.Allowed) return [];
            return await Radio.GetRadiosAsync();
        }
        catch (Exception) { return []; }
    }

    /// <summary>그 종류의 무선 장치를 모두 켜거나 끈다. 하나라도 거부되면 false.</summary>
    public static async Task<bool> SetRadioAsync(RadioKind kind, bool on)
    {
        var ok = false;
        foreach (var r in await RadiosAsync())
        {
            if (r.Kind != kind) continue;
            ok = await r.SetStateAsync(on ? RadioState.On : RadioState.Off) == RadioAccessStatus.Allowed;
            if (!ok) return false;
        }
        return ok;
    }

    // ── 비행기 모드 (RadioManagement COM) ──────────────────

    public static bool? IsAirplaneOn()
    {
        try
        {
            var rm = (IRadioManager)new RadioManagementApi();
            try
            {
                // enabled = 무선이 켜져 있다 = 비행기 모드 꺼짐
                return rm.GetSystemRadioState(out var enabled, out _, out _) == 0 ? enabled == 0 : null;
            }
            finally { Marshal.ReleaseComObject(rm); }
        }
        catch (Exception ex) when (ex is COMException or InvalidCastException) { return null; }
    }

    public static bool SetAirplane(bool on)
    {
        try
        {
            var rm = (IRadioManager)new RadioManagementApi();
            try { return rm.SetSystemRadioState(on ? 0 : 1) == 0; }
            finally { Marshal.ReleaseComObject(rm); }
        }
        catch (Exception ex) when (ex is COMException or InvalidCastException) { return false; }
    }

    // ── 블루투스 기기 ───────────────────────────────────

    private const string IsConnected = "System.Devices.Aep.IsConnected";
    private const string Category = "System.Devices.Aep.Category";

    /// <summary>페어링된 기기 (클래식 + 저전력). 연결된 것부터.</summary>
    public static async Task<List<BluetoothDeviceInfo>> PairedDevicesAsync()
    {
        var list = new List<BluetoothDeviceInfo>();
        try
        {
            foreach (var selector in new[] { BluetoothDevice.GetDeviceSelectorFromPairingState(true), BluetoothLEDevice.GetDeviceSelectorFromPairingState(true) })
            {
                var found = await DeviceInformation.FindAllAsync(selector, [IsConnected, Category], DeviceInformationKind.AssociationEndpoint);
                foreach (var d in found)
                {
                    if (string.IsNullOrWhiteSpace(d.Name) || list.Any(x => x.Name == AudioControls.Clean(d.Name))) continue; // 같은 기기가 클래식·LE로 두 번
                    var connected = d.Properties.TryGetValue(IsConnected, out var c) && c is true;
                    var cats = d.Properties.TryGetValue(Category, out var k) && k is string[] a ? a : [];
                    list.Add(new BluetoothDeviceInfo(d.Id, AudioControls.Clean(d.Name), connected, KindOf(cats)));
                }
            }
        }
        catch (Exception) { /* 블루투스 없음 */ }
        return [.. list.OrderByDescending(d => d.Connected).ThenBy(d => d.Name)];
    }

    private static string KindOf(string[] categories)
    {
        var all = string.Join('|', categories);
        if (all.Contains("Audio", StringComparison.OrdinalIgnoreCase)) return "Headphones";
        if (all.Contains("Gaming", StringComparison.OrdinalIgnoreCase) || all.Contains("Gamepad", StringComparison.OrdinalIgnoreCase)) return "Gamepad2";
        if (all.Contains("Mouse", StringComparison.OrdinalIgnoreCase)) return "Mouse";
        if (all.Contains("Keyboard", StringComparison.OrdinalIgnoreCase)) return "Keyboard";
        if (all.Contains("Phone", StringComparison.OrdinalIgnoreCase)) return "Smartphone";
        return "Bluetooth";
    }

    [ComImport, Guid("581333F6-28DB-41BE-BC7A-FF201F12F3F6")]
    private class RadioManagementApi;

    [ComImport, Guid("DB3AFBFB-08E6-46C6-AA70-BF9A34C30AB7"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IRadioManager
    {
        [PreserveSig] int IsRMSupported(out uint state);
        [PreserveSig] int GetUIRadioInstances([MarshalAs(UnmanagedType.IUnknown)] out object instances);
        [PreserveSig] int GetSystemRadioState(out int enabled, out int unused, out int reason);
        [PreserveSig] int SetSystemRadioState(int enabled);
        [PreserveSig] int Refresh();
        [PreserveSig] int OnHardwareSliderChange(int a, int b);
    }
}
