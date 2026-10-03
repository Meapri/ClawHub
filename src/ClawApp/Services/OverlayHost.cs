using System.Diagnostics;
using System.IO;
using System.IO.Pipes;
using System.Text;
using System.Text.Json;
using System.Windows;
using ClawApp.Input;
using ClawApp.Views;
using ClawCore.Mapping;

namespace ClawApp.Services;

/// <summary>오버레이 프로세스에 보내는 명령 한 줄.</summary>
public sealed class OverlayMessage
{
    public string Op { get; set; } = "";
    public List<RadialSlice>? Slices { get; set; }
    public int Index { get; set; }
    public int Size { get; set; }
    public bool Activated { get; set; }
    public string? Icon { get; set; }
    public string? Text { get; set; }
    public bool Warn { get; set; }
    public int Level { get; set; }
    public PerfOverlayData? Perf { get; set; }
}

/// <summary>
/// 전체 화면 게임 위에 뜨는 창(원형 메뉴·알림·성능 오버레이)을 그리는 별도 프로세스.
/// 일반 앱의 "항상 위" 창은 전체 화면 게임(독점·전체 화면 최적화) 위에 올라가지 못하고, Windows가 접근성(UIAccess)으로 인정한
/// 서명된 프로그램의 창만 그 위에 뜬다. 그런데 UIAccess 앱은 패키지 실행(Xbox 모드 홈 앱)으로는 시작되지 않으므로
/// ClawApp.exe는 일반 앱으로 두고, 같은 프로그램을 UIAccess 매니페스트로 서명한 ClawOverlay.exe(install.ps1이 만든다)를
/// "--overlay-host"로 띄워 창만 맡긴다. 앱 → 오버레이는 이름 있는 파이프로 JSON 한 줄씩.
/// ClawOverlay.exe가 없거나(개발 빌드) 연결이 끊기면 <see cref="Send"/>가 false — 앱이 직접 그린다.
/// </summary>
public static class OverlayHost
{
    public const string HostArg = "--overlay-host";

    private static readonly object Gate = new();
    private static StreamWriter? _writer;
    private static NamedPipeServerStream? _pipe;
    private static int _generation;
    private static DateTime _retryAt = DateTime.MinValue;
    private static bool _launching;

    /// <summary>이 프로세스가 오버레이 쪽인지.</summary>
    public static bool IsHost { get; private set; }

    private static string? ExePath =>
        Path.Combine(AppContext.BaseDirectory, "ClawOverlay.exe") is var p && File.Exists(p) ? p : null;

    // ── 앱 쪽 ────────────────────────────────────────

    /// <summary>앱 시작 때 (오버레이 프로그램이 있으면 띄워 둔다 — 원형 메뉴가 처음부터 바로 뜨게).</summary>
    public static void Start()
    {
        if (!IsHost) Launch();
    }

    private static void Launch()
    {
        if (ExePath is not { } exe) return;
        lock (Gate)
        {
            if (_writer is not null || _launching || DateTime.UtcNow < _retryAt) return;
            _launching = true;
            _retryAt = DateTime.UtcNow.AddSeconds(10); // 실패해도 너무 자주 다시 띄우지 않게
        }
        var name = $"ClawHub.Overlay.{Environment.ProcessId}.{Interlocked.Increment(ref _generation)}";
        NamedPipeServerStream pipe;
        try
        {
            pipe = new NamedPipeServerStream(name, PipeDirection.Out, 1, PipeTransmissionMode.Byte, PipeOptions.Asynchronous);
            // UIAccess 실행 파일은 셸(AppInfo)을 거쳐야 시작된다 — CreateProcess로는 거부된다
            Process.Start(new ProcessStartInfo(exe, $"{HostArg} {name}") { UseShellExecute = true });
        }
        catch (Exception)
        {
            lock (Gate) _launching = false;
            return;
        }
        _ = ConnectAsync(pipe);
    }

    private static async Task ConnectAsync(NamedPipeServerStream pipe)
    {
        try
        {
            using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(15));
            await pipe.WaitForConnectionAsync(timeout.Token);
            lock (Gate)
            {
                _pipe = pipe;
                _writer = new StreamWriter(pipe, new UTF8Encoding(false)) { AutoFlush = true };
            }
        }
        catch (Exception)
        {
            pipe.Dispose();
        }
        finally
        {
            lock (Gate) _launching = false;
        }
    }

    /// <summary>오버레이 프로세스에 보낸다. 연결이 없으면 false (호출한 쪽이 직접 그린다). 어느 스레드에서나.</summary>
    public static bool Send(OverlayMessage message)
    {
        if (IsHost) return false;
        lock (Gate)
        {
            if (_writer is { } w)
            {
                try
                {
                    w.WriteLine(JsonSerializer.Serialize(message, ClawCore.JsonStore.Options));
                    return true;
                }
                catch (Exception ex) when (ex is IOException or ObjectDisposedException)
                {
                    _writer = null;
                    _pipe?.Dispose();
                    _pipe = null;
                    _retryAt = DateTime.MinValue; // 오버레이가 죽었다 — 바로 다시 띄운다
                }
            }
        }
        Launch();
        return false;
    }

    // ── 오버레이 쪽 ───────────────────────────────────

    /// <summary>"--overlay-host 파이프이름"으로 시작했을 때: 창만 만들고 명령을 기다린다. 앱이 끝나면(파이프가 닫히면) 끝.</summary>
    public static void RunHost(Application app, string pipeName)
    {
        IsHost = true;
        app.ShutdownMode = ShutdownMode.OnExplicitShutdown;
        var radial = new RadialMenuWindow();
        var radialView = (AdvancedInputEngine.IRadialView)radial;
        PerfOverlayWindow? perf = null;

        void Handle(OverlayMessage m)
        {
            switch (m.Op)
            {
                case "radial.show": radialView.Show(m.Slices ?? [], m.Size); break;
                case "radial.select": radialView.Select(m.Index); break;
                case "radial.hide": radialView.Hide(m.Activated); break;
                case "osd": OsdWindow.Show(m.Icon ?? "", m.Text ?? "", m.Warn); break;
                case "perf" when m.Perf is not null: (perf ??= new PerfOverlayWindow()).Update(m.Level, m.Perf); break;
                case "perf.hide": perf?.Hide(); break;
            }
        }

        new Thread(() =>
        {
            try
            {
                using var pipe = new NamedPipeClientStream(".", pipeName, PipeDirection.In);
                pipe.Connect(10_000);
                using var reader = new StreamReader(pipe, Encoding.UTF8);
                while (reader.ReadLine() is { } line)
                {
                    OverlayMessage? m;
                    try { m = JsonSerializer.Deserialize<OverlayMessage>(line, ClawCore.JsonStore.Options); }
                    catch (JsonException) { continue; }
                    if (m is not null) app.Dispatcher.BeginInvoke(() => Handle(m));
                }
            }
            catch (Exception) { /* 앱이 끝났거나 연결 실패 */ }
            app.Dispatcher.BeginInvoke(() => app.Shutdown());
        }) { IsBackground = true, Name = "ClawHub.OverlayHost" }.Start();
    }
}

/// <summary>원형 메뉴: 오버레이 프로세스가 있으면 거기서, 없으면 앱 안 창으로.</summary>
public sealed class RadialViewRouter(AdvancedInputEngine.IRadialView local) : AdvancedInputEngine.IRadialView
{
    private bool _remote; // 지금 열린 메뉴가 어느 쪽인지 (열 때 정한다)

    public void Show(IReadOnlyList<RadialSlice> slices, int size)
    {
        _remote = OverlayHost.Send(new OverlayMessage { Op = "radial.show", Slices = [.. slices], Size = size });
        if (!_remote) local.Show(slices, size);
    }

    public void Select(int index)
    {
        if (!_remote || !OverlayHost.Send(new OverlayMessage { Op = "radial.select", Index = index })) local.Select(index);
    }

    public void Hide(bool activated)
    {
        if (!_remote || !OverlayHost.Send(new OverlayMessage { Op = "radial.hide", Activated = activated })) local.Hide(activated);
    }
}
