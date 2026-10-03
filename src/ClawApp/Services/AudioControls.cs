using System.Diagnostics;
using System.Runtime.InteropServices;
using Microsoft.Win32;
using NAudio.CoreAudioApi;

namespace ClawApp.Services;

/// <summary>오디오 장치 하나 (출력 또는 입력).</summary>
public sealed record AudioDevice(string Id, string Name, bool IsDefault);

/// <summary>소리를 내는 앱 하나 (볼륨 믹서).</summary>
public sealed record AudioApp(int ProcessId, string Name, float Volume, bool Muted, bool IsSystemSounds);

/// <summary>
/// 사운드 설정 (앱 = 사용자 세션에서 직접, 서비스 필요 없음): 출력·입력 장치 고르기, 볼륨·음소거, 마이크 레벨,
/// 앱별 볼륨(볼륨 믹서), 통화 중 다른 소리 줄이기. COM 개체는 쓰고 바로 놓는다.
/// 기본 장치 바꾸기는 Windows 설정·SoundSwitch와 같은 IPolicyConfig(문서화되지 않은 공개 COM)를 쓴다.
/// </summary>
public static class AudioControls
{
    // ── 장치 ───────────────────────────────────────────────

    public static List<AudioDevice> Devices(DataFlow flow)
    {
        var list = new List<AudioDevice>();
        try
        {
            using var e = new MMDeviceEnumerator();
            string? defaultId = null;
            try
            {
                using var d = e.GetDefaultAudioEndpoint(flow, Role.Multimedia);
                defaultId = d.ID;
            }
            catch (COMException) { /* 장치 없음 */ }
            foreach (var d in e.EnumerateAudioEndPoints(flow, DeviceState.Active))
                using (d)
                    list.Add(new AudioDevice(d.ID, Clean(d.FriendlyName), d.ID == defaultId));
        }
        catch (COMException) { /* 오디오 서비스 없음 */ }
        return list;
    }

    /// <summary>
    /// 표시용 이름: 이모지·기호·보이지 않는 문자(블루투스 기기 이름에 흔하다 — 글꼴에 없어 "≣"처럼 깨져 보였다)를 빼고
    /// 겹친 공백을 하나로, 너무 길면 줄인다.
    /// </summary>
    private static string Clean(string name)
    {
        var kept = name.Where(c => char.GetUnicodeCategory(c) is not (
            System.Globalization.UnicodeCategory.OtherSymbol or System.Globalization.UnicodeCategory.MathSymbol
            or System.Globalization.UnicodeCategory.Surrogate or System.Globalization.UnicodeCategory.Format
            or System.Globalization.UnicodeCategory.Control or System.Globalization.UnicodeCategory.PrivateUse
            or System.Globalization.UnicodeCategory.OtherNotAssigned));
        var text = System.Text.RegularExpressions.Regex.Replace(string.Concat(kept), @"\s{2,}", " ").Trim();
        return text.Length > 40 ? text[..39] + "…" : text;
    }

    /// <summary>기본 장치로 (게임·통화 모두 — 콘솔·멀티미디어·통신 역할을 함께).</summary>
    public static void SetDefault(string deviceId)
    {
        var policy = (IPolicyConfig)new PolicyConfigClient();
        try
        {
            foreach (var role in new[] { ERole.Console, ERole.Multimedia, ERole.Communications })
                Marshal.ThrowExceptionForHR(policy.SetDefaultEndpoint(deviceId, role));
        }
        finally
        {
            Marshal.ReleaseComObject(policy);
        }
    }

    // ── 출력 볼륨·음소거, 입력 레벨 ─────────────────────────

    private static T? WithDefault<T>(DataFlow flow, Func<MMDevice, T> use, T? none)
    {
        try
        {
            using var e = new MMDeviceEnumerator();
            using var d = e.GetDefaultAudioEndpoint(flow, flow == DataFlow.Capture ? Role.Communications : Role.Multimedia);
            return use(d);
        }
        catch (COMException) { return none; }
    }

    public static bool? IsSpeakerMuted() => WithDefault<bool?>(DataFlow.Render, d => d.AudioEndpointVolume.Mute, null);

    public static void SetSpeakerMuted(bool muted) => WithDefault(DataFlow.Render, d => d.AudioEndpointVolume.Mute = muted, false);

    public static int? GetMicLevel() =>
        WithDefault<int?>(DataFlow.Capture, d => (int)Math.Round(d.AudioEndpointVolume.MasterVolumeLevelScalar * 100), null);

    public static void SetMicLevel(int percent) =>
        WithDefault(DataFlow.Capture, d => { d.AudioEndpointVolume.MasterVolumeLevelScalar = Math.Clamp(percent, 0, 100) / 100f; return true; }, false);

    // ── 볼륨 믹서 (지금 출력 장치에서 소리를 낼 수 있는 앱) ─────────────

    public static List<AudioApp> Apps()
    {
        var list = new List<AudioApp>();
        try
        {
            using var e = new MMDeviceEnumerator();
            using var d = e.GetDefaultAudioEndpoint(DataFlow.Render, Role.Multimedia);
            var sessions = d.AudioSessionManager.Sessions;
            for (var i = 0; i < sessions.Count; i++)
            {
                var s = sessions[i];
                if (s.State == NAudio.CoreAudioApi.Interfaces.AudioSessionState.AudioSessionStateExpired) continue;
                var pid = (int)s.GetProcessID;
                var system = s.IsSystemSoundsSession;
                var name = system ? "시스템 소리" : AppName(pid, s.DisplayName);
                if (name is null) continue;
                // 같은 앱의 세션이 여럿이면(브라우저 등) 하나만 — 바꿀 때는 그 앱 세션 전부를 바꾼다
                if (list.Any(a => a.ProcessId == pid)) continue;
                list.Add(new AudioApp(pid, name, s.SimpleAudioVolume.Volume, s.SimpleAudioVolume.Mute, system));
            }
        }
        catch (COMException) { /* 장치 없음 */ }
        return [.. list.OrderBy(a => a.IsSystemSounds).ThenBy(a => a.Name)];
    }

    /// <summary>앱 이름: 세션 표시 이름 → 실행 파일 설명 → 프로세스 이름. 이미 끝난 프로세스면 null.</summary>
    private static string? AppName(int pid, string? display)
    {
        if (!string.IsNullOrWhiteSpace(display) && !display.StartsWith('@')) return display;
        try
        {
            using var p = Process.GetProcessById(pid);
            try
            {
                var desc = p.MainModule?.FileVersionInfo.FileDescription;
                if (!string.IsNullOrWhiteSpace(desc)) return desc;
            }
            catch (Exception) { /* 다른 사용자·보호된 프로세스 */ }
            return p.ProcessName;
        }
        catch (ArgumentException) { return null; }
    }

    /// <summary>그 앱의 모든 세션 볼륨(0~1)·음소거를 바꾼다. null이면 그대로.</summary>
    public static void SetApp(int processId, bool systemSounds, float? volume, bool? muted)
    {
        try
        {
            using var e = new MMDeviceEnumerator();
            using var d = e.GetDefaultAudioEndpoint(DataFlow.Render, Role.Multimedia);
            var sessions = d.AudioSessionManager.Sessions;
            for (var i = 0; i < sessions.Count; i++)
            {
                var s = sessions[i];
                if (systemSounds ? !s.IsSystemSoundsSession : s.IsSystemSoundsSession || (int)s.GetProcessID != processId) continue;
                if (volume is { } v) s.SimpleAudioVolume.Volume = Math.Clamp(v, 0f, 1f);
                if (muted is { } m) s.SimpleAudioVolume.Mute = m;
            }
        }
        catch (COMException) { /* 그사이 장치가 바뀜 */ }
    }

    // ── 통화 중 다른 소리 (Windows 사운드 제어판 "통신" 탭과 같은 값) ─────────

    private const string DuckingKey = @"HKEY_CURRENT_USER\Software\Microsoft\Multimedia\Audio";

    /// <summary>0 다른 소리 끄기 · 1 80% 줄이기(Windows 기본) · 2 50% 줄이기 · 3 그대로.</summary>
    public static int GetDucking() => Registry.GetValue(DuckingKey, "UserDuckingPreference", 1) is int v && v is >= 0 and <= 3 ? v : 1;

    public static void SetDucking(int value) =>
        Registry.SetValue(DuckingKey, "UserDuckingPreference", Math.Clamp(value, 0, 3), RegistryValueKind.DWord);

    // ── IPolicyConfig (기본 장치 바꾸기) ─────────────────────

    private enum ERole { Console = 0, Multimedia = 1, Communications = 2 }

    [ComImport, Guid("870af99c-171d-4f9e-af0d-e63df40c2bc9")]
    private class PolicyConfigClient;

    [ComImport, Guid("f8679f50-850a-41cf-9c72-430f290290c8"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IPolicyConfig
    {
        [PreserveSig] int GetMixFormat([MarshalAs(UnmanagedType.LPWStr)] string device, IntPtr format);
        [PreserveSig] int GetDeviceFormat([MarshalAs(UnmanagedType.LPWStr)] string device, bool useDefault, IntPtr format);
        [PreserveSig] int ResetDeviceFormat([MarshalAs(UnmanagedType.LPWStr)] string device);
        [PreserveSig] int SetDeviceFormat([MarshalAs(UnmanagedType.LPWStr)] string device, IntPtr endpointFormat, IntPtr mixFormat);
        [PreserveSig] int GetProcessingPeriod([MarshalAs(UnmanagedType.LPWStr)] string device, bool useDefault, IntPtr defaultPeriod, IntPtr minimumPeriod);
        [PreserveSig] int SetProcessingPeriod([MarshalAs(UnmanagedType.LPWStr)] string device, IntPtr period);
        [PreserveSig] int GetShareMode([MarshalAs(UnmanagedType.LPWStr)] string device, IntPtr mode);
        [PreserveSig] int SetShareMode([MarshalAs(UnmanagedType.LPWStr)] string device, IntPtr mode);
        [PreserveSig] int GetPropertyValue([MarshalAs(UnmanagedType.LPWStr)] string device, bool fxStore, IntPtr key, IntPtr value);
        [PreserveSig] int SetPropertyValue([MarshalAs(UnmanagedType.LPWStr)] string device, bool fxStore, IntPtr key, IntPtr value);
        [PreserveSig] int SetDefaultEndpoint([MarshalAs(UnmanagedType.LPWStr)] string device, ERole role);
        [PreserveSig] int SetEndpointVisibility([MarshalAs(UnmanagedType.LPWStr)] string device, bool visible);
    }
}
