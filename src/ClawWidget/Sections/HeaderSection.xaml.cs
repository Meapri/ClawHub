using System;
using System.Collections.Generic;
using ClawWidget.Ipc;
using Windows.UI.Xaml;
using Windows.UI.Xaml.Controls;

namespace ClawWidget.Sections
{
    /// <summary>실행 중 게임, 서비스 연결 점, 매핑 프로필 선택, ClawHub 열기, 게임 종료(확인).</summary>
    public sealed partial class HeaderSection : WidgetSection
    {
        public HeaderSection()
        {
            InitializeComponent();
        }

        /// <summary>연결이 없어도 보인다 ("연결 중…" / "서비스 연결 안 됨").</summary>
        public override bool IsAvailable(ServiceStatus status) => true;

        protected override void OnStatus(ServiceStatus s)
        {
            var online = s != null;
            var attempted = Host != null && Host.ConnectionAttempted;
            var app = online ? s.AppState : null;
            var game = app != null ? app.RunningGameTitle : null;

            if (!online) ConnDot.Fill = ThemeBrush(attempted ? "WidgetErrorBrush" : "WidgetTertiaryTextBrush");
            else if (!s.EcAvailable || !s.EcWritable) ConnDot.Fill = ThemeBrush("WidgetWarnBrush");
            else ConnDot.Fill = ThemeBrush("WidgetOkBrush");
            ToolTipService.SetToolTip(ConnDot, !online ? "서비스 연결 안 됨" : s.EcWritable ? "서비스 연결됨" : "서비스 연결됨 · EC 쓰기 불가");

            if (!online)
            {
                GameTitle.Text = attempted ? "서비스 연결 안 됨" : "연결 중…";
                GameSubtitle.Text = "";
            }
            else
            {
                GameTitle.Text = string.IsNullOrEmpty(game) ? "게임 실행 중 아님" : game;
                var parts = new List<string>();
                if (app == null) parts.Add("ClawHub 앱이 꺼져 있어요");
                else if (!string.IsNullOrEmpty(game)) parts.Add("실행 중");
                if (!s.ControllerConnected) parts.Add("컨트롤러 연결 안 됨");
                else if (s.MappingActive) parts.Add("키 매핑 적용 중");
                GameSubtitle.Text = string.Join(" · ", parts);
            }

            ProfileButton.IsEnabled = EditMappingButton.IsEnabled = app != null;
            ProfileText.Text = app == null ? "–" : string.IsNullOrEmpty(app.ProfileId) ? "매핑 없음" : (app.ProfileName ?? app.ProfileId);
            QuitButton.IsEnabled = !string.IsNullOrEmpty(game);
            QuitQuestion.Text = string.IsNullOrEmpty(game) ? "게임을 종료할까요?" : "'" + game + "' 게임을 종료할까요?";
            if (string.IsNullOrEmpty(game)) QuitFlyout.Hide();
        }

        public override void OnDensity(bool compact)
        {
            base.OnDensity(compact);
            ProfileButton.MinHeight = EditMappingButton.MinHeight = OpenAppButton.MinHeight = QuitButton.MinHeight = ItemHeight(compact);
        }

        private void ProfileFlyout_Opening(object sender, object e)
        {
            ProfileFlyout.Items.Clear();
            var app = Host != null && Host.Status != null ? Host.Status.AppState : null;
            if (app == null)
            {
                ProfileFlyout.Items.Add(new MenuFlyoutItem { Text = "ClawHub 앱이 꺼져 있어요", IsEnabled = false });
                return;
            }

            var cycle = new MenuFlyoutItem { Text = "다음 프로필로", Icon = new FontIcon { Glyph = "" } };
            cycle.Click += (s, a) => _ = Run(Requests.AppCommand(AppCommands.CycleProfile), "프로필을 바꾸지 못했어요");
            ProfileFlyout.Items.Add(cycle);
            ProfileFlyout.Items.Add(new MenuFlyoutSeparator());

            AddProfileItem("매핑 없음", "", string.IsNullOrEmpty(app.ProfileId));
            foreach (var p in app.Profiles) AddProfileItem(p.Name, p.Id, p.Id == app.ProfileId);
        }

        private void AddProfileItem(string name, string id, bool current)
        {
            var item = new ToggleMenuFlyoutItem { Text = name, IsChecked = current };
            item.Click += (s, a) =>
            {
                if (current) return;
                _ = Run(Requests.AppCommand(AppCommands.SetProfilePrefix + id), "프로필을 바꾸지 못했어요");
            };
            ProfileFlyout.Items.Add(item);
        }

        private async void OpenAppButton_Click(object sender, RoutedEventArgs e)
        {
            if (Host == null) return;
            // clawhub: 프로토콜을 Game Bar를 통해 실행 → Game Bar가 닫히고 ClawApp이 앞으로 온다
            // (이미 실행 중이면 두 번째 인스턴스가 기존 창을 홈으로 불러온다).
            if (await Host.LaunchUriAsync(new Uri("clawhub:"))) return;
            // 실행 실패 시에만 서비스 경유 명령으로 대체 (Game Bar는 열린 채로 남는다)
            await Run(Requests.AppCommand(AppCommands.ShowClawHub), "ClawHub를 열지 못했어요");
        }

        /// <summary>
        /// 이 게임 매핑 편집: 앱에 "매핑 편집을 열어 줘"를 먼저 알리고, clawhub:로 런처를 앞으로 부른다
        /// (Game Bar가 닫히고 런처가 매핑 화면으로 뜬다). 편집하고 게임으로 돌아가면 바로 적용된다.
        /// </summary>
        private async void EditMappingButton_Click(object sender, RoutedEventArgs e)
        {
            if (Host == null) return;
            if (!await Run(Requests.AppCommand(AppCommands.EditMapping), "매핑 편집을 열지 못했어요")) return;
            if (await Host.LaunchUriAsync(new Uri("clawhub:"))) return;
            await Run(Requests.AppCommand(AppCommands.ShowClawHub), "ClawHub를 열지 못했어요");
        }

        private async void QuitConfirmButton_Click(object sender, RoutedEventArgs e)
        {
            QuitFlyout.Hide();
            if (await Run(Requests.AppCommand(AppCommands.QuitGame), "게임을 종료하지 못했어요"))
                Host?.ShowToast("게임 종료를 요청했어요", false);
        }

        private void QuitCancelButton_Click(object sender, RoutedEventArgs e)
        {
            QuitFlyout.Hide();
        }
    }
}
