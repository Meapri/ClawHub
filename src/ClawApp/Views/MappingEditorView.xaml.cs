using System.ComponentModel;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Threading;
using ClawApp.ViewModels;

namespace ClawApp.Views;

/// <summary>
/// 선택판을 열고 닫을 때 포커스를 옮긴다: 열면 지금 지정된 키(없으면 첫 키), 닫으면 고르던 버튼 줄로.
/// (셸의 "페이지별 포커스 기억"은 화면 구성이 바뀌는 이 전환에는 맞지 않는다.)
/// </summary>
public partial class MappingEditorView : UserControl
{
    private MappingEditorViewModel? _vm;

    public MappingEditorView()
    {
        InitializeComponent();
        DataContextChanged += (_, _) =>
        {
            if (_vm is not null) _vm.PropertyChanged -= OnVmChanged;
            _vm = DataContext as MappingEditorViewModel;
            if (_vm is not null) _vm.PropertyChanged += OnVmChanged;
        };
    }

    private void OnVmChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName is nameof(MappingEditorViewModel.IsPickerOpen) or nameof(MappingEditorViewModel.CategoryIndex))
            Dispatcher.BeginInvoke(MoveFocus, DispatcherPriority.Loaded); // 보이기/숨기기가 레이아웃에 반영된 뒤
    }

    private void MoveFocus()
    {
        if (_vm is null) return;
        UIElement? target;
        if (_vm.IsPickerOpen)
        {
            target = Find(PickerContent, e => e is Button { DataContext: KeySlotViewModel { IsSelected: true } })
                     ?? Find(PickerContent, e => e is Button { DataContext: KeyComboViewModel { IsSelected: true } })
                     ?? Find(PickerContent, e => e is Button);
        }
        else
        {
            var row = _vm.EditingRow;
            target = row is null ? null : Find(Board, e => e is Button { DataContext: ButtonRowViewModel r } && r == row);
        }
        if (target is not null) Keyboard.Focus(target);
    }

    /// <summary>보이는 포커스 가능 요소 중 조건에 맞는 첫 번째 (화면 순서).</summary>
    private static UIElement? Find(DependencyObject root, Func<UIElement, bool> match)
    {
        if (root is UIElement { IsVisible: false }) return null;
        if (root is UIElement el && el.Focusable && el.IsEnabled && match(el)) return el;
        for (var i = 0; i < VisualTreeHelper.GetChildrenCount(root); i++)
            if (Find(VisualTreeHelper.GetChild(root, i), match) is { } found) return found;
        return null;
    }
}
