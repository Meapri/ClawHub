using System.IO;
using System.IO.Compression;
using System.Xml.Linq;

namespace ClawHubSetup;

/// <summary>MSIX/APPX의 AppxManifest.xml Identity.</summary>
internal sealed record AppxIdentity(string Name, string Publisher, Version Version, string Architecture)
{
    public static AppxIdentity Read(Stream package)
    {
        using var zip = new ZipArchive(package, ZipArchiveMode.Read, leaveOpen: true);
        var entry = zip.GetEntry("AppxManifest.xml") ?? throw new SetupException("패키지에 AppxManifest.xml이 없어요.");
        using var s = entry.Open();
        var doc = XDocument.Load(s);
        var id = doc.Root?.Elements().FirstOrDefault(e => e.Name.LocalName == "Identity")
            ?? throw new SetupException("AppxManifest.xml에 Identity가 없어요.");
        return new AppxIdentity(
            (string?)id.Attribute("Name") ?? "",
            (string?)id.Attribute("Publisher") ?? "",
            Version.Parse((string?)id.Attribute("Version") ?? "0.0.0.0"),
            ((string?)id.Attribute("ProcessorArchitecture") ?? "neutral").ToLowerInvariant());
    }

    public static AppxIdentity ReadFile(string path)
    {
        using var f = File.OpenRead(path);
        return Read(f);
    }

    public override string ToString() => $"{Name} {Version} ({Architecture})";
}

/// <summary>
/// ClawHubSetup.exe에 리소스로 들어 있는 payload.zip (tools/ClawHubSetup/build.ps1이 만든다).
/// 구조: version.txt, ClawHub.ico, service/…, app/…, widget/*.msix, widget/deps/*.appx, home/*.msix, certs/*.cer
/// </summary>
internal sealed class Payload : IDisposable
{
    private readonly Stream _stream;
    private readonly ZipArchive _zip;
    private readonly Dictionary<string, ZipArchiveEntry> _entries;

    private Payload(Stream stream)
    {
        _stream = stream;
        _zip = new ZipArchive(stream, ZipArchiveMode.Read);
        // Windows PowerShell 5.1(.NET Framework)로 만든 zip은 경로 구분자가 \일 수 있다
        _entries = _zip.Entries.ToDictionary(e => e.FullName.Replace('\\', '/'), StringComparer.OrdinalIgnoreCase);
        Version = ReadText("version.txt")?.Trim() ?? "0.0.0";
        WidgetMsix = Find("widget/", ".msix").FirstOrDefault();
        WidgetDeps = Find("widget/deps/", ".appx").ToList();
        HomeMsix = Find("home/", ".msix").FirstOrDefault();
        Certs = Find("certs/", ".cer").ToList();
        if (WidgetMsix is not null) WidgetIdentity = ReadIdentity(WidgetMsix);
        if (HomeMsix is not null) HomeIdentity = ReadIdentity(HomeMsix);
    }

    public static Payload? Open()
    {
        var s = typeof(Payload).Assembly.GetManifestResourceStream(K.PayloadResource);
        return s is null ? null : new Payload(s);
    }

    public string Version { get; }
    public string? WidgetMsix { get; }
    public IReadOnlyList<string> WidgetDeps { get; }
    public string? HomeMsix { get; }
    public IReadOnlyList<string> Certs { get; }
    public AppxIdentity? WidgetIdentity { get; }
    public AppxIdentity? HomeIdentity { get; }

    public int FileCount(string prefix) => _entries.Keys.Count(k => k.StartsWith(prefix, StringComparison.OrdinalIgnoreCase) && !k.EndsWith('/'));

    /// <summary>prefix 바로 아래(하위 폴더 제외) 파일 중 확장자가 맞는 것.</summary>
    private IEnumerable<string> Find(string prefix, string ext) => _entries.Keys
        .Where(k => k.StartsWith(prefix, StringComparison.OrdinalIgnoreCase)
                    && k.EndsWith(ext, StringComparison.OrdinalIgnoreCase)
                    && !k[prefix.Length..].Contains('/'))
        .Order(StringComparer.OrdinalIgnoreCase);

    private string? ReadText(string name)
    {
        if (!_entries.TryGetValue(name, out var e)) return null;
        using var r = new StreamReader(e.Open());
        return r.ReadToEnd();
    }

    public byte[] ReadBytes(string name)
    {
        using var s = _entries[name].Open();
        using var ms = new MemoryStream();
        s.CopyTo(ms);
        return ms.ToArray();
    }

    private AppxIdentity ReadIdentity(string name)
    {
        using var ms = new MemoryStream(ReadBytes(name));
        return AppxIdentity.Read(ms);
    }

    /// <summary>prefix("service/") 아래 전부를 target 폴더로 푼다. 반환: 파일 수.</summary>
    public int ExtractDir(string prefix, string target)
    {
        var count = 0;
        var root = Path.GetFullPath(target) + Path.DirectorySeparatorChar;
        foreach (var (name, entry) in _entries)
        {
            if (!name.StartsWith(prefix, StringComparison.OrdinalIgnoreCase)) continue;
            var rel = name[prefix.Length..];
            if (rel.Length == 0) continue;
            var dest = Path.GetFullPath(Path.Combine(target, rel.Replace('/', Path.DirectorySeparatorChar)));
            if (!dest.StartsWith(root, StringComparison.OrdinalIgnoreCase)) throw new SetupException($"잘못된 페이로드 경로: {name}");
            if (name.EndsWith('/')) { Directory.CreateDirectory(dest); continue; }
            Directory.CreateDirectory(Path.GetDirectoryName(dest)!);
            entry.ExtractToFile(dest, overwrite: true);
            count++;
        }
        return count;
    }

    /// <summary>파일 하나를 dir에 원래 이름으로 푼다.</summary>
    public string ExtractFile(string name, string dir)
    {
        Directory.CreateDirectory(dir);
        var dest = Path.Combine(dir, Path.GetFileName(name));
        _entries[name].ExtractToFile(dest, overwrite: true);
        return dest;
    }

    public bool Has(string name) => _entries.ContainsKey(name);

    public string Describe()
    {
        var parts = new List<string>
        {
            $"버전 {Version}",
            $"서비스 파일 {FileCount("service/")}개",
            $"앱 파일 {FileCount("app/")}개",
            WidgetIdentity is null ? "위젯 없음" : $"위젯 {WidgetIdentity.Version} + 의존 패키지 {WidgetDeps.Count}개",
            HomeIdentity is null ? "홈 신원 패키지 없음" : $"홈 신원 패키지 {HomeIdentity.Version}",
            $"인증서 {Certs.Count}개",
        };
        return string.Join(", ", parts);
    }

    public void Dispose()
    {
        _zip.Dispose();
        _stream.Dispose();
    }
}
