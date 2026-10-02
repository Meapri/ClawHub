using System.Collections.Generic;
using ClawWidget.Ipc;
using ClawWidget.UI;
using Windows.UI.Xaml;
using Windows.UI.Xaml.Controls.Primitives;

namespace ClawWidget.Sections
{
    /// <summary>
    /// 밝기(SetBrightness, 400 ms 디바운스), 볼륨(앱에 SetVolume 명령 — 위젯은 샌드박스라 직접 못 바꾼다, 앱이 꺼져 있으면 비활성),
    /// 충전 한도(SetChargeLimit 100/80/60 %).
    /// </summary>
    public sealed partial class DisplaySection : WidgetSection
    {
        private readonly ChoiceGroup<ChargeLimit> _charge;
        private readonly Debouncer _brightness;
        private readonly Debouncer _volume;

        public DisplaySection()
        {
            InitializeComponent();
            _charge = new ChoiceGroup<ChargeLimit>(ChargeGrid, 3, new[]
            {
                new KeyValuePair<ChargeLimit, object>(ChargeLimit.Full, "100%"),
                new KeyValuePair<ChargeLimit, object>(ChargeLimit.Balanced, "80%"),
                new KeyValuePair<ChargeLimit, object>(ChargeLimit.Battery, "60%"),
            });
            _charge.Chosen += v => _ = Run(Requests.SetChargeLimit(v), "충전 한도를 바꾸지 못했어요");
            _brightness = new Debouncer(() => _ = Run(Requests.SetBrightness((int)BrightnessSlider.Value), "밝기를 바꾸지 못했어요"));
            _volume = new Debouncer(() => _ = Run(Requests.AppCommand(AppCommands.SetVolumePrefix + (int)VolumeSlider.Value), "볼륨을 바꾸지 못했어요"));
        }

        public override void Stop()
        {
            _brightness.Stop();
            _volume.Stop();
        }

        public override void OnDensity(bool compact)
        {
            base.OnDensity(compact);
            _charge.ItemMinHeight = ItemHeight(compact);
        }

        protected override void OnStatus(ServiceStatus s)
        {
            var brightness = s != null ? s.Brightness : null;
            BrightnessSlider.IsEnabled = brightness.HasValue;
            if (brightness.HasValue && !_brightness.IsHolding)
                BrightnessSlider.Value = PerformanceState.Clamp(brightness.Value, 0, 100);
            BrightnessText.Text = brightness.HasValue ? (int)BrightnessSlider.Value + "%" : "–";

            var volume = s != null && s.AppState != null ? s.AppState.Volume : null;
            VolumeSlider.IsEnabled = volume.HasValue;
            if (volume.HasValue && !_volume.IsHolding)
                VolumeSlider.Value = PerformanceState.Clamp(volume.Value, 0, 100);
            VolumeText.Text = volume.HasValue ? ((int)VolumeSlider.Value).ToString() : "–";
            VolumeIcon.Glyph = !volume.HasValue || VolumeSlider.Value > 0 ? Icons.Volume2 : Icons.VolumeX;

            var limit = s != null ? s.ChargeLimit : null;
            _charge.Select(limit.HasValue, limit ?? ChargeLimit.Full);
            _charge.IsEnabled = s != null && s.EcWritable && limit.HasValue;
            ChargeDetail.Text = s != null && !limit.HasValue ? "이 기기에서 지원 안 됨" : "";

            var health = HealthLine(s != null ? s.BatteryHealth : null);
            HealthText.Text = health ?? "";
            HealthRow.Visibility = health != null ? Visibility.Visible : Visibility.Collapsed;
        }

        /// <summary>"배터리 상태 102% · 79.1/77.8 Wh · 사이클 120". 모르는 값은 뺀다. 아무것도 모르면 null.</summary>
        private static string HealthLine(BatteryHealth h)
        {
            if (h == null) return null;
            var parts = new List<string>();
            if (h.HealthPercent.HasValue) parts.Add("배터리 상태 " + h.HealthPercent.Value + "%");
            if (h.FullChargeCapacityMWh.HasValue && h.DesignedCapacityMWh.HasValue)
                parts.Add(Wh(h.FullChargeCapacityMWh.Value) + "/" + Wh(h.DesignedCapacityMWh.Value) + " Wh");
            else if (h.FullChargeCapacityMWh.HasValue)
                parts.Add("완충 " + Wh(h.FullChargeCapacityMWh.Value) + " Wh");
            if (h.CycleCount.HasValue) parts.Add("사이클 " + h.CycleCount.Value);
            if (parts.Count == 0) return null;
            if (!h.HealthPercent.HasValue) parts[0] = "배터리 " + parts[0];
            return string.Join(" · ", parts);
        }

        private static string Wh(int mWh) => (mWh / 1000.0).ToString("0.0");

        private void VolumeSlider_ValueChanged(object sender, RangeBaseValueChangedEventArgs e)
        {
            if (VolumeText != null) VolumeText.Text = ((int)e.NewValue).ToString();
            if (VolumeIcon != null) VolumeIcon.Glyph = e.NewValue > 0 ? Icons.Volume2 : Icons.VolumeX;
            if (IgnoreInput) return;
            _volume.Trigger();
        }

        private void BrightnessSlider_ValueChanged(object sender, RangeBaseValueChangedEventArgs e)
        {
            if (BrightnessText != null) BrightnessText.Text = (int)e.NewValue + "%";
            if (IgnoreInput) return;
            _brightness.Trigger();
        }
    }
}
