using System;
using System.Threading.Tasks;
using ClawWidget.Ipc;
using Windows.Data.Json;
using Windows.UI.Xaml;
using Windows.UI.Xaml.Controls;
using Windows.UI.Xaml.Media;

namespace ClawWidget.Sections
{
    /// <summary>섹션이 페이지(호스트)에게 요구하는 것. 연결·폴링·알림·테마는 페이지가 맡는다.</summary>
    public interface ISectionHost
    {
        /// <summary>마지막으로 받은 서비스 상태 (연결 안 됨이면 null).</summary>
        ServiceStatus Status { get; }

        /// <summary>한 번이라도 연결을 시도했는지 ("연결 중…"과 "연결 안 됨" 구분).</summary>
        bool ConnectionAttempted { get; }

        /// <summary>요청을 보내고 응답 Status로 전체 화면을 갱신. Ok=false면 failMessage + 서비스 Error를 잠깐 보여 준다.</summary>
        Task<bool> RunAsync(JsonObject request, string failMessage);

        void ShowToast(string message, bool error);

        /// <summary>현재 테마의 위젯 브러시 (App.xaml ThemeDictionaries 키).</summary>
        Brush ThemeBrush(string key);

        /// <summary>Game Bar를 통해 URI 실행 (Game Bar가 닫히고 앱이 앞으로 온다).</summary>
        Task<bool> LaunchUriAsync(Uri uri);
    }

    /// <summary>
    /// 위젯의 카드 하나. 새 기능 = 이 클래스를 상속한 UserControl 하나 + WidgetPage.CreateSections()에 한 줄.
    ///
    ///  - OnStatus: GetStatus(또는 명령 응답)의 상태로 화면을 맞춘다. 이 안에서 바뀐 컨트롤 값은 사용자 입력이 아니다
    ///    (이벤트 처리기에서 <see cref="IgnoreInput"/>을 확인).
    ///  - OnTelemetry: 2초마다 GetTelemetry 결과.
    ///  - IsAvailable: false면 카드를 숨긴다 (기본: 서비스에 연결돼 있을 때만 표시).
    ///  - 명령은 <see cref="Run"/>으로 — 응답 처리·오류 알림은 호스트가 한다.
    /// </summary>
    public class WidgetSection : UserControl
    {
        private bool _applying;

        protected ISectionHost Host { get; private set; }

        public void Attach(ISectionHost host) => Host = host;

        /// <summary>코드가 값을 넣는 중이거나 아직 붙지 않았으면 true — 이벤트 처리기에서 무시할 것.</summary>
        protected bool IgnoreInput => Host == null || _applying;

        public void ApplyStatus(ServiceStatus status)
        {
            _applying = true;
            try { OnStatus(status); }
            finally { _applying = false; }
        }

        public void ApplyTelemetry(Telemetry telemetry)
        {
            _applying = true;
            try { OnTelemetry(telemetry); }
            finally { _applying = false; }
        }

        public virtual bool IsAvailable(ServiceStatus status) => status != null;

        protected virtual void OnStatus(ServiceStatus status) { }

        protected virtual void OnTelemetry(Telemetry telemetry) { }

        /// <summary>컴팩트(핸드헬드) 모드: 여백은 줄이고 누르는 대상은 키운다. 기본 구현은 카드 여백만.</summary>
        public virtual void OnDensity(bool compact)
        {
            var card = Content as Border;
            if (card == null) return;
            card.Padding = new Thickness(compact ? 10 : 12);
            card.Margin = new Thickness(0, 0, 0, 6);
        }

        /// <summary>위젯이 처음 열릴 때 포커스를 줄 컨트롤 (없으면 null).</summary>
        public virtual Control InitialFocus => null;

        /// <summary>위젯 종료 — 타이머 등 정리.</summary>
        public virtual void Stop() { }

        protected Task<bool> Run(JsonObject request, string failMessage = "적용하지 못했어요") =>
            Host != null ? Host.RunAsync(request, failMessage) : Task.FromResult(false);

        protected Brush ThemeBrush(string key) => Host != null ? Host.ThemeBrush(key) : null;

        /// <summary>누르는 대상 높이. 손가락 크기 이상 (허브와 같이 44 이상), 핸드헬드(컴팩트)에서는 더 크게.</summary>
        protected static double ItemHeight(bool compact) => compact ? 52 : 44;
    }
}
