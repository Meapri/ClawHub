using System.ComponentModel;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Threading;
using ClawApp.ViewModels;
using ClawCore.Ipc;

namespace ClawApp.Views.Sections;

public partial class PowerPlanRowView : UserControl, IBackHandler
{
    /// <summary>줄 앞 아이콘 글자 (Lucide). 예: IconText="{DynamicResource Icon.Monitor}".</summary>
    public static readonly DependencyProperty IconTextProperty =
        DependencyProperty.Register(nameof(IconText), typeof(string), typeof(PowerPlanRowView), new PropertyMetadata(""));

    public string IconText
    {
        get => (string)GetValue(IconTextProperty);
        set => SetValue(IconTextProperty, value);
    }

    private PowerPlanRow? _row;
    /// <summary>마지막으로 펼친 칸 (접힐 때 그 값 버튼으로 포커스를 돌려준다).</summary>
    private PowerSourceKind _lastSource;

    public PowerPlanRowView()
    {
        InitializeComponent();
        DataContextChanged += (_, _) =>
        {
            if (_row is not null) _row.PropertyChanged -= OnRowChanged;
            _row = DataContext as PowerPlanRow;
            if (_row is not null) _row.PropertyChanged += OnRowChanged;
        };
        // 다른 줄·다른 곳으로 포커스가 나가면 접는다 (펼친 채 남아 화면을 차지하지 않게)
        IsKeyboardFocusWithinChanged += (_, e) =>
        {
            if (e.NewValue is false && _row is { IsExpanded: true }) _row.Expanded = null;
        };
    }

    private void OnRowChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName != nameof(PowerPlanRow.Expanded) || _row is null) return;
        if (_row.Expanded is { } source)
        {
            _lastSource = source;
            // 칩이 그려진 뒤 지금 값 칩으로 (없으면 첫 칩)
            Dispatcher.BeginInvoke(FocusSelectedChip, DispatcherPriority.ContextIdle);
        }
        else if (Keyboard.FocusedElement is not DependencyObject f || f is UIElement { IsVisible: false } || Chips.IsAncestorOf(f))
        {
            // 칩 안에 있던 포커스(접히며 사라짐)는 누른 값 버튼으로 돌려준다
            var button = _lastSource == PowerSourceKind.Ac && AcButton.IsVisible ? AcButton : DcButton;
            Dispatcher.BeginInvoke(() => button.Focus(), DispatcherPriority.ContextIdle);
        }
    }

    private void FocusSelectedChip()
    {
        var chips = FindChips(Chips).ToList();
        (chips.FirstOrDefault(c => c.IsChecked == true) ?? chips.FirstOrDefault())?.Focus();
    }

    private static IEnumerable<ChoiceChip> FindChips(DependencyObject parent)
    {
        for (var i = 0; i < VisualTreeHelper.GetChildrenCount(parent); i++)
        {
            var c = VisualTreeHelper.GetChild(parent, i);
            if (c is ChoiceChip chip) yield return chip;
            else foreach (var inner in FindChips(c)) yield return inner;
        }
    }

    /// <summary>B: 펼친 칩을 접는다 (페이지 뒤로 가기 전에).</summary>
    public bool HandleBack()
    {
        if (_row is not { IsExpanded: true }) return false;
        _row.Expanded = null;
        return true;
    }
}
