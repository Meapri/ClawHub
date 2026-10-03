using System.Windows.Controls;
using ClawApp.ViewModels;

namespace ClawApp.Views.Sections;

public partial class ConnectivitySection : UserControl
{
    public ConnectivitySection()
    {
        InitializeComponent();
        // 보일 때마다 다시 읽는다 (기기를 켜거나 네트워크가 바뀌었을 수 있다)
        IsVisibleChanged += (_, e) => { if (e.NewValue is true && DataContext is ConnectivityViewModel vm) _ = vm.RefreshAsync(); };
    }
}
