using System;
using System.Collections.Generic;
using ClawWidget.Ipc;
using Windows.Foundation;
using Windows.UI.Xaml.Controls;
using Windows.UI.Xaml.Media;
using Windows.UI.Xaml.Shapes;

namespace ClawWidget.UI
{
    /// <summary>
    /// 최근 2분 CPU 온도(선 + 옅은 면)와 배터리 방전 전력(선) 미니 그래프.
    /// 색은 XAML에서 ThemeResource로 지정하고 여기서는 점만 계산한다.
    /// </summary>
    public sealed class Sparkline
    {
        private readonly Canvas _canvas;
        private readonly Polyline _cpu;
        private readonly Polygon _cpuFill;
        private readonly Polyline _battery;
        private List<TelemetrySample> _data = new List<TelemetrySample>();

        public Sparkline(Canvas canvas, Polyline cpu, Polygon cpuFill, Polyline battery)
        {
            _canvas = canvas;
            _cpu = cpu;
            _cpuFill = cpuFill;
            _battery = battery;
            _canvas.SizeChanged += (s, e) => Redraw();
        }

        public void Update(List<TelemetrySample> history)
        {
            _data = history ?? new List<TelemetrySample>();
            Redraw();
        }

        private void Redraw()
        {
            var w = _canvas.ActualWidth;
            var h = _canvas.ActualHeight;
            var cpu = new PointCollection();
            var fill = new PointCollection();
            var bat = new PointCollection();
            if (w <= 0 || h <= 0 || _data.Count < 2)
            {
                _cpu.Points = cpu; _cpuFill.Points = fill; _battery.Points = bat;
                return;
            }

            // x: 시각 기준 (없으면 순서 기준). y: CPU 30–95 °C 이상은 자동 확장, 배터리 0–(최대×1.15) W
            long t0 = _data[0].Time, t1 = _data[_data.Count - 1].Time;
            var byTime = t1 > t0;
            double tempMin = 30, tempMax = 95, batMax = 10;
            foreach (var s in _data)
            {
                if (s.CpuTemp.HasValue) { tempMax = Math.Max(tempMax, s.CpuTemp.Value + 2); tempMin = Math.Min(tempMin, s.CpuTemp.Value - 2); }
                if (s.BatteryWatts.HasValue) batMax = Math.Max(batMax, s.BatteryWatts.Value * 1.15);
            }

            const double pad = 2; // 선 두께만큼 위아래 여백
            double first = -1, last = -1;
            for (var i = 0; i < _data.Count; i++)
            {
                var s = _data[i];
                var x = byTime ? (s.Time - t0) / (double)(t1 - t0) * w : i / (double)(_data.Count - 1) * w;
                if (s.CpuTemp.HasValue)
                {
                    var y = pad + (1 - (s.CpuTemp.Value - tempMin) / (tempMax - tempMin)) * (h - 2 * pad);
                    cpu.Add(new Point(x, y));
                    if (first < 0) first = x;
                    last = x;
                }
                if (s.BatteryWatts.HasValue)
                {
                    var y = pad + (1 - s.BatteryWatts.Value / batMax) * (h - 2 * pad);
                    bat.Add(new Point(x, y));
                }
            }

            if (cpu.Count >= 2)
            {
                fill.Add(new Point(first, h));
                foreach (var p in cpu) fill.Add(p);
                fill.Add(new Point(last, h));
            }
            _cpu.Points = cpu;
            _cpuFill.Points = fill;
            _battery.Points = bat;
        }
    }
}
