using System.Runtime.InteropServices;

namespace ClawCore.Gpu;

/// <summary>
/// Intel 그래픽 드라이버 설정 (FPS 제한, XeSS 프레임 생성).
/// MSI Center M에 들어 있는 intelGEDll.dll(Intel IGCL 래퍼, x64)을 쓴다 — MSI와 같은 호출.
/// 드라이버 전역 설정이라 관리자 권한이 필요 없다.
/// </summary>
public static class IntelGraphics
{
    internal const string DllPath = @"C:\Program Files (x86)\MSI\MSI Center M\intelGEDll.dll";
    private static readonly Lazy<bool> Loaded = new(() => File.Exists(DllPath) && NativeLibrary.TryLoad(DllPath, out _));

    /// <summary>
    /// DLL 함수마다 IGCL(ControlLib)을 LoadLibrary → ctlInit → ctlClose → FreeLibrary 하며 전역 모듈 핸들을 공유한다.
    /// 한 프로세스 안에서 동시에 부르면 서로의 핸들을 해제할 수 있어 모든 호출을 직렬화한다.
    /// </summary>
    internal static readonly object DriverLock = new();

    public static bool Available => Loaded.Value;

    public static readonly int[] FpsPresets = [30, 40, 45, 60, 90, 120];

    // 실기 확인(드라이버 Arc G3 Extreme): Set*은 성공 시 0, Get*은 값 / 0 = 끔 / -1 = 한 번도 설정 안 함(= 끔)

    /// <summary>현재 FPS 제한 (0 = 끔). 실패 시 null.</summary>
    public static int? GetFrameLimit() => Call(GetFrameLimitNative) is { } v ? Math.Max(v, 0) : null;

    public static bool SetFrameLimit(int fps) =>
        Call(() => SetFrameLimitNative(fps > 0, Math.Clamp(fps, 0, 240))) == 0;

    /// <summary>XeSS 프레임 생성: 0 끔, 1 = 2배, 2 = 3배, 3 = 4배. 지원 게임에서만 동작.</summary>
    public static int? GetFrameGeneration() => Call(GetFrameGenerationNative) is { } v ? Math.Max(v, 0) : null;

    public static bool SetFrameGeneration(int level) => Call(() => SetFrameGenerationNative(Math.Clamp(level, 0, 3))) == 0;

    private static int? Call(Func<int> f)
    {
        if (!Available) return null;
        try { lock (DriverLock) return f(); }
        catch (Exception ex) when (ex is EntryPointNotFoundException or DllNotFoundException or SEHException) { return null; }
    }

    [DllImport(DllPath, EntryPoint = "SetFrameLimit", CallingConvention = CallingConvention.StdCall)]
    private static extern int SetFrameLimitNative(bool enable, int frameLimit); // MSI 선언과 동일(BOOL 마샬링)

    [DllImport(DllPath, EntryPoint = "GetFrameLimit", CallingConvention = CallingConvention.StdCall)]
    private static extern int GetFrameLimitNative();

    [DllImport(DllPath, EntryPoint = "SetFrameGeneration", CallingConvention = CallingConvention.StdCall)]
    private static extern int SetFrameGenerationNative(int enableType);

    [DllImport(DllPath, EntryPoint = "GetFrameGeneration", CallingConvention = CallingConvention.StdCall)]
    private static extern int GetFrameGenerationNative();
}
