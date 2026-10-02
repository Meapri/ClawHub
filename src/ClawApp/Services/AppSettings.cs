using ClawCore;
using Microsoft.Win32;

namespace ClawApp.Services;

public enum StartupMode { Auto, Shell, Companion }

/// <summary>"데스크톱으로" 중 오른쪽 본체 버튼이 할 일.</summary>
public enum DesktopRightButton { TouchKeyboard, GameBar }

/// <summary>"데스크톱으로"·앱 실행 중 컨트롤러: 스틱이 마우스(Desktop 모드) 또는 게임패드 그대로.</summary>
public enum DesktopController { Mouse, Gamepad }

/// <summary>라이브러리 정렬.</summary>
public enum LibrarySort { Recent, Name, Added }

/// <summary>
/// 전체 화면 배경: 고른 게임의 아트(포커스를 따라 바뀜) · 전체 화면 이미지 · 단색 ·
/// 타일 그림(Windows 10 Mobile처럼 바탕은 단색, 사진은 타일 안에서만 보임).
/// </summary>
public enum BackdropMode { GameArt, Image, Plain, TilePicture }

public sealed class HomeSettings
{
    public BackdropMode BackdropMode { get; set; } = BackdropMode.GameArt;

    /// <summary>고정 이미지 경로 (게임 아트 또는 사용자 데이터 폴더에 복사한 파일).</summary>
    public string? BackdropImage { get; set; }

    /// <summary>배경 흐림 (0 = 선명, 80 = 아주 흐림).</summary>
    public int BackdropBlur { get; set; } = 56;

    /// <summary>배경을 덮는 테마색 층의 진하기 % (높을수록 글자가 잘 보이고 그림은 흐려진다).</summary>
    public int BackdropDim { get; set; } = 72;

    /// <summary>타일 투명도 % (0 = 불투명한 타일 색, 100 = 색 없이 배경·타일 그림만).</summary>
    public int TileTransparency { get; set; }

    /// <summary>배경 사진에서 강조색을 뽑아 쓴다 (전체 화면 이미지·타일 그림일 때).</summary>
    public bool AccentFromImage { get; set; }
}

public sealed class AppSettings
{
    private static string FilePath => Path.Combine(JsonStore.UserDir, "settings.json");
    private const string RunKey = @"Software\Microsoft\Windows\CurrentVersion\Run";

    /// <summary>셸이 보일 때 컨트롤러를 XInput 모드로, "데스크톱으로" 갈 때 Desktop 모드로 자동 전환.</summary>
    public bool AutoControllerMode { get; set; } = true;

    /// <summary>기본 FPS 제한 (null = ClawHub가 관리 안 함, 0 = 끔). 게임별 값이 있으면 게임 중엔 그게 우선.</summary>
    public int? FpsLimit { get; set; }

    /// <summary>배터리로 돌 때의 기본 FPS 제한 (null = 배터리여도 <see cref="FpsLimit"/>, 0 = 끔). 게임별 값이 있으면 그게 우선.</summary>
    public int? FpsLimitOnBattery { get; set; }

    /// <summary>
    /// 시작 방식. Companion = Windows Xbox 모드(전체 화면 환경)처럼 다른 홈 화면이 있을 때
    /// ClawHub는 숨어서 버튼/자동 매핑만 담당한다.
    /// </summary>
    public StartupMode StartupMode { get; set; } = StartupMode.Auto;

    /// <summary>데스크톱 모드(스틱 = 마우스)에서 오른쪽 버튼: 터치 키보드 토글(기본) 또는 Game Bar.</summary>
    public DesktopRightButton DesktopRightButton { get; set; } = DesktopRightButton.TouchKeyboard;

    /// <summary>"데스크톱으로"·앱 실행 때 컨트롤러 (기본: 스틱 = 마우스).</summary>
    public DesktopController DesktopController { get; set; } = DesktopController.Mouse;

    /// <summary>
    /// 게임 중(런처가 숨어 있을 때) 화면 꺼짐·절전 방지. 패드 입력은 Windows 유휴 타이머를 항상 되돌리지는 않아서
    /// 게임 도중 화면이 꺼지는 것을 막는다. 게임이 끝나거나 런처로 돌아오면 풀린다.
    /// </summary>
    public bool KeepAwakeWhileGaming { get; set; } = true;

    /// <summary>SteamGridDB API 키 (아트 고르기에서 사용, 없으면 Steam 검색만).</summary>
    public string? SteamGridDbKey { get; set; }

    /// <summary>라이브러리 정렬 (최근 플레이순·이름순·최근 추가순). 마지막으로 고른 것을 기억한다.</summary>
    public LibrarySort LibrarySort { get; set; } = LibrarySort.Recent;

    /// <summary>홈 화면 꾸미기: 타일 크기·배경.</summary>
    public HomeSettings Home { get; set; } = new();

    /// <summary>시작 화면 타일 배치 (고정·순서·크기·폴더).</summary>
    public StartLayout Start { get; set; } = new();

    public const string XboxAppId =@"shell:AppsFolder\Microsoft.GamingApp_8wekyb3d8bbwe!Microsoft.Xbox.App";

    /// <summary>Auto일 때: 시작 시점에 Xbox 앱이 이미 떠 있으면 Xbox 모드로 부팅한 것으로 본다.</summary>
    public bool ResolveCompanion() => (App.StartupOverride ?? StartupMode) switch
    {
        StartupMode.Companion => true,
        StartupMode.Shell => false,
        _ => AnyXboxAppRunning(),
    };

    private static bool AnyXboxAppRunning()
    {
        var all = System.Diagnostics.Process.GetProcesses();
        try
        {
            return all.Where(p => p.ProcessName.Contains("Xbox", StringComparison.OrdinalIgnoreCase)).Any(IsXboxAppProcess);
        }
        finally
        {
            foreach (var p in all) p.Dispose(); // 프로세스 핸들(MainModule로 연 것)을 GC까지 들고 있지 않게
        }
    }

    private static bool IsXboxAppProcess(System.Diagnostics.Process p)
    {
        try { return p.MainModule?.FileName.Contains(@"\Microsoft.GamingApp_", StringComparison.OrdinalIgnoreCase) == true; }
        catch (Exception) { return false; } // 접근 불가 프로세스
    }

    public static AppSettings Load() => JsonStore.Load<AppSettings>(FilePath);

    /// <summary>
    /// 뒤에서 미리 읽기용 (StartupPrewarm). 없거나 깨졌으면 null — <see cref="Load"/>와 달리 깨진 파일을 .broken으로 복사하지 않는다
    /// (그건 UI 스레드의 Load가 한다). 읽는 동안 이 파일을 저장(바꿔치기)하면 실패하므로 부르는 시점은 StartupPrewarm이 정한다.
    /// </summary>
    public static AppSettings? TryLoadShared()
    {
        try
        {
            if (!File.Exists(FilePath)) return null;
            using var stream = new FileStream(FilePath, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
            return System.Text.Json.JsonSerializer.Deserialize<AppSettings>(stream, JsonStore.Options);
        }
        catch (Exception) { return null; }
    }
    public void Save() => JsonStore.Save(FilePath, this);

    public static bool StartWithWindows
    {
        get
        {
            using var key = Registry.CurrentUser.OpenSubKey(RunKey);
            return key?.GetValue("ClawHub") is not null;
        }
        set
        {
            using var key = Registry.CurrentUser.CreateSubKey(RunKey);
            if (value) key.SetValue("ClawHub", $"\"{Environment.ProcessPath}\"");
            else key.DeleteValue("ClawHub", throwOnMissingValue: false);
        }
    }
}
