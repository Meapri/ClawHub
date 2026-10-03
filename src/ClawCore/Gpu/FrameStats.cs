using System.Diagnostics;
using System.Globalization;

namespace ClawCore.Gpu;

/// <summary>FPS 계산 결과. 값은 반올림돼 있다.</summary>
public readonly record struct FrameSnapshot(double Fps, double FrameTimeMs, double FpsLow1);

/// <summary>
/// PresentMon CSV(표준 출력) 한 줄씩 받아 FPS·프레임 시간·1% low를 계산한다.
/// 열 위치는 헤더에서 찾는다 — PresentMon 1.x("msBetweenPresents"/"MsBetweenPresents")와
/// 2.x("MsBetweenPresents"/"FrameTime") 모두 받는다. 스왑체인이 여럿이면 가장 많이 그리는 것 하나만 센다.
/// 스레드 안전 (PresentMon 출력 스레드와 조회 스레드가 다름).
/// </summary>
public sealed class FrameStats
{
    /// <summary>1% low 계산용으로 들고 있는 최근 프레임 수 (240fps × 30초 정도).</summary>
    private const int MaxFrames = 8192;
    /// <summary>1% low를 계산할 기간 (프레임 시간 합 기준).</summary>
    private const double LowWindowMs = 30_000;

    private readonly object _lock = new();
    private readonly Queue<double> _frames = new();
    private readonly Dictionary<string, int> _chainCounts = [];
    private readonly Stopwatch _clock = Stopwatch.StartNew();
    private double _queuedMs;
    private int _colFrameTime = -1, _colSwapChain = -1, _colCount;
    private string? _mainChain;
    private TimeSpan _chainWindowStart, _lastFrameAt = TimeSpan.MinValue;

    public bool HasHeader => _colFrameTime >= 0;

    public void Reset()
    {
        lock (_lock)
        {
            _frames.Clear();
            _chainCounts.Clear();
            _queuedMs = 0;
            _colFrameTime = _colSwapChain = -1;
            _mainChain = null;
            _lastFrameAt = TimeSpan.MinValue;
        }
    }

    /// <summary>CSV 한 줄 (헤더 또는 데이터). 알 수 없는 줄은 무시한다.</summary>
    public void AddLine(string? line)
    {
        if (string.IsNullOrWhiteSpace(line)) return;
        var cols = line.Split(',');
        lock (_lock)
        {
            if (TryHeader(cols)) return;
            if (_colFrameTime < 0 || cols.Length < _colCount) return;
            if (!double.TryParse(cols[_colFrameTime], NumberStyles.Float, CultureInfo.InvariantCulture, out var ms)) return;
            if (ms is <= 0 or > 5000) return; // 첫 프레임(0)·일시 정지 뒤 큰 값은 제외

            var now = _clock.Elapsed;
            if (_colSwapChain >= 0 && !IsMainChain(cols[_colSwapChain], now)) return;

            _frames.Enqueue(ms);
            _queuedMs += ms;
            while (_frames.Count > MaxFrames || (_frames.Count > 1 && _queuedMs - _frames.Peek() >= LowWindowMs))
                _queuedMs -= _frames.Dequeue();
            _lastFrameAt = now;
        }
    }

    private bool TryHeader(string[] cols)
    {
        int Find(params string[] names) =>
            Array.FindIndex(cols, c => names.Any(n => string.Equals(c.Trim(), n, StringComparison.OrdinalIgnoreCase)));

        var ft = Find("MsBetweenPresents", "FrameTime", "MsBetweenAppStart");
        if (ft < 0 || Find("ProcessID") < 0) return false; // 헤더가 아님
        _colFrameTime = ft;
        _colSwapChain = Find("SwapChainAddress");
        _colCount = Math.Max(ft, _colSwapChain) + 1;
        _frames.Clear();
        _queuedMs = 0;
        return true;
    }

    /// <summary>2초마다 가장 많이 그린 스왑체인을 주 스왑체인으로 정한다 (런처·오버레이 창 제외).</summary>
    private bool IsMainChain(string chain, TimeSpan now)
    {
        _chainCounts[chain] = _chainCounts.GetValueOrDefault(chain) + 1;
        if (_mainChain is null) _mainChain = chain;
        if (now - _chainWindowStart > TimeSpan.FromSeconds(2))
        {
            var best = _chainCounts.MaxBy(kv => kv.Value).Key;
            if (best != _mainChain && _chainCounts[best] > _chainCounts.GetValueOrDefault(_mainChain) * 2)
            {
                _mainChain = best;
                _frames.Clear();
                _queuedMs = 0;
            }
            _chainCounts.Clear();
            _chainWindowStart = now;
        }
        return chain == _mainChain;
    }

    /// <summary>최근 프레임 시간 (오래된 것부터): 합이 <paramref name="windowMs"/>가 될 때까지, 최대 <paramref name="max"/>개.</summary>
    public List<float> Recent(double windowMs, int max)
    {
        lock (_lock)
        {
            var list = new List<float>(Math.Min(max, _frames.Count));
            double sum = 0;
            foreach (var ms in _frames.Reverse())
            {
                if (list.Count >= max || sum >= windowMs) break;
                list.Add((float)Math.Round(ms, 2));
                sum += ms;
            }
            list.Reverse();
            return list;
        }
    }

    /// <summary>
    /// 최근 1초 평균 FPS·프레임 시간과 최근 30초의 1% low. 프레임이 <paramref name="staleAfter"/> 동안
    /// 없었으면(게임 일시 정지·최소화) null.
    /// </summary>
    public FrameSnapshot? Snapshot(TimeSpan staleAfter)
    {
        double[] frames;
        lock (_lock)
        {
            if (_frames.Count == 0 || _clock.Elapsed - _lastFrameAt > staleAfter) return null;
            frames = [.. _frames];
        }

        // 최근 1초: 뒤에서부터 프레임 시간 합이 1000ms가 될 때까지
        double sum = 0;
        var n = 0;
        for (var i = frames.Length - 1; i >= 0 && sum < 1000; i--, n++) sum += frames[i];
        var avgMs = sum / n;

        // 1% low = 99번째 백분위 프레임 시간의 역수
        Array.Sort(frames);
        var p99 = frames[Math.Clamp((int)Math.Ceiling(frames.Length * 0.99) - 1, 0, frames.Length - 1)];

        return new FrameSnapshot(Math.Round(1000 / avgMs, 1), Math.Round(avgMs, 2), Math.Round(1000 / p99, 1));
    }
}
