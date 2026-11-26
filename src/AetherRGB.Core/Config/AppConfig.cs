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

public enum ScreenCaptureMethod
{
    /// <summary>Desktop Duplication, bei Fehlern GDI.</summary>
    Auto,
    /// <summary>Nur Desktop Duplication (DXGI) — wie OBS „Bildschirmaufnahme“.</summary>
    DesktopDuplication,
    /// <summary>Nur GDI (BitBlt) — langsamer, aber auch dort, wo DXGI nicht geht. Zeigt bei exklusivem Vollbild oft Schwarz.</summary>
    Gdi,
}

/// <summary>Bildschirm-Sync: liest nur das fertige Bild eines Monitors über die Windows-Bildschirmaufnahme.</summary>
public sealed class ScreenSyncConfig
{
    /// <summary>Gerätename des Monitors (z. B. <c>\\.\DISPLAY1</c>), leer = automatisch der Monitor mit dem Fenster im Vordergrund.</summary>
    public string Monitor { get; set; } = "";

    [JsonConverter(typeof(JsonStringEnumConverter))]
    public ScreenCaptureMethod Method { get; set; } = ScreenCaptureMethod.Auto;

    /// <summary>Höchstens so viele Bilder pro Sekunde aufnehmen und auswerten. 15 reicht für Leben, Sturm und Schaden
    /// und kostet etwa halb so viel CPU wie 30.</summary>
    public int MaxFps { get; set; } = 15;

    /// <summary>Sättigung der gemessenen Mischfarben in Prozent (100 = wie auf dem Bildschirm). Gemittelte Farben sind
    /// blass, auf LEDs wirken sie mit mehr Sättigung wie das Bild.</summary>
    public int SaturationPercent { get; set; } = 160;

    public const int MinSaturationPercent = 100, MaxSaturationPercent = 300;

    /// <summary>Profil, wenn kein Spiel mit eigenem Profil im Vordergrund läuft.</summary>
    public string DefaultProfileId { get; set; } = "fortnite";
}

/// <summary>Sensor-Panel auf einem Zweitdisplay (z. B. 5"-USB-Display mit 1024 × 600), Werte aus Windows, NVIDIA und AIDA64.</summary>
public sealed class SensorPanelConfig
{
    /// <summary>Panel als randloses Vollbild auf dem Zweitdisplay zeigen.</summary>
    public bool DisplayEnabled { get; set; }

    /// <summary>Gerätename des Monitors (z. B. <c>\\.\DISPLAY3</c>), leer = automatisch: ein Monitor, der nicht der
    /// Hauptmonitor ist, bevorzugt mit genau der Panel-Auflösung.</summary>
    public string Monitor { get; set; } = "";

    public string PanelId { get; set; } = "uebersicht";

    /// <summary>Werte aus dem gemeinsamen Speicher von AIDA64 lesen (dort muss „Gemeinsamen Speicher aktivieren“ an sein).</summary>
    public bool UseAida64 { get; set; } = true;

    /// <summary>NVIDIA-Grafikkarten über nvml.dll aus dem Treiber abfragen.</summary>
    public bool UseNvidia { get; set; } = true;

    /// <summary>Abfrage-Intervall der Sensoren in Millisekunden.</summary>
    public int IntervalMs { get; set; } = 1000;

    /// <summary>Bilder pro Sekunde auf dem Display (Animationen); das 5"-Display schafft laut Hersteller 25.</summary>
    public int Fps { get; set; } = 25;

    /// <summary>Panel-Fenster immer im Vordergrund halten.</summary>
    public bool Topmost { get; set; } = true;
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

    /// <summary>Erlaubt die Windows-Aufgabenplanung, AetherRGB beim Anmelden mit Administratorrechten
    /// und OHNE UAC-Abfrage zu starten (nötig, damit das Mystic Light SDK im Autostart funktioniert).
    /// Null = noch nicht gefragt — die App fragt dann einmal nach, bevor sie die Aufgabe anlegt.
    /// False = abgelehnt, kein erneutes Nachfragen; Autostart läuft dann ohne Adminrechte (UAC bei jeder
    /// Anmeldung). Absichtlich kein automatisches "ja", weil ein elevierter Task ohne UAC-Abfrage genau
    /// das Muster ist, nach dem Antiviren-Heuristik (und wachsame Nutzer) suchen.</summary>
    public bool? ElevatedAutostartConsent { get; set; }

    /// <summary>Alter Schalter aus Version ≤ 0.3.0 — wird beim Laden in <see cref="Backend"/>
    /// übernommen und danach nicht mehr geschrieben.</summary>
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public bool? Enabled { get; set; }
}

/// <summary>Wie ein Lüfter angesteuert wird. <see cref="SmartEfficient"/>/<see cref="SmartQuiet"/> sind das
/// Ergebnis der Smart-Kalibrierung — technisch auch eine Kurve (<see cref="FanProfileConfig.Curve"/>), der
/// Modus merkt sich nur, welches Profil zuletzt angelegt wurde.</summary>
/// <summary>Redragon-Maus (z. B. M914) per eigenem, reverse-engineertem USB/HID-Protokoll — bewusst ohne
/// OpenRGB als Zwischenschicht, weil die M914 dort nicht bestätigt unterstützt ist. Siehe
/// <see cref="AetherRGB.Providers.Redragon.RedragonHidProtocol"/> für den Status (unverifiziert).</summary>
public sealed class RedragonConfig
{
    public bool Enabled { get; set; }

    /// <summary>0 = Standard-Vendor-ID 0x258A (Redragon/Dragonrise) verwenden.</summary>
    public int VendorIdOverride { get; set; }

    /// <summary>0 = alle HID-Geräte unter der Vendor-ID durchprobieren statt einer festen Produkt-ID.
    /// Hilfreich, falls mehrere Redragon-Geräte angeschlossen sind oder die Auto-Erkennung das falsche
    /// Interface trifft — der Wert steht nach einem erfolgreichen Verbinden im Log.</summary>
    public int ProductIdOverride { get; set; }

    /// <summary>LED-Anzahl der gemeldeten Sammelzone (die M914 hat nach öffentlich sichtbaren Fotos ein
    /// Logo und einen Scrollrad-Ring, ohne verifizierte Einzelansteuerung also eine Zone).</summary>
    public int LedCountOverride { get; set; } = 1;
}

public enum CoolingFanMode
{
    /// <summary>Feste Ansteuerung (<see cref="FanProfileConfig.ManualPercent"/>), unabhängig von der Temperatur.</summary>
    Manual,
    /// <summary>Eigene Kurve (<see cref="FanProfileConfig.Curve"/>).</summary>
    Curve,
    /// <summary>Durch die Smart-Kalibrierung erzeugt, auf niedrige Temperatur optimiert (dreht früher hoch).</summary>
    SmartEfficient,
    /// <summary>Durch die Smart-Kalibrierung erzeugt, auf Lautstärke optimiert (bleibt länger auf dem Minimum).</summary>
    SmartQuiet
}

public sealed class FanCurvePointConfig
{
    public double TempC { get; set; }
    public int Percent { get; set; }
}

/// <summary>Einstellungen für einen erkannten Lüfter (<see cref="Cooling.FanInfo.Id"/>).</summary>
public sealed class FanProfileConfig
{
    public required string FanId { get; set; }
    public string DisplayName { get; set; } = "";

    [JsonConverter(typeof(JsonStringEnumConverter))]
    public CoolingFanMode Mode { get; set; } = CoolingFanMode.Curve;

    public int ManualPercent { get; set; } = 60;

    /// <summary>Kurve für <see cref="CoolingFanMode.Curve"/> und die Smart-Profile — Standard entspricht
    /// dem Grundprofil für Lüfter ohne Drehzahlsignal: bis 40 °C 50 %, linear bis 70 °C auf 100 %.</summary>
    public List<FanCurvePointConfig> Curve { get; set; } = new()
    {
        new FanCurvePointConfig { TempC = 0, Percent = 50 },
        new FanCurvePointConfig { TempC = 40, Percent = 50 },
        new FanCurvePointConfig { TempC = 70, Percent = 100 },
        new FanCurvePointConfig { TempC = 100, Percent = 100 },
    };

    /// <summary>Sensor-ID aus dem <c>SensorHub</c> (z. B. <c>lhm/temp/cpu</c>, <c>aida/TCPU</c>), die die Kurve
    /// steuert. Leer = automatisch die erste gefundene CPU-Temperatur.</summary>
    public string TempSensorId { get; set; } = "";

    /// <summary>Niedrigste Ansteuerung in %, bei der der Lüfter laut Kalibrierung ein Drehzahlsignal liefert.
    /// <c>null</c> = nicht kalibriert.</summary>
    public int? MeasuredMinPercent { get; set; }

    /// <summary>false = der Lüfter hat beim Kalibrieren bis 100 % kein Drehzahlsignal geliefert (z. B. ein
    /// reiner Pumpen-/Lichtausgang ohne Tacho). Bleibt dann auf dem Grundprofil, keine Smart-Kalibrierung.</summary>
    public bool HasRpmSignal { get; set; } = true;

    public bool Calibrated { get; set; }
    public DateTime? LastCalibrationUtc { get; set; }
}

/// <summary>Automatische Lüftersteuerung über erkannte PWM-Kanäle (LibreHardwareMonitor, braucht Adminrechte).</summary>
public sealed class CoolingConfig
{
    public bool Enabled { get; set; }

    /// <summary>Wie oft Temperatur geprüft und ggf. nachgeregelt wird.</summary>
    public int IntervalMs { get; set; } = 1000;

    public List<FanProfileConfig> Fans { get; set; } = new();
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
    public RedragonConfig Redragon { get; set; } = new();
    public ScreenSyncConfig ScreenSync { get; set; } = new();
    public SensorPanelConfig SensorPanel { get; set; } = new();
    public CoolingConfig Cooling { get; set; } = new();
    public bool CheckForUpdatesOnStartup { get; set; } = true;

    /// <summary>Beim Anmelden an Windows minimiert (im Tray) starten. Standard an — fehlt in Dateien bis
    /// 0.3.6, dann gilt ebenfalls an.</summary>
    public bool StartWithWindows { get; set; } = true;

    /// <summary>Stand der Konfigurationsdatei für einmalige Umstellungen beim Laden. Fehlt in Dateien bis
    /// 0.3.5 (= 0); neue Konfigurationen und gespeicherte Dateien tragen <see cref="CurrentConfigVersion"/>.</summary>
    public int ConfigVersion { get; set; }
    public const int CurrentConfigVersion = 2;

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
                    // 0.3.12: Bildschirm-Sync-Standard von 30 auf 15 Bilder/s (CPU sparen). Nur den alten Standardwert umstellen.
                    if (loaded.ConfigVersion < 2 && loaded.ScreenSync.MaxFps == 30)
                        loaded.ScreenSync.MaxFps = 15;
                    loaded.ScreenSync.SaturationPercent = Math.Clamp(loaded.ScreenSync.SaturationPercent,
                        ScreenSyncConfig.MinSaturationPercent, ScreenSyncConfig.MaxSaturationPercent);
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
