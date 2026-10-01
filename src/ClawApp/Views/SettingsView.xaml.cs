using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using ClawApp.ViewModels;

namespace ClawApp.Views;

public partial class SettingsView : UserControl
{
    public SettingsView() => InitializeComponent();

    /// <summary>SteamOS처럼 분류에 포커스만 가도 오른쪽 내용이 바뀐다.</summary>
    private void Category_GotFocus(object sender, KeyboardFocusChangedEventArgs e)
    {
        if (DataContext is SettingsViewModel vm && sender is FrameworkElement { DataContext: SettingsCategory c })
            vm.Selected = c;
    }
}
