using Microsoft.Win32;
using Windows.ApplicationModel;
using Windows.Foundation;
using Windows.Management.Deployment;

namespace ClawHubSetup.Ops;

/// <summary>
/// MSIX 설치/제거 (현재 사용자 = 관리자 토큰의 사용자).
/// 의존 프레임워크 패키지는 같은 아키텍처의 같거나 새 버전이 이미 있으면 넘기지 않는다 —
/// 이미 설치돼 쓰이는 프레임워크(VCLibs, UI.Xaml, .NET Native)를 다시 넘기면 0x80073D02(사용 중)로 실패한다.
/// </summary>
internal static class PackageOps
{
    // 화면(STA)과 설치 작업(MTA) 스레드에서 모두 쓰므로 공유하지 않고 호출마다 만든다
    private static PackageManager Manager => new();

    public static Version ToVersion(PackageVersion v) => new(v.Major, v.Minor, v.Build, v.Revision);

    private static string Arch(Package p) => p.Id.Architecture.ToString().ToLowerInvariant();

    /// <summary>현재 사용자에게 설치된 패키지 중 가장 높은 버전.</summary>
    public static Package? Find(string name, string publisher) => Manager
        .FindPackagesForUser(string.Empty, name, publisher)
        .OrderByDescending(p => ToVersion(p.Id.Version))
        .FirstOrDefault();

    public static Version? InstalledVersion(string name, string publisher)
    {
        try { return Find(name, publisher) is { } p ? ToVersion(p.Id.Version) : null; }
        catch (Exception) { return null; }
    }

    /// <summary>같은 이름·게시자·아키텍처의 같거나 높은 버전이 이미 있나.</summary>
    public static bool IsSatisfied(AppxIdentity dep) => Manager
        .FindPackagesForUser(string.Empty, dep.Name, dep.Publisher)
        .Any(p => Arch(p) == dep.Architecture && ToVersion(p.Id.Version) >= dep.Version);

    public static bool DeveloperModeOn()
    {
        using var key = Registry.LocalMachine.OpenSubKey(K.DevModeKey);
        return key?.GetValue("AllowDevelopmentWithoutDevLicense") is int v && v == 1;
    }

    /// <summary>패키지 추가. externalLocation이 있으면 외부 위치(sparse) 패키지로 등록.</summary>
    public static void Add(string packagePath, IEnumerable<string> dependencyPaths, string? externalLocation)
    {
        var options = new AddPackageOptions { ForceTargetAppShutdown = true };
        foreach (var d in dependencyPaths) options.DependencyPackageUris.Add(new Uri(d));
        if (externalLocation is not null) options.ExternalLocationUri = new Uri(externalLocation);
        Wait(Manager.AddPackageByUriAsync(new Uri(packagePath), options), $"{System.IO.Path.GetFileName(packagePath)} 설치");
    }

    public static void Remove(Package package) =>
        Wait(Manager.RemovePackageAsync(package.Id.FullName), $"{package.Id.Name} 제거");

    private static void Wait(IAsyncOperationWithProgress<DeploymentResult, DeploymentProgress> op, string what)
    {
        try
        {
            var result = op.AsTask().GetAwaiter().GetResult();
            if (result.ExtendedErrorCode is { } err && err.HResult != 0)
                throw new SetupException($"{what} 실패 (0x{err.HResult:X8}): {result.ErrorText}");
        }
        catch (SetupException) { throw; }
        catch (Exception ex)
        {
            string? text = null;
            try { text = op.GetResults()?.ErrorText; } catch (Exception) { /* 결과 없음 */ }
            throw new SetupException($"{what} 실패 (0x{ex.HResult:X8}): {text ?? ex.Message}", ex);
        }
    }
}
