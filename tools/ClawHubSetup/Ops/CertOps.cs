using System.Security.Cryptography.X509Certificates;

namespace ClawHubSetup.Ops;

/// <summary>사이드로드 MSIX 서명용 공개 인증서(CN=ClawHub Dev)를 LocalMachine\TrustedPeople에 신뢰.</summary>
internal static class CertOps
{
    public static IReadOnlyList<X509Certificate2> Load(Payload payload) => payload.Certs
        .Select(c => X509CertificateLoader.LoadCertificate(payload.ReadBytes(c)))
        .DistinctBy(c => c.Thumbprint)
        .ToList();

    public static bool IsTrusted(string thumbprint)
    {
        using var store = new X509Store(StoreName.TrustedPeople, StoreLocation.LocalMachine);
        store.Open(OpenFlags.ReadOnly | OpenFlags.OpenExistingOnly);
        return store.Certificates.Find(X509FindType.FindByThumbprint, thumbprint, validOnly: false).Count > 0;
    }

    /// <summary>없는 것만 추가. 반환: 새로 추가한 지문 (제거 때 이것만 지운다).</summary>
    public static List<string> Import(IEnumerable<X509Certificate2> certs, SetupLog log)
    {
        var added = new List<string>();
        using var store = new X509Store(StoreName.TrustedPeople, StoreLocation.LocalMachine);
        store.Open(OpenFlags.ReadWrite);
        foreach (var cert in certs)
        {
            if (store.Certificates.Find(X509FindType.FindByThumbprint, cert.Thumbprint, validOnly: false).Count > 0)
            {
                log.Info($"이미 신뢰됨: {cert.Subject} ({cert.Thumbprint})");
                continue;
            }
            store.Add(cert);
            added.Add(cert.Thumbprint);
            log.Ok($"신뢰 추가: {cert.Subject} ({cert.Thumbprint}, {cert.NotAfter:yyyy-MM-dd}까지)");
        }
        return added;
    }

    public static void Remove(IEnumerable<string> thumbprints, SetupLog log)
    {
        using var store = new X509Store(StoreName.TrustedPeople, StoreLocation.LocalMachine);
        store.Open(OpenFlags.ReadWrite);
        foreach (var thumb in thumbprints)
        {
            foreach (var cert in store.Certificates.Find(X509FindType.FindByThumbprint, thumb, validOnly: false))
            {
                store.Remove(cert);
                log.Ok($"신뢰 제거: {cert.Subject} ({thumb})");
            }
        }
    }
}
