using System.Collections.Generic;
using ClawWidget.Ipc;
using ClawWidget.UI;
using Windows.UI;
using Windows.UI.Xaml;
using Windows.UI.Xaml.Automation;
using Windows.UI.Xaml.Controls;
using Windows.UI.Xaml.Controls.Primitives;
using Windows.UI.Xaml.Media;
using Windows.UI.Xaml.Shapes;

namespace ClawWidget.Sections
{
    /// <summary>
    /// 컨트롤러 조명. Status.ControllerSettings(원본 JSON 포함)를 복사해 Light·LightBrightness·LightColor·LightSpeed만 바꿔
    /// SetControllerSettings로 보낸다 — 진동·데드존 등 나머지 필드는 그대로.
    /// </summary>
    public sealed partial class LightingSection : WidgetSection
    {
        private static readonly KeyValuePair<string, byte[]>[] Palette =
        {
            new KeyValuePair<string, byte[]>("하늘", new byte[] { 0x4C, 0xC2, 0xFF }),
            new KeyValuePair<string, byte[]>("빨강", new byte[] { 0xFF, 0x45, 0x3A }),
            new KeyValuePair<string, byte[]>("주황", new byte[] { 0xFF, 0x9F, 0x0A }),
            new KeyValuePair<string, byte[]>("노랑", new byte[] { 0xFF, 0xD6, 0x0A }),
            new KeyValuePair<string, byte[]>("초록", new byte[] { 0x32, 0xD7, 0x4B }),
            new KeyValuePair<string, byte[]>("보라", new byte[] { 0xBF, 0x5A, 0xF2 }),
            new KeyValuePair<string, byte[]>("흰색", new byte[] { 0xFF, 0xFF, 0xFF }),
        };

        private readonly ChoiceGroup<LightMode> _mode;
        private readonly ChoiceGroup<int> _speed;
        private readonly List<KeyValuePair<byte[], Button>> _swatches = new List<KeyValuePair<byte[], Button>>();
        private readonly Debouncer _brightness;

        public LightingSection()
        {
            InitializeComponent();
            _mode = new ChoiceGroup<LightMode>(LightModeGrid, 3, new[]
            {
                new KeyValuePair<LightMode, object>(LightMode.Default, "기본"),
                new KeyValuePair<LightMode, object>(LightMode.Static, "단색"),
                new KeyValuePair<LightMode, object>(LightMode.Breath, "숨쉬기"),
                new KeyValuePair<LightMode, object>(LightMode.Rainbow, "무지개"),
                new KeyValuePair<LightMode, object>(LightMode.Wave, "웨이브"),
                new KeyValuePair<LightMode, object>(LightMode.Off, "끄기"),
            });
            _mode.Chosen += m => Send(m, null, null, null);

            _speed = new ChoiceGroup<int>(LightSpeedGrid, 3, new[]
            {
                new KeyValuePair<int, object>(0, "느리게"),
                new KeyValuePair<int, object>(1, "보통"),
                new KeyValuePair<int, object>(2, "빠르게"),
            });
            _speed.Chosen += v => Send(null, null, null, v);

            foreach (var p in Palette) AddSwatch(p.Key, p.Value);
            _brightness = new Debouncer(() => Send(null, (int)LightBrightnessSlider.Value, null, null));

            Motion.EnableShowHide(SwatchPanel);
            Motion.EnableShowHide(LightSpeedGrid);
            Motion.EnableShowHide(LightBrightnessRow);
        }

        private void AddSwatch(string name, byte[] rgb)
        {
            var b = new Button
            {
                Width = 44, Height = 44, MinWidth = 0, MinHeight = 0, Padding = new Thickness(0),
                CornerRadius = new CornerRadius(0), BorderThickness = new Thickness(3),
                Background = new SolidColorBrush(Colors.Transparent),
                Content = new Rectangle { Width = 30, Height = 30, Fill = new SolidColorBrush(Color.FromArgb(0xFF, rgb[0], rgb[1], rgb[2])) },
            };
            AutomationProperties.SetName(b, name);
            ToolTipService.SetToolTip(b, name);
            b.Click += (s, e) => OnSwatch(rgb);
            SwatchPanel.Children.Add(b);
            _swatches.Add(new KeyValuePair<byte[], Button>(rgb, b));
        }

        public override void Stop() => _brightness.Stop();

        public override void OnDensity(bool compact)
        {
            base.OnDensity(compact);
            _mode.ItemMinHeight = _speed.ItemMinHeight = ItemHeight(compact);
            var size = compact ? 52 : 44; // 손가락 크기 (메트로: 정사각 색 칸, 고른 색은 강조색 테두리)
            foreach (var kv in _swatches)
            {
                kv.Value.Width = kv.Value.Height = size;
                ((FrameworkElement)kv.Value.Content).Width = ((FrameworkElement)kv.Value.Content).Height = size - 14;
            }
        }

        private ControllerSettings Current => Host != null && Host.Status != null ? Host.Status.ControllerSettings : null;

        protected override void OnStatus(ServiceStatus s)
        {
            var cs = s != null ? s.ControllerSettings : null;
            var connected = s != null && s.ControllerConnected;
            var enabled = connected && cs != null;

            Header.Detail = s == null ? "" : !connected ? "컨트롤러 연결 안 됨" : cs == null ? "설정 정보 없음" : "";
            _mode.Select(cs != null, cs != null ? cs.Light : LightMode.Default);
            _mode.IsEnabled = enabled;

            var mode = cs != null ? cs.Light : LightMode.Default;
            var usesColor = mode == LightMode.Static || mode == LightMode.Breath;
            var usesSpeed = mode == LightMode.Breath || mode == LightMode.Rainbow || mode == LightMode.Wave;
            SwatchPanel.Visibility = cs != null && usesColor ? Visibility.Visible : Visibility.Collapsed;
            LightSpeedGrid.Visibility = cs != null && usesSpeed ? Visibility.Visible : Visibility.Collapsed;
            LightBrightnessRow.Visibility = cs != null && mode != LightMode.Off ? Visibility.Visible : Visibility.Collapsed;

            var accent = ThemeBrush("WidgetAccentBrush");
            var stroke = ThemeBrush("WidgetCardStrokeBrush");
            foreach (var kv in _swatches)
            {
                kv.Value.BorderBrush = cs != null && SameColor(kv.Key, cs.LightColor) ? accent : stroke;
                kv.Value.IsEnabled = enabled;
            }

            _speed.Select(cs != null, cs != null ? cs.LightSpeed : 1);
            _speed.IsEnabled = enabled;

            LightBrightnessSlider.IsEnabled = enabled;
            if (cs != null && !_brightness.IsHolding)
                LightBrightnessSlider.Value = PerformanceState.Clamp(cs.LightBrightness ?? 100, 0, 100);
            LightBrightnessText.Text = cs != null ? (int)LightBrightnessSlider.Value + "%" : "–";
        }

        private static bool SameColor(byte[] a, byte[] b) =>
            a != null && b != null && a.Length >= 3 && b.Length >= 3 && a[0] == b[0] && a[1] == b[1] && a[2] == b[2];

        private void OnSwatch(byte[] rgb)
        {
            var cs = Current;
            if (cs == null) return;
            var colorMode = cs.Light == LightMode.Static || cs.Light == LightMode.Breath;
            if (colorMode && SameColor(cs.LightColor, rgb)) return;
            // 색은 단색·숨쉬기에서만 쓰인다 — 다른 모드에서 색을 고르면 단색으로
            Send(colorMode ? cs.Light : LightMode.Static, null, rgb, null);
        }

        private void LightBrightnessSlider_ValueChanged(object sender, RangeBaseValueChangedEventArgs e)
        {
            if (LightBrightnessText != null) LightBrightnessText.Text = (int)e.NewValue + "%";
            if (IgnoreInput) return;
            _brightness.Trigger();
        }

        /// <summary>null 인자 = 현재 값 유지.</summary>
        private void Send(LightMode? mode, int? brightness, byte[] color, int? speed)
        {
            var cs = Current;
            if (cs == null) return;
            var body = ControllerSettings.WithLight(cs,
                mode ?? cs.Light,
                brightness ?? cs.LightBrightness,
                color ?? cs.LightColor,
                speed ?? cs.LightSpeed);
            _ = Run(Requests.SetControllerSettings(body), "조명을 바꾸지 못했어요");
        }
    }
}
