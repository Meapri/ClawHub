using System.Collections.Generic;
using ClawWidget.Ipc;
using ClawWidget.UI;
using Windows.UI.Xaml;

namespace ClawWidget.Sections
{
    /// <summary>
    /// 자동 TDP = Intel Endurance Gaming (Status.Graphics.Endurance) → SetEnduranceGaming.
    /// 끔 / 켬(항상) / 자동(배터리일 때만) + 강도(성능 우선 / 균형 / 배터리 최대). Available=false면 카드 숨김.
    /// </summary>
    public sealed partial class EnduranceSection : WidgetSection
    {
        private readonly ChoiceGroup<EnduranceControl> _control;
        private readonly ChoiceGroup<EnduranceMode> _mode;

        public EnduranceSection()
        {
            InitializeComponent();
            _control = new ChoiceGroup<EnduranceControl>(ControlGrid, 3, new[]
            {
                new KeyValuePair<EnduranceControl, object>(EnduranceControl.Off, "끔"),
                new KeyValuePair<EnduranceControl, object>(EnduranceControl.On, "켬"),
                new KeyValuePair<EnduranceControl, object>(EnduranceControl.Auto, "자동"),
            });
            _control.Chosen += c =>
            {
                var e = Current;
                _ = Run(Requests.SetEnduranceGaming(c, e != null ? e.Mode : null), "자동 TDP를 바꾸지 못했어요");
            };

            _mode = new ChoiceGroup<EnduranceMode>(ModeGrid, 3, new[]
            {
                new KeyValuePair<EnduranceMode, object>(EnduranceMode.Performance, "성능 우선"),
                new KeyValuePair<EnduranceMode, object>(EnduranceMode.Balanced, "균형"),
                new KeyValuePair<EnduranceMode, object>(EnduranceMode.MaxBattery, "배터리 최대"),
            });
            _mode.Chosen += m =>
            {
                var e = Current;
                var control = e != null && e.Control.HasValue && e.Control.Value != EnduranceControl.Off ? e.Control.Value : EnduranceControl.Auto;
                _ = Run(Requests.SetEnduranceGaming(control, m), "자동 TDP 강도를 바꾸지 못했어요");
            };

            Motion.EnableShowHide(ModePanel);
            Motion.EnableShowHide(Note);
        }

        private EnduranceState Current =>
            Host != null && Host.Status != null && Host.Status.Graphics != null ? Host.Status.Graphics.Endurance : null;

        public override bool IsAvailable(ServiceStatus s) =>
            s != null && s.Graphics != null && s.Graphics.Endurance != null && s.Graphics.Endurance.Available;

        public override void OnDensity(bool compact)
        {
            base.OnDensity(compact);
            _control.ItemMinHeight = _mode.ItemMinHeight = ItemHeight(compact);
        }

        protected override void OnStatus(ServiceStatus s)
        {
            if (!IsAvailable(s)) return;
            var e = s.Graphics.Endurance;

            _control.Select(e.Control.HasValue, e.Control ?? EnduranceControl.Off);
            _mode.Select(e.Mode.HasValue, e.Mode ?? EnduranceMode.Balanced);
            ModePanel.Visibility = e.Control.HasValue && e.Control.Value == EnduranceControl.Off ? Visibility.Collapsed : Visibility.Visible;

            Header.Detail = !e.Control.HasValue ? "" :
                e.Control.Value == EnduranceControl.Off ? "꺼짐" :
                e.Control.Value == EnduranceControl.On ? "항상 켜짐" :
                s.OnBattery ? "자동 · 지금 켜짐" : "자동 · 충전 중이라 꺼짐";

            string note = null;
            if (!e.Control.HasValue) note = "현재 값을 알 수 없어요";
            else if (e.Source == "Saved") note = "드라이버에서 읽을 수 없어 ClawHub에 저장된 값을 보여 줘요";
            Note.Text = note ?? "";
            Note.Visibility = note != null ? Visibility.Visible : Visibility.Collapsed;
        }
    }
}
