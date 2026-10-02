using System.ComponentModel;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Threading;
using ClawApp.ViewModels;

namespace ClawApp.Views;

public partial class LibraryView : UserControl
{
    private LibraryViewModel? _vm;

    public LibraryView()
    {
        InitializeComponent();
        DataContextChanged += (_, _) =>
        {
            if (_vm is not null)
            {
                _vm.PropertyChanged -= OnVmChanged;
                _vm.SearchRequested -= OnSearchRequested;
            }
            _vm = DataContext as LibraryViewModel;
            if (_vm is not null)
            {
                _vm.PropertyChanged += OnVmChanged;
                _vm.SearchRequested += OnSearchRequested;
            }
        };
        SearchBox.Committed += OnSearchCommitted;
    }

    // ── 포커스한 타일 → VM (X 힌트·고정 대상) ─────────────────

    private void Tile_GotKeyboardFocus(object sender, KeyboardFocusChangedEventArgs e)
    {
        if (_vm is not null && sender is FrameworkElement { DataContext: GameTileViewModel tile }) _vm.FocusedTile = tile;
    }

    private void Tile_LostKeyboardFocus(object sender, KeyboardFocusChangedEventArgs e)
    {
        // 다른 타일로 옮겨 가면 그쪽 GotKeyboardFocus가 바꾼다 — 힌트가 깜빡이지 않게 타일 밖으로 갈 때만 비운다
        if (_vm is null || e.NewFocus is FrameworkElement { DataContext: GameTileViewModel } next && TileList.IsAncestorOf(next)) return;
        _vm.FocusedTile = null;
    }

    // ── 목록이 바뀌면 (탭·정렬·찾기) 포커스 정리 ───────────────

    private void OnVmChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName != nameof(LibraryViewModel.Tiles)) return;
        Dispatcher.BeginInvoke(() =>
        {
            if (!IsLoaded || _vm is null) return;
            // 도구 모음 등 아직 보이는 곳에 있으면 그대로 (터치로 정렬을 눌렀을 때 등)
            if (Keyboard.FocusedElement is UIElement { IsVisible: true } f && IsAncestorOf(f)) return;
            FocusTile(_vm.PreferredFocus);
        }, DispatcherPriority.Loaded);
    }

    /// <summary>원하는 타일(없으면 첫 타일, 비었으면 빈 화면의 버튼)에 포커스.</summary>
    private void FocusTile(GameTileViewModel? preferred)
    {
        if (_vm is null) return;
        if (_vm.IsEmpty)
        {
            Keyboard.Focus(EmptyPrimary);
            return;
        }
        var item = preferred ?? _vm.Tiles.FirstOrDefault();
        if (item is not null && TileList.ItemContainerGenerator.ContainerFromItem(item) is DependencyObject c && FindButton(c) is { } b)
            Keyboard.Focus(b);
    }

    private static Button? FindButton(DependencyObject root)
    {
        if (root is Button { IsVisible: true } b) return b;
        for (var i = 0; i < VisualTreeHelper.GetChildrenCount(root); i++)
            if (FindButton(VisualTreeHelper.GetChild(root, i)) is { } found) return found;
        return null;
    }

    // ── 이름 찾기 (LT 또는 A/터치) ─────────────────────────────

    private void OnSearchRequested() => SearchBox.BeginEdit();

    /// <summary>찾기를 끝내면 결과의 첫 타일로 (입력칸에 머물러 있을 때만 — 터치로 다른 곳을 눌렀으면 그쪽).</summary>
    private void OnSearchCommitted(object? sender, EventArgs e) => Dispatcher.BeginInvoke(() =>
    {
        if (!IsLoaded || _vm is not { HasFilter: true, IsEmpty: false }) return;
        if (Keyboard.FocusedElement is DependencyObject f && SearchBox.IsAncestorOf(f)) FocusTile(null);
    }, DispatcherPriority.ContextIdle);
}
