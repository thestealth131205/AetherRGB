namespace AetherRGB.Core.Cooling;

/// <summary>Ein erkannter Lüfter (PWM-Kanal am Mainboard/der Grafikkarte). <see cref="Id"/> ist stabil über
/// Neustarts und wird in <see cref="Config.FanProfileConfig"/> gespeichert.</summary>
public sealed record FanInfo(string Id, string Name, bool Controllable, double? Rpm, int? Percent);

/// <summary>Zugriff auf die Lüfter-PWM-Kanäle der Hardware. Implementiert in <c>AetherRGB.App</c> (braucht
/// Windows/Adminrechte, siehe <c>LibreHardwareMonitorFanSource</c>) — Core bleibt plattformneutral.</summary>
public interface IFanControlSource : IDisposable
{
    /// <summary>Kurzname, z. B. "LibreHardwareMonitor".</summary>
    string Name { get; }

    /// <summary>Zustand für Einstellungen und Fenster, z. B. "verbunden, 3 Lüfter" oder "keine Adminrechte".</summary>
    string Status { get; }

    bool IsAvailable { get; }

    /// <summary>Aktuelle Lüfterliste mit Live-Drehzahl/-Ansteuerung neu einlesen.</summary>
    IReadOnlyList<FanInfo> Detect();

    /// <summary>Ansteuerung in Prozent (0–100) setzen (Software-Modus).</summary>
    void SetPercent(string fanId, int percent);

    /// <summary>Steuerung an die Firmware zurückgeben (automatisch, wie vor AetherRGB).</summary>
    void ReleaseControl(string fanId);
}
