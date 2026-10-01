using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using ClawApp.ViewModels;

namespace ClawApp.Views;

public partial class SettingsView : UserControl
{
    public SettingsView() => InitializeComponent();

    /// <summary>SteamOS처럼 분류에 포커스만 가도 오른쪽 내용이 바뀐다.</summary>
    private void Category_GotFocus(object sender, KeyboardFocusChangedEventArgs e)
    {
        if (DataContext is not SettingsViewModel vm || sender is not FrameworkElement { DataContext: SettingsCategory c }) return;

        // 내용에서 왼쪽으로 나오면 화면상 가장 가까운 분류가 아니라 지금 보던 분류로 돌아간다
        if (c != vm.Selected && e.OldFocus is DependencyObject old && ContentHost.IsAncestorOf(old)
            && CategoryList.ItemContainerGenerator.ContainerFromItem(vm.Selected) is DependencyObject container
            && FindButton(container) is { } current)
        {
            e.Handled = true;
            Dispatcher.BeginInvoke(() => Keyboard.Focus(current));
            return;
        }
        vm.Selected = c;
    }

    private static Button? FindButton(DependencyObject root)
    {
        if (root is Button b) return b;
        for (var i = 0; i < VisualTreeHelper.GetChildrenCount(root); i++)
            if (FindButton(VisualTreeHelper.GetChild(root, i)) is { } found) return found;
        return null;
    }
}
