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

    /// <summary>Höchstens ein Befehl pro HA-Licht alle so viele Sekunden (Mittelwert der Effektfarben
    /// dazwischen). Mindestens 3 — mehr vertragen viele Lampen/Funknetze nicht.</summary>
    public double MinUpdateIntervalSeconds { get; set; } = 3;

    /// <summary>HA-<c>transition</c> = Intervall mitsenden, damit Lampen weich überblenden.</summary>
    public bool UseTransition { get; set; } = true;
}

/// <summary>Spiele mit Razer-Chroma-Unterstützung: Empfang über die Bridge-DLL (UDP 127.0.0.1:34535) und
/// den nachgebauten Chroma-REST-Server (localhost:54235). Beides nur lokal.</summary>
public sealed class RazerChromaConfig
{
    public bool Enabled { get; set; } = true;
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
    /// Problemen regelt der Provider selbst unter diesen Wert herunter. Standard 10 (seit 0.3.6, vorher 15):
    /// am B550 mit 3 Anschlüssen à ~31 ms die natürliche Grenze, dort flüssig und stabil.</summary>
    public int SdkMaxUpdatesPerSecond { get; set; } = 10;

    /// <summary>Nur für <see cref="MsiBackend.MysticLightSdk"/>: ARGB-Anschlüsse (JRAINBOW, PCH) LED für LED
    /// über "Direct Lighting Control" steuern. Aus = jeder Anschluss zeigt eine Mischfarbe.</summary>
    public bool SdkPerLed { get; set; } = true;

    /// <summary>Nur für <see cref="MsiBackend.MysticLightSdk"/> mit <see cref="SdkPerLed"/>: nur die laut
    /// LED-Anordnung belegten LEDs senden, der Rest des Headers wird einmal schwarz gesetzt.</summary>
    public bool SdkSendOnlyUsedLeds { get; set; } = true;

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

    /// <summary>WLED-Effektbilder per UDP-Echtzeit (DRGB/DNRGB) statt per JSON-API senden.</summary>
    public bool WledUseUdpRealtime { get; set; } = true;
    public HomeAssistantConfig HomeAssistant { get; set; } = new();
    public LocalApiConfig LocalApi { get; set; } = new();
    public RazerChromaConfig RazerChroma { get; set; } = new();
    public MsiMysticLightConfig MsiMysticLight { get; set; } = new();
    public bool CheckForUpdatesOnStartup { get; set; } = true;

    /// <summary>Beim Anmelden an Windows minimiert (im Tray) starten. Standard an — fehlt in Dateien bis
    /// 0.3.6, dann gilt ebenfalls an.</summary>
    public bool StartWithWindows { get; set; } = true;

    /// <summary>Stand der Konfigurationsdatei für einmalige Umstellungen beim Laden. Fehlt in Dateien bis
    /// 0.3.5 (= 0); neue Konfigurationen und gespeicherte Dateien tragen <see cref="CurrentConfigVersion"/>.</summary>
    public int ConfigVersion { get; set; }
    public const int CurrentConfigVersion = 1;

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
                    // 0.3.6: MSI-Standard von 15 auf 10 Updates/s. Nur den alten Standardwert umstellen,
                    // einen bewusst anders gewählten Wert behalten.
                    if (loaded.ConfigVersion < 1 && loaded.MsiMysticLight.SdkMaxUpdatesPerSecond == 15)
                        loaded.MsiMysticLight.SdkMaxUpdatesPerSecond = 10;
                    loaded.ConfigVersion = CurrentConfigVersion;
                    return loaded;
                }
            }
        }
        catch
        {
            // Defekte/unlesbare Konfiguration -> auf Default zurückfallen, nicht die App zum Absturz bringen.
            // Die "Reparieren"-Funktion (RepairService) überschreibt die Datei danach mit einer gültigen Version.
        }
        return new AppConfig { ConfigVersion = CurrentConfigVersion };
    }

    public void Save(string? path = null)
    {
        path ??= DefaultPath;
        var dir = Path.GetDirectoryName(path)!;
        Directory.CreateDirectory(dir);
        File.WriteAllText(path, JsonSerializer.Serialize(this, JsonOptions));
    }
}
