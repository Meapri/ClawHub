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
            if (_vm is not null)
            {
                _vm.PropertyChanged -= OnVmChanged;
                foreach (var r in _vm.Rows) r.PropertyChanged -= OnRowChanged;
            }
            _vm = DataContext as MappingEditorViewModel;
            if (_vm is not null)
            {
                _vm.PropertyChanged += OnVmChanged;
                foreach (var r in _vm.Rows) r.PropertyChanged += OnRowChanged;
            }
            ScheduleLeaders(0);
        };

        // 안내선: 크기·카메라가 바뀌거나 카드가 미끄러져 들어온 뒤 다시 그린다
        _leaderTimer.Tick += (_, _) => { _leaderTimer.Stop(); DrawLeaders(); };
        Map.Arranged += () => ScheduleLeaders(0);
        Board.SizeChanged += (_, _) => ScheduleLeaders(0);
        Board.IsVisibleChanged += (_, e) => { if (e.NewValue is true) ScheduleLeaders(650); }; // 카드 등장 모션이 끝난 뒤

        // 버튼 줄에 포커스가 가거나 마우스가 올라가면 가운데 그림에서 그 버튼을 밝힌다
        foreach (var list in new[] { LeftList, RightList })
        {
            list.AddHandler(Keyboard.GotKeyboardFocusEvent, new KeyboardFocusChangedEventHandler((_, e) => Spotlight(e.NewFocus as DependencyObject)), true);
            list.MouseMove += (_, e) => Spotlight(e.OriginalSource as DependencyObject, clear: false); // 줄 사이 틈에서는 그대로
            list.MouseLeave += (_, _) => Spotlight(Keyboard.FocusedElement as DependencyObject);
        }
        // 버튼 줄 밖(가운데 동작 등)으로 포커스가 가면 그림은 본체 전체로 물러난다
        AddHandler(Keyboard.GotKeyboardFocusEvent, new KeyboardFocusChangedEventHandler((_, e) => Spotlight(e.NewFocus as DependencyObject)), true);
    }

    private void Spotlight(DependencyObject? from, bool clear = true)
    {
        if (_vm is null) return;
        for (var d = from; d is not null; d = d is Visual ? VisualTreeHelper.GetParent(d) : LogicalTreeHelper.GetParent(d))
            if (d is FrameworkElement { DataContext: ButtonRowViewModel row })
            {
                _vm.Spotlight(row);
                return;
            }
        if (clear) _vm.Spotlight(null);
    }

    private void OnVmChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName is nameof(MappingEditorViewModel.IsPickerOpen) or nameof(MappingEditorViewModel.CategoryIndex))
            Dispatcher.BeginInvoke(MoveFocus, DispatcherPriority.Loaded); // 보이기/숨기기가 레이아웃에 반영된 뒤
        else if (e.PropertyName == nameof(MappingEditorViewModel.IsOptionsOpen))
            Dispatcher.BeginInvoke(FocusOptions, DispatcherPriority.Loaded);
        else if (e.PropertyName == nameof(MappingEditorViewModel.FocusedButton))
            DrawLeaders(); // 선 모양만 바뀐다 (위치는 그대로)
    }

    private void OnRowChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName is nameof(ButtonRowViewModel.IsMapped) or nameof(ButtonRowViewModel.IsLive)) DrawLeaders();
        else if (e.PropertyName is nameof(ButtonRowViewModel.Conflict) or nameof(ButtonRowViewModel.KeyLabels)) ScheduleLeaders(80); // 카드 높이가 바뀔 수 있다
    }

    /// <summary>옵션 패널을 열면 그 안 첫 버튼으로, 닫으면 옵션 버튼으로 포커스.</summary>
    private void FocusOptions()
    {
        if (_vm is null) return;
        var target = _vm.IsOptionsOpen ? Find(OptionsPanel, e => e is Button) : OptionsButton;
        if (target is not null) Keyboard.Focus(target);
        // 포커스를 따라 내려간 스크롤을 맨 위로 (패널 제목부터 보이게)
        if (_vm.IsOptionsOpen && OptionsPanel.Child is ScrollViewer sv)
            Dispatcher.BeginInvoke(() => Motion.SmoothScroll.ScrollTo(sv, 0, 0), DispatcherPriority.ContextIdle); // 부드러운 스크롤의 목표도 함께
    }

    // ── 안내선 ─────────────────────────────────────────────

    private readonly DispatcherTimer _leaderTimer = new() { Interval = TimeSpan.FromMilliseconds(1) };

    private DateTime _leaderDue;

    /// <summary>그리기를 예약한다. 이미 더 늦게 잡힌 예약(카드 등장 모션 대기)이 있으면 그것을 지킨다.</summary>
    private void ScheduleLeaders(int delayMs)
    {
        var due = DateTime.UtcNow.AddMilliseconds(delayMs);
        if (_leaderTimer.IsEnabled && _leaderDue > due) return;
        _leaderDue = due;
        _leaderTimer.Stop();
        _leaderTimer.Interval = TimeSpan.FromMilliseconds(Math.Max(1, delayMs));
        _leaderTimer.Start();
    }

    /// <summary>
    /// 카드의 본체 쪽 가장자리 가운데 → 짧게 수평으로 나온 뒤 → 사진 속 그 버튼 가운데. 끝에는 작은 점.
    /// 고른 카드 = 강조색 굵게, 키가 지정된 카드 = 강조색 옅게, 그 밖 = 흐린 회색 (사진을 어지럽히지 않게).
    /// </summary>
    private void DrawLeaders()
    {
        Leaders.Children.Clear();
        if (_vm is null || !Board.IsVisible) return;
        foreach (var (list, left) in new[] { (LeftList, true), (RightList, false) })
        {
            for (var i = 0; i < list.Items.Count; i++)
            {
                if (list.ItemContainerGenerator.ContainerFromIndex(i) is not FrameworkElement container ||
                    container.DataContext is not ButtonRowViewModel row ||
                    Find(container, e => e is Button) is not FrameworkElement card || card.ActualWidth < 1) continue;
                if (Map.ButtonPoint(row.Button) is not { } onMap) continue;
                Point a, b;
                try
                {
                    a = card.TranslatePoint(new Point(left ? card.ActualWidth : 0, card.ActualHeight / 2), Leaders);
                    b = Map.TranslatePoint(onMap, Leaders);
                }
                catch (InvalidOperationException) { continue; }

                var focused = _vm.FocusedButton == row.Button || row.IsLive;
                var line = new System.Windows.Shapes.Polyline
                {
                    Points = [a, new Point(a.X + (left ? 18 : -18), a.Y), b],
                    StrokeThickness = focused ? 2.5 : 1.5,
                    StrokeLineJoin = PenLineJoin.Round,
                    Opacity = focused ? 1 : row.IsMapped ? 0.55 : 0.3,
                };
                line.SetResourceReference(System.Windows.Shapes.Shape.StrokeProperty, focused || row.IsMapped ? "Accent" : "StrokeStrong");
                Leaders.Children.Add(line);

                var r = focused ? 4.5 : 3;
                var dot = new System.Windows.Shapes.Ellipse { Width = r * 2, Height = r * 2, Opacity = line.Opacity };
                dot.SetResourceReference(System.Windows.Shapes.Shape.FillProperty, focused || row.IsMapped ? "Accent" : "StrokeStrong");
                Canvas.SetLeft(dot, b.X - r);
                Canvas.SetTop(dot, b.Y - r);
                Leaders.Children.Add(dot);
            }
        }
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
