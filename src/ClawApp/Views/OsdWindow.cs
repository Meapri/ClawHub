using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Threading;

namespace ClawApp.Views;

/// <summary>
/// 게임 위에 잠깐 뜨는 알림 (마이크 꺼짐, 밝기 60%, 매핑 바뀜 등). 포커스를 뺏지 않고 클릭이 통과한다.
/// 허브가 보이는 동안은 허브의 토스트를 쓴다 (<see cref="ViewModels.ShellViewModel.Notify"/>).
/// </summary>
public sealed class OsdWindow : Window
{
    private static OsdWindow? _instance;
    private readonly TextBlock _icon = new() { FontSize = 22, VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(0, 0, 12, 0) };
    private readonly TextBlock _text = new() { FontSize = 18, FontWeight = FontWeights.SemiBold, VerticalAlignment = VerticalAlignment.Center, Foreground = Brushes.White };
    private readonly Border _pill;
    private readonly TranslateTransform _slide = new();
    private readonly DispatcherTimer _hide = new() { Interval = TimeSpan.FromMilliseconds(1400) };

    private OsdWindow()
    {
        WindowStyle = WindowStyle.None;
        AllowsTransparency = true;
        Background = Brushes.Transparent;
        ResizeMode = ResizeMode.NoResize;
        Topmost = true;
        ShowActivated = false;
        ShowInTaskbar = false;
        Focusable = false;
        SizeToContent = SizeToContent.WidthAndHeight;
        _icon.FontFamily = Application.Current.TryFindResource("LucideFont") as FontFamily ?? new FontFamily("Segoe UI");
        _pill = new Border
        {
            CornerRadius = new CornerRadius(26),
            Padding = new Thickness(22, 12, 26, 12),
            Margin = new Thickness(16),
            Background = new SolidColorBrush(Color.FromArgb(0xEE, 0x1A, 0x1A, 0x1C)),
            BorderBrush = new SolidColorBrush(Color.FromArgb(0x30, 0xFF, 0xFF, 0xFF)),
            BorderThickness = new Thickness(1),
            Child = new StackPanel { Orientation = Orientation.Horizontal, Children = { _icon, _text } },
            RenderTransform = _slide,
        };
        Content = _pill;
        Opacity = 0;
        SourceInitialized += (_, _) =>
        {
            var hwnd = new WindowInteropHelper(this).Handle;
            SetWindowLong(hwnd, -20, GetWindowLong(hwnd, -20) | 0x08000000 /* NOACTIVATE */ | 0x20 /* TRANSPARENT */ | 0x80 /* TOOLWINDOW */);
        };
        _hide.Tick += (_, _) =>
        {
            _hide.Stop();
            var fade = new DoubleAnimation(0, TimeSpan.FromMilliseconds(180));
            fade.Completed += (_, _) => { if (Opacity == 0) Hide(); };
            BeginAnimation(OpacityProperty, fade);
        };
    }

    /// <summary>알림을 띄운다 (UI 스레드). <paramref name="iconKey"/> = Icon.* 리소스 이름.</summary>
    public static void Show(string iconKey, string text, bool warn = false)
    {
        var w = _instance ??= new OsdWindow();
        w._icon.Text = Application.Current.TryFindResource(iconKey) as string ?? "";
        w._icon.Foreground = warn ? new SolidColorBrush(Color.FromRgb(0xFF, 0x9F, 0x43))
            : Application.Current.TryFindResource("Accent") as Brush ?? Brushes.White;
        w._text.Text = text;
        w.Open();
    }

    private void Open()
    {
        var first = !IsVisible || Opacity < 0.05;
        if (!IsVisible) base.Show();
        UpdateLayout();
        // 게임이 있는 모니터 위쪽 가운데 (물리 픽셀)
        var hwnd = new WindowInteropHelper(this).Handle;
        var mi = new MonitorInfo { Size = Marshal.SizeOf<MonitorInfo>() };
        GetWindowRect(hwnd, out var me);
        if (GetMonitorInfo(MonitorFromWindow(GetForegroundWindow(), 2), ref mi))
        {
            var m = mi.Monitor;
            var w = me.Right - me.Left;
            SetWindowPos(hwnd, new IntPtr(-1), m.Left + (m.Right - m.Left - w) / 2, m.Top + (m.Bottom - m.Top) / 14, 0, 0, 0x0001 | 0x0010 | 0x0040);
        }
        var ease = new CubicEase { EasingMode = EasingMode.EaseOut };
        BeginAnimation(OpacityProperty, new DoubleAnimation(1, TimeSpan.FromMilliseconds(140)) { EasingFunction = ease });
        if (first) _slide.BeginAnimation(TranslateTransform.YProperty, new DoubleAnimation(-14, 0, TimeSpan.FromMilliseconds(220)) { EasingFunction = ease });
        _hide.Stop();
        _hide.Start();
    }

    [StructLayout(LayoutKind.Sequential)] private struct Rect { public int Left, Top, Right, Bottom; }
    [StructLayout(LayoutKind.Sequential)] private struct MonitorInfo { public int Size; public Rect Monitor, Work; public int Flags; }
    [DllImport("user32.dll")] private static extern int GetWindowLong(IntPtr hwnd, int index);
    [DllImport("user32.dll")] private static extern int SetWindowLong(IntPtr hwnd, int index, int value);
    [DllImport("user32.dll")] private static extern IntPtr GetForegroundWindow();
    [DllImport("user32.dll")] private static extern IntPtr MonitorFromWindow(IntPtr hwnd, uint flags);
    [DllImport("user32.dll")] private static extern bool GetMonitorInfo(IntPtr monitor, ref MonitorInfo info);
    [DllImport("user32.dll")] private static extern bool GetWindowRect(IntPtr hwnd, out Rect rect);
    [DllImport("user32.dll")] private static extern bool SetWindowPos(IntPtr hwnd, IntPtr after, int x, int y, int cx, int cy, uint flags);
}
