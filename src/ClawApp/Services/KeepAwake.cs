using System.Runtime.InteropServices;

namespace ClawApp.Services;

/// <summary>
/// 화면 꺼짐·절전 방지 (SetThreadExecutionState). 이 상태는 호출한 스레드에 묶이고 스레드가 끝나면 풀리므로
/// UI 스레드 대신 전용 백그라운드 스레드가 켜고 끈다. 앱이 종료되면 Windows가 알아서 푼다.
/// </summary>
public sealed class KeepAwake
{
    private const uint EsContinuous = 0x80000000, EsSystemRequired = 0x00000001, EsDisplayRequired = 0x00000002;

    private readonly object _lock = new();
    private readonly AutoResetEvent _signal = new(false);
    private Thread? _thread;
    private bool _want;

    /// <summary>방지를 요청한 상태인지 (실제 적용은 전용 스레드에서 곧바로).</summary>
    public bool Requested
    {
        get { lock (_lock) return _want; }
    }

    public void Set(bool on)
    {
        lock (_lock)
        {
            if (_want == on) return;
            _want = on;
            if (_thread is null)
            {
                if (!on) return; // 한 번도 켠 적 없으면 끌 것도 없다
                _thread = new Thread(Run) { IsBackground = true, Name = "ClawHub KeepAwake" };
                _thread.Start();
            }
        }
        _signal.Set();
    }

    private void Run()
    {
        var applied = false;
        while (true)
        {
            bool want;
            lock (_lock) want = _want;
            if (want != applied)
            {
                var flags = want ? EsContinuous | EsSystemRequired | EsDisplayRequired : EsContinuous;
                if (SetThreadExecutionState(flags) != 0) applied = want;
            }
            _signal.WaitOne();
        }
    }

    [DllImport("kernel32.dll")]
    private static extern uint SetThreadExecutionState(uint flags);
}
