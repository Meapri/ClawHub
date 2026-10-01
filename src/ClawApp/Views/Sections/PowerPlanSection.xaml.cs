using System.Windows.Controls;
using ClawApp.ViewModels;

namespace ClawApp.Views.Sections;

public partial class PowerPlanSection : UserControl
{
    public PowerPlanSection()
    {
        InitializeComponent();
        // 화면이 보일 때마다 서비스가 캐시 없이 다시 읽게 (Windows 설정에서 바꾼 값 반영)
        Loaded += (_, _) => _ = (DataContext as PowerPlanViewModel)?.ReloadAsync();
    }
}
