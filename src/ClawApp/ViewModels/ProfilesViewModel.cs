using System.Collections.ObjectModel;
using ClawCore.Mapping;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;

namespace ClawApp.ViewModels;

public sealed partial class ProfilesViewModel(ShellViewModel shell) : ObservableObject, INavPage
{
    public sealed record Row(MappingProfile Profile, string Summary, string UsedBy);

    [ObservableProperty] private ObservableCollection<Row> _rows = [];

    public string Title => "매핑 프로필";
    public IReadOnlyList<Hint> Hints => [new("A", "편집"), new("B", "뒤로")];

    public void OnShown()
    {
        Rows = new ObservableCollection<Row>(shell.Profiles.All.Select(p => new Row(
            p,
            $"{p.Bindings.Count}개 버튼 매핑 · {(p.Mode == ClawCore.Controller.GamepadMode.Desktop ? "데스크톱 모드" : "XInput 모드")}",
            string.Join(", ", shell.Library.Games.Where(g => g.MappingProfileId == p.Id).Select(g => g.Title)) is { Length: > 0 } s ? s : "사용하는 게임 없음")));
    }

    [RelayCommand] private void Edit(Row row) => shell.Navigate(new MappingEditorViewModel(shell, row.Profile));

    [RelayCommand]
    private void New()
    {
        var profile = new MappingProfile { Name = $"프로필 {shell.Profiles.All.Count + 1}" };
        shell.Profiles.Upsert(profile);
        shell.Navigate(new MappingEditorViewModel(shell, profile));
    }
}
