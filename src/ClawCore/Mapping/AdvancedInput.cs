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
    /// <summary>원형 메뉴 (null = 아님): 누르고 있으면 메뉴가 뜨고 오른쪽 스틱으로 고른 뒤 떼면 그 칸의 키.</summary>
    public List<RadialSlice>? Radial { get; set; }

    /// <summary>소프트웨어 처리가 필요한지 (아니면 펌웨어가 그대로 처리).</summary>
    [JsonIgnore]
    public bool NeedsEngine => Long.Count > 0 || Double.Count > 0 || TurboMs > 0 || Toggle || LayerShift || Layer.Count > 0 || Radial is not null;

    public AdvancedButton Clone() => new()
    {
        Long = [.. Long], LongMs = LongMs, Double = [.. Double], DoubleMs = DoubleMs, TurboMs = TurboMs, Toggle = Toggle,
        LayerShift = LayerShift, Layer = [.. Layer],
        Radial = Radial?.Select(s => new RadialSlice { Label = s.Label, Keys = [.. s.Keys] }).ToList(),
    };
}

/// <summary>
/// 고급 입력 (2단계): 길게·두 번 누르기, 연사, 토글, 레이어, 원형 메뉴. 드라이버 없이 동작한다:
/// 펌웨어가 그 버튼을 "F23 + 글자"(거의 안 쓰는 키 조합 = 운반 키)로 보내면 ClawHub가 저수준 키보드 훅에서 먼저 가로채고
/// 동작에 맞는 키보드·마우스 입력을 소프트웨어로 낸다 (SendInput — 일부 안티치트가 싫어할 수 있어 프로필마다 켠다).
/// 고급 동작이 없는 버튼은 켜져 있어도 펌웨어 매핑 그대로 (지연·안티치트 영향 없음).
/// </summary>
public sealed class AdvancedInput
{
    public bool Enabled { get; set; }
    public Dictionary<ClawButton, AdvancedButton> Buttons { get; set; } = [];

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
    public bool HasLayerShift => Buttons.Values.Any(b => b.LayerShift);

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
        if (profile.Advanced is not { } adv) return result;
        foreach (var b in adv.EngineButtons())
            if (CarrierLetter.TryGetValue(b, out var letter)) result[b] = [CarrierPrefix, "KB_" + letter];
        return result;
    }

    public AdvancedInput Clone() => new() { Enabled = Enabled, Buttons = Buttons.ToDictionary(kv => kv.Key, kv => kv.Value.Clone()) };
}
