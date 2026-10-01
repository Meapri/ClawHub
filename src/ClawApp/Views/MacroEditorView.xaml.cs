using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using ClawApp.ViewModels;

namespace ClawApp.Views;

public partial class MacroEditorView : UserControl
{
    public MacroEditorView() => InitializeComponent();

    /// <summary>단계에 포커스만 가도 오른쪽에 그 단계 시간이 보이게.</summary>
    private void Step_GotFocus(object sender, KeyboardFocusChangedEventArgs e)
    {
        if (DataContext is MacroEditorViewModel vm && sender is FrameworkElement { DataContext: MacroStepViewModel s })
            vm.Selected = s;
    }
}
