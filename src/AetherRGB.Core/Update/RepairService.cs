using AetherRGB.Core.Config;

namespace AetherRGB.Core.Update;

public sealed record RepairResult(List<string> StepsPerformed, List<string> Warnings);

/// <summary>
/// "Reparieren"-Funktion aus dem Hauptfenster: behebt die typischen, lokal lösbaren
/// Probleme ohne Neuinstallation — defekte/fehlende Konfiguration, verlorene Verbindung
/// zum lokalen OpenRGB-SDK-Server. Fasst nie Dateien außerhalb des eigenen Config-Ordners an.
/// </summary>
public sealed class RepairService
{
    public RepairResult Repair(string? configPath = null)
    {
        var steps = new List<string>();
        var warnings = new List<string>();
        var path = configPath ?? AppConfig.DefaultPath;

        if (!File.Exists(path))
        {
            new AppConfig().Save(path);
            steps.Add("Keine Konfigurationsdatei gefunden — neue Standardkonfiguration angelegt.");
        }
        else
        {
            try
            {
                var cfg = AppConfig.LoadOrCreateDefault(path);
                cfg.Save(path);
                steps.Add("Konfiguration geladen und neu geschrieben (Validierung bestanden).");
            }
            catch
            {
                File.Copy(path, path + ".broken-" + DateTime.UtcNow.Ticks, overwrite: false);
                new AppConfig().Save(path);
                warnings.Add("Konfiguration war beschädigt — defekte Datei als Backup gesichert, Standard wiederhergestellt.");
            }
        }

        steps.Add("Lokale API wird beim nächsten Start neu an den konfigurierten Port gebunden.");
        steps.Add("OpenRGB-/WLED-/Home-Assistant-Verbindungen werden beim nächsten Start neu aufgebaut.");

        return new RepairResult(steps, warnings);
    }
}
