using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Text;

namespace ClawHubSetup;

/// <summary>화면 로그 + 파일 로그 (%TEMP%\ClawHubSetup-*.log).</summary>
internal sealed class SetupLog : IDisposable
{
    private readonly StreamWriter? _file;
    private readonly Lock _gate = new();

    public SetupLog(string? filePath)
    {
        FilePath = filePath;
        if (filePath is null) return;
        try { _file = new StreamWriter(filePath, append: false, new UTF8Encoding(true)) { AutoFlush = true }; }
        catch (IOException) { FilePath = null; }
        catch (UnauthorizedAccessException) { FilePath = null; }
    }

    public string? FilePath { get; }
    public event Action<string>? Line;

    public static string NewFilePath(string kind) =>
        Path.Combine(Path.GetTempPath(), $"ClawHubSetup-{kind}-{DateTime.Now:yyyyMMdd-HHmmss}.log");

    public void Info(string message) => Write("  ", message);
    public void Step(string message) => Write("▶ ", message);
    public void Ok(string message) => Write("  ✓ ", message);
    public void Warn(string message) => Write("  ! ", message);
    public void Error(string message) => Write("  ✗ ", message);

    private void Write(string prefix, string message)
    {
        var line = prefix + message;
        lock (_gate) _file?.WriteLine($"{DateTime.Now:HH:mm:ss} {line}");
        Line?.Invoke(line);
    }

    public void Dispose() => _file?.Dispose();
}

internal static class ProcessRunner
{
    static ProcessRunner() => Encoding.RegisterProvider(CodePagesEncodingProvider.Instance);

    /// <summary>콘솔 도구(sc.exe, schtasks.exe)를 창 없이 실행. 출력은 OEM 코드 페이지(한국어 Windows = 949).</summary>
    public static (int ExitCode, string Output) Run(string exe, string arguments, int timeoutMs = 30000)
    {
        var oem = Encoding.GetEncoding(CultureInfo.CurrentCulture.TextInfo.OEMCodePage);
        var psi = new ProcessStartInfo(exe, arguments)
        {
            CreateNoWindow = true,
            UseShellExecute = false,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            StandardOutputEncoding = oem,
            StandardErrorEncoding = oem,
        };
        using var p = Process.Start(psi) ?? throw new SetupException($"{exe}를 실행할 수 없어요.");
        var stdout = p.StandardOutput.ReadToEndAsync();
        var stderr = p.StandardError.ReadToEndAsync();
        if (!p.WaitForExit(timeoutMs))
        {
            try { p.Kill(); } catch (InvalidOperationException) { }
            throw new SetupException($"{exe} {arguments} 시간 초과");
        }
        var output = (stdout.Result + stderr.Result).Trim();
        return (p.ExitCode, string.Join(" ", output.Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)));
    }
}
