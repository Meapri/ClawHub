using System.Windows;
using System.Windows.Controls;
using System.Windows.Data;
using System.Windows.Input;
using System.Windows.Threading;

namespace ClawApp.Views;

/// <summary>버튼처럼 보이다가 A를 누르면 입력칸이 되는 글자 편집 (패드·터치 기기용).</summary>
public partial class InlineEdit : UserControl
{
    public static readonly DependencyProperty TextProperty = DependencyProperty.Register(
        nameof(Text), typeof(string), typeof(InlineEdit),
        new FrameworkPropertyMetadata("", FrameworkPropertyMetadataOptions.BindsTwoWayByDefault, (d, _) => ((InlineEdit)d).UpdatePlaceholder()));

    public string Text
    {
        get => (string)GetValue(TextProperty);
        set => SetValue(TextProperty, value);
    }

    public static readonly DependencyProperty PlaceholderProperty = DependencyProperty.Register(
        nameof(Placeholder), typeof(string), typeof(InlineEdit), new PropertyMetadata("", (d, _) => ((InlineEdit)d).UpdatePlaceholder()));

    /// <summary>값이 비었을 때 버튼에 보이는 안내.</summary>
    public string Placeholder
    {
        get => (string)GetValue(PlaceholderProperty);
        set => SetValue(PlaceholderProperty, value);
    }

    public static readonly DependencyProperty TextAlignmentProperty = DependencyProperty.Register(
        nameof(TextAlignment), typeof(TextAlignment), typeof(InlineEdit), new PropertyMetadata(TextAlignment.Left));

    public TextAlignment TextAlignment
    {
        get => (TextAlignment)GetValue(TextAlignmentProperty);
        set => SetValue(TextAlignmentProperty, value);
    }

    public static readonly DependencyProperty InnerPaddingProperty = DependencyProperty.Register(
        nameof(InnerPadding), typeof(Thickness), typeof(InlineEdit), new PropertyMetadata(new Thickness(18, 12, 18, 12)));

    /// <summary>버튼 안쪽 여백 (UserControl.Padding은 바깥에도 먹으므로 따로 둔다).</summary>
    public Thickness InnerPadding
    {
        get => (Thickness)GetValue(InnerPaddingProperty);
        set => SetValue(InnerPaddingProperty, value);
    }

    /// <summary>입력이 끝났을 때 (Enter·B·포커스 이동). 검색 실행 등에 쓴다.</summary>
    public event EventHandler? Committed;

    public InlineEdit()
    {
        InitializeComponent();
        UpdatePlaceholder();
    }

    private void UpdatePlaceholder()
    {
        if (HintText is null) return; // InitializeComponent 전
        var empty = string.IsNullOrEmpty(Text);
        HintText.Visibility = empty ? Visibility.Visible : Visibility.Collapsed;
        ValueText.Visibility = empty ? Visibility.Collapsed : Visibility.Visible;
    }

    private void Display_Click(object sender, RoutedEventArgs e)
    {
        Editor.Text = Text;
        Editor.HorizontalContentAlignment = TextAlignment == TextAlignment.Center ? HorizontalAlignment.Center : HorizontalAlignment.Left;
        Editor.MinHeight = Display.ActualHeight;
        Display.Visibility = Visibility.Hidden;
        Editor.Visibility = Visibility.Visible;
        Dispatcher.BeginInvoke(() =>
        {
            Keyboard.Focus(Editor);
            Editor.SelectAll();
            Services.SystemControls.ShowTouchKeyboard();
        }, DispatcherPriority.Loaded);
    }

    private void Editor_LostFocus(object sender, KeyboardFocusChangedEventArgs e)
    {
        if (Editor.Visibility != Visibility.Visible) return;
        Text = Editor.Text;
        GetBindingExpression(TextProperty)?.UpdateSource();
        Editor.Visibility = Visibility.Collapsed;
        Display.Visibility = Visibility.Visible;
        // 패드·키보드로 끝냈으면(Enter·B) 이 칸에 머문다. 마우스로 다른 곳을 눌렀으면 그쪽으로.
        if (Mouse.LeftButton != MouseButtonState.Pressed)
            Dispatcher.BeginInvoke(() => Keyboard.Focus(Display), DispatcherPriority.Loaded);
        Committed?.Invoke(this, EventArgs.Empty);
    }
}
