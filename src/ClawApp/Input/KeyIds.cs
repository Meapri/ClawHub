using System.Windows.Input;

namespace ClawApp.Input;

/// <summary>WPF 키·마우스 입력 → 컨트롤러 펌웨어 키 ID (KeyCodes). 매핑 테스트가 "무슨 키가 나왔는지" 보여 줄 때 쓴다.</summary>
public static class KeyIds
{
    private static readonly Dictionary<Key, string> Map = Build();

    public static string? From(KeyEventArgs e)
    {
        var key = e.Key switch
        {
            Key.System => e.SystemKey,          // Alt, F10
            Key.ImeProcessed => e.ImeProcessedKey, // 한글 입력 중 글자 키
            _ => e.Key,
        };
        return Map.GetValueOrDefault(key);
    }

    public static string? From(MouseButton button) => button switch
    {
        MouseButton.Left => "MS_LEFT",
        MouseButton.Middle => "MS_MIDDLE",
        MouseButton.Right => "MS_RIGHT",
        MouseButton.XButton1 => "MS_B4",
        MouseButton.XButton2 => "MS_B5",
        _ => null,
    };

    private static Dictionary<Key, string> Build()
    {
        var m = new Dictionary<Key, string>();
        for (var k = Key.A; k <= Key.Z; k++) m[k] = "KB_" + k;
        for (var i = 0; i <= 9; i++)
        {
            m[Key.D0 + i] = $"KB_{i}";
            m[Key.NumPad0 + i] = $"KB_NUM{i}";
        }
        for (var i = 1; i <= 12; i++) m[Key.F1 + i - 1] = $"KB_F{i}";
        m[Key.F22] = "KB_F22"; m[Key.F23] = "KB_F23";
        m[Key.Escape] = "KB_ESC"; m[Key.Oem3] = "KB_GRAVE"; m[Key.OemMinus] = "KB_MINUS"; m[Key.OemPlus] = "KB_EQUAL";
        m[Key.Back] = "KB_BACKSPACE"; m[Key.Tab] = "KB_TAB"; m[Key.OemOpenBrackets] = "KB_LBRACKET"; m[Key.Oem6] = "KB_RBRACKET";
        m[Key.Oem5] = "KB_BACKSLASH"; m[Key.Capital] = "KB_CAPSLOCK"; m[Key.Oem1] = "KB_SEMICOLON"; m[Key.OemQuotes] = "KB_QUOTE";
        m[Key.Return] = "KB_ENTER"; m[Key.LeftShift] = "KB_LSHIFT"; m[Key.RightShift] = "KB_RSHIFT";
        m[Key.OemComma] = "KB_COMMA"; m[Key.OemPeriod] = "KB_PERIOD"; m[Key.OemQuestion] = "KB_SLASH";
        m[Key.LeftCtrl] = "KB_LCTRL"; m[Key.LWin] = "KB_WIN"; m[Key.RWin] = "KB_WIN"; m[Key.LeftAlt] = "KB_LALT"; m[Key.Space] = "KB_SPACE";
        // 한국어 키보드: 오른쪽 Alt = 한/영(VK_HANGUL), 오른쪽 Ctrl = 한자(VK_HANJA)
        m[Key.RightAlt] = "KB_RALT"; m[Key.HangulMode] = "KB_RALT";
        m[Key.RightCtrl] = "KB_RCTRL"; m[Key.HanjaMode] = "KB_RCTRL";
        m[Key.Insert] = "KB_INSERT"; m[Key.Home] = "KB_HOME"; m[Key.PageUp] = "KB_PAGEUP";
        m[Key.Delete] = "KB_DELETE"; m[Key.End] = "KB_END"; m[Key.PageDown] = "KB_PAGEDOWN";
        m[Key.Up] = "KB_UP"; m[Key.Down] = "KB_DOWN"; m[Key.Left] = "KB_LEFT"; m[Key.Right] = "KB_RIGHT";
        m[Key.NumLock] = "KB_NUMLOCK"; m[Key.Divide] = "KB_NUM_DIV"; m[Key.Multiply] = "KB_NUM_MUL";
        m[Key.Subtract] = "KB_NUM_SUB"; m[Key.Add] = "KB_NUM_ADD"; m[Key.Decimal] = "KB_NUM_DOT";
        m[Key.MediaPlayPause] = "MD_PLAY"; m[Key.MediaStop] = "MD_STOP"; m[Key.MediaNextTrack] = "MD_NEXT";
        m[Key.MediaPreviousTrack] = "MD_PREV"; m[Key.VolumeUp] = "MD_VOLUP"; m[Key.VolumeDown] = "MD_VOLDOWN"; m[Key.VolumeMute] = "MD_MUTE";
        return m;
    }
}
