using System.Collections.Generic;
using ClawWidget.Ipc;
using ClawWidget.UI;
using Windows.UI.Xaml;
using Windows.UI.Xaml.Controls;
using Windows.UI.Xaml.Controls.Primitives;

namespace ClawWidget.Sections
{
    /// <summary>
    /// 성능 모드 4개 + 수동 PL1/PL2(400 ms 디바운스, PL1 ≤ PL2 자동 유지) + 쿨러 부스트 → SetPerformance.
    ///
    /// 전원별 성능 (서비스가 PerformanceOnAc를 보내 줄 때만):
    ///  - "배터리용 따로 설정" 끔 = 배터리에서도 같은 값 → 타일은 전원 연결 칸(Ac)을 바꾼다.
    ///    끄는 순간 {"Performance":null,"PowerSource":"Battery"}로 배터리 칸을 지운다.
    ///  - 켬 = "충전 중 / 배터리" 선택이 나타나고(기본: 지금 전원), 타일은 고른 칸을 바꾼다 (PowerSource 지정).
    /// 타일은 고른 칸의 사용자 기본값을 보여 준다. 게임별 임시 성능이 실제로 적용 중이면 안내 문구로 알린다.
    /// </summary>
    public sealed partial class PerformanceSection : WidgetSection
    {
        private readonly ChoiceGroup<PerformanceMode> _modes;
        private readonly ChoiceGroup<PowerSourceKind> _target;
        private readonly Debouncer _plDebounce;
        /// <summary>사용자가 고른 편집 칸 (null = 지금 전원을 따라감).</summary>
        private PowerSourceKind? _chosenTarget;
        private bool? _lastOnBattery;

        public PerformanceSection()
        {
            InitializeComponent();
            _modes = new ChoiceGroup<PerformanceMode>(ModeGrid, 4, new[]
            {
                Tile(PerformanceMode.Battery, Icons.Leaf, "절전"),
                Tile(PerformanceMode.Balanced, Icons.Scale, "균형"),
                Tile(PerformanceMode.Performance, Icons.Rocket, "성능"),
                Tile(PerformanceMode.Manual, Icons.SlidersHorizontal, "수동"),
            });
            _modes.Chosen += OnModeChosen;

            _target = new ChoiceGroup<PowerSourceKind>(TargetGrid, 2, new[]
            {
                new KeyValuePair<PowerSourceKind, object>(PowerSourceKind.Ac, Inline(Icons.Plug, "충전 중")),
                new KeyValuePair<PowerSourceKind, object>(PowerSourceKind.Battery, Inline(Icons.Battery, "배터리")),
            });
            _target.Chosen += t =>
            {
                _chosenTarget = t;
                if (Host != null) ApplyStatus(Host.Status);
            };

            _plDebounce = new Debouncer(SendManualPl);
            Motion.EnableShowHide(ManualPanel);
            Motion.EnableShowHide(PerfNote);
            Motion.EnableShowHide(TargetPanel);
        }

        private static KeyValuePair<PerformanceMode, object> Tile(PerformanceMode mode, string glyph, string label)
        {
            var panel = new StackPanel { HorizontalAlignment = HorizontalAlignment.Center, Spacing = 4 };
            panel.Children.Add(Icons.Create(glyph, 18));
            panel.Children.Add(new TextBlock { Text = label, FontSize = 12, HorizontalAlignment = HorizontalAlignment.Center });
            return new KeyValuePair<PerformanceMode, object>(mode, panel);
        }

        private static object Inline(string glyph, string label)
        {
            var panel = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 6, HorizontalAlignment = HorizontalAlignment.Center };
            panel.Children.Add(Icons.Create(glyph, 14));
            panel.Children.Add(new TextBlock { Text = label, FontSize = 12, VerticalAlignment = VerticalAlignment.Center });
            return panel;
        }

        public override Control InitialFocus => _modes.FocusTarget;

        public override void Stop() => _plDebounce.Stop();

        public override void OnDensity(bool compact)
        {
            base.OnDensity(compact);
            _modes.ItemMinHeight = compact ? 72 : 64;
            _target.ItemMinHeight = ItemHeight(compact);
        }

        // ── 어느 칸을 보여 주고 바꾸는지 ──

        /// <summary>서비스가 전원별 성능을 지원하는지 (이전 서비스는 PerformanceOnAc가 없다).</summary>
        private static bool PerSource(ServiceStatus s) => s != null && s.PerformanceOnAc != null;

        private static bool Separate(ServiceStatus s) => PerSource(s) && s.PerformanceOnBattery != null;

        /// <summary>타일이 바꿀 칸. 전원별 성능이 없으면 null (PowerSource 생략).</summary>
        private PowerSourceKind? Target(ServiceStatus s)
        {
            if (!PerSource(s)) return null;
            if (!Separate(s)) return PowerSourceKind.Ac;
            return _chosenTarget ?? (s.OnBattery ? PowerSourceKind.Battery : PowerSourceKind.Ac);
        }

        /// <summary>타일이 보여 주는(=바꿀) 상태.</summary>
        private PerformanceState Edited(ServiceStatus s)
        {
            if (s == null) return null;
            var target = Target(s);
            if (target == null) return s.EffectivePerformance; // 이전 서비스: 예전처럼 실제 적용값
            return target == PowerSourceKind.Battery ? s.PerformanceOnBattery : s.PerformanceOnAc;
        }

        private ServiceStatus Status => Host != null ? Host.Status : null;

        protected override void OnStatus(ServiceStatus s)
        {
            if (s != null && _lastOnBattery != s.OnBattery)
            {
                // 전원이 바뀌면 편집 칸도 지금 전원으로 돌아간다
                _lastOnBattery = s.OnBattery;
                _chosenTarget = null;
            }

            var edited = Edited(s);
            var effective = s != null ? s.EffectivePerformance : null;
            var writable = s != null && s.EcWritable && edited != null;
            var perSource = PerSource(s);
            var separate = Separate(s);
            var target = Target(s);

            // 머리: 지금 전원 + 실제 적용 중인 PL
            Header.DetailIcon = s == null || !perSource ? "" : s.OnBattery ? Icons.Battery : Icons.Plug;
            var power = s == null || !perSource ? "" : s.OnBattery ? "배터리 · " : "전원 연결 · ";
            Header.Detail = effective != null ? power + string.Format("PL {0}/{1} W", effective.Pl1, effective.Pl2) : "";

            TargetPanel.Visibility = separate ? Visibility.Visible : Visibility.Collapsed;
            _target.Select(target.HasValue, target ?? PowerSourceKind.Ac);
            _target.IsEnabled = s != null && s.EcWritable;

            _modes.Select(edited != null, edited != null ? edited.Mode : PerformanceMode.Balanced);
            _modes.IsEnabled = writable;

            ManualPanel.Visibility = edited != null && edited.Mode == PerformanceMode.Manual ? Visibility.Visible : Visibility.Collapsed;
            Pl1Slider.IsEnabled = Pl2Slider.IsEnabled = writable;
            if (edited != null && !_plDebounce.IsHolding)
            {
                Pl1Slider.Value = PerformanceState.Clamp(edited.Pl1, Limits.MinPl, Limits.MaxPl);
                Pl2Slider.Value = PerformanceState.Clamp(edited.Pl2, Limits.MinPl, Limits.MaxPl);
            }
            UpdatePlTexts();

            PerfNote.Text = Note(s) ?? "";
            PerfNote.Visibility = string.IsNullOrEmpty(PerfNote.Text) ? Visibility.Collapsed : Visibility.Visible;

            CoolerToggle.IsOn = edited != null && edited.Fan == FanMode.CoolerBoost;
            CoolerToggle.IsEnabled = writable;

            SeparateRow.Visibility = perSource ? Visibility.Visible : Visibility.Collapsed;
            SeparateToggle.IsOn = separate;
            SeparateToggle.IsEnabled = s != null && s.EcWritable;
            SeparateCaption.Text = separate ? "위에서 고른 칸만 바뀌어요" : "끄면 배터리에서도 같은 설정을 써요";
        }

        private string Note(ServiceStatus s)
        {
            if (s == null) return null;
            if (!s.EcAvailable) return "EC를 사용할 수 없어 성능 설정을 바꿀 수 없어요";
            if (!s.EcWritable) return "EC 쓰기가 막혀 있어 읽기만 할 수 있어요";

            var lines = new List<string>();
            var app = s.AppState;
            var game = app != null ? app.RunningGameTitle : null;
            if (app != null && (app.GamePerformanceMode != null || app.GamePerformanceModeOnBattery != null))
            {
                var parts = new List<string>();
                if (app.GamePerformanceMode != null) parts.Add((app.GamePerformanceModeOnBattery != null ? "충전 중 " : "") + ModeName(app.GamePerformanceMode));
                if (app.GamePerformanceModeOnBattery != null) parts.Add("배터리 " + ModeName(app.GamePerformanceModeOnBattery));
                lines.Add("이 게임 전용 설정: " + string.Join(" · ", parts));
            }
            if (s.TemporaryPerformance != null)
            {
                var t = s.TemporaryPerformance;
                lines.Add((string.IsNullOrEmpty(game) ? "게임별 성능" : "'" + game + "' 전용 성능") + " 적용 중 ("
                          + ModeName(t.Mode.ToString()) + ", PL " + t.Pl1 + "/" + t.Pl2 + " W) · 여기서 바꾸는 건 기본 성능이에요");
            }
            return lines.Count > 0 ? string.Join("\n", lines) : null;
        }

        private static string ModeName(string mode)
        {
            switch (mode)
            {
                case "Battery": return "절전";
                case "Balanced": return "균형";
                case "Performance": return "성능";
                case "Manual": return "수동";
                default: return mode;
            }
        }

        // ── 명령 ──

        private void Send(PerformanceState state, string fail)
        {
            _ = Run(Requests.SetPerformance(state, Target(Status)), fail);
        }

        private void OnModeChosen(PerformanceMode mode)
        {
            var current = Edited(Status);
            if (current == null) return;
            // 수동으로 바꿀 땐 지금 쓰던 PL 값에서 시작
            var state = mode == PerformanceMode.Manual
                ? PerformanceState.Manual(current.Pl1, current.Pl2, current.Fan)
                : PerformanceState.Preset(mode, current.Fan);
            Send(state, "성능 모드를 바꾸지 못했어요");
        }

        private bool _pushing;

        private void PlSlider_ValueChanged(object sender, RangeBaseValueChangedEventArgs e)
        {
            if (Pl1Slider == null || Pl2Slider == null) return;
            if (!_pushing)
            {
                // PL1 ≤ PL2 유지: 움직인 쪽을 기준으로 다른 쪽을 밀어 준다
                _pushing = true;
                if (sender == Pl1Slider && Pl1Slider.Value > Pl2Slider.Value) Pl2Slider.Value = Pl1Slider.Value;
                if (sender == Pl2Slider && Pl2Slider.Value < Pl1Slider.Value) Pl1Slider.Value = Pl2Slider.Value;
                _pushing = false;
            }
            UpdatePlTexts();
            if (IgnoreInput || _pushing) return;
            _plDebounce.Trigger();
        }

        private void UpdatePlTexts()
        {
            if (Pl1Text == null || Pl2Text == null || Pl1Slider == null || Pl2Slider == null) return;
            Pl1Text.Text = (int)Pl1Slider.Value + " W";
            Pl2Text.Text = (int)Pl2Slider.Value + " W";
        }

        private void SendManualPl()
        {
            var current = Edited(Status);
            var fan = current != null ? current.Fan : FanMode.Auto;
            Send(PerformanceState.Manual((int)Pl1Slider.Value, (int)Pl2Slider.Value, fan), "전력 제한을 바꾸지 못했어요");
        }

        private void CoolerToggle_Toggled(object sender, RoutedEventArgs e)
        {
            if (IgnoreInput) return;
            var current = Edited(Status);
            if (current == null) return;
            var fan = CoolerToggle.IsOn ? FanMode.CoolerBoost : FanMode.Auto;
            if (fan == current.Fan) return;
            Send(current.WithFan(fan), "쿨러 부스트를 바꾸지 못했어요");
        }

        private void SeparateToggle_Toggled(object sender, RoutedEventArgs e)
        {
            if (IgnoreInput) return;
            var s = Status;
            if (!PerSource(s) || SeparateToggle.IsOn == Separate(s)) return;
            if (SeparateToggle.IsOn)
            {
                // 배터리 칸을 지금 전원 연결 값으로 시작하고, 편집 칸을 배터리로
                _chosenTarget = PowerSourceKind.Battery;
                var ac = s.PerformanceOnAc;
                _ = Run(Requests.SetPerformance(new PerformanceState(ac.Mode, ac.Pl1, ac.Pl2, ac.Fan), PowerSourceKind.Battery),
                        "배터리용 설정을 만들지 못했어요");
            }
            else
            {
                _chosenTarget = null;
                _ = Run(Requests.SetPerformance(null, PowerSourceKind.Battery), "배터리용 설정을 지우지 못했어요");
            }
        }
    }
}
