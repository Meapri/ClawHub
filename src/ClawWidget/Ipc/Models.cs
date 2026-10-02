using System;
using System.Collections.Generic;
using Windows.Data.Json;

namespace ClawWidget.Ipc
{
    // ClawCore.Ipc.Messages / ClawCore.Ec.PerformanceManager / ClawCore.Controller.ControllerSettings의 최소 복제본.
    // 위젯은 UWP .NET Native라 net10 ClawCore를 참조할 수 없다. 서비스는 System.Text.Json + JsonStringEnumConverter
    // (정수 enum 거부)를 쓰므로 enum은 반드시 이름 문자열로 보낸다. 속성 이름은 PascalCase 그대로.

    public enum PerformanceMode { Battery, Balanced, Performance, Manual }

    public enum FanMode { Auto, CoolerBoost }

    /// <summary>서비스 enum 값: Full = 100, Balanced = 80, Battery = 60. JSON에는 이름으로 간다.</summary>
    public enum ChargeLimit { Full, Balanced, Battery }

    public enum LightMode { Default, Static, Off, Breath, Rainbow, Wave }

    /// <summary>Intel Endurance Gaming: 끔 / 항상 켬 / 배터리일 때만(Auto).</summary>
    public enum EnduranceControl { Off, On, Auto }

    public enum EnduranceMode { Performance, Balanced, MaxBattery }

    /// <summary>SetPerformance가 바꿀 칸: 전원 연결(Ac) / 배터리(Battery).</summary>
    public enum PowerSourceKind { Ac, Battery }

    public static class Ops
    {
        public const string GetStatus = "GetStatus";
        public const string SetPerformance = "SetPerformance";
        public const string SetChargeLimit = "SetChargeLimit";
        public const string GetTelemetry = "GetTelemetry";
        public const string SetGraphics = "SetGraphics";
        public const string SetBrightness = "SetBrightness";
        public const string SetControllerSettings = "SetControllerSettings";
        public const string AppCommand = "AppCommand";
        public const string SetEnduranceGaming = "SetEnduranceGaming";
        // Subscribe는 쓰지 않는다: 서비스는 마지막 구독자가 끊기면 게임 매핑·게임별 성능을 복원하는데,
        // 위젯이 구독자로 남으면 ClawApp이 꺼져도 복원이 일어나지 않는다. 위젯은 폴링만 한다.
    }

    public static class AppCommands
    {
        public const string CycleProfile = "CycleProfile";
        /// <summary>"SetProfile:&lt;Id&gt;" — 빈 Id = 매핑 사용 안 함.</summary>
        public const string SetProfilePrefix = "SetProfile:";
        public const string QuitGame = "QuitGame";
        public const string ShowClawHub = "ShowClawHub";
        /// <summary>Game Bar 스크린샷 (런처가 Win+Alt+PrtSc).</summary>
        public const string Screenshot = "Screenshot";
        /// <summary>Game Bar 녹화 시작/중지 (Win+Alt+R).</summary>
        public const string ToggleRecording = "ToggleRecording";
        /// <summary>최근 30초 저장 (Win+Alt+G, 백그라운드 녹화가 켜져 있어야 함).</summary>
        public const string SaveClip = "SaveClip";
        /// <summary>Game Bar가 열려 이 위젯이 보인다 / 닫혀 안 보인다 (고정 HUD만 남은 것은 닫힘). 런처가 매핑을 풀고 되돌리는 기준.</summary>
        public const string WidgetShown = "WidgetShown";
        public const string WidgetHidden = "WidgetHidden";
    }

    public static class Limits
    {
        public const int MinPl = 8;
        public const int MaxPl = 45;
    }

    public sealed class PerformanceState
    {
        public PerformanceMode Mode;
        public int Pl1;
        public int Pl2;
        public FanMode Fan;

        public PerformanceState(PerformanceMode mode, int pl1, int pl2, FanMode fan)
        {
            Mode = mode; Pl1 = pl1; Pl2 = pl2; Fan = fan;
        }

        /// <summary>PerformanceManager.Preset과 같은 값 (1T91). Manual은 주어진 PL을 그대로 쓴다.</summary>
        public static PerformanceState Preset(PerformanceMode mode, FanMode fan)
        {
            switch (mode)
            {
                case PerformanceMode.Battery: return new PerformanceState(mode, 15, 20, fan);
                case PerformanceMode.Balanced: return new PerformanceState(mode, 25, 37, fan);
                case PerformanceMode.Performance: return new PerformanceState(mode, 30, 37, fan);
                default: return new PerformanceState(PerformanceMode.Manual, 17, 37, fan);
            }
        }

        public static PerformanceState Manual(int pl1, int pl2, FanMode fan)
        {
            pl2 = Clamp(pl2, Limits.MinPl, Limits.MaxPl);
            pl1 = Clamp(pl1, Limits.MinPl, pl2);
            return new PerformanceState(PerformanceMode.Manual, pl1, pl2, fan);
        }

        public PerformanceState WithFan(FanMode fan) => new PerformanceState(Mode, Pl1, Pl2, fan);

        public JsonObject ToJson()
        {
            var o = new JsonObject();
            o["Mode"] = JsonValue.CreateStringValue(Mode.ToString());
            o["Pl1"] = JsonValue.CreateNumberValue(Pl1);
            o["Pl2"] = JsonValue.CreateNumberValue(Pl2);
            o["Fan"] = JsonValue.CreateStringValue(Fan.ToString());
            return o;
        }

        public static PerformanceState FromJson(IJsonValue v)
        {
            var o = Json.AsObject(v);
            if (o == null) return null;
            return new PerformanceState(
                Json.Enum(o, "Mode", PerformanceMode.Balanced),
                Json.Int(o, "Pl1") ?? 0,
                Json.Int(o, "Pl2") ?? 0,
                Json.Enum(o, "Fan", FanMode.Auto));
        }

        internal static int Clamp(int v, int min, int max) => v < min ? min : v > max ? max : v;
    }

    public sealed class GraphicsState
    {
        public bool Available;
        public int FpsLimit;
        /// <summary>0 끔, 1 = 2배, 2 = 3배, 3 = 4배.</summary>
        public int FrameGeneration;
        public EnduranceState Endurance;

        public static GraphicsState FromJson(IJsonValue v)
        {
            var o = Json.AsObject(v);
            if (o == null) return null;
            return new GraphicsState
            {
                Available = Json.Bool(o, "Available") ?? false,
                FpsLimit = Json.Int(o, "FpsLimit") ?? 0,
                FrameGeneration = Json.Int(o, "FrameGeneration") ?? 0,
                Endurance = EnduranceState.FromJson(Json.Get(o, "Endurance")),
            };
        }
    }

    public sealed class ProfileItem
    {
        public string Id;
        public string Name;
    }

    /// <summary>런처 앱이 서비스에 맡겨 두는 상태. 앱이 꺼져 있으면 Status.AppState = null.</summary>
    public sealed class AppState
    {
        public string RunningGameTitle;
        public string ProfileId;
        public string ProfileName;
        public List<ProfileItem> Profiles = new List<ProfileItem>();
        public string GamePerformanceMode;
        public int? GameFpsLimit;
        /// <summary>게임 전용 배터리 성능 모드 (null = 배터리여도 GamePerformanceMode/기본값).</summary>
        public string GamePerformanceModeOnBattery;
        /// <summary>게임 전용 배터리 FPS 제한 (null = 배터리여도 GameFpsLimit/기본값, 0 = 끔).</summary>
        public int? GameFpsLimitOnBattery;
        /// <summary>앱이 본 전원 상태 (true = 배터리).</summary>
        public bool OnBattery;

        /// <summary>지금 전원 상태에서 이 게임에 적용되는 전용 성능 모드 이름 (없으면 null).</summary>
        public string CurrentGamePerformanceMode =>
            OnBattery && GamePerformanceModeOnBattery != null ? GamePerformanceModeOnBattery : GamePerformanceMode;

        /// <summary>지금 전원 상태에서 이 게임에 적용되는 전용 FPS 제한 (없으면 null).</summary>
        public int? CurrentGameFpsLimit =>
            OnBattery && GameFpsLimitOnBattery.HasValue ? GameFpsLimitOnBattery : GameFpsLimit;

        public static AppState FromJson(IJsonValue v)
        {
            var o = Json.AsObject(v);
            if (o == null) return null;
            var s = new AppState
            {
                RunningGameTitle = Json.Str(o, "RunningGameTitle"),
                ProfileId = Json.Str(o, "ProfileId"),
                ProfileName = Json.Str(o, "ProfileName"),
                GamePerformanceMode = Json.Str(o, "GamePerformanceMode"),
                GameFpsLimit = Json.Int(o, "GameFpsLimit"),
                GamePerformanceModeOnBattery = Json.Str(o, "GamePerformanceModeOnBattery"),
                GameFpsLimitOnBattery = Json.Int(o, "GameFpsLimitOnBattery"),
                OnBattery = Json.Bool(o, "OnBattery") ?? false,
            };
            var arr = Json.Get(o, "Profiles");
            if (arr != null && arr.ValueType == JsonValueType.Array)
                foreach (var item in arr.GetArray())
                {
                    var p = Json.AsObject(item);
                    if (p == null) continue;
                    var id = Json.Str(p, "Id");
                    if (string.IsNullOrEmpty(id)) continue;
                    s.Profiles.Add(new ProfileItem { Id = id, Name = Json.Str(p, "Name") ?? id });
                }
            return s;
        }
    }

    /// <summary>
    /// 컨트롤러 설정 중 위젯이 만지는 조명 값 + 원본 JSON. 보낼 때는 원본 객체를 복사해 조명 필드만 바꾼다
    /// (진동·데드존 등 나머지 필드와 null 값을 그대로 유지).
    /// </summary>
    public sealed class ControllerSettings
    {
        public JsonObject Raw;
        public LightMode Light;
        public int? LightBrightness;
        /// <summary>RGB 3바이트.</summary>
        public byte[] LightColor;
        public int LightSpeed;

        public static readonly byte[] DefaultColor = { 0x4C, 0xC2, 0xFF };

        public static ControllerSettings FromJson(IJsonValue v)
        {
            var o = Json.AsObject(v);
            if (o == null) return null;
            byte[] color = null;
            var b64 = Json.Str(o, "LightColor");
            if (b64 != null)
            {
                try { color = Convert.FromBase64String(b64); }
                catch (FormatException) { color = null; }
            }
            if (color == null || color.Length < 3) color = DefaultColor;
            return new ControllerSettings
            {
                Raw = o,
                Light = Json.Enum(o, "Light", LightMode.Default),
                LightBrightness = Json.Int(o, "LightBrightness"),
                LightColor = color,
                LightSpeed = Json.Int(o, "LightSpeed") ?? 1,
            };
        }

        /// <summary>원본 전체를 복사하고 조명 필드만 바꾼 요청 본문.</summary>
        public static JsonObject WithLight(ControllerSettings current, LightMode light, int? brightness, byte[] color, int speed)
        {
            JsonObject o;
            if (current != null && current.Raw != null) o = JsonObject.Parse(current.Raw.Stringify());
            else o = new JsonObject();
            o["Light"] = JsonValue.CreateStringValue(light.ToString());
            o["LightBrightness"] = brightness.HasValue ? JsonValue.CreateNumberValue(brightness.Value) : JsonValue.CreateNullValue();
            o["LightColor"] = JsonValue.CreateStringValue(Convert.ToBase64String(color ?? DefaultColor, 0, 3));
            o["LightSpeed"] = JsonValue.CreateNumberValue(speed);
            return o;
        }
    }

    /// <summary>Intel Endurance Gaming (Status.Graphics.Endurance).</summary>
    public sealed class EnduranceState
    {
        public bool Available;
        /// <summary>null = 모름 (드라이버에서 못 읽었고 저장값도 없음).</summary>
        public EnduranceControl? Control;
        public EnduranceMode? Mode;
        /// <summary>"Driver" = 드라이버에서 읽음, "Saved" = 저장된 값, null = 모름.</summary>
        public string Source;
        /// <summary>ClawHub가 정한 값을 시작·절전 복귀 때 다시 적용하는지.</summary>
        public bool Managed;

        public static EnduranceState FromJson(IJsonValue v)
        {
            var o = Json.AsObject(v);
            if (o == null) return null;
            return new EnduranceState
            {
                Available = Json.Bool(o, "Available") ?? false,
                Control = Json.NullableEnum<EnduranceControl>(o, "Control"),
                Mode = Json.NullableEnum<EnduranceMode>(o, "Mode"),
                Source = Json.Str(o, "Source"),
                Managed = Json.Bool(o, "Managed") ?? false,
            };
        }
    }

    /// <summary>배터리 수명 (서비스 60초 캐시). 모르는 항목은 null.</summary>
    public sealed class BatteryHealth
    {
        public int? DesignedCapacityMWh;
        public int? FullChargeCapacityMWh;
        public int? HealthPercent;
        public int? CycleCount;
        public string Manufacturer;

        public static BatteryHealth FromJson(IJsonValue v)
        {
            var o = Json.AsObject(v);
            if (o == null) return null;
            return new BatteryHealth
            {
                DesignedCapacityMWh = Json.Int(o, "DesignedCapacityMWh"),
                FullChargeCapacityMWh = Json.Int(o, "FullChargeCapacityMWh"),
                HealthPercent = Json.Int(o, "HealthPercent"),
                CycleCount = Json.Int(o, "CycleCount"),
                Manufacturer = Json.Str(o, "Manufacturer"),
            };
        }
    }

    public sealed class ServiceStatus
    {
        public bool ControllerConnected;
        public bool MappingActive;
        public bool EcAvailable;
        public bool EcWritable;
        public PerformanceState Performance;
        public PerformanceState TemporaryPerformance;
        /// <summary>null = EC가 충전 한도를 지원하지 않음.</summary>
        public ChargeLimit? ChargeLimit;
        public GraphicsState Graphics;
        public int? Brightness;
        public AppState AppState;
        public ControllerSettings ControllerSettings;
        /// <summary>서비스가 본 전원 상태 (true = 배터리).</summary>
        public bool OnBattery;
        /// <summary>전원 연결 때의 사용자 기본 성능. null이면 전원별 성능이 없는 이전 서비스.</summary>
        public PerformanceState PerformanceOnAc;
        /// <summary>배터리 전용 사용자 기본 성능 (null = 배터리여도 PerformanceOnAc).</summary>
        public PerformanceState PerformanceOnBattery;
        /// <summary>서비스 시작 직후엔 null일 수 있다.</summary>
        public BatteryHealth BatteryHealth;
        public List<string> Warnings = new List<string>();

        public static ServiceStatus FromJson(IJsonValue v)
        {
            var o = Json.AsObject(v);
            if (o == null) return null;
            var s = new ServiceStatus
            {
                ControllerConnected = Json.Bool(o, "ControllerConnected") ?? false,
                MappingActive = Json.Bool(o, "MappingActive") ?? false,
                EcAvailable = Json.Bool(o, "EcAvailable") ?? false,
                EcWritable = Json.Bool(o, "EcWritable") ?? false,
                Performance = PerformanceState.FromJson(Json.Get(o, "Performance")),
                TemporaryPerformance = PerformanceState.FromJson(Json.Get(o, "TemporaryPerformance")),
                ChargeLimit = Json.NullableEnum<ChargeLimit>(o, "ChargeLimit"),
                Graphics = GraphicsState.FromJson(Json.Get(o, "Graphics")),
                Brightness = Json.Int(o, "Brightness"),
                AppState = AppState.FromJson(Json.Get(o, "AppState")),
                ControllerSettings = ControllerSettings.FromJson(Json.Get(o, "ControllerSettings")),
                OnBattery = Json.Bool(o, "OnBattery") ?? false,
                PerformanceOnAc = PerformanceState.FromJson(Json.Get(o, "PerformanceOnAc")),
                PerformanceOnBattery = PerformanceState.FromJson(Json.Get(o, "PerformanceOnBattery")),
                BatteryHealth = BatteryHealth.FromJson(Json.Get(o, "BatteryHealth")),
            };
            var w = Json.Get(o, "Warnings");
            if (w != null && w.ValueType == JsonValueType.Array)
                foreach (var item in w.GetArray())
                    if (item.ValueType == JsonValueType.String) s.Warnings.Add(item.GetString());
            return s;
        }

        /// <summary>게임별 임시 성능이 걸려 있으면 그게 실제 적용값.</summary>
        public PerformanceState EffectivePerformance => TemporaryPerformance ?? Performance;
    }

    public sealed class TelemetrySample
    {
        public long Time;
        public int? CpuTemp;
        public int? CpuWatts;
        public double? BatteryWatts;
    }

    public sealed class Telemetry
    {
        public int? CpuTemp;
        public int? CpuWatts;
        public int? BatteryPercent;
        public bool Charging;
        /// <summary>방전 전력 (W). 충전 중이면 null.</summary>
        public double? BatteryWatts;
        public int? RemainingMinutes;
        public List<TelemetrySample> History = new List<TelemetrySample>();

        public static Telemetry FromJson(IJsonValue v)
        {
            var o = Json.AsObject(v);
            if (o == null) return null;
            var t = new Telemetry
            {
                CpuTemp = Json.Int(o, "CpuTemp"),
                CpuWatts = Json.Int(o, "CpuWatts"),
                BatteryPercent = Json.Int(o, "BatteryPercent"),
                Charging = Json.Bool(o, "Charging") ?? false,
                BatteryWatts = Json.Double(o, "BatteryWatts"),
                RemainingMinutes = Json.Int(o, "RemainingMinutes"),
            };
            var h = Json.Get(o, "History");
            if (h != null && h.ValueType == JsonValueType.Array)
                foreach (var item in h.GetArray())
                {
                    var s = Json.AsObject(item);
                    if (s == null) continue;
                    t.History.Add(new TelemetrySample
                    {
                        Time = (long)(Json.Double(s, "Time") ?? 0),
                        CpuTemp = Json.Int(s, "CpuTemp"),
                        CpuWatts = Json.Int(s, "CpuWatts"),
                        BatteryWatts = Json.Double(s, "BatteryWatts"),
                    });
                }
            return t;
        }
    }

    public sealed class Response
    {
        public int Id;
        public bool Ok = true;
        public string Error;
        public string Event;
        public ServiceStatus Status;
        public Telemetry Telemetry;

        public static Response Parse(string line)
        {
            JsonObject o;
            if (!JsonObject.TryParse(line, out o)) return null;
            return new Response
            {
                Id = Json.Int(o, "Id") ?? 0,
                Ok = Json.Bool(o, "Ok") ?? true,
                Error = Json.Str(o, "Error"),
                Event = Json.Str(o, "Event"),
                Status = ServiceStatus.FromJson(Json.Get(o, "Status")),
                Telemetry = Telemetry.FromJson(Json.Get(o, "Telemetry")),
            };
        }
    }

    public static class Requests
    {
        public static JsonObject Create(string op)
        {
            var o = new JsonObject();
            o["Op"] = JsonValue.CreateStringValue(op);
            return o;
        }

        public static JsonObject SetPerformance(PerformanceState state) => SetPerformance(state, null);

        /// <summary>source: 바꿀 칸 (null = 서비스가 지금 적용 중인 칸). state null + Battery = 배터리 전용 값 지우기.</summary>
        public static JsonObject SetPerformance(PerformanceState state, PowerSourceKind? source)
        {
            var o = Create(Ops.SetPerformance);
            o["Performance"] = state != null ? (IJsonValue)state.ToJson() : JsonValue.CreateNullValue();
            if (source.HasValue) o["PowerSource"] = JsonValue.CreateStringValue(source.Value.ToString());
            return o;
        }

        /// <summary>mode null = 서비스의 지금 값 (없으면 Balanced).</summary>
        public static JsonObject SetEnduranceGaming(EnduranceControl control, EnduranceMode? mode)
        {
            var o = Create(Ops.SetEnduranceGaming);
            o["EnduranceControl"] = JsonValue.CreateStringValue(control.ToString());
            if (mode.HasValue) o["EnduranceMode"] = JsonValue.CreateStringValue(mode.Value.ToString());
            return o;
        }

        public static JsonObject SetChargeLimit(ChargeLimit limit)
        {
            var o = Create(Ops.SetChargeLimit);
            o["ChargeLimit"] = JsonValue.CreateStringValue(limit.ToString());
            return o;
        }

        /// <summary>null = 그대로.</summary>
        public static JsonObject SetGraphics(int? fpsLimit, int? frameGeneration)
        {
            var o = Create(Ops.SetGraphics);
            if (fpsLimit.HasValue) o["FpsLimit"] = JsonValue.CreateNumberValue(fpsLimit.Value);
            if (frameGeneration.HasValue) o["FrameGeneration"] = JsonValue.CreateNumberValue(frameGeneration.Value);
            return o;
        }

        public static JsonObject SetBrightness(int brightness)
        {
            var o = Create(Ops.SetBrightness);
            o["Brightness"] = JsonValue.CreateNumberValue(PerformanceState.Clamp(brightness, 0, 100));
            return o;
        }

        public static JsonObject SetControllerSettings(JsonObject settings)
        {
            var o = Create(Ops.SetControllerSettings);
            o["ControllerSettings"] = settings;
            return o;
        }

        public static JsonObject AppCommand(string command)
        {
            var o = Create(Ops.AppCommand);
            o["Command"] = JsonValue.CreateStringValue(command);
            return o;
        }
    }

    /// <summary>Windows.Data.Json 헬퍼 — 없는 키, null, 타입 불일치는 전부 null로.</summary>
    internal static class Json
    {
        public static IJsonValue Get(JsonObject o, string key)
        {
            IJsonValue v;
            if (o == null || !o.TryGetValue(key, out v) || v == null || v.ValueType == JsonValueType.Null) return null;
            return v;
        }

        public static JsonObject AsObject(IJsonValue v) =>
            v != null && v.ValueType == JsonValueType.Object ? v.GetObject() : null;

        public static double? Double(JsonObject o, string key)
        {
            var v = Get(o, key);
            return v != null && v.ValueType == JsonValueType.Number ? (double?)v.GetNumber() : null;
        }

        public static int? Int(JsonObject o, string key)
        {
            var d = Double(o, key);
            return d.HasValue ? (int?)(int)Math.Round(d.Value) : null;
        }

        public static bool? Bool(JsonObject o, string key)
        {
            var v = Get(o, key);
            return v != null && v.ValueType == JsonValueType.Boolean ? (bool?)v.GetBoolean() : null;
        }

        public static string Str(JsonObject o, string key)
        {
            var v = Get(o, key);
            return v != null && v.ValueType == JsonValueType.String ? v.GetString() : null;
        }

        public static T Enum<T>(JsonObject o, string key, T fallback) where T : struct
        {
            var r = NullableEnum<T>(o, key);
            return r ?? fallback;
        }

        public static T? NullableEnum<T>(JsonObject o, string key) where T : struct
        {
            var s = Str(o, key);
            T r;
            return s != null && System.Enum.TryParse(s, false, out r) ? (T?)r : null;
        }
    }
}
