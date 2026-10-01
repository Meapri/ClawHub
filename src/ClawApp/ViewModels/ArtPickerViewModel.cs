using System.Collections.ObjectModel;
using System.Windows.Media;
using ClawApp.Input;
using ClawApp.Services;
using ClawCore.Games;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Microsoft.Win32;

namespace ClawApp.ViewModels;

/// <summary>검색 결과 이미지 하나 (미리보기는 따로 받아 온다).</summary>
public sealed partial class ArtCandidateViewModel(ArtCandidate candidate) : ObservableObject
{
    public ArtCandidate Candidate { get; } = candidate;
    public string Title => Candidate.Title;
    public string SourceLabel => Candidate.Source;

    [ObservableProperty] [NotifyPropertyChangedFor(nameof(HasThumb))] private ImageSource? _thumb;
    [ObservableProperty] private bool _isLoading = true;
    public bool HasThumb => Thumb is not null;
}

/// <summary>
/// 게임 아트(커버·배경·로고) 직접 고르기. Steam 스토어 검색 후보 여러 개 + SteamGridDB(API 키가 있으면),
/// 이미지 파일에서 선택, 기본으로 되돌리기. 고른 아트는 아트 캐시 폴더에 user-* 파일로 저장되고 자동 아트보다 우선한다.
/// 패드: 방향키로 후보 이동, A 적용, LB/RB 종류 바꾸기, X 파일에서 선택, Y 기본으로 되돌리기.
/// </summary>
public sealed partial class ArtPickerViewModel : ObservableObject, INavPage
{
    public sealed record KindItem(ArtKind Kind, string Label);

    public static readonly IReadOnlyList<KindItem> Kinds =
        [new(ArtKind.Cover, "커버"), new(ArtKind.Hero, "배경"), new(ArtKind.Logo, "로고")];

    private const int ThumbConcurrency = 4;

    private readonly ShellViewModel _shell;
    private CancellationTokenSource? _searchCts;
    private bool _searchedOnce;

    public GameTileViewModel Tile { get; }

    [ObservableProperty] private int _kindIndex;
    [ObservableProperty] private string _query;
    [ObservableProperty] private ObservableCollection<ArtCandidateViewModel> _candidates = [];
    [ObservableProperty] private bool _isSearching;
    [ObservableProperty] private bool _isApplying;
    [ObservableProperty] private string? _statusText;

    public ArtPickerViewModel(ShellViewModel shell, GameTileViewModel tile)
    {
        _shell = shell;
        Tile = tile;
        _query = tile.Game.SearchName ?? tile.Game.Title;
    }

    public string Title => $"아트 바꾸기 · {Tile.Title}";
    public IReadOnlyList<Hint> Hints =>
        [new("A", "적용"), new("B", "뒤로"), new("LB", "이전 종류"), new("RB", "다음 종류"), new("X", "파일에서"), new("Y", "기본으로")];

    public ArtKind Kind => Kinds[KindIndex].Kind;
    public string KindLabel => Kinds[KindIndex].Label;
    public bool IsCover { get => Kind == ArtKind.Cover; set { if (value) KindIndex = 0; } }
    public bool IsHero { get => Kind == ArtKind.Hero; set { if (value) KindIndex = 1; } }
    public bool IsLogo { get => Kind == ArtKind.Logo; set { if (value) KindIndex = 2; } }

    /// <summary>후보 타일 크기 (종류별 비율: 커버 2:3, 배경 약 3.1:1, 로고 2:1).</summary>
    public double TileWidth => Kind switch { ArtKind.Cover => 150, ArtKind.Hero => 330, _ => 240 };
    public double TileHeight => Kind switch { ArtKind.Cover => 225, ArtKind.Hero => 106, _ => 120 };

    /// <summary>지금 적용된 이 종류의 아트 (없으면 null).</summary>
    public ImageSource? CurrentImage => Kind switch
    {
        ArtKind.Cover => Tile.Cover,
        ArtKind.Hero => ArtCache.LoadImage(Tile.Game.HeroImage),
        _ => Tile.Logo,
    };
    public bool HasCurrentImage => CurrentImage is not null;
    public bool HasUserArt => _shell.Library.HasUserArt(Tile.Game, Kind);
    public bool IsEmpty => !IsSearching && Candidates.Count == 0;

    /// <summary>SteamGridDB API 키 (TextBox에서 벗어날 때 저장). steamgriddb.com → 프로필 → 환경설정 → API.</summary>
    public string SteamGridDbKey
    {
        get => _shell.Settings.SteamGridDbKey ?? "";
        set
        {
            var key = string.IsNullOrWhiteSpace(value) ? null : value.Trim();
            if (key == _shell.Settings.SteamGridDbKey) return;
            _shell.Settings.SteamGridDbKey = key;
            _shell.Settings.Save();
            OnPropertyChanged();
            OnPropertyChanged(nameof(HasSteamGridDb));
            _ = SearchAsync(); // 키를 넣으면 바로 SteamGridDB 결과도
        }
    }

    public bool HasSteamGridDb => !string.IsNullOrWhiteSpace(_shell.Settings.SteamGridDbKey);

    partial void OnKindIndexChanged(int value)
    {
        foreach (var p in new[] { nameof(Kind), nameof(KindLabel), nameof(IsCover), nameof(IsHero), nameof(IsLogo),
                     nameof(TileWidth), nameof(TileHeight) })
            OnPropertyChanged(p);
        OnArtChanged();
        _ = SearchAsync(); // 종류마다 후보 이미지가 다르다
    }

    partial void OnIsSearchingChanged(bool value) => OnPropertyChanged(nameof(IsEmpty));
    partial void OnCandidatesChanged(ObservableCollection<ArtCandidateViewModel> value) => OnPropertyChanged(nameof(IsEmpty));

    private void OnArtChanged()
    {
        OnPropertyChanged(nameof(CurrentImage));
        OnPropertyChanged(nameof(HasCurrentImage));
        OnPropertyChanged(nameof(HasUserArt));
    }

    public void OnShown()
    {
        if (_searchedOnce) return;
        _searchedOnce = true;
        _ = SearchAsync();
    }

    public void OnHidden() => _searchCts?.Cancel();

    // ── 검색 ───────────────────────────────────────────────

    [RelayCommand]
    private Task Search() => SearchAsync();

    private async Task SearchAsync()
    {
        _searchCts?.Cancel();
        var cts = _searchCts = new CancellationTokenSource();
        var ct = cts.Token;
        var term = Query.Trim();
        if (term.Length == 0) { StatusText = "검색어를 입력하세요"; return; }

        var kind = Kind;
        var key = _shell.Settings.SteamGridDbKey;
        IsSearching = true;
        StatusText = $"'{term}' {KindLabel} 찾는 중…";
        Candidates = [];
        try
        {
            var steamTask = ArtSources.SearchSteamAsync(term, kind, ct);
            var sgdbTask = string.IsNullOrWhiteSpace(key)
                ? Task.FromResult<IReadOnlyList<ArtCandidate>>([])
                : ArtSources.SearchSteamGridDbAsync(term, kind, key, ct);

            var errors = new List<string>();
            async Task<IReadOnlyList<ArtCandidate>> Safe(Task<IReadOnlyList<ArtCandidate>> t, string source)
            {
                try { return await t; }
                catch (OperationCanceledException) when (ct.IsCancellationRequested) { throw; }
                catch (OperationCanceledException) { errors.Add($"{source} 응답 없음 (시간 초과)"); }
                catch (UnauthorizedAccessException ex) { errors.Add(ex.Message); }
                catch (Exception ex) { errors.Add($"{source} 검색 실패: {ex.Message}"); } // 한쪽이 실패해도 다른 쪽 결과는 보여 준다
                return [];
            }

            var sgdb = await Safe(sgdbTask, "SteamGridDB");
            var steam = await Safe(steamTask, "Steam");
            if (ct.IsCancellationRequested) return;

            // SteamGridDB(다양한 커뮤니티 아트)를 먼저, 그다음 Steam 공식 아트
            Candidates = new ObservableCollection<ArtCandidateViewModel>(sgdb.Concat(steam).Select(c => new ArtCandidateViewModel(c)));
            StatusText = errors.Count > 0 ? string.Join(" · ", errors) : $"'{term}' {KindLabel} 미리보기 받는 중…";
            _shell.RequestFocus();
            await LoadThumbsAsync(Candidates, ct);
            if (ct.IsCancellationRequested) return;
            OnPropertyChanged(nameof(IsEmpty));
            if (errors.Count == 0)
                StatusText = Candidates.Count == 0 ? "찾은 이미지가 없어요 — 검색어를 바꾸거나 이미지 파일에서 고르세요"
                    : $"{Candidates.Count}개 찾음{(HasSteamGridDb ? "" : " · SteamGridDB API 키를 넣으면 더 많은 아트")}";
        }
        catch (OperationCanceledException) { /* 새 검색으로 대체됨 / 페이지 떠남 */ }
        finally
        {
            if (_searchCts == cts) IsSearching = false;
        }
    }

    /// <summary>
    /// 미리보기를 동시에 몇 개씩 받는다. 받을 수 없는 후보(사운드트랙 등 해당 아트가 없는 Steam 항목)는 목록에서 뺀다.
    /// UI 스레드에서 호출 → await 뒤에도 UI 스레드라 컬렉션을 바로 고쳐도 된다.
    /// </summary>
    private static async Task LoadThumbsAsync(ObservableCollection<ArtCandidateViewModel> list, CancellationToken ct)
    {
        using var gate = new SemaphoreSlim(ThumbConcurrency);
        await Task.WhenAll(list.ToList().Select(async item =>
        {
            try
            {
                await gate.WaitAsync(ct);
                try
                {
                    var got = await ArtSources.DownloadImageAsync([item.Candidate.ThumbUrl], ct);
                    item.Thumb = got is { } g ? await Task.Run(() => ArtCache.FromBytes(g.Bytes), ct) : null;
                    if (item.Thumb is null && !ct.IsCancellationRequested) list.Remove(item);
                }
                finally
                {
                    item.IsLoading = false;
                    gate.Release();
                }
            }
            catch (OperationCanceledException) { /* 새 검색/페이지 떠남 */ }
        }));
    }

    // ── 적용 ───────────────────────────────────────────────

    [RelayCommand]
    private async Task Choose(ArtCandidateViewModel item)
    {
        if (IsApplying) return;
        IsApplying = true;
        var kind = Kind;
        StatusText = $"{item.Title} {KindLabel} 받는 중…";
        try
        {
            var got = await ArtSources.DownloadImageAsync(item.Candidate.Urls);
            if (got is not { } image)
            {
                StatusText = "이 이미지는 받을 수 없어요 — 다른 후보를 고르세요";
                return;
            }
            _shell.Library.SetUserArt(Tile.Game, kind, image.Bytes);
            AfterArtChanged($"{KindLabel} 이미지를 바꿨습니다");
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or InvalidDataException)
        {
            StatusText = $"저장 실패: {ex.Message}";
        }
        finally
        {
            IsApplying = false;
        }
    }

    [RelayCommand]
    private void PickFile()
    {
        var dlg = new OpenFileDialog
        {
            Title = $"{Tile.Title} {KindLabel} 이미지",
            Filter = "이미지 (*.png;*.jpg;*.jpeg;*.webp;*.bmp;*.gif)|*.png;*.jpg;*.jpeg;*.webp;*.bmp;*.gif",
        };
        if (dlg.ShowDialog() != true) return;
        try
        {
            _shell.Library.SetUserArtFromFile(Tile.Game, Kind, dlg.FileName);
            AfterArtChanged($"{KindLabel} 이미지를 바꿨습니다");
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or InvalidDataException)
        {
            StatusText = ex.Message;
        }
    }

    [RelayCommand]
    private void ResetToDefault()
    {
        if (!HasUserArt)
        {
            _shell.ShowToast($"이미 기본 {KindLabel}입니다");
            return;
        }
        _shell.Library.ResetArt(Tile.Game, Kind);
        AfterArtChanged($"{KindLabel} 이미지를 기본으로 되돌렸습니다");
    }

    private void AfterArtChanged(string message)
    {
        Tile.ReloadArt();
        _shell.RunningTile?.ReloadArt();
        _shell.Home.Reload();
        OnArtChanged();
        StatusText = message;
        _shell.ShowToast(message);
    }

    public bool Handle(NavAction action)
    {
        switch (action)
        {
            case NavAction.TabPrev: KindIndex = (KindIndex + Kinds.Count - 1) % Kinds.Count; break;
            case NavAction.TabNext: KindIndex = (KindIndex + 1) % Kinds.Count; break;
            case NavAction.Secondary: PickFile(); return true;
            case NavAction.Options: ResetToDefault(); return true;
            default: return false;
        }
        _shell.RequestFocus();
        return true;
    }
}
