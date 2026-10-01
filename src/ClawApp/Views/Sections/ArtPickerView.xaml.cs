using ClawApp.ViewModels;

namespace ClawApp.Views.Sections;

public partial class ArtPickerView
{
    public ArtPickerView() => InitializeComponent();

    /// <summary>검색어 입력을 마치면 바로 찾는다 (검색 버튼까지 이동하지 않아도).</summary>
    private void Query_Committed(object? sender, EventArgs e)
    {
        if (DataContext is ArtPickerViewModel vm && vm.SearchCommand.CanExecute(null)) vm.SearchCommand.Execute(null);
    }
}
