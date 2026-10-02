using System.ComponentModel;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Threading;
using ClawApp.ViewModels;

namespace ClawApp.Views;

/// <summary>
/// 시작 화면의 터치·마우스 조작. 판은 두 가지: 구역들(GroupsHost) 또는 열린 폴더 창(FolderBoard).
///  - 길게 누르기(0.5초) 또는 오른쪽 클릭 → 편집 시작.
///  - 편집 중 타일을 끌면 손가락을 따라오고, 다른 타일 가장자리에 잠시 머물면 그 자리로(다른 구역으로도), 가운데에 머물면 폴더 표시,
///    "새 구역" 자리에 머물면 새 구역. 폴더 창 밖으로 끌어 놓으면 폴더에서 꺼낸다.
///  - 화면 가장자리로 끌면 저절로 넘어간다. 빈 곳을 누르면 편집 끝(폴더 창 바깥이면 창 닫기). 휠·빈 곳 끌기로 가로 스크롤.
/// </summary>
public partial class HomeView : UserControl
{
    private const double DragThreshold = 10;
    private const double EdgeZone = 90;

    private HomeViewModel? _vm;

    private StartTileViewModel? _pressTile;
    private Point _pressPos;
    private Point _grabOffset;
    private bool _dragging;
    private StartTileViewModel? _dragTile;

    private object? _hoverTarget; // 타일 · "새 구역" · "폴더 밖"
    private bool _hoverCenter;

    private bool _panning;
    private bool _panMoved;
    private Point _panStart;
    private double _panOffset;

    private readonly DispatcherTimer _hold = new() { Interval = TimeSpan.FromMilliseconds(520) };
    private readonly DispatcherTimer _hover = new() { Interval = TimeSpan.FromMilliseconds(320) };
    private readonly DispatcherTimer _autoScroll = new() { Interval = TimeSpan.FromMilliseconds(30) };

    private static readonly object NewGroupTarget = new();
    private static readonly object OutsideFolderTarget = new();

    public HomeView()
    {
        InitializeComponent();
        _hold.Tick += OnHold;
        _hover.Tick += OnHover;
        _autoScroll.Tick += OnAutoScroll;

        DataContextChanged += (_, _) =>
        {
            if (_vm is not null) { _vm.FocusTile -= FocusTile; _vm.PropertyChanged -= OnVmChanged; _vm.Locate = null; }
            _vm = DataContext as HomeViewModel;
            if (_vm is not null) { _vm.FocusTile += FocusTile; _vm.PropertyChanged += OnVmChanged; _vm.Locate = Locate; }
        };

        foreach (var surface in new FrameworkElement[] { GroupsHost, FolderBoard })
        {
            surface.PreviewMouseLeftButtonDown += OnDown;
            surface.PreviewMouseMove += OnMove;
            surface.PreviewMouseLeftButtonUp += OnUp;
            surface.PreviewMouseRightButtonUp += OnRightUp;
            // 판 자신이 잡기를 잃었을 때만 (타일 버튼이 잃는 이벤트도 올라오는데, 그건 판이 가져온 것)
            var s = surface;
            s.LostMouseCapture += (_, e) => { if (_dragging && ReferenceEquals(e.OriginalSource, s)) EndDragging(); };
            // 편집 중 방향키로 다른 타일에 가면 그 타일이 선택된다 (잡고 옮기는 중엔 그대로)
            s.GotKeyboardFocus += (_, e) =>
            {
                if (_vm is { IsEditing: true, Grabbed: null } && TileAt(e.NewFocus) is { } t && t != _vm.Selected) _vm.Select(t);
            };
        }

        Scroller.PreviewMouseLeftButtonDown += OnScrollerDown;
        Scroller.PreviewMouseMove += OnScrollerMove;
        Scroller.PreviewMouseLeftButtonUp += OnScrollerUp;
        FolderScrim.MouseLeftButtonUp += (_, _) => { if (!_dragging) _vm?.CloseFolder(); };
        Unloaded += (_, _) => { _hold.Stop(); _hover.Stop(); _autoScroll.Stop(); };
    }

    private void OnVmChanged(object? sender, PropertyChangedEventArgs e)
    {
        switch (e.PropertyName)
        {
            case nameof(HomeViewModel.IsEditing):
                // 편집 중엔 손가락 끌기가 타일 옮기기 — 화면 넘기기는 빈 곳 끌기·가장자리로
                Scroller.PanningMode = _vm!.IsEditing ? PanningMode.None : PanningMode.HorizontalOnly;
                FolderScroller.PanningMode = Scroller.PanningMode;
                break;
            case nameof(HomeViewModel.Renaming) when _vm!.Renaming is not null:
                Dispatcher.BeginInvoke(() =>
                {
                    if (FindButton(RenameBox) is { } b) Keyboard.Focus(b);
                }, DispatcherPriority.Loaded);
                break;
        }
    }

    /// <summary>지금 조작하는 판 (폴더 창이 열려 있으면 폴더).</summary>
    private FrameworkElement Surface => _vm?.OpenFolder is not null ? FolderBoard : GroupsHost;
    private ScrollViewer SurfaceScroller => _vm?.OpenFolder is not null ? FolderScroller : Scroller;

    // ── 찾기 ──────────────────────────────────────────────

    private static StartTileViewModel? TileAt(object? source)
    {
        for (var d = source as DependencyObject; d is not null; d = UpOf(d))
        {
            if (d is FrameworkElement { DataContext: StartTileViewModel t }) return t;
            if (d is ItemsControl { Name: "GroupsHost" or "FolderBoard" }) return null;
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
            if (d is ItemsControl { Name: "GroupsHost" or "FolderBoard" }) return false;
        }
        return false;
    }

    private static DependencyObject? UpOf(DependencyObject d) =>
        d is Visual or System.Windows.Media.Media3D.Visual3D ? VisualTreeHelper.GetParent(d) : LogicalTreeHelper.GetParent(d);

    /// <summary>타일의 항목 컨테이너 (구역마다 ItemsControl이 따로라 시각 트리에서 찾는다).</summary>
    private FrameworkElement? ContainerOf(StartTileViewModel tile)
    {
        // 끄는 동안 마우스가 움직일 때마다 타일마다 찾으므로 기억해 두고, 아직 그 판에 붙어 있을 때만 다시 쓴다
        // (다른 구역으로 옮겨지면 컨테이너가 새로 생긴다)
        var surface = Surface;
        if (_containers.TryGetValue(tile, out var c) && c is ContentPresenter { Content: var content, IsLoaded: true }
            && ReferenceEquals(content, tile) && surface.IsAncestorOf(c))
            return c;
        c = FindContainer(surface, tile);
        if (c is null) _containers.Remove(tile);
        else _containers[tile] = c;
        return c;
    }

    private readonly Dictionary<StartTileViewModel, FrameworkElement> _containers = [];

    private static FrameworkElement? FindContainer(DependencyObject root, StartTileViewModel tile)
    {
        if (root is ContentPresenter { Content: var c } cp && ReferenceEquals(c, tile)) return cp;
        for (var i = 0; i < VisualTreeHelper.GetChildrenCount(root); i++)
            if (FindContainer(VisualTreeHelper.GetChild(root, i), tile) is { } found) return found;
        return null;
    }

    private static Button? FindButton(DependencyObject root)
    {
        if (root is Button b) return b;
        for (var i = 0; i < VisualTreeHelper.GetChildrenCount(root); i++)
            if (FindButton(VisualTreeHelper.GetChild(root, i)) is { } found) return found;
        return null;
    }

    /// <summary>판 기준 타일 위치 (패드 이동·끌기 판정). 끌리는 중인 이동은 빼고 원래 자리.</summary>
    private Rect? Locate(StartTileViewModel tile)
    {
        if (ContainerOf(tile) is not { IsVisible: true } c) return null;
        try
        {
            var at = c.TransformToAncestor(Surface).Transform(new Point(0, 0));
            if (c.RenderTransform is TranslateTransform t) at.Offset(-t.X, -t.Y);
            return new Rect(at, c.RenderSize);
        }
        catch (InvalidOperationException) { return null; }
    }

    private void FocusTile(StartTileViewModel tile) => Dispatcher.BeginInvoke(() =>
    {
        if (ContainerOf(tile) is { } c && FindButton(c) is { } b)
        {
            Keyboard.Focus(b);
            b.BringIntoView();
        }
    }, DispatcherPriority.Loaded);

    // ── 타일 누르기·끌기 ───────────────────────────────────

    private void OnDown(object sender, MouseButtonEventArgs e)
    {
        if (_vm is null || OnEditButton(e.OriginalSource) || TileAt(e.OriginalSource) is not { } tile) return;
        _pressTile = tile;
        _pressPos = e.GetPosition(Surface);
        if (!_vm.IsEditing) _hold.Start(); // 길게 누르면 편집 + 바로 끌기
    }

    private void OnMove(object sender, MouseEventArgs e)
    {
        if (_vm is null || _pressTile is null) return;
        if (e.LeftButton != MouseButtonState.Pressed) { Reset(); return; }
        if (_dragging)
        {
            UpdateDrag();
            e.Handled = true;
            return;
        }
        if ((e.GetPosition(Surface) - _pressPos).Length < DragThreshold) return;
        _hold.Stop();
        if (_vm.IsEditing) StartDrag(_pressTile); // 편집 중: 움직이면 바로 끌기 (평소엔 화면 넘기기에 양보)
        else _pressTile = null;
    }

    private void OnHold(object? sender, EventArgs e)
    {
        _hold.Stop();
        if (Motion.SmoothScroll.IsPanning(SurfaceScroller)) { _pressTile = null; return; } // 손가락으로 화면을 넘기는 중
        if (_pressTile is { } t && Mouse.LeftButton == MouseButtonState.Pressed) StartDrag(t);
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

    private void StartDrag(StartTileViewModel tile)
    {
        if (_vm is null || ContainerOf(tile) is not { } container) return;
        _grabOffset = Mouse.GetPosition(container);
        _vm.BeginDrag(tile);
        _dragTile = tile;
        _dragging = true;
        Surface.CaptureMouse();
        _autoScroll.Start();
        ApplyDragTransform();
    }

    private void UpdateDrag()
    {
        ApplyDragTransform();
        var (target, center) = HitTarget(Mouse.GetPosition(Surface));
        if (target == _hoverTarget && center == _hoverCenter) return;
        _hoverTarget = target;
        _hoverCenter = center;
        _vm?.ClearMergeHint();
        _hover.Stop();
        if (target is not null && target != OutsideFolderTarget) _hover.Start(); // 잠시 머물러야 반응 (폴더 밖은 놓을 때)
    }

    private void OnHover(object? sender, EventArgs e)
    {
        _hover.Stop();
        if (_vm is null || _dragTile is null) return;
        if (_hoverTarget is StartTileViewModel t) _vm.DragOver(t, _hoverCenter);
        else if (_hoverTarget == NewGroupTarget) _vm.DragToNewGroup();
        else return;
        Surface.UpdateLayout();
        ApplyDragTransform(); // 자리가 바뀌어도 손가락 밑에 그대로
        if (!_hoverCenter) _hoverTarget = null; // 옮긴 뒤 같은 곳에 계속 있으면 다시 판단 (폴더 표시는 유지)
    }

    /// <summary>끌고 있는 위치 밑: 다른 타일(가운데=안쪽 절반인지), "새 구역" 자리, 또는 폴더 창 밖.</summary>
    private (object? Target, bool Center) HitTarget(Point pos)
    {
        if (_vm is null) return (null, false);
        foreach (var t in _vm.Surface)
        {
            if (t == _dragTile || Locate(t) is not { } r || !r.Contains(pos)) continue;
            var inner = r;
            inner.Inflate(-r.Width / 4, -r.Height / 4);
            return (t, inner.Contains(pos));
        }
        if (_vm.OpenFolder is not null)
            return Inside(FolderCard) ? (null, false) : (OutsideFolderTarget, false);
        if (NewGroupZone.IsVisible && Inside(NewGroupZone)) return (NewGroupTarget, false);
        return (null, false);
    }

    private static bool Inside(FrameworkElement el)
    {
        var p = Mouse.GetPosition(el);
        return p.X >= 0 && p.Y >= 0 && p.X <= el.ActualWidth && p.Y <= el.ActualHeight;
    }

    private void ApplyDragTransform()
    {
        if (_dragTile is null || ContainerOf(_dragTile) is not { } c || VisualTreeHelper.GetParent(c) is not UIElement panel) return;
        var slot = VisualTreeHelper.GetOffset(c);
        var pos = Mouse.GetPosition(panel);
        if (c.RenderTransform is not TranslateTransform t || t.IsFrozen) c.RenderTransform = t = new TranslateTransform();
        t.BeginAnimation(TranslateTransform.XProperty, null);
        t.BeginAnimation(TranslateTransform.YProperty, null);
        t.X = pos.X - _grabOffset.X - slot.X;
        t.Y = pos.Y - _grabOffset.Y - slot.Y;
    }

    private void EndDragging()
    {
        _dragging = false;
        _containers.Clear(); // 끌기가 끝나면 기억한 컨테이너는 버린다 (지운 타일이 남지 않게)
        _autoScroll.Stop();
        _hover.Stop();
        var outside = _hoverTarget == OutsideFolderTarget;
        _hoverTarget = null;
        var surface = Surface;
        if (surface.IsMouseCaptured) surface.ReleaseMouseCapture();
        var tile = _dragTile;
        _dragTile = null;
        var container = tile is null ? null : ContainerOf(tile);
        if (outside) _vm?.DragOutOfFolder(); // 폴더 창 밖에 놓음 → 꺼내기
        _vm?.EndDrag();
        // 손을 떼면 제자리로 미끄러져 들어간다
        if (container is { RenderTransform: TranslateTransform t })
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

    /// <summary>화면(또는 폴더 창) 가장자리로 끌면 그쪽으로 넘어간다.</summary>
    private void OnAutoScroll(object? sender, EventArgs e)
    {
        if (!_dragging) { _autoScroll.Stop(); return; }
        var sv = SurfaceScroller;
        var p = Mouse.GetPosition(sv);
        var step = p.X < EdgeZone ? -18 : p.X > sv.ActualWidth - EdgeZone ? 18 : 0;
        if (step == 0 || p.Y < 0 || p.Y > sv.ActualHeight) return;
        sv.ScrollToHorizontalOffset(sv.HorizontalOffset + step);
        sv.UpdateLayout();
        UpdateDrag();
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
        if (!_panMoved && _vm is { IsEditing: true, Renaming: null, OpenFolder: null }) _vm.EndEdit();
    }
}
