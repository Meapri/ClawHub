using Windows.UI.Xaml;
using Windows.UI.Xaml.Controls;
using Windows.UI.Xaml.Media;

namespace ClawWidget.UI
{
    /// <summary>
    /// Lucide 아이콘 글꼴 (v1.49.0, ISC — Assets/Fonts/lucide.ttf, 라이선스 Assets/Fonts/Lucide-LICENSE.txt).
    /// App.xaml의 "WidgetIconFont"가 이 글꼴이다. 코드포인트는 lucide-static font/codepoints.json 기준.
    /// XAML에서는 &amp;#xE1B4; 처럼 직접 쓰고, 코드에서는 아래 상수를 쓴다.
    /// </summary>
    public static class Icons
    {
        public const string Activity = "";
        public const string Battery = "";
        public const string BatteryCharging = "";
        public const string Check = "";
        public const string ChevronDown = "";
        public const string CircleAlert = "";
        public const string Cpu = "";
        public const string ExternalLink = "";
        public const string Gamepad = "";
        public const string Gauge = "";
        public const string Info = "";
        public const string Keyboard = "";
        public const string Leaf = "";
        public const string Lightbulb = "";
        public const string Monitor = "";
        public const string MonitorPlay = "";
        public const string Power = "";
        public const string RefreshCw = "";
        public const string Repeat = "";
        public const string Rocket = "";
        public const string Scale = "";
        public const string SlidersHorizontal = "";
        public const string Snowflake = "";
        public const string Sun = "";
        public const string Thermometer = "";
        public const string TriangleAlert = "";
        public const string X = "";
        public const string Zap = "";
        public const string Plug = "";
        public const string Camera = "";
        public const string Video = "";
        public const string Film = "";
        public const string Sparkles = "";
        public const string HeartPulse = "";

        public static FontFamily Font => (FontFamily)Application.Current.Resources["WidgetIconFont"];

        public static FontIcon Create(string glyph, double size = 16) =>
            new FontIcon { FontFamily = Font, Glyph = glyph, FontSize = size };
    }

    /// <summary>카드 머리: [아이콘] 제목 ……… 보조 문구. XAML: &lt;ui:SectionHeader Icon="&amp;#xE1BF;" Title="성능" /&gt;</summary>
    public sealed class SectionHeader : Grid
    {
        private readonly FontIcon _icon = new FontIcon { FontSize = 16, Margin = new Thickness(0, 0, 8, 0), VerticalAlignment = VerticalAlignment.Center };
        private readonly TextBlock _title = new TextBlock { VerticalAlignment = VerticalAlignment.Center };
        private readonly TextBlock _detail = new TextBlock
        {
            VerticalAlignment = VerticalAlignment.Center,
            HorizontalAlignment = HorizontalAlignment.Right,
            TextTrimming = TextTrimming.CharacterEllipsis,
        };
        private readonly FontIcon _detailIcon = new FontIcon { FontSize = 13, VerticalAlignment = VerticalAlignment.Center, Visibility = Visibility.Collapsed };
        private readonly StackPanel _detailPanel = new StackPanel
        {
            Orientation = Orientation.Horizontal,
            Spacing = 4,
            HorizontalAlignment = HorizontalAlignment.Right,
            VerticalAlignment = VerticalAlignment.Center,
            Margin = new Thickness(8, 0, 0, 0),
        };

        public SectionHeader()
        {
            ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            _icon.FontFamily = Icons.Font; // 색은 글자색을 그대로 따른다 (단색, 테마 자동)
            _title.Style = (Style)Application.Current.Resources["SectionHeaderStyle"];
            _detail.Style = (Style)Application.Current.Resources["CaptionStyle"];
            _detail.TextWrapping = TextWrapping.NoWrap;
            _detailIcon.FontFamily = Icons.Font;
            _detailPanel.Children.Add(_detailIcon);
            _detailPanel.Children.Add(_detail);
            SetColumn(_title, 1);
            SetColumn(_detailPanel, 2);
            Children.Add(_icon);
            Children.Add(_title);
            Children.Add(_detailPanel);
        }

        /// <summary>보조 문구 앞 작은 아이콘 (예: 전원 연결/배터리). 빈 값이면 숨김.</summary>
        public string DetailIcon
        {
            get { return _detailIcon.Glyph; }
            set
            {
                _detailIcon.Glyph = value ?? "";
                _detailIcon.Visibility = string.IsNullOrEmpty(value) ? Visibility.Collapsed : Visibility.Visible;
            }
        }

        public string Icon
        {
            get { return _icon.Glyph; }
            set { _icon.Glyph = value ?? ""; }
        }

        public string Title
        {
            get { return _title.Text; }
            set { _title.Text = value ?? ""; }
        }

        public string Detail
        {
            get { return _detail.Text; }
            set { _detail.Text = value ?? ""; }
        }
    }
}
