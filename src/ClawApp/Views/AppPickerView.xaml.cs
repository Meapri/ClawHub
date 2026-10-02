using System.ComponentModel;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Threading;
using ClawApp.ViewModels;

namespace ClawApp.Views;

public partial class AppPickerView : UserControl
{
    private AppPickerViewModel? _vm;

    public AppPickerView()
    {
        InitializeComponent();
        DataContextChanged += (_, _) =>
        {
            if (_vm is not null) _vm.PropertyChanged -= OnVmChanged;
            _vm = DataContext as AppPickerViewModel;
            if (_vm is not null) _vm.PropertyChanged += OnVmChanged;
        };
    }

    /// <summary>목록은 뒤에서 읽어 오므로, 다 읽으면 첫 프로그램에 포커스 (검색 결과가 바뀌어도 같은 규칙).</summary>
    private void OnVmChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName != nameof(AppPickerViewModel.Items)) return;
        Dispatcher.BeginInvoke(() =>
        {
            if (Keyboard.FocusedElement is DependencyObject f && List.IsAncestorOf(f)) return; // 이미 목록 안
            if (FirstButton(List) is { } first) Keyboard.Focus(first);
        }, DispatcherPriority.Loaded);
    }

    private static Button? FirstButton(DependencyObject root)
    {
        if (root is Button { IsVisible: true } b) return b;
        for (var i = 0; i < VisualTreeHelper.GetChildrenCount(root); i++)
            if (FirstButton(VisualTreeHelper.GetChild(root, i)) is { } found) return found;
        return null;
    }
}
