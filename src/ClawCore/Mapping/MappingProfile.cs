using ClawCore.Controller;

namespace ClawCore.Mapping;

/// <summary>게임별 버튼 → 키 매핑. 바인딩 값은 펌웨어 키 코드 ID 목록 (예: ["KB_LCTRL", "KB_1"]).</summary>
public sealed class MappingProfile
{
    public string Id { get; set; } = Guid.NewGuid().ToString("N");
    public string Name { get; set; } = "새 프로필";
    /// <summary>게임 중 컨트롤러 모드. XInput에서 키 출력이 안 되는 펌웨어면 Desktop으로 바꾼다.</summary>
    public GamepadMode Mode { get; set; } = GamepadMode.XInput;
    public Dictionary<ClawButton, List<string>> Bindings { get; set; } = [];

    /// <summary>M1/M2 매크로 (있으면 그 버튼의 일반 바인딩보다 우선).</summary>
    public Dictionary<ClawButton, List<MacroStepDef>> Macros { get; set; } = [];

    public sealed class MacroStepDef
    {
        public string Key { get; set; } = "KB_1";
        public int HoldMs { get; set; } = 50;
        public int GapMs { get; set; } = 50;
    }

    public Dictionary<ClawButton, MacroStep[]> ToMacros() =>
        Macros.Where(kv => kv.Value.Count > 0 && kv.Key is ClawButton.M1 or ClawButton.M2).ToDictionary(
            kv => kv.Key,
            kv => kv.Value.Select(s => new MacroStep(
                KeyCodes.Find(s.Key)?.Code ?? throw new InvalidOperationException($"알 수 없는 키 {s.Key}"),
                (ushort)Math.Clamp(s.HoldMs, 10, 10000), (ushort)Math.Clamp(s.GapMs, 0, 10000))).ToArray());

    public IReadOnlyDictionary<ClawButton, byte[]> ToCodes() =>
        Bindings.Where(kv => kv.Value.Count > 0).ToDictionary(
            kv => kv.Key,
            kv => kv.Value.Select(id => KeyCodes.Find(id)?.Code
                ?? throw new InvalidOperationException($"알 수 없는 키 {id}")).ToArray());

    public MappingProfile Clone() => new()
    {
        Id = Guid.NewGuid().ToString("N"),
        Name = Name + " 사본",
        Mode = Mode,
        Bindings = Bindings.ToDictionary(kv => kv.Key, kv => kv.Value.ToList()),
        Macros = Macros.ToDictionary(kv => kv.Key, kv => kv.Value.Select(s => new MacroStepDef { Key = s.Key, HoldMs = s.HoldMs, GapMs = s.GapMs }).ToList()),
    };

}

/// <summary>
/// 처음부터 제공하는 템플릿. 스틱은 펌웨어상 키 매핑이 안 되므로 이동은 D-pad에 둔다.
/// 사용자는 복제해서 게임에 맞게 고친다.
/// </summary>
public static class MappingPresets
{
    public const string MapleStoryId = "maplestory-default";

    public static IReadOnlyList<MappingProfile> All =>
    [
        Make("preset-arrows", "키보드 · 방향키형 (횡스크롤·RPG)", new()
        {
            [ClawButton.DpadUp] = ["KB_UP"], [ClawButton.DpadDown] = ["KB_DOWN"],
            [ClawButton.DpadLeft] = ["KB_LEFT"], [ClawButton.DpadRight] = ["KB_RIGHT"],
            [ClawButton.A] = ["KB_SPACE"], [ClawButton.B] = ["KB_ESC"], [ClawButton.X] = ["KB_Z"], [ClawButton.Y] = ["KB_X"],
            [ClawButton.LB] = ["KB_Q"], [ClawButton.RB] = ["KB_E"], [ClawButton.LT] = ["KB_LSHIFT"], [ClawButton.RT] = ["KB_LCTRL"],
            [ClawButton.M1] = ["KB_1"], [ClawButton.M2] = ["KB_2"],
        }),
        Make("preset-wasd", "키보드 · WASD형 (액션·생존)", new()
        {
            [ClawButton.DpadUp] = ["KB_W"], [ClawButton.DpadDown] = ["KB_S"],
            [ClawButton.DpadLeft] = ["KB_A"], [ClawButton.DpadRight] = ["KB_D"],
            [ClawButton.A] = ["KB_SPACE"], [ClawButton.B] = ["KB_ESC"], [ClawButton.X] = ["KB_E"], [ClawButton.Y] = ["KB_R"],
            [ClawButton.LB] = ["KB_Q"], [ClawButton.RB] = ["KB_F"], [ClawButton.LT] = ["KB_LCTRL"], [ClawButton.RT] = ["KB_LSHIFT"],
            [ClawButton.L3] = ["KB_TAB"], [ClawButton.R3] = ["KB_M"],
            [ClawButton.M1] = ["KB_1"], [ClawButton.M2] = ["KB_2"],
        }),
        Make("preset-mouse", "마우스 클릭형 (전략·클릭 게임)", new()
        {
            [ClawButton.RT] = ["MS_LEFT"], [ClawButton.LT] = ["MS_RIGHT"],
            [ClawButton.A] = ["KB_ENTER"], [ClawButton.B] = ["KB_ESC"],
            [ClawButton.RB] = ["MS_WHEELDOWN"], [ClawButton.LB] = ["MS_WHEELUP"],
            [ClawButton.DpadUp] = ["KB_UP"], [ClawButton.DpadDown] = ["KB_DOWN"],
            [ClawButton.DpadLeft] = ["KB_LEFT"], [ClawButton.DpadRight] = ["KB_RIGHT"],
        }, GamepadMode.Desktop), // 데스크톱 모드: 스틱이 마우스 커서
        // 메이플스토리 기본 키 설정: Alt 점프, Ctrl 공격, Z 줍기
        Make(MapleStoryId, "메이플스토리", new()
        {
            [ClawButton.DpadUp] = ["KB_UP"], [ClawButton.DpadDown] = ["KB_DOWN"],
            [ClawButton.DpadLeft] = ["KB_LEFT"], [ClawButton.DpadRight] = ["KB_RIGHT"],
            [ClawButton.A] = ["KB_LALT"], [ClawButton.X] = ["KB_LCTRL"], [ClawButton.B] = ["KB_Z"], [ClawButton.Y] = ["KB_LSHIFT"],
            [ClawButton.LB] = ["KB_INSERT"], [ClawButton.RB] = ["KB_DELETE"], [ClawButton.LT] = ["KB_HOME"], [ClawButton.RT] = ["KB_END"],
            [ClawButton.L3] = ["KB_PAGEUP"], [ClawButton.R3] = ["KB_PAGEDOWN"],
            [ClawButton.M1] = ["KB_1"], [ClawButton.M2] = ["KB_2"],
        }),
    ];

    private static MappingProfile Make(string id, string name, Dictionary<ClawButton, List<string>> bindings,
        GamepadMode mode = GamepadMode.XInput) => new() { Id = id, Name = name, Bindings = bindings, Mode = mode };
}
