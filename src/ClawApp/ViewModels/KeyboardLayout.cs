using ClawCore.Controller;
using CommunityToolkit.Mvvm.ComponentModel;

namespace ClawApp.ViewModels;

/// <summary>
/// 키 선택 화면의 한 칸: 키캡(Key 있음) 또는 빈 자리(Key 없음, 간격용).
/// Width는 픽셀(1칸 = <see cref="KeyboardLayout.Unit"/>).
/// </summary>
public sealed partial class KeySlotViewModel(KeyCode? key, double units, string? cap = null, string? glyph = null, string? icon = null) : ObservableObject
{
    /// <summary>타일용 Lucide 아이콘 이름 (미디어 키). 글리프 문자열로 바꿔 둔다.</summary>
    public string? Icon { get; } = icon is null ? null : System.Windows.Application.Current?.TryFindResource("Icon." + icon) as string;
    public bool HasIcon => Icon is not null;
    public KeyCode? Key { get; } = key;
    public bool IsSpacer => Key is null;
    public double Width { get; } = units * KeyboardLayout.Unit - KeyboardLayout.Gap;
    /// <summary>키캡에 찍는 짧은 이름 (바인딩 표시는 KeyCode.Label).</summary>
    public string Cap { get; } = cap ?? KeyboardLayout.CapLabel(key);
    /// <summary>타일용 그림 (ButtonGlyph Key: "A"·"DpadUp"·"MouseLeft" 등). 없으면 글자 타일.</summary>
    public string? Glyph { get; } = glyph;
    public bool HasGlyph => Glyph is not null;

    [ObservableProperty] private bool _isSelected;
    /// <summary>이 프로필에서 이미 이 키를 쓰는 버튼들 (예: "A · X"). 없으면 null.</summary>
    [ObservableProperty] private string? _usedBy;
}

/// <summary>실제 키보드처럼 배치한 키 선택판 (메인 15칸 + 편집키 3칸 + 숫자패드 4칸).</summary>
public static class KeyboardLayout
{
    public const double Unit = 48;
    public const double Gap = 4;

    private static readonly Dictionary<string, string> Caps = new(StringComparer.OrdinalIgnoreCase)
    {
        ["KB_BACKSPACE"] = "⌫", ["KB_CAPSLOCK"] = "Caps", ["KB_ENTER"] = "Enter", ["KB_LSHIFT"] = "Shift", ["KB_RSHIFT"] = "Shift",
        ["KB_RALT"] = "한/영", ["KB_RCTRL"] = "한자", ["KB_PAGEUP"] = "PgUp", ["KB_PAGEDOWN"] = "PgDn", ["KB_INSERT"] = "Ins",
        ["KB_DELETE"] = "Del", ["KB_NUMLOCK"] = "Num", ["KB_NUM_DIV"] = "/", ["KB_NUM_MUL"] = "*", ["KB_NUM_SUB"] = "-",
        ["KB_NUM_ADD"] = "+", ["KB_NUM_DOT"] = ".", ["KB_NUM_ENTER"] = "Enter", ["KB_SPACE"] = "Space",
    };

    public static string CapLabel(KeyCode? key)
    {
        if (key is null) return "";
        if (Caps.TryGetValue(key.Id, out var cap)) return cap;
        if (key.Id.StartsWith("KB_NUM", StringComparison.Ordinal) && key.Id.Length == 7) return key.Id[^1..]; // KB_NUM7 → 7
        return key.Label;
    }

    private static KeySlotViewModel K(string id, double units = 1) =>
        new(KeyCodes.Find("KB_" + id) ?? throw new InvalidOperationException(id), units);

    private static KeySlotViewModel Space(double units) => new(null, units);

    private static IEnumerable<KeySlotViewModel> Ks(string ids) => ids.Split(' ').Select(id => K(id));

    /// <summary>줄마다 새 객체 (선택 표시가 화면마다 따로).</summary>
    public static List<List<KeySlotViewModel>> Build()
    {
        List<KeySlotViewModel>[] main =
        [
            [K("ESC"), Space(1), .. Ks("F1 F2 F3 F4"), Space(0.5), .. Ks("F5 F6 F7 F8"), Space(0.5), .. Ks("F9 F10 F11 F12")],
            [.. Ks("GRAVE 1 2 3 4 5 6 7 8 9 0 MINUS EQUAL"), K("BACKSPACE", 2)],
            [K("TAB", 1.5), .. Ks("Q W E R T Y U I O P LBRACKET RBRACKET"), K("BACKSLASH", 1.5)],
            [K("CAPSLOCK", 1.75), .. Ks("A S D F G H J K L SEMICOLON QUOTE"), K("ENTER", 2.25)],
            [K("LSHIFT", 2.25), .. Ks("Z X C V B N M COMMA PERIOD SLASH"), K("RSHIFT", 2.75)],
            [K("LCTRL", 1.5), K("WIN", 1.25), K("LALT", 1.25), K("SPACE", 7), K("RALT", 2), K("RCTRL", 2)],
        ];
        List<KeySlotViewModel>[] nav =
        [
            [Space(3)],
            [.. Ks("INSERT HOME PAGEUP")],
            [.. Ks("DELETE END PAGEDOWN")],
            [Space(3)],
            [Space(1), K("UP"), Space(1)],
            [.. Ks("LEFT DOWN RIGHT")],
        ];
        List<KeySlotViewModel>[] num =
        [
            [K("F22"), K("F23"), Space(2)],
            [.. Ks("NUMLOCK NUM_DIV NUM_MUL NUM_SUB")],
            [.. Ks("NUM7 NUM8 NUM9 NUM_ADD")],
            [.. Ks("NUM4 NUM5 NUM6"), Space(1)],
            [.. Ks("NUM1 NUM2 NUM3 NUM_ENTER")],
            [K("NUM0", 2), K("NUM_DOT"), Space(1)],
        ];
        return [.. Enumerable.Range(0, main.Length).Select(i => (List<KeySlotViewModel>)
            [.. main[i], Space(0.5), .. nav[i], Space(0.5), .. num[i]])];
    }

    /// <summary>마우스 탭 (Kenney 마우스 그림).</summary>
    public static List<KeySlotViewModel> Mouse() =>
    [
        Tile("MS_LEFT", "MouseLeft"), Tile("MS_RIGHT", "MouseRight"), Tile("MS_MIDDLE", "MouseMiddle"),
        Tile("MS_WHEELUP", "MouseWheelUp"), Tile("MS_WHEELDOWN", "MouseWheelDown"),
        Tile("MS_B4", "MouseBack"), Tile("MS_B5", "MouseForward"),
    ];

    /// <summary>게임패드 탭: 다른 패드 버튼처럼 동작 (예: 뒷면 M1 = A).</summary>
    public static List<KeySlotViewModel> Gamepad() =>
    [
        Tile("PAD_A", "A", "A 버튼"), Tile("PAD_B", "B", "B 버튼"), Tile("PAD_X", "X", "X 버튼"), Tile("PAD_Y", "Y", "Y 버튼"),
        Tile("PAD_UP", "DpadUp", "방향 ↑"), Tile("PAD_DOWN", "DpadDown", "방향 ↓"), Tile("PAD_LEFT", "DpadLeft", "방향 ←"), Tile("PAD_RIGHT", "DpadRight", "방향 →"),
        Tile("PAD_LB", "LB"), Tile("PAD_RB", "RB"), Tile("PAD_LT", "LT"), Tile("PAD_RT", "RT"),
        Tile("PAD_L3", "L3", "왼쪽 스틱 누름"), Tile("PAD_R3", "R3", "오른쪽 스틱 누름"),
        Tile("PAD_VIEW", "View", "보기"), Tile("PAD_MENU", "Menu", "메뉴"), Tile("PAD_HOME", "Guide", "Xbox 버튼"),
    ];

    private static readonly Dictionary<string, string> MediaIcons = new()
    {
        ["MD_PLAY"] = "Play", ["MD_STOP"] = "Square", ["MD_NEXT"] = "SkipForward", ["MD_PREV"] = "SkipBack",
        ["MD_VOLUP"] = "Volume2", ["MD_VOLDOWN"] = "Volume1", ["MD_MUTE"] = "VolumeX",
    };

    public static List<KeySlotViewModel> Media() =>
        [.. KeyCodes.All.Where(k => k.Category == KeyCategory.Media).Select(k => new KeySlotViewModel(k, 3, k.Label, icon: MediaIcons.GetValueOrDefault(k.Id)))];

    private static readonly Dictionary<string, string> SystemIcons = new()
    {
        ["SYS_MIC"] = "MicOff", ["SYS_KEYBOARD"] = "Keyboard", ["SYS_HUB"] = "House", ["SYS_QUICK"] = "SlidersHorizontal",
        ["SYS_SCREENSHOT"] = "Camera", ["SYS_RECORD"] = "CircleDot", ["SYS_CLIP"] = "History",
        ["SYS_PROFILE"] = "Gamepad2", ["SYS_BRIGHT_UP"] = "Sun", ["SYS_BRIGHT_DOWN"] = "SunDim",
    };

    /// <summary>시스템 탭: 마이크 켜기/끄기 등 ClawHub가 처리하는 동작.</summary>
    public static List<KeySlotViewModel> System() =>
        [.. KeyCodes.All.Where(k => k.Category == KeyCategory.System).Select(k => new KeySlotViewModel(k, 3, k.Label, icon: SystemIcons.GetValueOrDefault(k.Id)))];

    private static KeySlotViewModel Tile(string id, string glyph, string? cap = null)
    {
        var key = KeyCodes.Find(id) ?? throw new InvalidOperationException(id);
        return new(key, 3, cap ?? key.Label, glyph);
    }
}
