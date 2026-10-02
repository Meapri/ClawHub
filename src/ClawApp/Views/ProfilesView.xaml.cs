using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Threading;
using ClawApp.ViewModels;

namespace ClawApp.Views;

public partial class ProfilesView : UserControl
{
    private ProfilesViewModel? _vm;

    public ProfilesView()
    {
        InitializeComponent();
        DataContextChanged += (_, _) =>
        {
            if (_vm is not null)
            {
                _vm.FocusProfileRequested -= OnFocusProfileRequested;
                _vm.RenameRequested -= OnRenameRequested;
            }
            _vm = DataContext as ProfilesViewModel;
            if (_vm is not null)
            {
                _vm.FocusProfileRequested += OnFocusProfileRequested;
                _vm.RenameRequested += OnRenameRequested;
            }
        };
        // 어느 줄(본문/동작 버튼)에 포커스가 있는지 VM에 알려 준다 → X/Y 대상, 힌트
        AddHandler(Keyboard.GotKeyboardFocusEvent, new KeyboardFocusChangedEventHandler((_, e) =>
        {
            if (_vm is null) return;
            if (e.NewFocus is Button { DataContext: ProfileRow row } b) _vm.SetFocus(row, Equals(b.Tag, "main"));
            else _vm.SetFocus(null, false);
        }), true);
        RenameBox.Committed += (_, _) => _vm?.EndRename();
    }

    private void OnRenameRequested() =>
        // 입력줄이 막 보이게 됐으므로 레이아웃 뒤에 입력을 연다 (터치 키보드 포함)
        Dispatcher.BeginInvoke(() => RenameBox.BeginEdit(), DispatcherPriority.Loaded);

    /// <summary>목록을 다시 만든 뒤 해당 프로필 줄 본문으로 (없으면 첫 줄).</summary>
    private void OnFocusProfileRequested(string? id) => Dispatcher.BeginInvoke(() =>
    {
        if (!IsLoaded || _vm is null) return;
        var row = (id is null ? null : _vm.UserRows.Concat(_vm.PresetRows).FirstOrDefault(r => r.Profile.Id == id))
                  ?? _vm.UserRows.Concat(_vm.PresetRows).FirstOrDefault();
        if (row is null) return;
        var list = _vm.SystemRows.Contains(row) ? SystemList : _vm.UserRows.Contains(row) ? UserList : PresetList;
        if (list.ItemContainerGenerator.ContainerFromItem(row) is DependencyObject c && FindMain(c) is { } b) Keyboard.Focus(b);
    }, DispatcherPriority.ContextIdle);

    private static Button? FindMain(DependencyObject root)
    {
        if (root is Button { Tag: "main" } b) return b;
        for (var i = 0; i < VisualTreeHelper.GetChildrenCount(root); i++)
            if (FindMain(VisualTreeHelper.GetChild(root, i)) is { } found) return found;
        return null;
    }
}
