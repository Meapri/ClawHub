namespace ClawApp.Input;

/// <summary>
/// 진단용: 사용자 데이터 폴더에 engine-debug.on 파일이 있을 때만 engine-debug.log에 고급 입력 엔진의 흐름을 적는다
/// (적용된 프로필, 엔진 버튼의 운반 키, 원형 메뉴 열기·닫기). 일반 키 입력은 적지 않는다.
/// </summary>
internal static class EngineLog
{
    private static readonly object Gate = new();
    private static readonly StreamWriter? Writer = Open();

    public static bool On => Writer is not null;

    private static StreamWriter? Open()
    {
        try
        {
            var dir = ClawCore.JsonStore.UserDir;
            if (!File.Exists(Path.Combine(dir, "engine-debug.on"))) return null;
            var w = new StreamWriter(Path.Combine(dir, "engine-debug.log"), append: true) { AutoFlush = true };
            w.WriteLine($"--- {DateTime.Now:yyyy-MM-dd HH:mm:ss} pid {Environment.ProcessId} {Environment.ProcessPath} raised={ClawCore.ShellLauncher.IsRaised}");
            return w;
        }
        catch (Exception) { return null; }
    }

    public static void Write(string text)
    {
        if (Writer is null) return;
        lock (Gate)
        {
            try { Writer.WriteLine($"{DateTime.Now:HH:mm:ss.fff} {text}"); }
            catch (Exception) { /* 기록 실패는 무시 */ }
        }
    }
}
