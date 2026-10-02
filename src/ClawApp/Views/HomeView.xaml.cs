using System.ComponentModel;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Threading;
using ClawApp.ViewModels;

namespace ClawApp.Views;

/// <summary>
/// 시작 화면의 터치·마우스 조작.
///  - 길게 누르기(0.5초) 또는 오른쪽 클릭(터치는 "누르고 있다 떼기") → 편집 시작.
///  - 편집 중 타일을 끌면 손가락을 따라오고, 다른 타일 가장자리에 잠시 머물면 그 자리로 옮겨지며, 가운데에 머물면 폴더 표시.
///  - 화면 가장자리로 끌면 저절로 넘어간다. 빈 곳을 누르면 편집 끝. 휠·빈 곳 끌기로 가로 스크롤.
/// </summary>
public partial class HomeView : UserControl
{
    private const double DragThreshold = 10;
    private const double EdgeZone = 90;

    private HomeViewModel? _vm;

    private StartTileViewModel? _pressTile;
    private Point _pressPos;
    private Point _lastPos;
    private Point _grabOffset;
    private bool _dragging;
    private StartTileViewModel? _dragTile;

    private StartTileViewModel? _hoverTarget;
    private bool _hoverCenter;

    private bool _panning;
    private bool _panMoved;
    private Point _panStart;
    private double _panOffset;

    private readonly DispatcherTimer _hold = new() { Interval = TimeSpan.FromMilliseconds(520) };
    private readonly DispatcherTimer _hover = new() { Interval = TimeSpan.FromMilliseconds(320) };
    private readonly DispatcherTimer _autoScroll = new() { Interval = TimeSpan.FromMilliseconds(30) };

    public HomeView()
    {
        InitializeComponent();
        _hold.Tick += OnHold;
        _hover.Tick += OnHover;
        _autoScroll.Tick += OnAutoScroll;

        DataContextChanged += (_, _) =>
        {
            if (_vm is not null) { _vm.FocusTile -= FocusTile; _vm.PropertyChanged -= OnVmChanged; }
            _vm = DataContext as HomeViewModel;
            if (_vm is not null) { _vm.FocusTile += FocusTile; _vm.PropertyChanged += OnVmChanged; }
        };

        Board.PreviewMouseLeftButtonDown += OnDown;
        Board.PreviewMouseMove += OnMove;
        Board.PreviewMouseLeftButtonUp += OnUp;
        Board.PreviewMouseRightButtonUp += OnRightUp;
        // 판 자신이 잡기를 잃었을 때만 (타일 버튼이 잃는 이벤트도 올라오는데, 그건 판이 가져온 것)
        Board.LostMouseCapture += (_, e) => { if (_dragging && ReferenceEquals(e.OriginalSource, Board)) EndDragging(); };
        // 편집 중 방향키로 다른 타일에 가면 그 타일이 선택된다 (잡고 옮기는 중엔 그대로)
        Board.GotKeyboardFocus += (_, e) =>
        {
            if (_vm is { IsEditing: true, Grabbed: null } && TileAt(e.NewFocus) is { } t && t != _vm.Selected) _vm.Select(t);
        };

        Scroller.PreviewMouseLeftButtonDown += OnScrollerDown;
        Scroller.PreviewMouseMove += OnScrollerMove;
        Scroller.PreviewMouseLeftButtonUp += OnScrollerUp;
        Unloaded += (_, _) => { _hold.Stop(); _hover.Stop(); _autoScroll.Stop(); };
    }

    private void OnVmChanged(object? sender, PropertyChangedEventArgs e)
    {
        switch (e.PropertyName)
        {
            case nameof(HomeViewModel.IsEditing):
                // 편집 중엔 손가락 끌기가 타일 옮기기 — 화면 넘기기는 빈 곳 끌기·가장자리로
                Scroller.PanningMode = _vm!.IsEditing ? PanningMode.None : PanningMode.HorizontalOnly;
                break;
            case nameof(HomeViewModel.Renaming) when _vm!.Renaming is not null:
                Dispatcher.BeginInvoke(() =>
                {
                    if (FindButton(RenameBox) is { } b) Keyboard.Focus(b);
                }, DispatcherPriority.Loaded);
                break;
        }
    }

    // ── 찾기 ──────────────────────────────────────────────

    private static StartTileViewModel? TileAt(object source)
    {
        for (var d = source as DependencyObject; d is not null; d = UpOf(d))
        {
            if (d is FrameworkElement { DataContext: StartTileViewModel t }) return t;
            if (d is ItemsControl { Name: "Board" }) return null;
        }
        return null;
    }

    /// <summary>편집 모서리 버튼 위를 누른 경우 (끌기 대신 그 버튼이 눌리게).</summary>
    private static bool OnEditButton(object source)
    {
        for (var d = source as DependencyObject; d is not null; d = UpOf(d))
        {
            if (d is Button { Name: "Tile" }) return false;
            if (d is Button) return true;
            if (d is ItemsControl { Name: "Board" }) return false;
        }
        return false;
    }

    private static DependencyObject? UpOf(DependencyObject d) =>
        d is Visual or System.Windows.Media.Media3D.Visual3D ? VisualTreeHelper.GetParent(d) : LogicalTreeHelper.GetParent(d);

    private FrameworkElement? ContainerOf(StartTileViewModel tile) =>
        Board.ItemContainerGenerator.ContainerFromItem(tile) as FrameworkElement;

    private static Button? FindButton(DependencyObject root)
    {
        if (root is Button b) return b;
        for (var i = 0; i < VisualTreeHelper.GetChildrenCount(root); i++)
            if (FindButton(VisualTreeHelper.GetChild(root, i)) is { } found) return found;
        return null;
    }

    private void FocusTile(StartTileViewModel tile) => Dispatcher.BeginInvoke(() =>
    {
        if (ContainerOf(tile) is { } c && FindButton(c) is { } b) Keyboard.Focus(b);
    }, DispatcherPriority.Loaded);

    // ── 타일 누르기·끌기 ───────────────────────────────────

    private void OnDown(object sender, MouseButtonEventArgs e)
    {
        if (_vm is null || OnEditButton(e.OriginalSource) || TileAt(e.OriginalSource) is not { } tile) return;
        _pressTile = tile;
        _pressPos = _lastPos = e.GetPosition(Board);
        if (!_vm.IsEditing) _hold.Start(); // 길게 누르면 편집 + 바로 끌기
    }

    private void OnMove(object sender, MouseEventArgs e)
    {
        if (_vm is null || _pressTile is null) return;
        if (e.LeftButton != MouseButtonState.Pressed) { Reset(); return; }
        var pos = e.GetPosition(Board);
        _lastPos = pos;
        if (_dragging)
        {
            UpdateDrag(pos);
            e.Handled = true;
            return;
        }
        if ((pos - _pressPos).Length < DragThreshold) return;
        _hold.Stop();
        if (_vm.IsEditing) StartDrag(_pressTile, pos); // 편집 중: 움직이면 바로 끌기 (평소엔 화면 넘기기에 양보)
        else _pressTile = null;
    }

    private void OnHold(object? sender, EventArgs e)
    {
        _hold.Stop();
        if (_pressTile is { } t && Mouse.LeftButton == MouseButtonState.Pressed) StartDrag(t, _lastPos);
    }

    private void OnUp(object sender, MouseButtonEventArgs e)
    {
        _hold.Stop();
        if (_dragging)
        {
            EndDragging();
            e.Handled = true; // 놓을 때 타일이 눌리지(실행되지) 않게
        }
        _pressTile = null;
    }

    private void OnRightUp(object sender, MouseButtonEventArgs e)
    {
        if (_vm is null || TileAt(e.OriginalSource) is not { } tile) return;
        if (_vm.IsEditing) _vm.Select(tile);
        else _vm.BeginEdit(tile);
        e.Handled = true; // 터치 "누르고 있다 떼기"의 오른쪽 클릭 메뉴 대신
    }

    private void StartDrag(StartTileViewModel tile, Point pos)
    {
        if (_vm is null || ContainerOf(tile) is not { } container) return;
        var slot = VisualTreeHelper.GetOffset(container);
        _grabOffset = new Point(_pressPos.X - slot.X, _pressPos.Y - slot.Y);
        _vm.BeginDrag(tile);
        _dragTile = tile;
        _dragging = true;
        Board.CaptureMouse();
        _autoScroll.Start();
        ApplyDragTransform(pos);
    }

    private void UpdateDrag(Point pos)
    {
        ApplyDragTransform(pos);
        var (target, center) = HitTile(pos);
        if (target == _hoverTarget && center == _hoverCenter) return;
        _hoverTarget = target;
        _hoverCenter = center;
        _vm?.ClearMergeHint();
        _hover.Stop();
        if (target is not null) _hover.Start(); // 잠시 머물러야 반응 (지나가기만 하면 그대로)
    }

    private void OnHover(object? sender, EventArgs e)
    {
        _hover.Stop();
        if (_vm is null || _dragTile is null || _hoverTarget is null) return;
        var after = _vm.Tiles.IndexOf(_hoverTarget) > _vm.Tiles.IndexOf(_dragTile);
        _vm.DragOver(_hoverTarget, _hoverCenter, after);
        Board.UpdateLayout();
        ApplyDragTransform(_lastPos); // 자리가 바뀌어도 손가락 밑에 그대로
        _hoverTarget = null; // 옮긴 뒤 같은 타일 위에 계속 있으면 다시 판단
    }

    /// <summary>끌고 있는 타일 밑의 다른 타일, 그리고 그 가운데(안쪽 절반)인지.</summary>
    private (StartTileViewModel? Tile, bool Center) HitTile(Point pos)
    {
        if (_vm is null) return (null, false);
        foreach (var t in _vm.Tiles)
        {
            if (t == _dragTile) continue;
            var size = t.Span * StartPanel.Unit - StartPanel.Gap;
            var r = new Rect(t.Col * StartPanel.Unit, t.Row * StartPanel.Unit, size, size);
            if (!r.Contains(pos)) continue;
            var inner = r;
            inner.Inflate(-size / 4, -size / 4);
            return (t, inner.Contains(pos));
        }
        return (null, false);
    }

    private void ApplyDragTransform(Point pos)
    {
        if (_dragTile is null || ContainerOf(_dragTile) is not { } c) return;
        var slot = VisualTreeHelper.GetOffset(c);
        if (c.RenderTransform is not TranslateTransform t || t.IsFrozen) c.RenderTransform = t = new TranslateTransform();
        t.BeginAnimation(TranslateTransform.XProperty, null);
        t.BeginAnimation(TranslateTransform.YProperty, null);
        t.X = pos.X - _grabOffset.X - slot.X;
        t.Y = pos.Y - _grabOffset.Y - slot.Y;
    }

    private void EndDragging()
    {
        _dragging = false;
        _autoScroll.Stop();
        _hover.Stop();
        _hoverTarget = null;
        if (Board.IsMouseCaptured) Board.ReleaseMouseCapture();
        var tile = _dragTile;
        _dragTile = null;
        _vm?.EndDrag();
        // 손을 떼면 제자리로 미끄러져 들어간다
        if (tile is not null && ContainerOf(tile) is { RenderTransform: TranslateTransform t })
        {
            var ease = new CubicEase { EasingMode = EasingMode.EaseOut };
            t.BeginAnimation(TranslateTransform.XProperty, new DoubleAnimation(t.X, 0, TimeSpan.FromMilliseconds(200)) { EasingFunction = ease });
            t.BeginAnimation(TranslateTransform.YProperty, new DoubleAnimation(t.Y, 0, TimeSpan.FromMilliseconds(200)) { EasingFunction = ease });
        }
    }

    private void Reset()
    {
        _hold.Stop();
        if (_dragging) EndDragging();
        _pressTile = null;
    }

    /// <summary>화면 가장자리로 끌면 그쪽으로 넘어간다.</summary>
    private void OnAutoScroll(object? sender, EventArgs e)
    {
        if (!_dragging) { _autoScroll.Stop(); return; }
        var p = Mouse.GetPosition(Scroller);
        var step = p.X < EdgeZone ? -18 : p.X > Scroller.ActualWidth - EdgeZone ? 18 : 0;
        if (step == 0) return;
        Scroller.ScrollToHorizontalOffset(Scroller.HorizontalOffset + step);
        Scroller.UpdateLayout();
        _lastPos = Mouse.GetPosition(Board);
        UpdateDrag(_lastPos);
    }

    // ── 빈 곳: 누르면 편집 끝, 끌면 가로 스크롤 ───────────────

    private void OnScrollerDown(object sender, MouseButtonEventArgs e)
    {
        if (TileAt(e.OriginalSource) is not null || OnEditButton(e.OriginalSource)) return;
        // 터치는 평소엔 ScrollViewer가 직접 넘긴다 — 마우스와 편집 중일 때만 여기서
        if (e.StylusDevice is not null && _vm is { IsEditing: false }) return;
        _panning = true;
        _panMoved = false;
        _panStart = e.GetPosition(Scroller);
        _panOffset = Scroller.HorizontalOffset;
    }

    private void OnScrollerMove(object sender, MouseEventArgs e)
    {
        if (!_panning || _dragging) return;
        if (e.LeftButton != MouseButtonState.Pressed) { _panning = false; return; }
        var dx = e.GetPosition(Scroller).X - _panStart.X;
        if (!_panMoved && Math.Abs(dx) < DragThreshold) return;
        _panMoved = true;
        Scroller.ScrollToHorizontalOffset(_panOffset - dx);
    }

    private void OnScrollerUp(object sender, MouseButtonEventArgs e)
    {
        if (!_panning) return;
        _panning = false;
        if (!_panMoved && _vm is { IsEditing: true, Renaming: null }) _vm.EndEdit();
    }

    private void Scroller_Wheel(object sender, MouseWheelEventArgs e)
    {
        Scroller.ScrollToHorizontalOffset(Scroller.HorizontalOffset - e.Delta);
        e.Handled = true;
    }
}
