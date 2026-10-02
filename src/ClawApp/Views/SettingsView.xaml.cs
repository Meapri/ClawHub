using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using ClawApp.ViewModels;

namespace ClawApp.Views;

public partial class SettingsView : UserControl
{
    public SettingsView()
    {
        InitializeComponent();
        DataContextChanged += (_, e) =>
        {
            if (e.OldValue is SettingsViewModel old) old.PropertyChanged -= OnVmChanged;
            if (e.NewValue is SettingsViewModel vm) vm.PropertyChanged += OnVmChanged;
        };
    }

    /// <summary>
    /// LB/RB로 분류를 바꾸면 포커스도 그 분류로 따라간다 (예전에는 선택 막대만 옮겨 가고 포커스 테두리는 이전 분류에 남았다).
    /// 오른쪽 내용을 조작 중이면 그대로 둔다.
    /// </summary>
    private void OnVmChanged(object? sender, System.ComponentModel.PropertyChangedEventArgs e)
    {
        if (e.PropertyName != nameof(SettingsViewModel.Selected) || sender is not SettingsViewModel vm) return;
        if (Keyboard.FocusedElement is not DependencyObject focused || !CategoryList.IsAncestorOf(focused)) return;
        Dispatcher.BeginInvoke(() =>
        {
            if (CategoryList.ItemContainerGenerator.ContainerFromItem(vm.Selected) is DependencyObject container
                && FindButton(container) is { } target && !target.IsKeyboardFocused)
                Keyboard.Focus(target);
        });
    }

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
