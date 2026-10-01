using ClawCore.Ipc;

namespace ClawCore.Power;

/// <summary>
/// 활성 전원 구성표를 읽어 <see cref="PowerPlanState"/>를 만들고, 쓰기 전에 값을 검증한다 (읽기 전용 — 쓰기는 서비스).
/// 이 기기에 없는 설정은 빼고, 목록형 설정은 기기가 허용하는 값(PowerReadPossibleValue)만 내보낸다.
/// </summary>
public static class PowerPlanReader
{
    /// <summary>항목 정의. Presets = 칩으로 보여 줄 추천 값 (Seconds/Percent만).</summary>
    public sealed record Definition(PowerPlanSetting Setting, Guid Sub, Guid Guid, PowerPlanUnit Unit, bool AcApplies, int[] Presets);

    private const int Minute = 60, Hour = 3600;

    public static readonly IReadOnlyList<Definition> Definitions =
    [
        new(PowerPlanSetting.DisplayOff, PowerPlanNative.SubVideo, PowerPlanNative.VideoIdle, PowerPlanUnit.Seconds, true,
            [1 * Minute, 2 * Minute, 3 * Minute, 5 * Minute, 10 * Minute, 15 * Minute, 30 * Minute, 0]),
        new(PowerPlanSetting.Sleep, PowerPlanNative.SubSleep, PowerPlanNative.StandbyIdle, PowerPlanUnit.Seconds, true,
            [1 * Minute, 2 * Minute, 3 * Minute, 5 * Minute, 10 * Minute, 15 * Minute, 30 * Minute, 0]),
        new(PowerPlanSetting.Hibernate, PowerPlanNative.SubSleep, PowerPlanNative.HibernateIdle, PowerPlanUnit.Seconds, true,
            [1 * Hour, 2 * Hour, 4 * Hour, 8 * Hour, 0]),
        new(PowerPlanSetting.PowerButton, PowerPlanNative.SubButtons, PowerPlanNative.PButtonAction, PowerPlanUnit.Choice, true, []),
        new(PowerPlanSetting.EnergySaverThreshold, PowerPlanNative.SubEnergySaver, PowerPlanNative.EsBattThreshold, PowerPlanUnit.Percent, false,
            [0, 10, 20, 30, 50, 100]),
        new(PowerPlanSetting.EnergySaverBrightness, PowerPlanNative.SubEnergySaver, PowerPlanNative.EsBrightness, PowerPlanUnit.Percent, false,
            [30, 50, 70, 100]),
        new(PowerPlanSetting.AdaptiveBrightness, PowerPlanNative.SubVideo, PowerPlanNative.AdaptBright, PowerPlanUnit.Choice, true, []),
    ];

    public static Definition Find(PowerPlanSetting setting) => Definitions.First(d => d.Setting == setting);

    /// <summary>시간 설정은 0(사용 안 함) 또는 1분 단위로 최대 하루까지 받는다.</summary>
    private const int MaxSeconds = 24 * Hour;

    public static PowerPlanState Read()
    {
        var state = new PowerPlanState();
        try
        {
            if (PowerPlanNative.ActiveScheme() is not { } scheme)
            {
                state.Error = "활성 전원 구성표를 읽지 못했어요";
                return state;
            }
            var caps = PowerPlanNative.ReadCapabilities();
            state.SchemeGuid = scheme.ToString();
            state.SchemeName = PowerPlanNative.SchemeName(scheme);
            state.ModernStandby = caps?.ModernStandby ?? false;
            state.HibernateEnabled = PowerPlanNative.HibernateEnabled(caps);
            state.AmbientLightSensor = PowerPlanNative.HasAmbientLightSensor();

            foreach (var d in Definitions)
            {
                if (d.Setting == PowerPlanSetting.AdaptiveBrightness && !state.AmbientLightSensor) continue; // 센서 없으면 효과 없음
                if (PowerPlanNative.Read(scheme, d.Sub, d.Guid) is not { } v) continue; // 이 기기에 없음
                var s = new PowerPlanSettingState
                {
                    PowerSetting = d.Setting,
                    Unit = d.Unit,
                    AcApplies = d.AcApplies,
                    Ac = Clamp(v.Ac),
                    Dc = Clamp(v.Dc),
                };
                if (d.Unit == PowerPlanUnit.Choice)
                {
                    var count = PowerPlanNative.PossibleValueCount(d.Sub, d.Guid);
                    if (count == 0) continue;
                    s.Min = 0;
                    s.Max = count - 1;
                    for (var i = 0; i < count; i++)
                    {
                        // 최대 절전이 꺼져 있으면 전원 단추의 "최대 절전"은 Windows 설정에서도 안 보인다
                        if (d.Setting == PowerPlanSetting.PowerButton && i == 2 && !state.HibernateEnabled) continue;
                        s.Choices.Add(new PowerPlanChoice { Value = i, Label = Label(d.Setting, i) });
                    }
                }
                else
                {
                    if (PowerPlanNative.Range(d.Sub, d.Guid) is not { } r) continue;
                    s.Min = Clamp(r.Min);
                    s.Max = Clamp(r.Max);
                    var values = d.Presets.Where(p => p >= s.Min && p <= s.Max).ToList();
                    // 지금 쓰는 값이 추천에 없으면(Windows 설정에서 20분 등) 칩에도 넣어 선택 상태로 보이게
                    if (!values.Contains(s.Ac) && d.AcApplies) values.Add(s.Ac);
                    if (!values.Contains(s.Dc)) values.Add(s.Dc);
                    s.Choices = [.. values.Distinct()
                        .OrderBy(x => d.Unit == PowerPlanUnit.Seconds && x == 0 ? int.MaxValue : x)
                        .Select(x => new PowerPlanChoice { Value = x, Label = Label(d.Setting, x) })];
                }
                s.AcText = Label(d.Setting, s.Ac);
                s.DcText = Label(d.Setting, s.Dc);
                state.Settings.Add(s);
            }
            state.Available = state.Settings.Count > 0;
            if (!state.Available) state.Error = "바꿀 수 있는 전원 설정이 없어요";
        }
        catch (Exception ex)
        {
            state.Available = false;
            state.Error = ex.Message;
        }
        return state;
    }

    /// <summary>
    /// 쓰기 전 검증. 문제가 있으면 ArgumentException / InvalidOperationException (메시지는 한국어 그대로 앱·위젯에 표시).
    /// <paramref name="state"/>는 방금 읽은 상태.
    /// </summary>
    public static void Validate(PowerPlanState state, PowerPlanSetting setting, PowerSourceKind source, int value)
    {
        var s = state.Settings.FirstOrDefault(x => x.PowerSetting == setting)
            ?? throw new InvalidOperationException("이 기기에서 바꿀 수 없는 전원 설정이에요");
        if (source == PowerSourceKind.Ac && !s.AcApplies)
            throw new ArgumentException("이 설정은 배터리 칸만 있어요 (PowerSource \"Battery\")");
        if (setting == PowerPlanSetting.Hibernate && !state.HibernateEnabled && value != 0)
            throw new InvalidOperationException("최대 절전 모드가 꺼져 있어요 (관리자 명령 프롬프트에서 powercfg /h on)");

        switch (s.Unit)
        {
            case PowerPlanUnit.Choice:
                if (!s.Choices.Any(c => c.Value == value)) throw new ArgumentException($"허용되지 않는 값 {value}");
                break;
            case PowerPlanUnit.Seconds:
                if (value != 0 && (value % Minute != 0 || value < Minute || value > MaxSeconds))
                    throw new ArgumentException("시간은 0(사용 안 함) 또는 1분 단위로 최대 24시간이에요");
                if (value < s.Min || value > s.Max) throw new ArgumentException($"허용 범위({s.Min}~{s.Max}) 밖의 값이에요");
                break;
            case PowerPlanUnit.Percent:
                if (value < Math.Max(0, s.Min) || value > Math.Min(100, s.Max)) throw new ArgumentException("0~100% 사이여야 해요");
                break;
        }
    }

    /// <summary>값의 한국어 표시 문자열.</summary>
    public static string Label(PowerPlanSetting setting, int value) => setting switch
    {
        PowerPlanSetting.DisplayOff or PowerPlanSetting.Sleep or PowerPlanSetting.Hibernate => Duration(value),
        PowerPlanSetting.PowerButton => value switch
        {
            0 => "아무것도 안 함",
            1 => "절전",
            2 => "최대 절전",
            3 => "시스템 종료",
            4 => "화면 끄기",
            _ => $"동작 {value}",
        },
        PowerPlanSetting.EnergySaverThreshold => value switch { 0 => "끔", 100 => "항상", _ => $"{value}%" },
        PowerPlanSetting.EnergySaverBrightness => value >= 100 ? "그대로" : $"{value}%",
        PowerPlanSetting.AdaptiveBrightness => value == 0 ? "끔" : "켬",
        _ => value.ToString(),
    };

    private static string Duration(int seconds)
    {
        if (seconds <= 0) return "사용 안 함";
        if (seconds < Minute) return $"{seconds}초";
        var h = seconds / Hour;
        var m = seconds % Hour / Minute;
        return h == 0 ? $"{m}분" : m == 0 ? $"{h}시간" : $"{h}시간 {m}분";
    }

    private static int Clamp(uint v) => v > int.MaxValue ? int.MaxValue : (int)v;
}
