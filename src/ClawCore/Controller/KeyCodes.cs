namespace ClawCore.Controller;

public enum KeyCategory { Gamepad, Keyboard, Mouse, Media, System }

public sealed record KeyCode(byte Code, string Id, string Label, KeyCategory Category);

/// <summary>
/// 시스템 동작 (SYS_): 펌웨어 키가 아니라 ClawHub가 직접 처리한다 (마이크 켜기/끄기 등).
/// 이런 키가 들어간 버튼은 고급 입력처럼 운반 키로 펌웨어에 쓰이고 ClawHub가 받아서 실행한다 — 게임 입력이 아니라 안티치트와 상관없다.
/// </summary>
public static class SystemKeys
{
    public const string Prefix = "SYS_";
    public static bool Is(string id) => id.StartsWith(Prefix, StringComparison.Ordinal);
}

/// <summary>컨트롤러 펌웨어 키 코드 (벤더 인덱스, HID usage 아님).</summary>
public static class KeyCodes
{
    public static readonly IReadOnlyList<KeyCode> All = Build();
    private static readonly Dictionary<byte, KeyCode> ByCode = All.Where(k => k.Category != KeyCategory.System).ToDictionary(k => k.Code);
    private static readonly Dictionary<string, KeyCode> ById = All.ToDictionary(k => k.Id, StringComparer.OrdinalIgnoreCase);

    public static KeyCode? Find(byte code) => ByCode.GetValueOrDefault(code);
    public static KeyCode? Find(string id) => ById.GetValueOrDefault(id);
    public static bool IsValidTarget(byte code) => ByCode.ContainsKey(code);

    public static string Describe(IEnumerable<byte> codes)
    {
        var labels = codes.Select(c => Find(c)?.Label ?? $"#{c}").ToList();
        return labels.Count == 0 ? "없음" : string.Join(" + ", labels);
    }

    private static List<KeyCode> Build()
    {
        var list = new List<KeyCode>();
        void Add(int code, string id, string label, KeyCategory cat) => list.Add(new((byte)code, id, label, cat));
        void Kb(int code, string id, string label) => Add(code, "KB_" + id, label, KeyCategory.Keyboard);

        string[] pad = ["", "PAD_UP", "PAD_DOWN", "PAD_LEFT", "PAD_RIGHT", "PAD_LB", "PAD_RB", "PAD_L3", "PAD_R3",
            "PAD_A", "PAD_B", "PAD_X", "PAD_Y", "PAD_HOME", "PAD_VIEW", "PAD_MENU", "PAD_CAMERA", "PAD_M1", "PAD_M2", "PAD_LT", "PAD_RT"];
        string[] padLabel = ["", "패드 ↑", "패드 ↓", "패드 ←", "패드 →", "LB", "RB", "L3", "R3",
            "패드 A", "패드 B", "패드 X", "패드 Y", "Home", "View", "Menu", "Camera", "M1", "M2", "LT", "RT"];
        for (var i = 1; i < pad.Length; i++) Add(i, pad[i], padLabel[i], KeyCategory.Gamepad);

        Kb(50, "ESC", "Esc");
        for (var i = 1; i <= 12; i++) Kb(50 + i, $"F{i}", $"F{i}");
        Kb(63, "GRAVE", "`");
        for (var i = 1; i <= 9; i++) Kb(63 + i, $"{i}", $"{i}");
        Kb(73, "0", "0");
        Kb(74, "MINUS", "-"); Kb(75, "EQUAL", "="); Kb(76, "BACKSPACE", "Backspace"); Kb(77, "TAB", "Tab");
        var row1 = "QWERTYUIOP";
        for (var i = 0; i < row1.Length; i++) Kb(78 + i, row1[i].ToString(), row1[i].ToString());
        Kb(88, "LBRACKET", "["); Kb(89, "RBRACKET", "]"); Kb(90, "BACKSLASH", "\\"); Kb(91, "CAPSLOCK", "Caps Lock");
        var row2 = "ASDFGHJKL";
        for (var i = 0; i < row2.Length; i++) Kb(92 + i, row2[i].ToString(), row2[i].ToString());
        Kb(101, "SEMICOLON", ";"); Kb(102, "QUOTE", "'"); Kb(103, "ENTER", "Enter"); Kb(104, "LSHIFT", "Shift");
        var row3 = "ZXCVBNM";
        for (var i = 0; i < row3.Length; i++) Kb(105 + i, row3[i].ToString(), row3[i].ToString());
        Kb(112, "COMMA", ","); Kb(113, "PERIOD", "."); Kb(114, "SLASH", "/"); Kb(115, "RSHIFT", "R Shift");
        Kb(116, "LCTRL", "Ctrl"); Kb(117, "WIN", "Win"); Kb(118, "LALT", "Alt"); Kb(119, "SPACE", "Space");
        Kb(120, "RALT", "R Alt (한/영)"); Kb(121, "RCTRL", "R Ctrl (한자)");
        Kb(122, "INSERT", "Insert"); Kb(123, "HOME", "Home"); Kb(124, "PAGEUP", "Page Up");
        Kb(125, "DELETE", "Delete"); Kb(126, "END", "End"); Kb(127, "PAGEDOWN", "Page Down");
        Kb(128, "UP", "↑"); Kb(129, "DOWN", "↓"); Kb(130, "LEFT", "←"); Kb(131, "RIGHT", "→");
        Kb(132, "NUMLOCK", "Num Lock"); Kb(133, "NUM_DIV", "Num /"); Kb(134, "NUM_MUL", "Num *");
        Kb(135, "NUM_SUB", "Num -"); Kb(136, "NUM_ADD", "Num +"); Kb(137, "NUM_DOT", "Num ."); Kb(138, "NUM_ENTER", "Num Enter");
        for (var i = 0; i <= 9; i++) Kb(139 + i, $"NUM{i}", $"Num {i}");
        Kb(156, "F23", "F23"); Kb(157, "F22", "F22");

        Add(149, "MD_PLAY", "재생/일시정지", KeyCategory.Media); Add(150, "MD_STOP", "정지", KeyCategory.Media);
        Add(151, "MD_NEXT", "다음 트랙", KeyCategory.Media); Add(152, "MD_PREV", "이전 트랙", KeyCategory.Media);
        Add(153, "MD_VOLUP", "볼륨 +", KeyCategory.Media); Add(154, "MD_VOLDOWN", "볼륨 -", KeyCategory.Media);
        Add(155, "MD_MUTE", "음소거", KeyCategory.Media);

        Add(200, "MS_LEFT", "마우스 왼쪽", KeyCategory.Mouse); Add(201, "MS_MIDDLE", "마우스 가운데", KeyCategory.Mouse);
        Add(202, "MS_RIGHT", "마우스 오른쪽", KeyCategory.Mouse); Add(203, "MS_B4", "마우스 뒤로", KeyCategory.Mouse);
        Add(204, "MS_B5", "마우스 앞으로", KeyCategory.Mouse); Add(205, "MS_WHEELUP", "휠 위", KeyCategory.Mouse);
        Add(206, "MS_WHEELDOWN", "휠 아래", KeyCategory.Mouse);

        // 시스템 동작: 펌웨어 코드 없음 (0) — ClawHub가 처리
        void Sys(string id, string label) => Add(0, SystemKeys.Prefix + id, label, KeyCategory.System);
        Sys("MIC", "마이크 켜기/끄기"); Sys("KEYBOARD", "터치 키보드"); Sys("HUB", "ClawHub 열기"); Sys("QUICK", "빠른 설정");
        Sys("SCREENSHOT", "스크린샷"); Sys("RECORD", "녹화 시작/중지"); Sys("CLIP", "방금 장면 저장");
        Sys("PROFILE", "다음 매핑 프로필"); Sys("BRIGHT_UP", "밝기 +"); Sys("BRIGHT_DOWN", "밝기 −");
        return list;
    }
}
