using System.IO;
using System.Security.Principal;
using Microsoft.Win32;

namespace ClawHubSetup;

/// <summary>
/// 관리자 토큰의 사용자와 실제 로그인한 사용자.
/// 관리자 계정 사용자가 UAC "예"로 올리면 둘이 같다. 표준 사용자가 다른 관리자 계정의 암호를 넣어 올리면 다르다 —
/// 그때 HKCU는 관리자 계정의 것이므로 Run 키는 HKEY_USERS\{로그인 사용자 SID}에 쓰고,
/// 사용자별 MSIX(위젯·홈 신원 패키지)는 다른 사용자에게 설치할 수 없어 건너뛴다.
/// </summary>
internal sealed class UserContext
{
    public required SecurityIdentifier ElevatedSid { get; init; }
    public required string ElevatedName { get; init; }
    public SecurityIdentifier? InteractiveSid { get; init; }
    public string? InteractiveName { get; init; }

    /// <summary>로그인 사용자를 알 수 없으면 같은 사용자로 본다.</summary>
    public bool SameUser => InteractiveSid is null || InteractiveSid == ElevatedSid;
    public SecurityIdentifier TargetSid => InteractiveSid ?? ElevatedSid;
    public string TargetName => InteractiveName ?? ElevatedName;

    public static UserContext Detect()
    {
        using var me = WindowsIdentity.GetCurrent();
        SecurityIdentifier? sid = null;
        var name = Native.SessionUser();
        if (name is not null)
        {
            try { sid = (SecurityIdentifier)new NTAccount(name).Translate(typeof(SecurityIdentifier)); }
            catch (Exception) { name = null; } // 번역 실패 → 같은 사용자로 간주
        }
        return new UserContext { ElevatedSid = me.User!, ElevatedName = me.Name, InteractiveSid = sid, InteractiveName = name };
    }

    public static bool IsAdministrator()
    {
        using var me = WindowsIdentity.GetCurrent();
        return new WindowsPrincipal(me).IsInRole(WindowsBuiltInRole.Administrator);
    }

    /// <summary>로그인 사용자의 HKCU 아래 키를 연다 (다른 사용자면 HKEY_USERS\SID — 로그인 중이라 하이브가 로드돼 있다).</summary>
    private RegistryKey? OpenUserKey(string subKey, bool create)
    {
        var root = SameUser ? Registry.CurrentUser : Registry.Users;
        var path = SameUser ? subKey : $@"{TargetSid.Value}\{subKey}";
        return create ? root.CreateSubKey(path, writable: true) : root.OpenSubKey(path, writable: true);
    }

    public string? GetRunValue()
    {
        try
        {
            var root = SameUser ? Registry.CurrentUser : Registry.Users;
            using var key = root.OpenSubKey(SameUser ? K.RunKey : $@"{TargetSid.Value}\{K.RunKey}");
            return key?.GetValue(K.RunValue) as string;
        }
        catch (Exception) { return null; }
    }

    public void SetRunValue(string command)
    {
        using var key = OpenUserKey(K.RunKey, create: true) ?? throw new SetupException("Run 키를 열 수 없어요.");
        key.SetValue(K.RunValue, command, RegistryValueKind.String);
    }

    /// <summary>Run 값을 지운다. sid가 주어지면 그 사용자의 하이브에서 (로드돼 있을 때만).</summary>
    public static bool DeleteRunValue(string? sid)
    {
        using var key = sid is null
            ? Registry.CurrentUser.OpenSubKey(K.RunKey, writable: true)
            : Registry.Users.OpenSubKey($@"{sid}\{K.RunKey}", writable: true);
        if (key?.GetValue(K.RunValue) is null) return false;
        key.DeleteValue(K.RunValue, throwOnMissingValue: false);
        return true;
    }

    /// <summary>로그인 사용자의 %LocalAppData%\ClawHub (사용자 데이터: 설정·라이브러리·매핑 프로필).</summary>
    public string UserDataDir
    {
        get
        {
            if (SameUser)
                return Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "ClawHub");
            using var key = Registry.LocalMachine.OpenSubKey($@"SOFTWARE\Microsoft\Windows NT\CurrentVersion\ProfileList\{TargetSid.Value}");
            var profile = key?.GetValue("ProfileImagePath") as string
                ?? throw new SetupException($"{TargetName}의 프로필 폴더를 찾을 수 없어요.");
            return Path.Combine(Environment.ExpandEnvironmentVariables(profile), "AppData", "Local", "ClawHub");
        }
    }

    public string Describe() => SameUser
        ? $"사용자: {TargetName}"
        : $"관리자 계정: {ElevatedName} / 로그인 사용자: {TargetName} (서로 다름)";
}

/// <summary>사용자에게 그대로 보여 줄 수 있는 설치 오류.</summary>
internal sealed class SetupException(string message, Exception? inner = null) : Exception(message, inner);
