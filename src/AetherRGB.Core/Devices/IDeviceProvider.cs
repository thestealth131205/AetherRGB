namespace AetherRGB.Core.Devices;

/// <summary>
/// Gemeinsame Schnittstelle für jede Geräteklasse (OpenRGB-Bridge für Motherboard-ARGB,
/// WLED, Home Assistant, künftig native Hersteller-Treiber). Die Effekt-Engine kennt nur
/// diese Schnittstelle, nie die konkrete Hersteller-API.
/// </summary>
public interface IDeviceProvider : IAsyncDisposable
{
    /// <summary>Kurzer, eindeutiger Name des Providers (z. B. "openrgb", "wled", "homeassistant").</summary>
    string ProviderId { get; }

    /// <summary>True, sobald eine aktive Verbindung besteht (lokal — nie über das Internet).</summary>
    bool IsConnected { get; }

    /// <summary>Baut die Verbindung auf bzw. aktualisiert die Geräteliste. Wirft nicht bei Nichterreichbarkeit,
    /// sondern liefert eine leere Liste und setzt <see cref="IsConnected"/> auf false — ein einzelner
    /// nicht erreichbarer Provider darf die anderen nie blockieren.</summary>
    Task ConnectAsync(CancellationToken ct = default);

    Task<IReadOnlyList<RgbDevice>> GetDevicesAsync(CancellationToken ct = default);

    /// <summary>Setzt alle LEDs einer Zone auf dieselbe Farbe (für einfache Sync-Effekte/statische Farben).</summary>
    Task SetZoneColorAsync(string deviceId, int zoneIndex, RgbColor color, CancellationToken ct = default);

    /// <summary>Setzt jede LED einer Zone individuell — für Effekte, die pro Pixel unterschiedliche Farben brauchen
    /// (Regenbogen-Welle, Lauflicht usw.). <paramref name="colors"/> muss genau <c>LedZone.LedCount</c> Einträge haben.</summary>
    Task SetZoneColorsAsync(string deviceId, int zoneIndex, IReadOnlyList<RgbColor> colors, CancellationToken ct = default);
}
