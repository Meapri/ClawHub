using System.Windows.Controls;
using ClawApp.ViewModels;

namespace ClawApp.Views.Sections;

public partial class SoundSection : UserControl
{
    public SoundSection()
    {
        InitializeComponent();
        // 보일 때마다 다시 읽는다 (장치를 꽂거나 앱이 소리를 내기 시작했을 수 있다)
        IsVisibleChanged += (_, e) => { if (e.NewValue is true && DataContext is SoundViewModel vm) _ = vm.RefreshAsync(); };
    }
}
