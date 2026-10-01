using System.ComponentModel;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Threading;
using ClawApp.ViewModels;

namespace ClawApp.Views;

public partial class MacroEditorView : UserControl
{
    private MacroEditorViewModel? _vm;

    public MacroEditorView()
    {
        InitializeComponent();
        DataContextChanged += (_, _) =>
        {
            if (_vm is not null) _vm.PropertyChanged -= OnVmChanged;
            _vm = DataContext as MacroEditorViewModel;
            if (_vm is not null) _vm.PropertyChanged += OnVmChanged;
        };
    }

    /// <summary>단계에 포커스만 가도 오른쪽에 그 단계 시간이 보이게.</summary>
    private void Step_GotFocus(object sender, KeyboardFocusChangedEventArgs e)
    {
        if (DataContext is MacroEditorViewModel vm && sender is FrameworkElement { DataContext: MacroStepViewModel s })
            vm.Selected = s;
    }

    /// <summary>키 판을 열면 지금 키(없으면 첫 키), 닫으면 고른 단계(없으면 "단계 추가")로 포커스.</summary>
    private void OnVmChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(MacroEditorViewModel.IsPickerOpen))
            Dispatcher.BeginInvoke(MoveFocus, DispatcherPriority.Loaded);
    }

    private void MoveFocus()
    {
        if (_vm is null) return;
        var target = _vm.IsPickerOpen
            ? Find(Picker, e => e is Button { DataContext: KeySlotViewModel { IsSelected: true } }) ?? Find(Picker, e => e is Button)
            : Find(this, e => e is Button { DataContext: MacroStepViewModel { IsSelected: true } }) ?? Find(this, e => e is Button);
        if (target is not null) Keyboard.Focus(target);
    }

    private static UIElement? Find(DependencyObject root, Func<UIElement, bool> match)
    {
        if (root is UIElement { IsVisible: false }) return null;
        if (root is UIElement el && el.Focusable && el.IsEnabled && match(el)) return el;
        for (var i = 0; i < VisualTreeHelper.GetChildrenCount(root); i++)
            if (Find(VisualTreeHelper.GetChild(root, i), match) is { } found) return found;
        return null;
    }
}
