using System.Collections.ObjectModel;
using ClawApp.Services;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Windows.Devices.Radios;

namespace ClawApp.ViewModels;

/// <summary>페어링된 블루투스 기기 한 줄.</summary>
public sealed partial class BluetoothDeviceViewModel(BluetoothDeviceInfo d) : ObservableObject
{
    public string Id { get; } = d.Id;
    public string Name { get; } = d.Name;
    public string Icon { get; } = System.Windows.Application.Current?.TryFindResource("Icon." + d.Kind) as string ?? "";
    [ObservableProperty] private bool _connected = d.Connected;
    public string StatusText => Connected ? "연결됨" : "연결 안 됨";
    partial void OnConnectedChanged(bool value) => OnPropertyChanged(nameof(StatusText));
}

/// <summary>
/// 연결: Wi-Fi · 블루투스 · 비행기 모드 스위치, 지금 네트워크, 페어링된 블루투스 기기. 빠른 설정 '연결' 탭과 설정 화면이 같이 쓴다.
/// 바꾼 뒤에는 Windows가 실제로 적용한 값을 다시 읽는다 (비행기 모드를 켜면 Wi-Fi·블루투스도 꺼진다).
/// </summary>
public sealed partial class ConnectivityViewModel(ShellViewModel shell) : ObservableObject
{
    private bool _loading;
    private bool _refreshing;

    [ObservableProperty] private bool _wifiOn;
    [ObservableProperty] private bool _bluetoothOn;
    [ObservableProperty] private bool _airplaneOn;
    // 읽기 전에는 있다고 본다 (꺼진 타일은 패드 첫 포커스를 받지 못한다)
    [ObservableProperty] private bool _hasWifi = true;
    [ObservableProperty] private bool _hasBluetooth = true;
    [ObservableProperty] private bool _hasAirplane = true;
    [ObservableProperty] private string _networkText = "";

    public ObservableCollection<BluetoothDeviceViewModel> Devices { get; } = [];
    public bool HasDevices => Devices.Count > 0;

    public async Task RefreshAsync()
    {
        if (_refreshing) return;
        _refreshing = true;
        try
        {
            var s = await Connectivity.GetAsync();
            var devices = await Connectivity.PairedDevicesAsync();
            _loading = true;
            try
            {
                HasWifi = s.WifiOn is not null;
                HasBluetooth = s.BluetoothOn is not null;
                HasAirplane = s.AirplaneOn is not null;
                WifiOn = s.WifiOn ?? false;
                BluetoothOn = s.BluetoothOn ?? false;
                AirplaneOn = s.AirplaneOn ?? false;
                NetworkText = s.Network is { } n
                    ? (s.NetworkIsWifi ? $"{n} 연결됨" + (s.SignalBars is { } b ? $" · 신호 {b}/5" : "") : $"{n} 연결됨 (유선)")
                    : AirplaneOn ? "비행기 모드" : WifiOn ? "연결된 네트워크 없음" : "Wi-Fi 꺼짐";
                SyncDevices(devices);
            }
            finally { _loading = false; }
            OnPropertyChanged(nameof(HasDevices));
        }
        finally { _refreshing = false; }
    }

    private void SyncDevices(List<BluetoothDeviceInfo> list)
    {
        for (var i = Devices.Count - 1; i >= 0; i--)
            if (list.All(d => d.Id != Devices[i].Id)) Devices.RemoveAt(i);
        for (var i = 0; i < list.Count; i++)
        {
            if (Devices.FirstOrDefault(x => x.Id == list[i].Id) is { } vm)
            {
                vm.Connected = list[i].Connected;
                var at = Devices.IndexOf(vm);
                if (at != i && i < Devices.Count) Devices.Move(at, i);
            }
            else Devices.Insert(Math.Min(i, Devices.Count), new BluetoothDeviceViewModel(list[i]));
        }
    }

    partial void OnWifiOnChanged(bool value)
    {
        if (!_loading) _ = SetRadioAsync(RadioKind.WiFi, value, value ? "Wi-Fi 켜짐" : "Wi-Fi 꺼짐", value ? "Icon.Wifi" : "Icon.WifiOff");
    }

    partial void OnBluetoothOnChanged(bool value)
    {
        if (!_loading) _ = SetRadioAsync(RadioKind.Bluetooth, value, value ? "블루투스 켜짐" : "블루투스 꺼짐", value ? "Icon.Bluetooth" : "Icon.BluetoothOff");
    }

    private async Task SetRadioAsync(RadioKind kind, bool on, string done, string icon)
    {
        if (await Connectivity.SetRadioAsync(kind, on)) shell.Notify(icon, done);
        else shell.Notify(icon, "바꾸지 못했어요 (비행기 모드이거나 장치가 꺼져 있어요)", warn: true);
        await Task.Delay(600); // 장치가 켜지고 네트워크에 다시 붙는 동안
        await RefreshAsync();
    }

    partial void OnAirplaneOnChanged(bool value)
    {
        if (_loading) return;
        _ = Task.Run(() => Connectivity.SetAirplane(value)).ContinueWith(async t =>
        {
            shell.Notify("Icon.Plane", !t.Result ? "비행기 모드를 바꾸지 못했어요" : value ? "비행기 모드 켜짐" : "비행기 모드 꺼짐", warn: !t.Result);
            await Task.Delay(800);
            await RefreshAsync();
        }, TaskScheduler.FromCurrentSynchronizationContext());
    }

    [RelayCommand]
    private void ToggleWifi() => WifiOn = !WifiOn;

    [RelayCommand]
    private void ToggleBluetooth() => BluetoothOn = !BluetoothOn;

    [RelayCommand]
    private void ToggleAirplane() => AirplaneOn = !AirplaneOn;

    /// <summary>주변 네트워크 고르기·새 기기 연결은 Windows 화면에서 (데스크톱 모드로 나가 연다).</summary>
    [RelayCommand]
    private void OpenWifiSettings() => shell.OpenWindowsPageCommand.Execute("network-wifi");

    [RelayCommand]
    private void OpenBluetoothSettings() => shell.OpenWindowsPageCommand.Execute("bluetooth");
}
