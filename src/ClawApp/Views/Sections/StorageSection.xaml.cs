using System.Windows.Controls;
using ClawApp.ViewModels;

namespace ClawApp.Views.Sections;

public partial class StorageSection : UserControl
{
    public StorageSection()
    {
        InitializeComponent();
        // 보일 때마다 드라이브를 다시 읽는다 (게임 크기·기기 정보는 한 번만)
        IsVisibleChanged += (_, e) =>
        {
            if (e.NewValue is not true || DataContext is not SystemInfoViewModel vm) return;
            _ = vm.RefreshStorageAsync();
            _ = vm.RefreshDetailsAsync();
        };
    }
}
