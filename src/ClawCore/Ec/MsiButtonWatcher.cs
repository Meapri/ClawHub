using System.Management;

namespace ClawCore.Ec;

public enum MsiButton { MainMenu, QuickSettings }

/// <summary>본체 전용 버튼 감시 (root\WMI MSI_Event, MSIEvt 하위 바이트 41/88).</summary>
public sealed class MsiButtonWatcher : IDisposable
{
    private const int CallMsiCenter = 41;
    private const int CallMsiCenterOsd = 88;

    private readonly ManagementEventWatcher _watcher;

    public event Action<MsiButton>? Pressed;

    public MsiButtonWatcher()
    {
        var scope = new ManagementScope(@"\\.\root\WMI");
        scope.Connect();
        _watcher = new ManagementEventWatcher(scope, new WqlEventQuery("SELECT * FROM MSI_Event"));
        _watcher.EventArrived += OnEvent;
        _watcher.Start();
    }

    private void OnEvent(object sender, EventArrivedEventArgs e)
    {
        if (!int.TryParse(e.NewEvent["MSIEvt"]?.ToString(), out var value)) return;
        switch (value & 0xFF)
        {
            case CallMsiCenter: Pressed?.Invoke(MsiButton.MainMenu); break;
            case CallMsiCenterOsd: Pressed?.Invoke(MsiButton.QuickSettings); break;
        }
    }

    public void Dispose()
    {
        _watcher.Stop();
        _watcher.Dispose();
    }
}
