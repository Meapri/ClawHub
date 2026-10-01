using System.Threading.Tasks;
using ClawWidget.Ipc;
using Windows.UI.Xaml;

namespace ClawWidget.Sections
{
    /// <summary>
    /// 캡처 버튼 줄 → AppCommand Screenshot / ToggleRecording / SaveClip.
    /// 서비스가 런처 앱에 전달하고, 런처가 자기 창을 숨긴 뒤 Win+Alt+PrtSc / Win+Alt+R / Win+Alt+G를 보낸다.
    ///
    /// 한계: 위젯 SDK에는 Game Bar 오버레이를 닫는 API가 없다 (XboxGameBarWidget.MinimizeAsync는 위젯 창만 접는다).
    /// Game Bar 캡처 단축키는 Game Bar가 열려 있어도 마지막 게임 창을 대상으로 동작하므로 그대로 보낸다.
    /// 런처 앱이 꺼져 있으면 서비스가 Ok=false로 답하고 그 오류를 알림으로 보여 준다.
    /// </summary>
    public sealed partial class CaptureSection : WidgetSection
    {
        public CaptureSection()
        {
            InitializeComponent();
        }

        public override void OnDensity(bool compact)
        {
            Row.Margin = new Thickness(0, 0, 0, compact ? 6 : 8);
            ScreenshotButton.MinHeight = RecordButton.MinHeight = ClipButton.MinHeight = ItemHeight(compact);
        }

        protected override void OnStatus(ServiceStatus s)
        {
            // 런처 앱이 있어야 단축키를 대신 눌러 줄 수 있다
            var ok = s != null && s.AppState != null;
            ScreenshotButton.IsEnabled = RecordButton.IsEnabled = ClipButton.IsEnabled = ok;
        }

        private async Task SendAsync(string command, string fail, string done)
        {
            if (await Run(Requests.AppCommand(command), fail)) Host?.ShowToast(done, false);
        }

        private async void ScreenshotButton_Click(object sender, RoutedEventArgs e) =>
            await SendAsync(AppCommands.Screenshot, "스크린샷을 찍지 못했어요", "스크린샷을 요청했어요");

        private async void RecordButton_Click(object sender, RoutedEventArgs e) =>
            await SendAsync(AppCommands.ToggleRecording, "녹화를 바꾸지 못했어요", "녹화 시작/중지를 요청했어요");

        private async void ClipButton_Click(object sender, RoutedEventArgs e) =>
            await SendAsync(AppCommands.SaveClip, "클립을 저장하지 못했어요", "최근 30초 저장을 요청했어요 (백그라운드 녹화가 켜져 있어야 해요)");
    }
}
