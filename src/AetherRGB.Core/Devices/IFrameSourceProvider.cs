namespace AetherRGB.Core.Devices;

/// <summary>Liefert die Farben einer Zone für den Zeitpunkt "jetzt + <paramref name="lead"/>" auf der
/// Effekt-Uhr. Das Array enthält nur die LEDs, die laut LED-Anordnung belegt sind (LED 1 … n) — alle
/// LEDs dahinter sind unbenutzt und schwarz. Wird aus dem Sende-Thread des Providers aufgerufen.</summary>
public delegate RgbColor[] ZoneFrameSource(TimeSpan lead);

/// <summary>Optional für Provider, die langsamer senden, als die Effekt-Engine rechnet (MSI SDK: ~31 ms
/// pro Aufruf). Statt das zuletzt gepushte, bis zu einen Engine-Takt alte Bild zu übernehmen, holt sich
/// der Provider die Farben genau dann, wenn er sendet — so sind die Zeitschritte auf den LEDs gleich
/// groß, auch wenn der Provider nur 10 Bilder/s schafft. Die Engine meldet für solche Provider nur, welche
/// Zonen gerade von einem laufenden Effekt-Fenster gesteuert werden, und pusht keine Farben.</summary>
public interface IFrameSourceProvider
{
    /// <summary><paramref name="source"/> = null: Die Zone wird von keinem laufenden Effekt mehr gesteuert
    /// (entfernt, Fenster pausiert/geschlossen) — dann nicht mehr senden, die LEDs behalten ihre Farbe.</summary>
    void SetZoneFrameSource(string deviceId, int zoneIndex, ZoneFrameSource? source);
}
