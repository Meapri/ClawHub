using System.Text.Json.Serialization;
using ClawCore.Controller;

namespace ClawCore.Mapping;

/// <summary>원형 메뉴 한 칸: 이름 + 고르면 한 번 누르는 키.</summary>
public sealed class RadialSlice
{
    public string Label { get; set; } = "";
    public List<string> Keys { get; set; } = [];
}

/// <summary>
/// 버튼 하나의 고급 동작 (Steam Input식). "누를 때" 키는 프로필의 일반 바인딩(<see cref="MappingProfile.Bindings"/>)을 쓴다.
/// </summary>
public sealed class AdvancedButton
{
    /// <summary>길게 누르면 (비어 있으면 없음). 길게 누르는 동안 누르고 있는다.</summary>
    public List<string> Long { get; set; } = [];
    public int LongMs { get; set; } = 400;
    /// <summary>두 번 누르면 (비어 있으면 없음).</summary>
    public List<string> Double { get; set; } = [];
    public int DoubleMs { get; set; } = 250;
    /// <summary>누르고 있는 동안 반복 간격 ms (0 = 연사 끔).</summary>
    public int TurboMs { get; set; }
    /// <summary>누를 때마다 눌림/뗌을 바꾼다 (달리기 유지 등).</summary>
    public bool Toggle { get; set; }
    /// <summary>이 버튼을 누르고 있는 동안 다른 버튼이 "레이어 중" 키를 낸다 (Steam의 모드 전환).</summary>
    public bool LayerShift { get; set; }
    /// <summary>레이어 버튼을 누르고 있는 동안 이 버튼이 내는 키.</summary>
    public List<string> Layer { get; set; } = [];
    /// <summary>원형 메뉴 (null = 아님): 누르고 있으면 메뉴가 뜨고 스틱으로 고른 뒤 떼면 그 칸의 키. 칸 수 = 목록 길이 (4·6·8·12).</summary>
    public List<RadialSlice>? Radial { get; set; }
    /// <summary>원형 메뉴를 고르는 스틱. Auto = 여는 버튼 쪽 (L3·LB·LT·방향 버튼 → 왼쪽, 나머지 → 오른쪽).</summary>
    public RadialStick RadialStick { get; set; }

    /// <summary>예전 저장값 읽기용 (true였으면 왼쪽). 더는 쓰지 않는다.</summary>
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingDefault)]
    public bool RadialLeftStick
    {
        get => false;
        set { if (value) RadialStick = RadialStick.Left; }
    }

    /// <summary>이 버튼으로 연 원형 메뉴를 왼쪽 스틱으로 고르는지.</summary>
    public bool UsesLeftStick(ClawButton button) => RadialStick switch
    {
        RadialStick.Left => true,
        RadialStick.Right => false,
        _ => button is ClawButton.L3 or ClawButton.LB or ClawButton.LT
            or ClawButton.DpadUp or ClawButton.DpadDown or ClawButton.DpadLeft or ClawButton.DpadRight,
    };

    /// <summary>
    /// 고르는 스틱을 눌러 연 메뉴 (L3 + 왼쪽 스틱 등): 누른 채로 같은 스틱을 기울이기 어려우므로
    /// 한 번 누르면 열린 채 있고, 기울였다 놓으면 그 칸 실행 · 다시 누르면 취소.
    /// </summary>
    public bool RadialClickToOpen(ClawButton button) =>
        button == ClawButton.L3 && UsesLeftStick(button) || button == ClawButton.R3 && !UsesLeftStick(button);
    /// <summary>원형 메뉴 크기: 0 작게 · 1 보통 · 2 크게.</summary>
    public int RadialSize { get; set; } = 1;
    /// <summary>고른 칸이 바뀔 때 짧은 진동.</summary>
    public bool RadialHaptics { get; set; } = true;

    /// <summary>소프트웨어 처리가 필요한지 (아니면 펌웨어가 그대로 처리).</summary>
    [JsonIgnore]
    public bool NeedsEngine => Long.Count > 0 || Double.Count > 0 || TurboMs > 0 || Toggle || LayerShift || Layer.Count > 0 || Radial is not null;

    public AdvancedButton Clone() => new()
    {
        Long = [.. Long], LongMs = LongMs, Double = [.. Double], DoubleMs = DoubleMs, TurboMs = TurboMs, Toggle = Toggle,
        LayerShift = LayerShift, Layer = [.. Layer],
        Radial = Radial?.Select(s => new RadialSlice { Label = s.Label, Keys = [.. s.Keys] }).ToList(),
        RadialStick = RadialStick, RadialSize = RadialSize, RadialHaptics = RadialHaptics,
    };
}

/// <summary>
/// 고급 입력 (2단계): 길게·두 번 누르기, 연사, 토글, 레이어, 원형 메뉴. 드라이버 없이 동작한다:
/// 펌웨어가 그 버튼을 "F23 + 글자"(거의 안 쓰는 키 조합 = 운반 키)로 보내면 ClawHub가 저수준 키보드 훅에서 먼저 가로채고
/// 동작에 맞는 키보드·마우스 입력을 소프트웨어로 낸다 (SendInput — 일부 안티치트가 싫어할 수 있어 프로필마다 켠다).
/// 고급 동작이 없는 버튼은 켜져 있어도 펌웨어 매핑 그대로 (지연·안티치트 영향 없음).
/// </summary>
/// <summary>가상 패드에서 스틱 하나를 무엇으로 낼지.</summary>
public enum StickOutput { Stick, Wasd, Arrows, Mouse }

/// <summary>원형 메뉴를 고르는 스틱 (Auto = 여는 버튼 쪽).</summary>
public enum RadialStick { Auto, Left, Right }

public sealed class AdvancedInput
{
    public bool Enabled { get; set; }
    public Dictionary<ClawButton, AdvancedButton> Buttons { get; set; } = [];

    /// <summary>
    /// 3단계 가상 패드: 실제 패드를 게임에서 숨기고(HidHide) ClawHub가 가공한 가상 Xbox 패드(ViGEm)를 대신 보여 준다.
    /// 그러면 고급 동작에서도 게임패드 버튼을 낼 수 있고, 스틱을 키·마우스로 바꿀 수 있다. 드라이버가 있어야 한다.
    /// </summary>
    public bool VirtualPad { get; set; }
    public StickOutput LeftStick { get; set; }
    public StickOutput RightStick { get; set; }
    /// <summary>스틱 → 마우스 속도 (1–100, 기본 40).</summary>
    public int MouseSpeed { get; set; } = 40;

    /// <summary>가상 패드를 실제로 쓰는지 (켜져 있고 고급 입력도 켜짐).</summary>
    [JsonIgnore]
    public bool UsesVirtualPad => Enabled && VirtualPad;

    /// <summary>운반 키의 머리 (펌웨어 코드 F23 = 156).</summary>
    public const string CarrierPrefix = "KB_F23";

    /// <summary>버튼마다 다른 운반 글자 — 훅이 F23과 함께 온 이 글자로 어느 버튼인지 안다.</summary>
    public static readonly IReadOnlyDictionary<ClawButton, char> CarrierLetter = new Dictionary<ClawButton, char>
    {
        [ClawButton.DpadUp] = 'A', [ClawButton.DpadDown] = 'B', [ClawButton.DpadLeft] = 'C', [ClawButton.DpadRight] = 'D',
        [ClawButton.LB] = 'E', [ClawButton.RB] = 'F', [ClawButton.L3] = 'G', [ClawButton.R3] = 'H',
        [ClawButton.A] = 'I', [ClawButton.B] = 'J', [ClawButton.X] = 'K', [ClawButton.Y] = 'L',
        [ClawButton.M1] = 'M', [ClawButton.M2] = 'N', [ClawButton.LT] = 'O', [ClawButton.RT] = 'P',
    };

    /// <summary>레이어 버튼이 하나라도 있으면 "레이어 중" 키가 있는 버튼도 소프트웨어로 처리해야 한다.</summary>
    [JsonIgnore]
    public bool HasLayerShift =>Buttons.Values.Any(b => b.LayerShift);

    /// <summary>엔진이 맡는 버튼 (고급 입력이 꺼져 있으면 없음).</summary>
    public IEnumerable<ClawButton> EngineButtons() => !Enabled ? []
        : Buttons.Where(kv => kv.Value.NeedsEngine && !(OnlyLayerKeys(kv.Value) && !HasLayerShift)).Select(kv => kv.Key);

    /// <summary>"레이어 중" 키만 있는 버튼 — 레이어 버튼이 없으면 쓸 일이 없다.</summary>
    private static bool OnlyLayerKeys(AdvancedButton b) => b.Layer.Count > 0 && b.Long.Count == 0 && b.Double.Count == 0
        && b.TurboMs == 0 && !b.Toggle && !b.LayerShift && b.Radial is null;

    /// <summary>
    /// 펌웨어에 쓸 바인딩: 엔진이 맡는 버튼은 운반 키(F23 + 글자), 나머지는 일반 바인딩 그대로.
    /// </summary>
    public static Dictionary<ClawButton, List<string>> FirmwareBindings(MappingProfile profile)
    {
        var result = profile.Bindings.Where(kv => kv.Value.Count > 0).ToDictionary(kv => kv.Key, kv => kv.Value.ToList());
        foreach (var b in profile.EngineButtons())
            if (CarrierLetter.TryGetValue(b, out var letter)) result[b] = [CarrierPrefix, "KB_" + letter];
        return result;
    }

    public AdvancedInput Clone() => new()
    {
        Enabled = Enabled, Buttons = Buttons.ToDictionary(kv => kv.Key, kv => kv.Value.Clone()),
        VirtualPad = VirtualPad, LeftStick = LeftStick, RightStick = RightStick, MouseSpeed = MouseSpeed,
    };
}
