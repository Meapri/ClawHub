using System.Net.NetworkInformation;

namespace ClawApp.Services;

/// <summary>
/// 상단 상태 표시줄용 네트워크 상태: Wi-Fi → 유선 → 끊김.
/// WLAN API(신호 세기·SSID)는 Windows 11에서 위치 권한 요청 창을 띄우므로 쓰지 않는다.
/// </summary>
public static class NetworkStatus
{
    public enum Kind { None, Wifi, Ethernet }

    public static Kind Get()
    {
        try
        {
            var found = Kind.None;
            foreach (var nic in NetworkInterface.GetAllNetworkInterfaces())
            {
                if (nic.OperationalStatus != OperationalStatus.Up || IsVirtual(nic)) continue;
                if (nic.NetworkInterfaceType == NetworkInterfaceType.Wireless80211) return Kind.Wifi;
                if (nic.NetworkInterfaceType is NetworkInterfaceType.Ethernet or NetworkInterfaceType.GigabitEthernet) found = Kind.Ethernet;
            }
            return found;
        }
        catch (NetworkInformationException) { return Kind.None; } // 조회 실패 = 끊김으로 표시
    }

    private static bool IsVirtual(NetworkInterface nic) =>
        nic.Description.Contains("Virtual", StringComparison.OrdinalIgnoreCase)
        || nic.Description.Contains("Hyper-V", StringComparison.OrdinalIgnoreCase)
        || nic.Description.Contains("Bluetooth", StringComparison.OrdinalIgnoreCase)
        || nic.Description.Contains("VPN", StringComparison.OrdinalIgnoreCase);
}
