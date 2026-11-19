using System.Text.Json;
using System.Text.Json.Serialization;

namespace AetherRGB.Core.Config;

public sealed class OpenRgbConfig
{
    public bool Enabled { get; set; } = true;
    public string Host { get; set; } = "127.0.0.1";
    public int Port { get; set; } = 6742;
}

public sealed class WledInstanceConfig
{
    public required string Name { get; set; }
    public required string Host { get; set; }
}

public sealed class HomeAssistantConfig
{
    public bool Enabled { get; set; } = false;
    public string BaseUrl { get; set; } = "http://homeassistant.local:8123";
    public string LongLivedToken { get; set; } = "";
}

public sealed class LocalApiConfig
{
    public bool Enabled { get; set; } = true;
    public int Port { get; set; } = 34533;
}

public sealed class AppConfig
{
    public OpenRgbConfig OpenRgb { get; set; } = new();
    public List<WledInstanceConfig> WledInstances { get; set; } = new();
    public HomeAssistantConfig HomeAssistant { get; set; } = new();
    public LocalApiConfig LocalApi { get; set; } = new();
    public bool CheckForUpdatesOnStartup { get; set; } = true;

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = true,
        DefaultIgnoreCondition = JsonIgnoreCondition.Never
    };

    public static string DefaultPath =>
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "AetherRGB", "config.json");

    public static AppConfig LoadOrCreateDefault(string? path = null)
    {
        path ??= DefaultPath;
        try
        {
            if (File.Exists(path))
            {
                var json = File.ReadAllText(path);
                var loaded = JsonSerializer.Deserialize<AppConfig>(json, JsonOptions);
                if (loaded is not null) return loaded;
            }
        }
        catch
        {
            // Defekte/unlesbare Konfiguration -> auf Default zurückfallen, nicht die App zum Absturz bringen.
            // Die "Reparieren"-Funktion (RepairService) überschreibt die Datei danach mit einer gültigen Version.
        }
        return new AppConfig();
    }

    public void Save(string? path = null)
    {
        path ??= DefaultPath;
        var dir = Path.GetDirectoryName(path)!;
        Directory.CreateDirectory(dir);
        File.WriteAllText(path, JsonSerializer.Serialize(this, JsonOptions));
    }
}
