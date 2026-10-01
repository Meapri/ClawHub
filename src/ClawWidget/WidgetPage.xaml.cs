using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using ClawWidget.Ipc;
using ClawWidget.Sections;
using ClawWidget.UI;
using Microsoft.Gaming.XboxGameBar;
using Windows.Data.Json;
using Windows.Foundation;
using Windows.System;
using Windows.UI;
using Windows.UI.Core;
using Windows.UI.Xaml;
using Windows.UI.Xaml.Controls;
using Windows.UI.Xaml.Media;
using Windows.UI.Xaml.Navigation;

namespace ClawWidget
{
    /// <summary>
    /// ClawHub 퀵 설정 위젯 (ClawApp 퀵 설정 오버레이 대체). 이 페이지는 "껍데기"다:
    /// 서비스 연결·폴링, Game Bar 상태(테마·투명도·컴팩트·핀·표시 여부), 알림, 미니 HUD를 맡고
    /// 실제 기능 카드는 <see cref="WidgetSection"/>들이 맡는다 — 새 기능은 <see cref="CreateSections"/>에 한 줄.
    ///
    /// 데이터: 2초마다 GetTelemetry, 전체 화면일 때는 4초마다 GetStatus (연결이 없으면 매 주기 재접속 시도).
    /// 명령 응답에도 Status가 오므로 누른 결과는 바로 반영된다. Subscribe는 하지 않는다 (Models.cs Ops 주석).
    /// </summary>
    public sealed partial class WidgetPage : Page, IDisposable, ISectionHost
    {
        private static readonly TimeSpan PollInterval = TimeSpan.FromSeconds(2);
        private static readonly Size DefaultSize = new Size(400, 640);
        private static readonly Size NormalMinSize = new Size(400, 360);
        private static readonly Size HudSize = new Size(300, 40);

        private readonly PipeClient _client = new PipeClient();
        private readonly DispatcherTimer _timer = new DispatcherTimer();
        private readonly DispatcherTimer _toastTimer = new DispatcherTimer();
        private readonly List<WidgetSection> _sections = new List<WidgetSection>();

        private XboxGameBarWidget _widget;
        private ServiceStatus _status;
        private bool _attempted;
        private bool _polling;
        private bool _disposed;
        private bool _hud;
        private bool _focusedOnce;
        private int _tick;
        private Size _savedSize = DefaultSize;

        public WidgetPage()
        {
            InitializeComponent();
            CreateSections();

            Motion.EnableShowHide(OfflineCard);
            Motion.EnableShowHide(Toast, 6);
            Motion.EnableShowHide(WarningText);

            _timer.Interval = PollInterval;
            _timer.Tick += async (s, e) => await PollAsync(false);
            _toastTimer.Interval = TimeSpan.FromSeconds(3.5);
            _toastTimer.Tick += (s, e) => { _toastTimer.Stop(); Toast.Visibility = Visibility.Collapsed; };

            Loaded += OnLoaded;
            ApplyStatus(null);
            ApplyTelemetry(null);
            ApplyDensity();
        }

        /// <summary>카드 순서 = 화면 순서. 새 섹션(자동 TDP, 배터리 수명, 실제 FPS, 화면 품질, 캡처 …)은 여기에 추가.</summary>
        private void CreateSections()
        {
            Add(new HeaderSection());
            Add(new CaptureSection());
            Add(new TelemetrySection());
            Add(new PerformanceSection());
            Add(new EnduranceSection());
            Add(new GraphicsSection());
            Add(new DisplaySection());
            Add(new LightingSection());
        }

        private void Add(WidgetSection section)
        {
            section.Attach(this);
            _sections.Add(section);
            SectionsPanel.Children.Add(section);
        }

        // ══════════════ ISectionHost ══════════════

        public ServiceStatus Status => _status;

        public bool ConnectionAttempted => _attempted;

        public async Task<bool> RunAsync(JsonObject request, string failMessage)
        {
            var resp = await _client.SendAsync(request, 10000);
            if (_disposed) return false;
            if (resp == null)
            {
                _attempted = true;
                ShowToast("서비스 연결 안 됨 · " + (_client.LastError ?? "ClawHub 서비스에 연결할 수 없어요"), true);
                ApplyStatus(null);
                return false;
            }
            if (!resp.Ok)
                ShowToast((failMessage ?? "적용하지 못했어요") + " · " + (string.IsNullOrEmpty(resp.Error) ? "알 수 없는 오류" : resp.Error), true);
            // 실패해도 서비스가 현재 상태를 함께 보낸다 → 토글처럼 미리 움직인 컨트롤을 실제 값으로 되돌림
            ApplyStatus(resp.Status ?? _status);
            return resp.Ok;
        }

        public void ShowToast(string message, bool error)
        {
            if (_hud || string.IsNullOrEmpty(message)) return;
            ToastText.Text = message;
            ToastIcon.Glyph = error ? Icons.CircleAlert : Icons.Info;
            ToastIcon.Foreground = ThemeBrush(error ? "WidgetErrorBrush" : "WidgetOkBrush");
            Toast.Visibility = Visibility.Visible;
            _toastTimer.Stop();
            _toastTimer.Start();
        }

        /// <summary>현재 요소 테마에 맞는 위젯 브러시 (App.xaml ThemeDictionaries). 위젯 테마는 앱 테마와 다를 수 있다.</summary>
        public Brush ThemeBrush(string key)
        {
            var themeKey = ActualTheme == ElementTheme.Light ? "Light" : "Dark";
            object dict;
            if (Application.Current.Resources.ThemeDictionaries.TryGetValue(themeKey, out dict))
            {
                var rd = dict as ResourceDictionary;
                object b;
                if (rd != null && rd.TryGetValue(key, out b)) return b as Brush;
            }
            return null;
        }

        public async Task<bool> LaunchUriAsync(Uri uri)
        {
            try
            {
                return _widget != null ? await _widget.LaunchUriAsync(uri) : await Launcher.LaunchUriAsync(uri);
            }
            catch (Exception)
            {
                return false;
            }
        }

        // ══════════════ 수명 · Game Bar 연동 ══════════════

        protected override void OnNavigatedTo(NavigationEventArgs e)
        {
            _widget = e.Parameter as XboxGameBarWidget;
            if (_widget == null) return;

            RequestedTheme = _widget.RequestedTheme;
            _widget.RequestedThemeChanged += Widget_RequestedThemeChanged;
            _widget.RequestedOpacityChanged += Widget_RequestedOpacityChanged;
            _widget.VisibleChanged += Widget_StateChanged;
            _widget.WindowStateChanged += Widget_StateChanged;
            _widget.PinnedChanged += Widget_ViewModeChanged;
            _widget.GameBarDisplayModeChanged += Widget_ViewModeChanged;
            _widget.CompactModeEnabledChanged += Widget_CompactModeChanged;
            ApplyDensity();
            ApplyBackground();
        }

        private async void OnLoaded(object sender, RoutedEventArgs e)
        {
            await UpdateViewModeAsync();
            UpdatePolling();
            await PollAsync(true);
        }

        private bool ShouldPoll =>
            !_disposed && (_widget == null || (_widget.Visible && _widget.WindowState != XboxGameBarWidgetWindowState.Minimized));

        private void UpdatePolling()
        {
            if (ShouldPoll) { if (!_timer.IsEnabled) _timer.Start(); }
            else _timer.Stop();
        }

        /// <summary>앱 일시 중단 — 폴링을 멈추고 파이프를 닫는다.</summary>
        public void Suspend()
        {
            _timer.Stop();
            _client.Disconnect();
        }

        public async void Resume()
        {
            if (_disposed) return;
            UpdatePolling();
            await PollAsync(true);
        }

        public void Dispose()
        {
            if (_disposed) return;
            _disposed = true;
            _timer.Stop();
            _toastTimer.Stop();
            foreach (var s in _sections) s.Stop();
            if (_widget != null)
            {
                _widget.RequestedThemeChanged -= Widget_RequestedThemeChanged;
                _widget.RequestedOpacityChanged -= Widget_RequestedOpacityChanged;
                _widget.VisibleChanged -= Widget_StateChanged;
                _widget.WindowStateChanged -= Widget_StateChanged;
                _widget.PinnedChanged -= Widget_ViewModeChanged;
                _widget.GameBarDisplayModeChanged -= Widget_ViewModeChanged;
                _widget.CompactModeEnabledChanged -= Widget_CompactModeChanged;
                _widget = null;
            }
            _client.Dispose();
        }

        // Game Bar 이벤트는 UI 스레드가 아닐 수 있다 → Dispatcher로 넘긴다

        private async void Widget_RequestedThemeChanged(XboxGameBarWidget sender, object args)
        {
            await Dispatcher.RunAsync(CoreDispatcherPriority.Normal, () =>
            {
                if (_disposed) return;
                RequestedTheme = sender.RequestedTheme;
                ApplyBackground();
                ApplyStatus(_status); // 코드에서 넣은 상태 색도 새 테마로
            });
        }

        private async void Widget_RequestedOpacityChanged(XboxGameBarWidget sender, object args)
        {
            await Dispatcher.RunAsync(CoreDispatcherPriority.Normal, () => { if (!_disposed) ApplyBackground(); });
        }

        private async void Widget_StateChanged(XboxGameBarWidget sender, object args)
        {
            await Dispatcher.RunAsync(CoreDispatcherPriority.Normal, async () =>
            {
                if (_disposed) return;
                var wasRunning = _timer.IsEnabled;
                UpdatePolling();
                if (!wasRunning && _timer.IsEnabled) await PollAsync(true);
            });
        }

        private async void Widget_ViewModeChanged(XboxGameBarWidget sender, object args)
        {
            await Dispatcher.RunAsync(CoreDispatcherPriority.Normal, async () =>
            {
                if (_disposed) return;
                await UpdateViewModeAsync();
                UpdatePolling();
                await PollAsync(!_hud);
            });
        }

        private async void Widget_CompactModeChanged(XboxGameBarWidget sender, object args)
        {
            await Dispatcher.RunAsync(CoreDispatcherPriority.Normal, () => { if (!_disposed) ApplyDensity(); });
        }

        /// <summary>고정 + Game Bar 닫힘(PinnedOnly) → 미니 HUD로 창을 줄인다. Game Bar가 다시 열리면 원래 크기로.</summary>
        private async Task UpdateViewModeAsync()
        {
            var hud = _widget != null && _widget.Pinned && _widget.GameBarDisplayMode == XboxGameBarDisplayMode.PinnedOnly;
            if (hud == _hud) return;
            _hud = hud;

            FullView.Visibility = hud ? Visibility.Collapsed : Visibility.Visible;
            PinnedView.Visibility = hud ? Visibility.Visible : Visibility.Collapsed;
            if (hud) Toast.Visibility = Visibility.Collapsed;
            ApplyBackground();

            try
            {
                if (hud)
                {
                    var bounds = _widget.WindowBounds;
                    if (bounds.Height >= NormalMinSize.Height) _savedSize = new Size(bounds.Width, bounds.Height);
                    _widget.MinWindowSize = new Size(120, 32);
                    await _widget.TryResizeWindowAsync(HudSize);
                }
                else if (_widget != null)
                {
                    await _widget.TryResizeWindowAsync(_savedSize);
                    _widget.MinWindowSize = NormalMinSize;
                }
            }
            catch (Exception)
            {
                // 크기 조절이 거부돼도 HUD는 왼쪽 위에 그대로 보인다 (나머지 영역은 투명)
            }
        }

        /// <summary>Game Bar 투명도 설정을 배경에만 적용 (글자는 불투명 유지). HUD에서는 알약 배경에만.</summary>
        private void ApplyBackground()
        {
            var opacity = _widget != null ? _widget.RequestedOpacity : 1.0;
            var light = ActualTheme == ElementTheme.Light;
            if (_hud)
            {
                RootGrid.Background = new SolidColorBrush(Colors.Transparent);
                var pill = light ? Color.FromArgb(0xFF, 0xF9, 0xF9, 0xF9) : Color.FromArgb(0xFF, 0x20, 0x20, 0x20);
                PinnedView.Background = new SolidColorBrush(pill) { Opacity = Math.Max(0.35, opacity) };
            }
            else
            {
                var bg = light ? Color.FromArgb(0xFF, 0xF3, 0xF3, 0xF3) : Color.FromArgb(0xFF, 0x20, 0x20, 0x20);
                RootGrid.Background = new SolidColorBrush(bg) { Opacity = opacity };
            }
        }

        /// <summary>컴팩트 모드(핸드헬드): 여백은 줄이고 누르는 대상은 키운다.</summary>
        private void ApplyDensity()
        {
            var compact = _widget != null && _widget.CompactModeEnabled;
            MainStack.Padding = compact ? new Thickness(8, 6, 8, 8) : new Thickness(12, 10, 12, 12);
            OfflineCard.Margin = new Thickness(0, 0, 0, compact ? 6 : 8);
            RetryButton.MinHeight = compact ? 44 : 32;
            foreach (var s in _sections) s.OnDensity(compact);
        }

        // ══════════════ 폴링 ══════════════

        private async Task PollAsync(bool forceStatus)
        {
            if (_polling || _disposed) return;
            _polling = true;
            try
            {
                _tick++;
                // HUD에선 상태가 필요 없다. 전체 화면에선 4초마다 (아직 상태가 없으면 매번).
                var wantStatus = forceStatus || (!_hud && (_status == null || _tick % 2 == 0));
                if (wantStatus)
                {
                    var resp = await _client.SendAsync(Requests.Create(Ops.GetStatus));
                    if (_disposed) return;
                    _attempted = true;
                    if (resp == null || resp.Status == null)
                    {
                        ApplyStatus(null);
                        ApplyTelemetry(null);
                        return;
                    }
                    ApplyStatus(resp.Status);
                }

                var tel = await _client.SendAsync(Requests.Create(Ops.GetTelemetry), 3000);
                if (_disposed) return;
                if (tel == null)
                {
                    _attempted = true;
                    ApplyStatus(null);
                    ApplyTelemetry(null);
                    return;
                }
                ApplyTelemetry(tel.Ok ? tel.Telemetry : null);
            }
            finally
            {
                _polling = false;
            }
        }

        // ══════════════ 화면 갱신 ══════════════

        private void ApplyStatus(ServiceStatus s)
        {
            _status = s;
            var online = s != null;

            OfflineCard.Visibility = !online && _attempted ? Visibility.Visible : Visibility.Collapsed;
            if (!online) OfflineDetail.Text = _client.LastError ?? "ClawHub 서비스에 연결할 수 없어요";

            foreach (var section in _sections)
            {
                var available = section.IsAvailable(s);
                section.Visibility = available ? Visibility.Visible : Visibility.Collapsed;
                if (available || s == null) section.ApplyStatus(s);
            }

            var warnings = online ? s.Warnings.Where(w => !string.IsNullOrWhiteSpace(w)).ToList() : new List<string>();
            WarningText.Text = string.Join("\n", warnings);
            WarningText.Visibility = warnings.Count > 0 ? Visibility.Visible : Visibility.Collapsed;

            if (online && !_focusedOnce && !_hud)
            {
                // 하드웨어 버튼으로 열렸을 때 바로 패드로 조작할 수 있게 (현재 성능 모드 타일)
                _focusedOnce = true;
                foreach (var section in _sections)
                {
                    var target = section.Visibility == Visibility.Visible ? section.InitialFocus : null;
                    if (target != null && target.IsEnabled)
                    {
                        target.Focus(FocusState.Keyboard);
                        break;
                    }
                }
            }
        }

        private void ApplyTelemetry(Telemetry t)
        {
            foreach (var section in _sections) section.ApplyTelemetry(t);

            // 미니 HUD (숫자 애니메이션 없이 바로)
            if (t == null)
            {
                HudCpuTemp.Text = _attempted && _status == null ? "연결 안 됨" : "–";
                HudCpuWatts.Text = HudBattery.Text = "–";
                HudBatteryWatts.Text = "";
                return;
            }
            HudCpuTemp.Text = t.CpuTemp.HasValue ? t.CpuTemp.Value + "°C" : "–";
            HudCpuWatts.Text = t.CpuWatts.HasValue ? t.CpuWatts.Value + " W" : "–";
            HudBatteryIcon.Glyph = t.Charging ? Icons.BatteryCharging : Icons.Battery;
            HudBattery.Text = t.BatteryPercent.HasValue ? t.BatteryPercent.Value + "%" : "–";
            HudBatteryWatts.Text = !t.Charging && t.BatteryWatts.HasValue ? t.BatteryWatts.Value.ToString("0.0") + " W" : "";
        }

        private async void RetryButton_Click(object sender, RoutedEventArgs e)
        {
            await PollAsync(true);
        }
    }
}
