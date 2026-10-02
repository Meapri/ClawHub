using System.Windows.Media;

namespace ClawApp.Services;

/// <summary>
/// 진단용: CLAWHUB_FPSLOG=경로 를 주면 1초마다 실제로 그린 프레임 수를 그 파일에 적는다 (프레임이 없던 초는 건너뜀).
/// 평소에는 아무것도 하지 않는다.
/// </summary>
public static class FrameMeter
{
    private static int _frames;
    private static long _since;
    private static TimeSpan _lastRender;
    private static string? _path;

    public static void StartIfRequested()
    {
        if (Environment.GetEnvironmentVariable("CLAWHUB_FPSLOG") is not { Length: > 0 } path) return;
        _path = path;
        File.AppendAllText(path, $"start tier={RenderCapability.Tier >> 16} hz={DisplayControls.Current()?.Hz}\n");
        _since = Environment.TickCount64;
        CompositionTarget.Rendering += OnRendering;
    }

    private static void OnRendering(object? sender, EventArgs e)
    {
        // 같은 프레임에 Rendering이 두 번 올 수 있다 — RenderingTime이 바뀐 것만 센다
        if (e is RenderingEventArgs r)
        {
            if (r.RenderingTime == _lastRender) return;
            _lastRender = r.RenderingTime;
        }
        _frames++;
        var now = Environment.TickCount64;
        if (now - _since < 1000) return;
        File.AppendAllText(_path!, $"{DateTime.Now:HH:mm:ss.f} {_frames * 1000.0 / (now - _since):F0} fps\n");
        _frames = 0;
        _since = now;
    }
}
