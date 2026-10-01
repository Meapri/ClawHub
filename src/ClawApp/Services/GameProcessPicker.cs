using System.Diagnostics;
using System.Runtime.InteropServices;
using ClawCore.Games;

namespace ClawApp.Services;

/// <summary>
/// 게임 세션의 프로세스 중 실제로 화면을 그리는 것 하나를 고른다 (서비스의 FPS 측정 대상).
/// 런처·크래시 리포터·안티치트 같은 보조 프로세스가 같은 폴더에 있어도
/// 앞에 떠 있는 창 → 메인 창이 있는 것 → 메모리를 가장 많이 쓰는 것 순으로 고른다.
/// </summary>
public static class GameProcessPicker
{
    public static int? Pick(GameSession? session)
    {
        if (session is null) return null;
        var procs = new List<Process>();
        foreach (var id in session.FindProcessIds())
        {
            try { procs.Add(Process.GetProcessById(id)); }
            catch (ArgumentException) { /* 이미 종료 */ }
        }
        try
        {
            if (procs.Count == 0) return null;
            GetWindowThreadProcessId(GetForegroundWindow(), out var fg);
            if (procs.FirstOrDefault(p => p.Id == (int)fg) is { } front) return front.Id;

            return procs
                .Select(p => (Proc: p, Window: HasWindow(p), Memory: Memory(p)))
                .OrderByDescending(x => x.Window)
                .ThenByDescending(x => x.Memory)
                .First().Proc.Id;
        }
        finally
        {
            foreach (var p in procs) p.Dispose();
        }
    }

    private static bool HasWindow(Process p)
    {
        try { return p.MainWindowHandle != IntPtr.Zero; }
        catch (InvalidOperationException) { return false; }
    }

    private static long Memory(Process p)
    {
        try { return p.WorkingSet64; }
        catch (Exception ex) when (ex is InvalidOperationException or System.ComponentModel.Win32Exception) { return 0; }
    }

    [DllImport("user32.dll")]
    private static extern IntPtr GetForegroundWindow();

    [DllImport("user32.dll")]
    private static extern uint GetWindowThreadProcessId(IntPtr hwnd, out uint pid);
}
