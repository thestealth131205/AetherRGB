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

/// <summary>Über welchen Weg AetherRGB ein MSI-Mainboard (Mystic Light) steuert.</summary>
public enum MsiBackend
{
    /// <summary>Noch nicht entschieden — bei erkanntem MSI-Board fragt die App beim Start nach.</summary>
    Ask,
    /// <summary>Offizielles MSI Mystic Light SDK: klinkt sich in den laufenden MSI-Center-Dienst
    /// (LEDKeeper2) ein, saubere Koexistenz mit MSI Center, aber begrenzte Bildrate.</summary>
    MysticLightSdk,
    /// <summary>Eigene Variante: direkt per USB/HID, ohne MSI Center (experimentell, siehe
    /// AetherRGB.Providers.MsiMysticLight).</summary>
    DirectHid,
    /// <summary>MSI-Board nicht direkt steuern (z. B. weil es schon über OpenRGB läuft).</summary>
    Off
}

public sealed class MsiMysticLightConfig
{
    [JsonConverter(typeof(JsonStringEnumConverter))]
    public MsiBackend Backend { get; set; } = MsiBackend.Ask;

    /// <summary>Optionaler Pfad zur MysticLight_SDK(_x64).dll. Leer = im Programmordner und in
    /// %APPDATA%\AetherRGB suchen. Die DLL wird nicht mitgeliefert (MSI-Lizenz).</summary>
    public string SdkDllPath { get; set; } = "";

    /// <summary>Nur für <see cref="MsiBackend.MysticLightSdk"/>: höchstens so viele Farb-Updates pro
    /// Sekunde an MSI schicken. Jeder SetLedColor-Aufruf wird zu einem USB-Schreibvorgang am
    /// Board-Controller; zu viele davon bringen ihn zum Absturz (USB trennt und verbindet neu). Bei
    /// Problemen regelt der Provider selbst unter diesen Wert herunter.</summary>
    public int SdkMaxUpdatesPerSecond { get; set; } = 15;

    /// <summary>Nur für <see cref="MsiBackend.MysticLightSdk"/>: ARGB-Anschlüsse (JRAINBOW, PCH) LED für LED
    /// über "Direct Lighting Control" steuern. Aus = jeder Anschluss zeigt eine Mischfarbe.</summary>
    public bool SdkPerLed { get; set; } = true;

    /// <summary>Nur für <see cref="MsiBackend.DirectHid"/>: LED-Anzahl der Sammelzone.</summary>
    public int LedCountOverride { get; set; } = 1;

    /// <summary>Alter Schalter aus Version ≤ 0.3.0 — wird beim Laden in <see cref="Backend"/>
    /// übernommen und danach nicht mehr geschrieben.</summary>
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public bool? Enabled { get; set; }
}

public sealed class AppConfig
{
    public OpenRgbConfig OpenRgb { get; set; } = new();
    public List<WledInstanceConfig> WledInstances { get; set; } = new();
    public HomeAssistantConfig HomeAssistant { get; set; } = new();
    public LocalApiConfig LocalApi { get; set; } = new();
    public MsiMysticLightConfig MsiMysticLight { get; set; } = new();
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
                if (loaded is not null)
                {
                    if (loaded.MsiMysticLight.Enabled == true && loaded.MsiMysticLight.Backend == MsiBackend.Ask)
                        loaded.MsiMysticLight.Backend = MsiBackend.DirectHid;
                    loaded.MsiMysticLight.Enabled = null;
                    return loaded;
                }
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
