using System.Runtime.InteropServices;
using ClawCore.Ipc;

namespace ClawCore.Gpu;

/// <summary>
/// Intel Graphics Control Library(IGCL)로 화면 품질 옵션을 읽고 바꾼다:
/// 레트로(정수) 스케일링, 디스플레이 선명도, 저지연 모드, 가변 주사율(읽기 전용).
/// 드라이버와 함께 설치되는 Intel 런타임 C:\Windows\System32\ControlLib.dll을 우선 쓰고,
/// 없으면 MSI Center M의 intelGEDll.dll(같은 ctl* 함수를 다시 내보냄)을 쓴다.
/// 구조체 배치는 intel/drivers.gpu.control-library include/igcl_api.h (x64) 기준이며
/// 크기는 <see cref="CheckLayout"/>에서 확인한다. 모든 호출은 한 락 안에서 직렬화한다.
/// </summary>
public sealed class IgclDisplay : IDisposable
{
    private static readonly string[] LibraryPaths =
    [
        Path.Combine(Environment.SystemDirectory, "ControlLib.dll"),
        @"C:\Program Files (x86)\MSI\MSI Center M\intelGEDll.dll",
    ];

    private readonly object _lock = new();
    private readonly Native _n;
    private IntPtr _api;
    private IntPtr _device;
    private IntPtr _display;
    private readonly IntPtr _emptyName; // 3D 기능 전역 설정용 빈 애플리케이션 이름 ("")

    public string LibraryPath { get; }

    private IgclDisplay(Native n, string path)
    {
        _n = n;
        LibraryPath = path;
        _emptyName = Marshal.AllocHGlobal(4);
        Marshal.WriteInt32(_emptyName, 0);
    }

    /// <summary>IGCL을 열어 어댑터·내장 디스플레이를 찾는다. 실패하면 null과 이유.</summary>
    public static IgclDisplay? TryOpen(out string? error)
    {
        CheckLayout();
        foreach (var path in LibraryPaths)
        {
            if (!File.Exists(path) || !NativeLibrary.TryLoad(path, out var lib)) continue;
            if (Native.TryBind(lib) is not { } n) continue;
            var d = new IgclDisplay(n, path);
            try
            {
                d.Open();
                error = null;
                return d;
            }
            catch (Exception ex) when (ex is IgclException or InvalidOperationException)
            {
                d.Dispose();
                error = ex.Message;
                return null;
            }
        }
        error = "Intel ControlLib.dll을 찾지 못했습니다";
        return null;
    }

    private void Open()
    {
        var args = new InitArgs
        {
            Size = (uint)Marshal.SizeOf<InitArgs>(),
            Version = 0,
            AppVersion = (1u << 16) | 1u, // CTL_IMPL_VERSION 1.1
            Flags = 0,                    // Level Zero 불필요 (디스플레이·3D 설정만)
        };
        Check(_n.Init(ref args, out _api), "ctlInit");

        uint count = 0;
        Check(_n.EnumerateDevices(_api, ref count, null), "ctlEnumerateDevices");
        if (count == 0) throw new InvalidOperationException("Intel 그래픽 어댑터가 없습니다");
        var devices = new IntPtr[count];
        Check(_n.EnumerateDevices(_api, ref count, devices), "ctlEnumerateDevices");
        _device = devices[0]; // Claw에는 내장 Arc GPU 하나뿐

        _display = FindInternalDisplay();
    }

    /// <summary>활성 내장 패널(eDP/MIPI) 출력. 없으면 첫 활성 출력.</summary>
    private IntPtr FindInternalDisplay()
    {
        uint count = 0;
        if (_n.EnumerateDisplayOutputs(_device, ref count, null) != 0 || count == 0) return IntPtr.Zero;
        var outputs = new IntPtr[count];
        if (_n.EnumerateDisplayOutputs(_device, ref count, outputs) != 0) return IntPtr.Zero;

        IntPtr firstActive = IntPtr.Zero;
        foreach (var o in outputs.Take((int)count))
        {
            if (!TryGetDisplayProperties(o, out var p) || (p.DisplayConfigFlags & DisplayConfigActive) == 0) continue;
            if (firstActive == IntPtr.Zero) firstActive = o;
            var enc = new EncoderProperties { Size = (uint)Marshal.SizeOf<EncoderProperties>() };
            if (_n.GetEncoderProperties(o, ref enc) == 0 && (enc.EncoderConfigFlags & EncoderInternalDisplay) != 0) return o;
        }
        return firstActive;
    }

    private bool TryGetDisplayProperties(IntPtr output, out DisplayProperties p)
    {
        p = new DisplayProperties { Size = (uint)Marshal.SizeOf<DisplayProperties>(), TimingSize = (uint)TimingStructSize };
        return _n.GetDisplayProperties(output, ref p) == 0;
    }

    // ── 공개 API ───────────────────────────────────────

    /// <summary>현재 값을 모두 읽는다. 항목별 실패는 그 항목만 null로 남긴다.</summary>
    public DisplayQualityState Read()
    {
        lock (_lock)
        {
            var s = new DisplayQualityState { Available = true };
            var errors = new List<string>();

            // 레트로 스케일링 (어댑터 단위)
            var caps = new RetroScalingCaps { Size = (uint)Marshal.SizeOf<RetroScalingCaps>() };
            var rc = _n.GetRetroScalingCaps(_device, ref caps);
            if (rc == 0 && caps.Supported != 0)
            {
                if ((caps.Supported & RetroInteger) != 0) s.RetroScalingModes.Add(RetroScalingMode.Integer);
                if ((caps.Supported & RetroNearest) != 0) s.RetroScalingModes.Add(RetroScalingMode.NearestNeighbour);
                var r = new RetroScalingSettings { Size = (uint)Marshal.SizeOf<RetroScalingSettings>(), Get = 1 };
                rc = _n.GetSetRetroScaling(_device, ref r);
                if (rc == 0)
                {
                    s.RetroScaling = r.Enable != 0;
                    s.RetroScalingMode = (r.Type & RetroNearest) != 0 ? RetroScalingMode.NearestNeighbour
                        : (r.Type & RetroInteger) != 0 ? RetroScalingMode.Integer : null;
                }
                else errors.Add($"레트로 스케일링 {Code(rc)}");
            }

            // 저지연 (3D 전역)
            // 한 번도 설정한 적 없으면 DATA_NOT_FOUND → 드라이버 기본값(caps의 DefaultType, 실기 = 끔)
            rc = Get3D(Feature3D.LowLatency, ValueTypeEnum, out var ll);
            if (rc is 0 or DataNotFound)
            {
                var value = rc == 0 ? ll.EnumValue : LowLatencyDefault();
                s.LowLatency = value switch { 0 => LowLatencyMode.Off, 1 => LowLatencyMode.On, 2 => LowLatencyMode.Boost, _ => null };
                s.LowLatencyModes = SupportedLowLatency();
            }
            else if (rc != Unsupported) errors.Add($"저지연 {Code(rc)}");

            if (_display != IntPtr.Zero)
            {
                // 디스플레이 선명도
                if (ReadSharpnessRange() is { } range)
                {
                    (s.SharpeningMin, s.SharpeningMax) = range;
                    var sh = new SharpnessSettings { Size = (uint)Marshal.SizeOf<SharpnessSettings>() };
                    rc = _n.GetCurrentSharpness(_display, ref sh);
                    if (rc == 0)
                    {
                        s.Sharpening = sh.Enable != 0;
                        s.SharpeningLevel = (int)MathF.Round(sh.Intensity);
                    }
                    else errors.Add($"선명도 {Code(rc)}");
                }

                // 가변 주사율 (읽기 전용): 패널 기능 플래그 + Intel Arc Sync 프로필(OFF면 꺼짐)
                if (TryGetDisplayProperties(_display, out var p))
                {
                    s.AdaptiveSyncSupported = (p.FeatureSupportedFlags & StdFeatureVrr) != 0;
                    s.AdaptiveSync = (p.FeatureEnabledFlags & StdFeatureVrr) != 0;
                    if (p.RefreshRate > 0) s.RefreshRate = Math.Round(p.RefreshRate, 2);
                }
                var prof = new ArcSyncProfileParams { Size = (uint)Marshal.SizeOf<ArcSyncProfileParams>() };
                if (_n.GetArcSyncProfile(_display, ref prof) == 0 && prof.Profile is > 0 and < 8)
                {
                    s.VrrProfile = ((ArcSyncProfile)prof.Profile).ToString();
                    if (prof.Profile == (int)ArcSyncProfile.Off) s.AdaptiveSync = false;
                    if (prof.MaxHz > 0) (s.VrrMinHz, s.VrrMaxHz) = (Math.Round(prof.MinHz, 2), Math.Round(prof.MaxHz, 2));
                }
                var mon = new ArcSyncMonitorParams { Size = (uint)Marshal.SizeOf<ArcSyncMonitorParams>() };
                if (_n.GetArcSyncInfo(_display, ref mon) == 0 && mon.IsSupported != 0 && mon.MaxHz > 0)
                {
                    s.PanelVrrMinHz = Math.Round(mon.MinHz, 2);
                    s.PanelVrrMaxHz = Math.Round(mon.MaxHz, 2);
                }
            }
            else errors.Add("활성 디스플레이 없음");

            if (errors.Count > 0) s.Error = string.Join(", ", errors);
            return s;
        }
    }

    /// <summary>바꿀 항목만 적용한다. 실패하면 <see cref="IgclException"/>.</summary>
    public void Apply(DisplayQualityChange c)
    {
        lock (_lock)
        {
            if (c.Sharpening is not null || c.SharpeningLevel is not null)
            {
                if (_display == IntPtr.Zero) throw new InvalidOperationException("활성 디스플레이가 없습니다");
                var range = ReadSharpnessRange() ?? throw new InvalidOperationException("선명도 조절을 지원하지 않습니다");
                var sh = new SharpnessSettings { Size = (uint)Marshal.SizeOf<SharpnessSettings>() };
                Check(_n.GetCurrentSharpness(_display, ref sh), "ctlGetCurrentSharpness");
                if (c.Sharpening is { } en) sh.Enable = en ? (byte)1 : (byte)0;
                if (c.SharpeningLevel is { } lv) sh.Intensity = Math.Clamp(lv, sh.Enable != 0 ? range.Min : 0, range.Max);
                // 꺼진 상태의 강도는 0으로 읽히므로, 켤 때는 범위 안으로 맞춘다
                if (sh.Enable != 0) sh.Intensity = Math.Clamp(sh.Intensity, range.Min, range.Max);
                if ((sh.FilterType & (SharpnessAdaptive | SharpnessNonAdaptive)) == 0) sh.FilterType = _sharpnessFilter;
                Check(_n.SetCurrentSharpness(_display, ref sh), "ctlSetCurrentSharpness");
            }

            if (c.LowLatency is { } mode)
            {
                if (!SupportedLowLatency().Contains(mode)) throw new InvalidOperationException($"저지연 {mode}는 지원하지 않습니다");
                var f = New3D(Feature3D.LowLatency, ValueTypeEnum, set: true);
                f.EnumValue = mode switch { LowLatencyMode.On => 1u, LowLatencyMode.Boost => 2u, _ => 0u };
                Check(_n.GetSet3DFeature(_device, ref f), "ctlGetSet3DFeature(LOW_LATENCY)");
            }

            if (c.RetroScaling is not null || c.RetroScalingMode is not null)
            {
                var cur = new RetroScalingSettings { Size = (uint)Marshal.SizeOf<RetroScalingSettings>(), Get = 1 };
                Check(_n.GetSetRetroScaling(_device, ref cur), "ctlGetSetRetroScaling(get)");
                var type = c.RetroScalingMode switch
                {
                    RetroScalingMode.NearestNeighbour => RetroNearest,
                    RetroScalingMode.Integer => RetroInteger,
                    _ => cur.Type != 0 ? cur.Type : RetroInteger,
                };
                var set = new RetroScalingSettings
                {
                    Size = (uint)Marshal.SizeOf<RetroScalingSettings>(),
                    Get = 0,
                    Enable = (c.RetroScaling ?? cur.Enable != 0) ? (byte)1 : (byte)0,
                    Type = type,
                };
                Check(_n.GetSetRetroScaling(_device, ref set), "ctlGetSetRetroScaling(set)");
            }
        }
    }

    // ── 내부 ───────────────────────────────────────────

    private uint _sharpnessFilter = SharpnessNonAdaptive;

    /// <summary>
    /// 선명도 필터의 강도 범위 (지원 안 하면 null). 필터 종류는 SupportedFilterFlags에서 고른다 (적응형 우선).
    /// 실기(Arc G3 Extreme, ControlLib 1.2.291): flags=ADAPTIVE, 속성 1개 type=1 min=1 max=5 step=1 —
    /// 속성의 FilterType은 플래그가 아니라 순번처럼 보여 범위만 쓴다.
    /// </summary>
    private (int Min, int Max)? ReadSharpnessRange()
    {
        var caps = new SharpnessCaps { Size = (uint)Marshal.SizeOf<SharpnessCaps>() };
        if (_n.GetSharpnessCaps(_display, ref caps) != 0 || caps.SupportedFilterFlags == 0 || caps.NumFilterTypes == 0) return null;
        _sharpnessFilter = (caps.SupportedFilterFlags & SharpnessAdaptive) != 0 ? SharpnessAdaptive : SharpnessNonAdaptive;

        int n = caps.NumFilterTypes;
        var size = Marshal.SizeOf<SharpnessFilterProperties>();
        var buf = Marshal.AllocHGlobal(size * n);
        try
        {
            for (var i = 0; i < size * n; i++) Marshal.WriteByte(buf, i, 0);
            caps.FilterProperties = buf;
            if (_n.GetSharpnessCaps(_display, ref caps) != 0) return null;
            (int, int)? first = null;
            for (var i = 0; i < Math.Min(n, (int)caps.NumFilterTypes); i++)
            {
                var fp = Marshal.PtrToStructure<SharpnessFilterProperties>(buf + i * size);
                var range = ((int)MathF.Round(fp.Min), (int)MathF.Round(fp.Max));
                if (range.Item2 <= range.Item1) continue;
                if (fp.FilterType == _sharpnessFilter) return range;
                first ??= range;
            }
            return first;
        }
        finally
        {
            Marshal.FreeHGlobal(buf);
        }
    }

    private uint LowLatencyDefault() =>
        Read3DCaps().FirstOrDefault(d => d.FeatureType == (int)Feature3D.LowLatency && d.ValueType == ValueTypeEnum).EnumDefaultType;

    private List<LowLatencyMode> SupportedLowLatency()
    {
        var modes = new List<LowLatencyMode> { LowLatencyMode.Off, LowLatencyMode.On };
        foreach (var d in Read3DCaps())
            if (d.FeatureType == (int)Feature3D.LowLatency && d.ValueType == ValueTypeEnum)
            {
                var mask = d.EnumSupportedTypes;
                if (mask == 0) break;
                modes.Clear();
                if ((mask & 1) != 0) modes.Add(LowLatencyMode.Off);
                if ((mask & 2) != 0) modes.Add(LowLatencyMode.On);
                if ((mask & 4) != 0) modes.Add(LowLatencyMode.Boost);
            }
        return modes;
    }

    private Feature3DGetSet New3D(Feature3D feature, int valueType, bool set) => new()
    {
        Size = (uint)Marshal.SizeOf<Feature3DGetSet>(),
        Version = 0,
        FeatureType = (int)feature,
        ApplicationName = _emptyName, // 빈 문자열 = 어댑터 전역 설정
        ApplicationNameLength = 0,
        IsSet = set ? (byte)1 : (byte)0,
        ValueType = valueType,
    };

    private int Get3D(Feature3D feature, int valueType, out Feature3DGetSet f)
    {
        f = New3D(feature, valueType, set: false);
        return _n.GetSet3DFeature(_device, ref f);
    }

    private List<Feature3DDetails> Read3DCaps()
    {
        var caps = new Feature3DCaps { Size = (uint)Marshal.SizeOf<Feature3DCaps>() };
        if (_n.GetSupported3DCapabilities(_device, ref caps) != 0 || caps.NumSupportedFeatures == 0) return [];
        var n = (int)caps.NumSupportedFeatures;
        var size = Marshal.SizeOf<Feature3DDetails>();
        var buf = Marshal.AllocHGlobal(size * n);
        try
        {
            for (var i = 0; i < size * n; i++) Marshal.WriteByte(buf, i, 0);
            caps.FeatureDetails = buf;
            if (_n.GetSupported3DCapabilities(_device, ref caps) != 0) return [];
            return [.. Enumerable.Range(0, Math.Min(n, (int)caps.NumSupportedFeatures))
                .Select(i => Marshal.PtrToStructure<Feature3DDetails>(buf + i * size))];
        }
        finally
        {
            Marshal.FreeHGlobal(buf);
        }
    }

    /// <summary>진단용 덤프 (tools/IgclProbe). 읽기 전용 호출만 한다.</summary>
    public IEnumerable<string> Dump()
    {
        lock (_lock)
        {
            var lines = new List<string> { $"library: {LibraryPath}", $"device: 0x{_device:X}  display: 0x{_display:X}" };

            uint count = 0;
            _n.EnumerateDisplayOutputs(_device, ref count, null);
            var outputs = new IntPtr[count];
            if (count > 0) _n.EnumerateDisplayOutputs(_device, ref count, outputs);
            foreach (var o in outputs)
            {
                var ok = TryGetDisplayProperties(o, out var p);
                var enc = new EncoderProperties { Size = (uint)Marshal.SizeOf<EncoderProperties>() };
                var erc = _n.GetEncoderProperties(o, ref enc);
                lines.Add($"output 0x{o:X}: props={ok} type={p.Type} config=0x{p.DisplayConfigFlags:X} featEn=0x{p.FeatureEnabledFlags:X} " +
                          $"featSup=0x{p.FeatureSupportedFlags:X} advEn=0x{p.AdvancedFeatureEnabledFlags:X} advSup=0x{p.AdvancedFeatureSupportedFlags:X} " +
                          $"{p.HActive}x{p.VActive}@{p.RefreshRate:0.##} | encoder {Code(erc)} cfg=0x{enc.EncoderConfigFlags:X}");
            }

            foreach (var d in Read3DCaps())
                lines.Add($"3D cap {(Feature3D)d.FeatureType}({d.FeatureType}) valueType={d.ValueType} " +
                          $"enumMask=0x{d.EnumSupportedTypes:X} enumDefault={d.EnumDefaultType} perApp={d.PerAppSupport} misc=0x{d.FeatureMiscSupport:X}");

            foreach (var (feature, vt) in new[]
                     {
                         (Feature3D.FrameLimit, ValueTypeInt32), (Feature3D.LowLatency, ValueTypeEnum),
                         (Feature3D.SharpeningFilter, ValueTypeEnum), (Feature3D.SharpeningFilter, ValueTypeInt32),
                         (Feature3D.GamingFlipModes, ValueTypeEnum), (Feature3D.FrameGeneration, ValueTypeEnum),
                         (Feature3D.EnduranceGaming, ValueTypeInt32),
                     })
            {
                var rc = Get3D(feature, vt, out var f);
                lines.Add($"3D get {feature} vt={vt}: {Code(rc)} enable={f.Enable} enum/u32={f.EnumValue} int={f.IntValue}");
            }

            var buf = Marshal.AllocHGlobal(8);
            try
            {
                Marshal.WriteInt64(buf, 0);
                var f = New3D(Feature3D.AdaptiveSyncPlus, ValueTypeCustom, set: false);
                f.CustomValueSize = 8;
                f.CustomValue = buf;
                var rc = _n.GetSet3DFeature(_device, ref f);
                lines.Add($"3D get AdaptiveSyncPlus(custom): {Code(rc)} adaptiveSync={Marshal.ReadByte(buf, 0)} " +
                          $"balance={Marshal.ReadByte(buf, 1)} asyncHighFps={Marshal.ReadByte(buf, 2)} " +
                          $"strength={BitConverter.Int32BitsToSingle(Marshal.ReadInt32(buf, 4))}");
            }
            finally { Marshal.FreeHGlobal(buf); }

            var rcaps = new RetroScalingCaps { Size = (uint)Marshal.SizeOf<RetroScalingCaps>() };
            lines.Add($"retro caps: {Code(_n.GetRetroScalingCaps(_device, ref rcaps))} supported=0x{rcaps.Supported:X}");
            var rs = new RetroScalingSettings { Size = (uint)Marshal.SizeOf<RetroScalingSettings>(), Get = 1 };
            lines.Add($"retro get: {Code(_n.GetSetRetroScaling(_device, ref rs))} enable={rs.Enable} type=0x{rs.Type:X}");

            if (_display != IntPtr.Zero)
            {
                var sc = new SharpnessCaps { Size = (uint)Marshal.SizeOf<SharpnessCaps>() };
                lines.Add($"sharpness caps: {Code(_n.GetSharpnessCaps(_display, ref sc))} flags=0x{sc.SupportedFilterFlags:X} n={sc.NumFilterTypes}");
                if (sc.NumFilterTypes > 0)
                {
                    var size = Marshal.SizeOf<SharpnessFilterProperties>() * sc.NumFilterTypes;
                    var fb = Marshal.AllocHGlobal(size);
                    try
                    {
                        for (var i = 0; i < size; i++) Marshal.WriteByte(fb, i, 0);
                        sc.FilterProperties = fb;
                        var rc = _n.GetSharpnessCaps(_display, ref sc);
                        for (var i = 0; i < sc.NumFilterTypes; i++)
                        {
                            var fp = Marshal.PtrToStructure<SharpnessFilterProperties>(fb + i * 20);
                            lines.Add($"  filter[{i}] {Code(rc)} type={fp.FilterType} min={fp.Min} max={fp.Max} step={fp.Step} default={fp.Default}");
                        }
                    }
                    finally { Marshal.FreeHGlobal(fb); }
                }
                lines.Add($"  range={ReadSharpnessRange()} filter={_sharpnessFilter}");
                var sh = new SharpnessSettings { Size = (uint)Marshal.SizeOf<SharpnessSettings>() };
                lines.Add($"sharpness get: {Code(_n.GetCurrentSharpness(_display, ref sh))} enable={sh.Enable} filter={sh.FilterType} intensity={sh.Intensity}");
                var mon = new ArcSyncMonitorParams { Size = (uint)Marshal.SizeOf<ArcSyncMonitorParams>() };
                lines.Add($"arc sync monitor: {Code(_n.GetArcSyncInfo(_display, ref mon))} supported={mon.IsSupported} {mon.MinHz}-{mon.MaxHz}Hz");
                var prof = new ArcSyncProfileParams { Size = (uint)Marshal.SizeOf<ArcSyncProfileParams>() };
                lines.Add($"arc sync profile: {Code(_n.GetArcSyncProfile(_display, ref prof))} profile={prof.Profile} {prof.MinHz}-{prof.MaxHz}Hz");
                var sc2 = new ScalingSettings { Size = (uint)Marshal.SizeOf<ScalingSettings>() };
                lines.Add($"scaling get: {Code(_n.GetCurrentScaling(_display, ref sc2))} enable={sc2.Enable} type=0x{sc2.ScalingType:X}");
                if (_n.DisplaySettingsGetSet is { } gs)
                {
                    var ds = new DisplaySettings { Size = (uint)Marshal.SizeOf<DisplaySettings>() };
                    lines.Add($"display settings: {Code(gs.Invoke(_display, ref ds))} sup=0x{ds.SupportedFlags:X} ctl=0x{ds.ControllableFlags:X} lowLatency={ds.LowLatency}");
                }
            }
            return lines;
        }
    }

    public void Dispose()
    {
        lock (_lock)
        {
            if (_api != IntPtr.Zero)
            {
                try { _n.Close(_api); } catch (SEHException) { /* 무시 */ }
                _api = IntPtr.Zero;
            }
            Marshal.FreeHGlobal(_emptyName);
        }
    }

    private static void Check(int rc, string what)
    {
        if (rc != 0) throw new IgclException($"{what} 실패 ({Code(rc)})", rc);
    }

    public static string Code(int rc) => rc switch
    {
        0 => "OK",
        0x40000006 => "권한 없음",
        0x4000000a => "미지원 기능",
        0x40000009 => "미지원 버전",
        0x4000000b => "잘못된 인자",
        0x40000015 => "미구현",
        0x48000003 => "디스플레이 없음",
        0x48000004 => "디스플레이 비활성",
        _ => $"0x{rc:X8}",
    };

    // ── 상수 (igcl_api.h) ─────────────────────────────

    private const int Unsupported = 0x4000000a;
    private const int DataNotFound = 0x40000014;
    private const int ValueTypeBool = 0, ValueTypeInt32 = 2, ValueTypeEnum = 4, ValueTypeCustom = 5;
    private const uint RetroInteger = 1, RetroNearest = 2;
    private const uint SharpnessNonAdaptive = 1, SharpnessAdaptive = 2;
    private const uint DisplayConfigActive = 1;
    private const uint EncoderInternalDisplay = 1;
    private const uint StdFeatureVrr = 1 << 3;
    private const int TimingStructSize = 64;

    private enum Feature3D
    {
        FramePacing = 0, EnduranceGaming = 1, FrameLimit = 2, Anisotropic = 3, Cmaa = 4, TextureFilteringQuality = 5,
        AdaptiveTessellation = 6, SharpeningFilter = 7, Msaa = 8, GamingFlipModes = 9, AdaptiveSyncPlus = 10,
        AppProfiles = 11, AppProfileDetails = 12, EmulatedTyped64BitAtomics = 13, VrrWindowedBlt = 14,
        GlobalOrPerApp = 15, LowLatency = 16, FrameGeneration = 17, PrebuiltShaderDownload = 18, LiveState = 19,
        FrameGenerationControl = 20,
    }

    private enum ArcSyncProfile { Invalid = 0, Recommended = 1, Excellent = 2, Good = 3, Compatible = 4, Off = 5, Vesa = 6, Custom = 7 }

    /// <summary>구조체 크기가 헤더(x64)와 다르면 드라이버가 메모리를 잘못 쓰므로 아예 쓰지 않는다.</summary>
    private static void CheckLayout()
    {
        if (!Environment.Is64BitProcess) throw new PlatformNotSupportedException("IGCL 구조체는 x64 기준");
        Expect<InitArgs>(36);
        Expect<Feature3DGetSet>(56);
        Expect<Feature3DCaps>(24);
        Expect<Feature3DDetails>(72);
        Expect<RetroScalingSettings>(12);
        Expect<RetroScalingCaps>(12);
        Expect<SharpnessCaps>(24);
        Expect<SharpnessSettings>(16);
        Expect<SharpnessFilterProperties>(20);
        Expect<DisplayProperties>(200);
        Expect<EncoderProperties>(112);
        Expect<ArcSyncMonitorParams>(24);
        Expect<ArcSyncProfileParams>(28);
        Expect<ScalingSettings>(28);
        Expect<DisplaySettings>(148);

        static void Expect<T>(int size) where T : struct
        {
            if (Marshal.SizeOf<T>() != size)
                throw new InvalidOperationException($"{typeof(T).Name} 크기 {Marshal.SizeOf<T>()} ≠ {size}");
        }
    }

    // ── 구조체 (bool은 C++ bool = 1바이트 → byte) ─────────────

    [StructLayout(LayoutKind.Sequential)]
    private struct InitArgs
    {
        public uint Size;
        public byte Version;
        public uint AppVersion;
        public uint Flags;
        public uint SupportedVersion;
        public Guid ApplicationUid; // ctl_application_id_t와 같은 배치 (u32,u16,u16,u8[8]); 전부 0 = 기본
    }

    /// <summary>ctl_3d_feature_getset_t. Value 공용체(ctl_property_t, 8바이트)는 오프셋 32.</summary>
    [StructLayout(LayoutKind.Explicit, Size = 56)]
    private struct Feature3DGetSet
    {
        [FieldOffset(0)] public uint Size;
        [FieldOffset(4)] public byte Version;
        [FieldOffset(8)] public int FeatureType;
        [FieldOffset(16)] public IntPtr ApplicationName;
        [FieldOffset(24)] public sbyte ApplicationNameLength;
        [FieldOffset(25)] public byte IsSet;
        [FieldOffset(28)] public int ValueType;
        [FieldOffset(32)] public byte Enable;      // BoolType.Enable / FloatType.Enable / IntType.Enable
        [FieldOffset(32)] public uint EnumValue;   // EnumType.EnableType
        [FieldOffset(36)] public int IntValue;     // IntType.Value
        [FieldOffset(40)] public int CustomValueSize;
        [FieldOffset(48)] public IntPtr CustomValue;
    }

    [StructLayout(LayoutKind.Explicit, Size = 24)]
    private struct Feature3DCaps
    {
        [FieldOffset(0)] public uint Size;
        [FieldOffset(4)] public byte Version;
        [FieldOffset(8)] public uint NumSupportedFeatures;
        [FieldOffset(16)] public IntPtr FeatureDetails;
    }

    /// <summary>ctl_3d_feature_details_t. Value 공용체(ctl_property_info_t)는 오프셋 8, 24바이트.</summary>
    [StructLayout(LayoutKind.Explicit, Size = 72)]
    private struct Feature3DDetails
    {
        [FieldOffset(0)] public int FeatureType;
        [FieldOffset(4)] public int ValueType;
        [FieldOffset(8)] public ulong EnumSupportedTypes; // EnumType.SupportedTypes
        [FieldOffset(16)] public uint EnumDefaultType;    // EnumType.DefaultType
        [FieldOffset(32)] public int CustomValueSize;
        [FieldOffset(40)] public IntPtr CustomValue;
        [FieldOffset(48)] public byte PerAppSupport;
        [FieldOffset(56)] public long ConflictingFeatures;
        [FieldOffset(64)] public short FeatureMiscSupport;
    }

    [StructLayout(LayoutKind.Explicit, Size = 12)]
    private struct RetroScalingSettings
    {
        [FieldOffset(0)] public uint Size;
        [FieldOffset(4)] public byte Version;
        [FieldOffset(5)] public byte Get;
        [FieldOffset(6)] public byte Enable;
        [FieldOffset(8)] public uint Type;
    }

    [StructLayout(LayoutKind.Explicit, Size = 12)]
    private struct RetroScalingCaps
    {
        [FieldOffset(0)] public uint Size;
        [FieldOffset(4)] public byte Version;
        [FieldOffset(8)] public uint Supported;
    }

    [StructLayout(LayoutKind.Explicit, Size = 24)]
    private struct SharpnessCaps
    {
        [FieldOffset(0)] public uint Size;
        [FieldOffset(4)] public byte Version;
        [FieldOffset(8)] public uint SupportedFilterFlags;
        [FieldOffset(12)] public byte NumFilterTypes;
        [FieldOffset(16)] public IntPtr FilterProperties;
    }

    [StructLayout(LayoutKind.Explicit, Size = 20)]
    private struct SharpnessFilterProperties
    {
        [FieldOffset(0)] public uint FilterType;
        [FieldOffset(4)] public float Min;
        [FieldOffset(8)] public float Max;
        [FieldOffset(12)] public float Step;
        [FieldOffset(16)] public float Default;
    }

    [StructLayout(LayoutKind.Explicit, Size = 16)]
    private struct SharpnessSettings
    {
        [FieldOffset(0)] public uint Size;
        [FieldOffset(4)] public byte Version;
        [FieldOffset(5)] public byte Enable;
        [FieldOffset(8)] public uint FilterType;
        [FieldOffset(12)] public float Intensity;
    }

    /// <summary>ctl_display_properties_t (+ 안의 ctl_display_timing_t는 오프셋 72, 64바이트).</summary>
    [StructLayout(LayoutKind.Explicit, Size = 200)]
    private struct DisplayProperties
    {
        [FieldOffset(0)] public uint Size;
        [FieldOffset(4)] public byte Version;
        [FieldOffset(24)] public int Type;
        [FieldOffset(48)] public uint DisplayConfigFlags;
        [FieldOffset(52)] public uint FeatureEnabledFlags;
        [FieldOffset(56)] public uint FeatureSupportedFlags;
        [FieldOffset(60)] public uint AdvancedFeatureEnabledFlags;
        [FieldOffset(64)] public uint AdvancedFeatureSupportedFlags;
        [FieldOffset(72)] public uint TimingSize;
        [FieldOffset(76)] public byte TimingVersion;
        [FieldOffset(88)] public uint HActive;
        [FieldOffset(92)] public uint VActive;
        [FieldOffset(120)] public float RefreshRate;
    }

    [StructLayout(LayoutKind.Explicit, Size = 112)]
    private struct EncoderProperties
    {
        [FieldOffset(0)] public uint Size;
        [FieldOffset(4)] public byte Version;
        [FieldOffset(24)] public int Type;
        [FieldOffset(36)] public uint EncoderConfigFlags;
        [FieldOffset(40)] public uint FeatureSupportedFlags;
        [FieldOffset(44)] public uint AdvancedFeatureSupportedFlags;
    }

    [StructLayout(LayoutKind.Explicit, Size = 24)]
    private struct ArcSyncMonitorParams
    {
        [FieldOffset(0)] public uint Size;
        [FieldOffset(4)] public byte Version;
        [FieldOffset(5)] public byte IsSupported;
        [FieldOffset(8)] public float MinHz;
        [FieldOffset(12)] public float MaxHz;
        [FieldOffset(16)] public uint MaxFrameTimeIncreaseUs;
        [FieldOffset(20)] public uint MaxFrameTimeDecreaseUs;
    }

    [StructLayout(LayoutKind.Explicit, Size = 28)]
    private struct ArcSyncProfileParams
    {
        [FieldOffset(0)] public uint Size;
        [FieldOffset(4)] public byte Version;
        [FieldOffset(8)] public int Profile;
        [FieldOffset(12)] public float MaxHz;
        [FieldOffset(16)] public float MinHz;
        [FieldOffset(20)] public uint MaxFrameTimeIncreaseUs;
        [FieldOffset(24)] public uint MaxFrameTimeDecreaseUs;
    }

    /// <summary>ctl_scaling_settings_t (진단용).</summary>
    [StructLayout(LayoutKind.Explicit, Size = 28)]
    private struct ScalingSettings
    {
        [FieldOffset(0)] public uint Size;
        [FieldOffset(4)] public byte Version;
        [FieldOffset(5)] public byte Enable;
        [FieldOffset(8)] public uint ScalingType;
        [FieldOffset(12)] public uint CustomScalingX;
        [FieldOffset(16)] public uint CustomScalingY;
        [FieldOffset(20)] public byte HardwareModeSet;
        [FieldOffset(24)] public uint PreferredScalingType;
    }

    [StructLayout(LayoutKind.Explicit, Size = 148)]
    private struct DisplaySettings
    {
        [FieldOffset(0)] public uint Size;
        [FieldOffset(4)] public byte Version;
        [FieldOffset(5)] public byte Set;
        [FieldOffset(8)] public uint SupportedFlags;
        [FieldOffset(12)] public uint ControllableFlags;
        [FieldOffset(16)] public uint ValidFlags;
        [FieldOffset(20)] public int LowLatency;
    }

    // ── 함수 바인딩 ────────────────────────────────────

    [UnmanagedFunctionPointer(CallingConvention.Cdecl)] private delegate int InitFn(ref InitArgs args, out IntPtr api);
    [UnmanagedFunctionPointer(CallingConvention.Cdecl)] private delegate int CloseFn(IntPtr api);
    [UnmanagedFunctionPointer(CallingConvention.Cdecl)] private delegate int EnumFn(IntPtr parent, ref uint count, [In, Out] IntPtr[]? handles);
    /// <summary>ctlXxx(handle, struct*) 꼴 함수 전부. 구조체는 <see cref="Fn{T}"/>가 네이티브 버퍼로 옮겨 넘긴다.</summary>
    [UnmanagedFunctionPointer(CallingConvention.Cdecl)] private delegate int PtrFn(IntPtr handle, IntPtr value);

    /// <summary>handle + 구조체 포인터 함수. 제네릭 델리게이트는 마샬링이 안 되므로 버퍼 복사로 감싼다.</summary>
    private sealed class Fn<T>(PtrFn f) where T : struct
    {
        public int Invoke(IntPtr handle, ref T value)
        {
            var buf = Marshal.AllocHGlobal(Marshal.SizeOf<T>());
            try
            {
                Marshal.StructureToPtr(value, buf, false);
                var rc = f(handle, buf);
                value = Marshal.PtrToStructure<T>(buf);
                return rc;
            }
            finally
            {
                Marshal.FreeHGlobal(buf);
            }
        }
    }

    private sealed class Native
    {
        public required InitFn Init;
        public required CloseFn Close;
        public required EnumFn EnumerateDevices;
        public required EnumFn EnumerateDisplayOutputs;
        public required Fn<Feature3DCaps> Supported3D;
        public required Fn<Feature3DGetSet> GetSet3D;
        public required Fn<RetroScalingCaps> RetroCaps;
        public required Fn<RetroScalingSettings> RetroGetSet;
        public required Fn<DisplayProperties> DisplayProps;
        public required Fn<EncoderProperties> EncoderProps;
        public required Fn<SharpnessCaps> SharpCaps;
        public required Fn<SharpnessSettings> SharpGet;
        public required Fn<SharpnessSettings> SharpSet;
        public required Fn<ArcSyncMonitorParams> ArcSyncInfo;
        public required Fn<ArcSyncProfileParams> ArcSyncProfile;
        public required Fn<ScalingSettings> ScalingGet;
        public Fn<DisplaySettings>? DisplaySettingsGetSet; // intelGEDll에는 없음

        public int GetSupported3DCapabilities(IntPtr h, ref Feature3DCaps v) => Supported3D.Invoke(h, ref v);
        public int GetSet3DFeature(IntPtr h, ref Feature3DGetSet v) => GetSet3D.Invoke(h, ref v);
        public int GetRetroScalingCaps(IntPtr h, ref RetroScalingCaps v) => RetroCaps.Invoke(h, ref v);
        public int GetSetRetroScaling(IntPtr h, ref RetroScalingSettings v) => RetroGetSet.Invoke(h, ref v);
        public int GetDisplayProperties(IntPtr h, ref DisplayProperties v) => DisplayProps.Invoke(h, ref v);
        public int GetEncoderProperties(IntPtr h, ref EncoderProperties v) => EncoderProps.Invoke(h, ref v);
        public int GetSharpnessCaps(IntPtr h, ref SharpnessCaps v) => SharpCaps.Invoke(h, ref v);
        public int GetCurrentSharpness(IntPtr h, ref SharpnessSettings v) => SharpGet.Invoke(h, ref v);
        public int SetCurrentSharpness(IntPtr h, ref SharpnessSettings v) => SharpSet.Invoke(h, ref v);
        public int GetArcSyncInfo(IntPtr h, ref ArcSyncMonitorParams v) => ArcSyncInfo.Invoke(h, ref v);
        public int GetArcSyncProfile(IntPtr h, ref ArcSyncProfileParams v) => ArcSyncProfile.Invoke(h, ref v);
        public int GetCurrentScaling(IntPtr h, ref ScalingSettings v) => ScalingGet.Invoke(h, ref v);

        public static Native? TryBind(IntPtr lib)
        {
            var missing = false;
            T D<T>(string name) where T : Delegate
            {
                if (NativeLibrary.TryGetExport(lib, name, out var p)) return Marshal.GetDelegateForFunctionPointer<T>(p);
                missing = true;
                return null!;
            }
            Fn<T> P<T>(string name) where T : struct => new(D<PtrFn>(name));

            var n = new Native
            {
                Init = D<InitFn>("ctlInit"),
                Close = D<CloseFn>("ctlClose"),
                EnumerateDevices = D<EnumFn>("ctlEnumerateDevices"),
                EnumerateDisplayOutputs = D<EnumFn>("ctlEnumerateDisplayOutputs"),
                Supported3D = P<Feature3DCaps>("ctlGetSupported3DCapabilities"),
                GetSet3D = P<Feature3DGetSet>("ctlGetSet3DFeature"),
                RetroCaps = P<RetroScalingCaps>("ctlGetSupportedRetroScalingCapability"),
                RetroGetSet = P<RetroScalingSettings>("ctlGetSetRetroScaling"),
                DisplayProps = P<DisplayProperties>("ctlGetDisplayProperties"),
                EncoderProps = P<EncoderProperties>("ctlGetAdaperDisplayEncoderProperties"),
                SharpCaps = P<SharpnessCaps>("ctlGetSharpnessCaps"),
                SharpGet = P<SharpnessSettings>("ctlGetCurrentSharpness"),
                SharpSet = P<SharpnessSettings>("ctlSetCurrentSharpness"),
                ArcSyncInfo = P<ArcSyncMonitorParams>("ctlGetIntelArcSyncInfoForMonitor"),
                ArcSyncProfile = P<ArcSyncProfileParams>("ctlGetIntelArcSyncProfile"),
                ScalingGet = P<ScalingSettings>("ctlGetCurrentScaling"),
            };
            if (missing) return null;
            if (NativeLibrary.TryGetExport(lib, "ctlGetSetDisplaySettings", out var ds))
                n.DisplaySettingsGetSet = new Fn<DisplaySettings>(Marshal.GetDelegateForFunctionPointer<PtrFn>(ds));
            return n;
        }
    }
}

public sealed class IgclException(string message, int code) : Exception(message)
{
    public int Code { get; } = code;
}
