using System.Collections.Generic;
using System.Linq;
using ClawWidget.Ipc;
using ClawWidget.UI;

namespace ClawWidget.Sections
{
    /// <summary>FPS 제한(끔/30/40/45/60/90/120)과 프레임 생성(끔/2배/3배/4배) → SetGraphics. Graphics.Available=false면 숨김.</summary>
    public sealed partial class GraphicsSection : WidgetSection
    {
        private static readonly int[] FpsChoices = { 0, 30, 40, 45, 60, 90, 120 };
        private readonly ChoiceGroup<int> _fps;
        private readonly ChoiceGroup<int> _frameGen;

        public GraphicsSection()
        {
            InitializeComponent();
            _fps = new ChoiceGroup<int>(FpsGrid, FpsChoices.Length,
                FpsChoices.Select(v => new KeyValuePair<int, object>(v, v == 0 ? "끔" : v.ToString())));
            _fps.Chosen += v => _ = Run(Requests.SetGraphics(v, null), "FPS 제한을 바꾸지 못했어요");

            _frameGen = new ChoiceGroup<int>(FrameGenGrid, 4, new[]
            {
                new KeyValuePair<int, object>(0, "끔"),
                new KeyValuePair<int, object>(1, "2배"),
                new KeyValuePair<int, object>(2, "3배"),
                new KeyValuePair<int, object>(3, "4배"),
            });
            _frameGen.Chosen += v => _ = Run(Requests.SetGraphics(null, v), "프레임 생성을 바꾸지 못했어요");
        }

        public override bool IsAvailable(ServiceStatus s) => s != null && s.Graphics != null && s.Graphics.Available;

        public override void OnDensity(bool compact)
        {
            base.OnDensity(compact);
            _fps.ItemMinHeight = _frameGen.ItemMinHeight = ItemHeight(compact);
        }

        protected override void OnStatus(ServiceStatus s)
        {
            if (!IsAvailable(s)) return;
            var g = s.Graphics;
            var known = FpsChoices.Contains(g.FpsLimit);
            _fps.Select(known, g.FpsLimit);
            var app = s.AppState;
            var gameFps = app != null ? app.CurrentGameFpsLimit : null;
            FpsDetail.Text = gameFps.HasValue
                ? "이 게임 전용 설정" + (app.OnBattery && app.GameFpsLimitOnBattery.HasValue ? " (배터리)" : "")
                : !known ? "현재 " + g.FpsLimit : "";
            _frameGen.Select(g.FrameGeneration >= 0 && g.FrameGeneration <= 3, g.FrameGeneration);
        }
    }
}
