using System.Text.Json;
using System.Text.Json.Serialization;

namespace ClawCore;

public static class JsonStore
{
    public static readonly JsonSerializerOptions Options = new()
    {
        WriteIndented = true,
        // 숫자로 된 enum 값은 받지 않는다 (예: "ChargeLimit": 5 → 검증 없이 EC로 가는 것 방지)
        Converters = { new JsonStringEnumConverter(allowIntegerValues: false) },
    };

    /// <summary>사용자 데이터 (프로필, 라이브러리, 설정) — 앱이 소유.</summary>
    /// <remarks>CLAWHUB_USERDIR 환경 변수로 바꿀 수 있다 (화면 점검용 실행이 실제 설정·배치를 건드리지 않게).</remarks>
    public static string UserDir => Ensure(Environment.GetEnvironmentVariable("CLAWHUB_USERDIR") is { Length: > 0 } dir
        ? dir
        : Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "ClawHub"));

    /// <summary>서비스 데이터 (컨트롤러 baseline 등) — 서비스가 소유.</summary>
    public static string ServiceDir => Ensure(Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData), "ClawHub"));

    public static T Load<T>(string path) where T : new()
    {
        try
        {
            return File.Exists(path) ? JsonSerializer.Deserialize<T>(File.ReadAllText(path), Options) ?? new T() : new T();
        }
        catch (JsonException)
        {
            File.Copy(path, path + ".broken", overwrite: true);
            return new T();
        }
    }

    public static void Save<T>(string path, T value)
    {
        var tmp = path + ".tmp";
        File.WriteAllText(tmp, JsonSerializer.Serialize(value, Options));
        File.Move(tmp, path, overwrite: true);
    }

    private static string Ensure(string dir)
    {
        Directory.CreateDirectory(dir);
        return dir;
    }
}
