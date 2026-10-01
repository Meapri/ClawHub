namespace ClawCore.Controller;

/// <summary>매핑 키 선택에서 한 번에 고를 수 있는 조합키 프리셋. 값은 <see cref="KeyCodes"/> ID.</summary>
public sealed record KeyCombo(string Id, string Label, IReadOnlyList<string> Keys);

/// <summary>
/// 컨트롤러 버튼 하나로 Windows 단축키를 누르게 하는 프리셋 (펌웨어 키보드 출력 = 동시 입력 최대 5개).
/// Game Bar 캡처 단축키는 Windows 기본값 기준 — 사용자가 Game Bar 설정에서 바꿨으면 동작하지 않는다.
/// 스크린샷(Win+Alt+PrtSc)은 펌웨어 키 표(MSI KeyMapDefine)에 PrintScreen 코드가 없어 버튼으로는 만들 수 없다
/// → 스크린샷은 위젯/앱 명령(<c>AppCommands.Screenshot</c>)으로만.
/// </summary>
public static class KeyCombos
{
    public static readonly IReadOnlyList<KeyCombo> All =
    [
        new("combo-record", "녹화 시작/중지 (Win+Alt+R)", ["KB_WIN", "KB_LALT", "KB_R"]),
        new("combo-clip", "최근 30초 저장 (Win+Alt+G)", ["KB_WIN", "KB_LALT", "KB_G"]),
    ];
}
