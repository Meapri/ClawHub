using System.Diagnostics;
using System.Windows.Media;

namespace ClawApp.Services;

/// <summary>
/// 진단용: CLAWHUB_FPSLOG=경로 를 주면 1초마다 실제로 그린 프레임 수를 그 파일에 적는다.
/// RenderingTime은 애니메이션 목표 프레임 수에 맞춰 반올림돼 같은 값이 겹치므로 벽시계 간격(2ms 이상)으로 센다.
/// 가장 긴 프레임 간격(끊김)도 같이 적는다. 평소에는 아무것도 하지 않는다.
/// </summary>
public static class FrameMeter
{
    private static readonly Stopwatch Clock = new();
    private static int _frames;
    private static double _prev, _secStart, _worst;
    private static string? _path;

    public static void StartIfRequested()
    {
        if (Environment.GetEnvironmentVariable("CLAWHUB_FPSLOG") is not { Length: > 0 } path) return;
        _path = path;
        File.AppendAllText(path, $"start tier={RenderCapability.Tier >> 16} hz={DisplayControls.Current()?.Hz}\n");
        Clock.Start();
        CompositionTarget.Rendering += OnRendering;

        // UI 스레드를 8ms 넘게 붙잡는 작업도 적는다 (프레임이 밀리는 원인)
        var hooks = System.Windows.Threading.Dispatcher.CurrentDispatcher.Hooks;
        var started = new Dictionary<System.Windows.Threading.DispatcherOperation, double>();
        hooks.OperationStarted += (_, e) => started[e.Operation] = Clock.Elapsed.TotalMilliseconds;
        hooks.OperationCompleted += (_, e) =>
        {
            if (!started.Remove(e.Operation, out var t0)) return;
            var ms = Clock.Elapsed.TotalMilliseconds - t0;
            if (ms < 8) return;
            var name = e.Operation.ToString() ?? "";
            try
            {
                var task = e.Operation.GetType().GetField("_method", System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance)?.GetValue(e.Operation) as Delegate;
                if (task is not null) name = $"{task.Method.DeclaringType?.Name}.{task.Method.Name}";
            }
            catch (Exception) { /* 진단용 */ }
            File.AppendAllText(_path!, $"{DateTime.Now:HH:mm:ss.f}   slow {ms:F0}ms {e.Operation.Priority} {name}\n");
        };
    }

    public static bool Enabled => _path is not null;

    /// <summary>진단 기록 한 줄 (켜져 있을 때만).</summary>
    public static void Note(string text)
    {
        if (_path is not null) File.AppendAllText(_path, $"{DateTime.Now:HH:mm:ss.f}   {text}\n");
    }

    private static void OnRendering(object? sender, EventArgs e)
    {
        var now = Clock.Elapsed.TotalMilliseconds;
        var gap = now - _prev;
        if (gap < 2) return; // 같은 프레임에 두 번 오는 경우
        _prev = now;
        _frames++;
        _worst = Math.Max(_worst, gap);
        if (now - _secStart < 1000) return;
        File.AppendAllText(_path!, $"{DateTime.Now:HH:mm:ss.f} {_frames * 1000.0 / (now - _secStart):F0} fps  worst {_worst:F0}ms\n");
        _frames = 0;
        _worst = 0;
        _secStart = now;
    }
}
