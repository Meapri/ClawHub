using System.Text.Json;
using ClawCore;
using ClawCore.Gpu;
using ClawCore.Ipc;

// IGCL 화면 품질 점검 (기본: 읽기 전용).
//   IgclProbe              → 진단 덤프 + 현재 값
//   IgclProbe --toggle     → 선명도·저지연을 잠깐 바꿨다가 즉시 원래대로 (무해한 항목만)

//   IgclProbe --framestats → PresentMon CSV 파서/FPS 계산 자체 시험 (가짜 데이터, ETW 불필요)

Console.OutputEncoding = System.Text.Encoding.UTF8;

if (args.Contains("--framestats"))
{
    // PresentMon 1.x(-no_track_display) 헤더 + 60fps 주 스왑체인 2초 + 가끔 30ms 끊김 + 오버레이 스왑체인(무시돼야 함)
    var stats = new FrameStats();
    stats.AddLine("Application,ProcessID,SwapChainAddress,Runtime,SyncInterval,PresentFlags,AllowsTearing,PresentMode,Dropped,TimeInSeconds,msInPresentAPI,msBetweenPresents");
    var t = 0.0;
    for (var i = 0; i < 120; i++)
    {
        var ms = i % 50 == 49 ? 30.0 : 16.0;
        t += ms / 1000;
        stats.AddLine($"game.exe,1234,0x0000021A,DXGI,1,0,0,Hardware: Independent Flip,0,{t:0.000000},0.1,{ms:0.000}");
        if (i % 10 == 0) stats.AddLine($"game.exe,1234,0x0000099B,DXGI,1,0,0,Composed: Flip,0,{t:0.000000},0.1,100.000");
    }
    var snap = stats.Snapshot(TimeSpan.FromSeconds(5));
    Console.WriteLine($"FrameStats: {snap}");
    var ok = snap is { } s && s.Fps is > 58 and < 62 && s.FpsLow1 is > 33 and < 34;
    Console.WriteLine(ok ? "OK (≈60fps, 1% low ≈33.3)" : "FAIL");
    return ok ? 0 : 1;
}

using var igcl = IgclDisplay.TryOpen(out var error);
if (igcl is null)
{
    Console.WriteLine($"IGCL 열기 실패: {error}");
    return 1;
}

foreach (var line in igcl.Dump()) Console.WriteLine(line);
Console.WriteLine();
Console.WriteLine($"intelGEDll GetFrameLimit = {IntelGraphics.GetFrameLimit()}, GetFrameGeneration = {IntelGraphics.GetFrameGeneration()}");

var sw = System.Diagnostics.Stopwatch.StartNew();
for (var i = 0; i < 10; i++) igcl.Read();
Console.WriteLine($"Read() 평균 {sw.Elapsed.TotalMilliseconds / 10:0.0} ms");

var before = igcl.Read();
Console.WriteLine("Read(): " + JsonSerializer.Serialize(before, JsonStore.Options));

// intelGEDll이 자체 ctlInit/ctlClose를 한 뒤에도 우리 핸들이 살아 있는지
_ = IntelGraphics.GetFrameLimit();
var again = igcl.Read();
Console.WriteLine($"intelGEDll 호출 뒤 다시 Read(): error={again.Error ?? "없음"} lowLatency={again.LowLatency} sharpening={again.Sharpening}");

if (args.Contains("--toggle"))
{
    Console.WriteLine();
    if (before.Sharpening is { } sh)
    {
        igcl.Apply(new DisplayQualityChange { Sharpening = !sh });
        var mid = igcl.Read();
        igcl.Apply(new DisplayQualityChange { Sharpening = sh, SharpeningLevel = before.SharpeningLevel });
        var after = igcl.Read();
        Console.WriteLine($"선명도 토글: {sh} → {mid.Sharpening} → 복원 {after.Sharpening} (강도 {before.SharpeningLevel} → {after.SharpeningLevel})");
    }
    if (before.LowLatency is { } ll)
    {
        var other = ll == LowLatencyMode.Off ? LowLatencyMode.On : LowLatencyMode.Off;
        igcl.Apply(new DisplayQualityChange { LowLatency = other });
        var mid = igcl.Read();
        igcl.Apply(new DisplayQualityChange { LowLatency = ll });
        var after = igcl.Read();
        Console.WriteLine($"저지연 토글: {ll} → {mid.LowLatency} → 복원 {after.LowLatency}");
    }
}
return 0;
