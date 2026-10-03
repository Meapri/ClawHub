using System.Collections.Generic;
using ClawWidget.Ipc;
using ClawWidget.UI;
using Windows.UI.Xaml;

namespace ClawWidget.Sections
{
    /// <summary>실시간 HUD 카드. 숫자는 이전 값에서 부드럽게 흘러가고, 그래프는 서비스의 최근 2분 기록(History).</summary>
    public sealed partial class TelemetrySection : WidgetSection
    {
        private readonly AnimatedNumber _cpuTemp;
        private readonly AnimatedNumber _cpuWatts;
        private readonly AnimatedNumber _battery;
        private readonly AnimatedNumber _batteryWatts;
        private readonly Sparkline _spark;
        private readonly ChoiceGroup<int> _overlay;

        public TelemetrySection()
        {
            InitializeComponent();
            _cpuTemp = new AnimatedNumber(CpuTempValue, v => System.Math.Round(v) + "°C");
            _cpuWatts = new AnimatedNumber(CpuWattsValue, v => System.Math.Round(v) + " W");
            _battery = new AnimatedNumber(BatteryValue, v => System.Math.Round(v) + "%");
            _batteryWatts = new AnimatedNumber(BatteryWattsValue, v => v.ToString("0.0") + " W");
            _spark = new Sparkline(SparkCanvas, CpuLine, CpuFill, BatteryLine);
            _overlay = new ChoiceGroup<int>(OverlayGrid, 4, new[]
            {
                new KeyValuePair<int, object>(0, "끔"),
                new KeyValuePair<int, object>(1, "FPS"),
                new KeyValuePair<int, object>(2, "한 줄"),
                new KeyValuePair<int, object>(3, "자세히"),
            });
            _overlay.Chosen += v => _ = Run(Requests.AppCommand(AppCommands.SetOverlayPrefix + v), "성능 오버레이를 바꾸지 못했어요");
        }

        /// <summary>연결이 없어도 자리는 지킨다 (값은 "–").</summary>
        public override bool IsAvailable(ServiceStatus status) => true;

        public override void OnDensity(bool compact)
        {
            base.OnDensity(compact);
            _overlay.ItemMinHeight = ItemHeight(compact);
        }

        protected override void OnStatus(ServiceStatus s)
        {
            // 오버레이는 런처 앱이 그린다 — 앱이 없으면 고를 수 없다
            var app = s != null ? s.AppState : null;
            OverlayGrid.IsHitTestVisible = app != null;
            OverlayGrid.Opacity = app != null ? 1 : 0.4;
            OverlayDetail.Text = app == null ? "ClawHub 앱이 꺼져 있어요" : "";
            if (app != null) _overlay.Select(true, app.OverlayLevel);
        }

        protected override void OnTelemetry(Telemetry t)
        {
            _cpuTemp.Set(t != null ? t.CpuTemp : null);
            _cpuWatts.Set(t != null ? t.CpuWatts : null);
            _battery.Set(t != null ? t.BatteryPercent : null);

            var charging = t != null && t.Charging;
            ChargingIcon.Visibility = charging ? Visibility.Visible : Visibility.Collapsed;
            BatteryWattsLabel.Text = charging ? "충전 중" : "배터리 전력";
            _batteryWatts.Set(t != null && !charging ? t.BatteryWatts : null);

            if (t == null) Header.Detail = "";
            else if (charging) Header.Detail = "충전 중";
            else if (t.RemainingMinutes.HasValue) Header.Detail = "남은 시간 " + Format.Minutes(t.RemainingMinutes.Value);
            else Header.Detail = "";

            _spark.Update(t != null ? t.History : null);
        }
    }

    public static class Format
    {
        public static string Minutes(int minutes)
        {
            if (minutes < 60) return minutes + "분";
            return (minutes / 60) + "시간 " + (minutes % 60) + "분";
        }
    }
}
