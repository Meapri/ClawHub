using ClawCore.Games;

namespace ClawApp.Services;

/// <summary>
/// 시작을 빠르게: UI 스레드가 첫 창(XAML)을 만드는 동안 뒤에서 미리 해 둔다.
///  · 라이브러리 검색 (레지스트리·런처 매니페스트·아트 파일 확인, 처음 실행 ~100ms, 디스크가 식어 있으면 ~1.3s) — ShellViewModel이 결과를 그대로 받는다.
///  · 배경 사진 디코딩 (화면 전체 크기, 10MB PNG면 ~230ms)과 사진 강조색 (~120ms), 설정의 미리보기 (~120ms) — ArtCache·AccentFromImage 캐시로 이어진다.
///  · 시작 화면에 고정된 게임의 타일 아트 (한 장 2–5ms) — ArtCache로 이어진다.
/// UI 스레드가 아직 디코딩 중인 그림을 찾으면 같은 작업을 다시 하지 않고 끝나기를 기다린다. 실패는 조용히 무시 (그때는 UI 스레드가 직접 읽는다).
/// 설정 파일은 한 번만 읽고, 그 읽기는 <see cref="TakeLibrary"/>가 돌아가기 전에 끝난다 — UI 스레드가 설정을 저장(임시 파일 → 바꿔치기)할 때
/// 이쪽이 파일을 열고 있으면 바꿔치기가 실패하므로 겹치지 않게 한다.
/// </summary>
public static class StartupPrewarm
{
    private static Task<GameLibrary>? _library;

    /// <summary>App.OnStartup에서 (UI 스레드) 가능한 한 일찍.</summary>
    public static void Begin()
    {
        // 창의 DPI·테마는 UI 스레드에서 읽어 넘긴다 (ShellViewModel이 창을 띄우기 전에 쓰는 값과 같다)
        var fullScreenWidth = ArtCache.FullScreenWidth();
        var light = SystemTheme.IsLight;

        var settings = Task.Run(AppSettings.TryLoadShared);
        settings.ContinueWith(t => PrewarmPicture(t.Result?.Home, fullScreenWidth, light), CancellationToken.None,
            TaskContinuationOptions.OnlyOnRanToCompletion, TaskScheduler.Default);

        _library = Task.Run(() =>
        {
            var lib = new GameLibrary();
            lib.Refresh();
            var start = settings.GetAwaiter().GetResult()?.Start; // 설정 읽기가 끝난 뒤에야 UI 스레드가 라이브러리를 받는다
            _ = Task.Run(() => PrewarmTiles(lib, start));
            return lib;
        });
    }

    /// <summary>미리 검색해 둔 라이브러리 (아직이면 끝날 때까지 기다린다). Begin을 안 불렀으면 지금 검색한다.</summary>
    public static GameLibrary TakeLibrary()
    {
        var task = Interlocked.Exchange(ref _library, null);
        if (task is not null) return task.GetAwaiter().GetResult();
        var lib = new GameLibrary();
        lib.Refresh();
        return lib;
    }

    /// <summary>배경 사진: 화면 전체 디코딩과 강조색은 UI 스레드가 바로 찾으므로 나란히, 설정 미리보기는 그 뒤에.</summary>
    private static void PrewarmPicture(HomeSettings? home, int fullScreenWidth, bool light)
    {
        if (home is not { BackdropMode: BackdropMode.Image or BackdropMode.TilePicture, BackdropImage: { } picture }) return;
        if (home.AccentFromImage) _ = Task.Run(() => AccentFromImage.FromFile(picture, light));
        try
        {
            ArtCache.Prewarm([], picture, fullScreenWidth);
            ArtCache.Prewarm([picture], null, 0); // 설정 → 홈 화면의 미리보기 (600px) — 설정을 열 때 멈칫하지 않게
        }
        catch (Exception) { /* UI 스레드가 직접 읽는다 */ }
    }

    /// <summary>시작 화면에 보일 게임(고정 타일·폴더 안·"이어서 플레이")의 커버·배경·로고.</summary>
    private static void PrewarmTiles(GameLibrary lib, StartLayout? start)
    {
        try
        {
            var ids = (start?.Groups ?? []).SelectMany(g => g.Tiles).SelectMany(t => t.Children.Prepend(t))
                .Where(t => t.Kind == StartTileKind.Game && t.Ref is not null).Select(t => t.Ref!).ToHashSet();
            var hero = lib.Recent.Concat(lib.Visible.Where(g => g.LastPlayed is null)).FirstOrDefault(g => !g.IsApp);
            var games = lib.Visible.Where(g => ids.Contains(g.Id) || g == hero);
            ArtCache.Prewarm(games.SelectMany(g => new[] { g.HeroImage, g.CoverImage, g.LogoImage }), null, 0);
        }
        catch (Exception) { /* UI 스레드가 직접 읽는다 */ }
    }
}
