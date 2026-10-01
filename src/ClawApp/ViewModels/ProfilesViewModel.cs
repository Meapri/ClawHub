using System.Collections.ObjectModel;
using ClawCore.Controller;
using ClawCore.Mapping;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;

namespace ClawApp.ViewModels;

public sealed partial class ProfilesViewModel(ShellViewModel shell) : ObservableObject, INavPage
{
    /// <summary>미리보기 한 칸: 버튼 → 키.</summary>
    public sealed record Pair(ClawButton Button, string Keys);

    public sealed record Row(MappingProfile Profile, string Summary, string UsedBy, bool InUse, IReadOnlyList<Pair> Preview);

    /// <summary>미리보기에 먼저 보여 줄 버튼 (자주 바꾸는 순).</summary>
    private static readonly ClawButton[] PreviewOrder =
        [ClawButton.A, ClawButton.B, ClawButton.X, ClawButton.Y, ClawButton.LB, ClawButton.RB, ClawButton.LT, ClawButton.RT, ClawButton.M1, ClawButton.M2];

    [ObservableProperty] private ObservableCollection<Row> _rows = [];

    public string Title => "매핑 프로필";
    public IReadOnlyList<Hint> Hints => [new("A", "편집"), new("B", "뒤로")];

    public void OnShown()
    {
        Rows = new ObservableCollection<Row>(shell.Profiles.All.Select(p =>
        {
            var games = shell.Library.Games.Where(g => g.MappingProfileId == p.Id).Select(g => g.Title).ToList();
            var preview = PreviewOrder
                .Where(b => p.Bindings.TryGetValue(b, out var l) && l.Count > 0)
                .Take(6)
                .Select(b => new Pair(b, string.Join("+", p.Bindings[b].Select(id => KeyCodes.Find(id)?.Label ?? id))))
                .ToList();
            return new Row(
                p,
                $"버튼 {p.Bindings.Count(kv => kv.Value.Count > 0) + p.Macros.Count(kv => kv.Value.Count > 0)}개 · {(p.Mode == GamepadMode.Desktop ? "데스크톱 모드" : "XInput 모드")}",
                games.Count > 0 ? string.Join(", ", games) : "사용하는 게임 없음",
                games.Count > 0,
                preview);
        }));
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
