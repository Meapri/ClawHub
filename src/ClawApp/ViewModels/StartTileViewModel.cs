using System.Windows;
using System.Windows.Media;
using ClawApp.Services;
using ClawApp.Views;
using CommunityToolkit.Mvvm.ComponentModel;

namespace ClawApp.ViewModels;

/// <summary>폴더 타일 미리보기 한 칸 (안에 든 타일의 그림 또는 아이콘).</summary>
public sealed record FolderPreview(ImageSource? Image, string? Glyph, bool Glass);

/// <summary>시작 화면 타일 하나. 같은 <see cref="StartTileDef"/>에 대해 계속 재사용된다 (위치 애니메이션·포커스 유지).</summary>
public sealed partial class StartTileViewModel : ObservableObject, IStartCell
{
    private readonly ShellViewModel _shell;

    public StartTileViewModel(ShellViewModel shell, StartTileDef def)
    {
        _shell = shell;
        Def = def;
    }

    public StartTileDef Def { get; }

    public StartTileKind Kind => Def.Kind;
    public bool IsGame => Kind == StartTileKind.Game && Game is { Game.IsApp: false };
    public bool IsApp => Kind == StartTileKind.Game && Game is { Game.IsApp: true };
    public bool IsContinue => Kind == StartTileKind.Continue;
    public bool IsAction => Kind == StartTileKind.Action;
    public bool IsFolder => Kind == StartTileKind.Folder;

    /// <summary>게임·앱 타일 (이어서 플레이는 가장 최근 게임).</summary>
    [ObservableProperty] private GameTileViewModel? _game;

    public StartActions.Info? Action => StartActions.Find(Def.Ref);

    public string Title => Kind switch
    {
        StartTileKind.Folder => Def.Title ?? "폴더",
        StartTileKind.Action => Action?.Label ?? "",
        StartTileKind.Continue => Game?.Title ?? "이어서 플레이",
        _ => Game?.Title ?? "",
    };

    /// <summary>기능·폴더 타일 아이콘 (Lucide 글리프).</summary>
    public string Glyph => Kind switch
    {
        StartTileKind.Folder => Res("Icon.Folder"),
        StartTileKind.Action => Res("Icon." + (Action?.Icon ?? "Square")),
        StartTileKind.Continue => Res("Icon.Play"),
        _ => "",
    };

    /// <summary>유리(반투명) 타일인지 — 아니면 강조색 타일.</summary>
    public bool IsGlass => Kind switch
    {
        StartTileKind.Action => Action?.Glass ?? false,
        StartTileKind.Folder or StartTileKind.Game => true,
        _ => false,
    };

    /// <summary>타일을 채우는 그림 (게임: 배경 아트 → 커버. 정사각형으로 가운데를 잘라 쓴다).</summary>
    public ImageSource? Art => Game is { } g && !IsApp ? g.Hero ?? g.Cover : null;
    public bool HasArt => Art is not null;
    public bool ShowLogo => Size != StartTileSize.Small && Game is { HasLogo: true } && HasArt;
    public bool ShowLabel => Size != StartTileSize.Small && !ShowLogo;

    // ── 크기 · 위치 (IStartCell) ───────────────────────────
    public StartTileSize Size => Def.Size;
    public int Span => Size switch { StartTileSize.Small => 1, StartTileSize.Large => 4, _ => 2 };
    public double Pixels => Span * StartPanel.Unit - StartPanel.Gap;
    public bool IsSmall => Size == StartTileSize.Small;
    public bool IsLarge => Size == StartTileSize.Large;
    public int Col { get; set; }
    public int Row { get; set; }

    // ── 편집 상태 ─────────────────────────────────────────
    [ObservableProperty] private bool _isEditing;
    [ObservableProperty] private bool _isSelected;
    /// <summary>패드로 잡아서 옮기는 중.</summary>
    [ObservableProperty] private bool _isGrabbed;
    [ObservableProperty] private bool _isDragging;
    /// <summary>끌고 있는 타일을 이 위에 놓으면 폴더가 된다는 표시.</summary>
    [ObservableProperty] private bool _isMergeTarget;
    /// <summary>열린 폴더 안의 타일 (폴더 뒤에 이어서 보인다).</summary>
    [ObservableProperty] private bool _isInOpenFolder;
    [ObservableProperty] private bool _isFolderOpen;

    /// <summary>편집 중인데 고르지 않은 타일 — 흐리고 작게 (W10M).</summary>
    public bool IsDimmed => IsEditing && !IsSelected && !IsDragging;

    partial void OnIsEditingChanged(bool value) => OnPropertyChanged(nameof(IsDimmed));
    partial void OnIsSelectedChanged(bool value) => OnPropertyChanged(nameof(IsDimmed));
    partial void OnIsDraggingChanged(bool value) => OnPropertyChanged(nameof(IsDimmed));

    /// <summary>폴더 미리보기: 안에 든 타일 최대 4개 (보통) 또는 9개 (크게).</summary>
    [ObservableProperty] private IReadOnlyList<FolderPreview> _previews = [];
    public int PreviewColumns => IsLarge ? 3 : 2;
    /// <summary>미리보기 한 칸 크기 (타일 안 여백·이름 줄을 빼고 정사각형으로).</summary>
    public double PreviewCell => IsLarge ? 92 : 54;

    /// <summary>정의(크기·이름·안의 타일)나 라이브러리가 바뀌었을 때. continueGame = 이어서 플레이에 보일 게임.</summary>
    public void Refresh(GameTileViewModel? continueGame)
    {
        Game = Kind switch
        {
            StartTileKind.Game => _shell.Library.Games.FirstOrDefault(g => g.Id == Def.Ref) is { Hidden: false } e
                ? (Game?.Game == e ? Game : new GameTileViewModel(_shell, e)) : null,
            StartTileKind.Continue => continueGame,
            _ => null,
        };
        if (IsFolder)
        {
            Previews = [.. Def.Children.Take(IsLarge ? 9 : 4).Select(c =>
            {
                var game = c.Kind == StartTileKind.Game ? _shell.Library.Games.FirstOrDefault(g => g.Id == c.Ref) : null;
                var action = c.Kind == StartTileKind.Action ? StartActions.Find(c.Ref) : null;
                ImageSource? img = game is null ? null
                    : game.IsApp ? ArtCache.LoadIcon(game.IconSource ?? game.LaunchTarget)
                    : ArtCache.LoadImage(game.HeroImage) ?? ArtCache.LoadImage(game.CoverImage) ?? ArtCache.LoadIcon(game.IconSource);
                return new FolderPreview(img, action is null ? (img is null ? Res("Icon.Square") : null) : Res("Icon." + action.Icon), action?.Glass ?? true);
            })];
        }
        OnPropertyChanged(string.Empty);
    }

    /// <summary>표시할 수 있는지 (라이브러리에서 지웠거나 숨긴 게임은 타일이 남아도 보이지 않음).</summary>
    public bool IsVisible => Kind != StartTileKind.Game || Game is not null;

    private static string Res(string key) => Application.Current?.TryFindResource(key) as string ?? "";
}
