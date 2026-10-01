using System.Text.Json;
using System.Text.Json.Serialization;

namespace ClawCore;

public static class JsonStore
{
    public static readonly JsonSerializerOptions Options = new()
    {
        WriteIndented = true,
        Converters = { new JsonStringEnumConverter() },
    };

    /// <summary>사용자 데이터 (프로필, 라이브러리, 설정) — 앱이 소유.</summary>
    public static string UserDir => Ensure(Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "ClawHub"));

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
