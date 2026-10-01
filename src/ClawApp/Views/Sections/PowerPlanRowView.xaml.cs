using System.Windows;
using System.Windows.Controls;

namespace ClawApp.Views.Sections;

public partial class PowerPlanRowView : UserControl
{
    /// <summary>줄 앞 아이콘 글자 (Lucide). 예: IconText="{DynamicResource Icon.Monitor}".</summary>
    public static readonly DependencyProperty IconTextProperty =
        DependencyProperty.Register(nameof(IconText), typeof(string), typeof(PowerPlanRowView), new PropertyMetadata(""));

    public string IconText
    {
        get => (string)GetValue(IconTextProperty);
        set => SetValue(IconTextProperty, value);
    }

    public PowerPlanRowView() => InitializeComponent();
}
