using System;
using Microsoft.Gaming.XboxGameBar;
using Windows.ApplicationModel;
using Windows.ApplicationModel.Activation;
using Windows.UI.Xaml;
using Windows.UI.Xaml.Controls;
using Windows.UI.Xaml.Navigation;

namespace ClawWidget
{
    /// <summary>
    /// Game Bar가 ms-gamebarwidget 프로토콜로 활성화한다 (WidgetSampleCS와 같은 흐름).
    /// 위젯 하나 = CoreWindow 하나. XboxGameBarWidget 객체를 창이 살아 있는 동안 붙잡고 있어야 한다.
    /// </summary>
    sealed partial class App : Application
    {
        private XboxGameBarWidget _widget;

        public App()
        {
            InitializeComponent();
            Suspending += OnSuspending;
            Resuming += OnResuming;
        }

        protected override void OnActivated(IActivatedEventArgs args)
        {
            XboxGameBarWidgetActivatedEventArgs widgetArgs = null;
            if (args.Kind == ActivationKind.Protocol)
            {
                var protocolArgs = args as IProtocolActivatedEventArgs;
                if (protocolArgs != null && protocolArgs.Uri.Scheme.Equals("ms-gamebarwidget"))
                    widgetArgs = args as XboxGameBarWidgetActivatedEventArgs;
            }
            if (widgetArgs == null) return;

            if (widgetArgs.IsLaunchActivation)
            {
                var rootFrame = new Frame();
                rootFrame.NavigationFailed += OnNavigationFailed;
                Window.Current.Content = rootFrame;

                // Game Bar와의 연결을 여는 객체 — 창이 닫힐 때까지 유지
                _widget = new XboxGameBarWidget(widgetArgs, Window.Current.CoreWindow, rootFrame);
                rootFrame.Navigate(typeof(WidgetPage), _widget);

                Window.Current.Closed += WidgetWindow_Closed;
                Window.Current.Activate();
            }
            // 이후 활성화(같은 위젯에 대한 재요청)는 무시
        }

        private void WidgetWindow_Closed(object sender, Windows.UI.Core.CoreWindowEventArgs e)
        {
            DisposeCurrentPage();
            _widget = null;
            Window.Current.Closed -= WidgetWindow_Closed;
        }

        /// <summary>AppListEntry="none"이라 보통은 호출되지 않는다. 디버거로 직접 실행하면 일반 창에 위젯 화면을 띄운다.</summary>
        protected override void OnLaunched(LaunchActivatedEventArgs e)
        {
            var rootFrame = Window.Current.Content as Frame;
            if (rootFrame == null)
            {
                rootFrame = new Frame();
                rootFrame.NavigationFailed += OnNavigationFailed;
                Window.Current.Content = rootFrame;
            }
            if (!e.PrelaunchActivated)
            {
                if (rootFrame.Content == null) rootFrame.Navigate(typeof(WidgetPage), null);
                Window.Current.Activate();
            }
        }

        private void OnNavigationFailed(object sender, NavigationFailedEventArgs e)
        {
            throw new Exception("Failed to load Page " + e.SourcePageType.FullName);
        }

        /// <summary>Game Bar가 위젯 연결을 유지하는 동안에는 일시 중단되지 않는다. 그래도 오면 폴링·파이프를 멈춘다.</summary>
        private async void OnSuspending(object sender, SuspendingEventArgs e)
        {
            var deferral = e.SuspendingOperation.GetDeferral();
            try
            {
                if ((Window.Current?.Content as Frame)?.Content is WidgetPage page) await page.SuspendAsync();
            }
            finally
            {
                deferral.Complete();
            }
        }

        private void OnResuming(object sender, object e)
        {
            ((Window.Current?.Content as Frame)?.Content as WidgetPage)?.Resume();
        }

        /// <summary>폴링 타이머와 파이프 연결을 닫는다.</summary>
        private static void DisposeCurrentPage()
        {
            var frame = Window.Current?.Content as Frame;
            (frame?.Content as IDisposable)?.Dispose();
        }
    }
}
