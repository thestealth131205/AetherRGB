using System.Diagnostics;
using AetherRGB.Core.Devices;
using AetherRGB.Core.Effects;

namespace AetherRGB.Core.Screen;

/// <summary>Effekt „Spiel: &lt;Profil&gt;“ — für jedes gespeicherte Bildschirm-Profil einer im Effekt-Dropdown. Anders als
/// „Bildschirm – Spiel-Profil“ gilt immer genau dieses Profil, egal welches Spiel im Vordergrund läuft. Geräte mit
/// zugewiesenen Bereichen zeigen deren Wert direkt (<see cref="ScreenGauge"/>), alle anderen die Ebenen des Profils
/// auf der Effekt-Fläche.</summary>
public sealed class CanvasGameProfileEffect : IDeviceAwareCanvasEffect
{
    /// <summary>Präfix der Katalog-IDs: <c>game:&lt;Profil-ID&gt;</c>.</summary>
    public const string CatalogPrefix = "game:";

    private readonly ScreenHub _hub;
    private long _lastDemand;

    public string ProfileId { get; }
    public string Name { get; }

    public CanvasGameProfileEffect(string profileId, string name, ScreenHub? hub = null)
    {
        ProfileId = profileId;
        Name = name;
        _hub = hub ?? ScreenHub.Shared;
    }

    public static string CatalogId(string profileId) => CatalogPrefix + profileId;

    /// <summary>Profil-ID aus einer Katalog-ID, null wenn es kein Spiel-Effekt ist.</summary>
    public static string? ProfileIdOf(string? effectId) =>
        effectId is not null && effectId.StartsWith(CatalogPrefix, StringComparison.Ordinal) ? effectId[CatalogPrefix.Length..] : null;

    /// <summary>Hält Aufnahme und Auswertung dieses Profils am Laufen. Wird pro LED und Bild gerufen, deshalb
    /// höchstens alle 250 ms an den Hub weitergereicht.</summary>
    private ScreenAnalysis? Current()
    {
        long tick = Environment.TickCount64;
        if (tick - Volatile.Read(ref _lastDemand) > 250)
        {
            Volatile.Write(ref _lastDemand, tick);
            _hub.Demand(ProfileId);
        }
        return _hub.IsLive ? _hub.LatestFor(ProfileId) : null;
    }

    public RgbColor Sample(double x, double y, double canvasWidth, double canvasHeight, TimeSpan elapsed)
    {
        var analysis = Current();
        if (analysis is null) return RgbColor.Black;
        double u = canvasWidth <= 0 ? 0 : x / canvasWidth, v = canvasHeight <= 0 ? 0 : y / canvasHeight;
        return CanvasScreenEffect.SampleProfile(analysis, u, v, elapsed, Stopwatch.GetTimestamp());
    }

    public void ApplyToDevice(Layout.DeviceLayoutItem item, RgbColor[] colors, TimeSpan elapsed) =>
        ScreenGauge.Apply(Current(), item.ScreenRegions, colors, elapsed, Stopwatch.GetTimestamp());

    /// <summary>Katalog-Einträge für alle Profile (setzt <see cref="EffectCatalog.SetScreenProfileEffects"/>).</summary>
    public static void PublishToCatalog(IEnumerable<ScreenProfile> profiles) =>
        EffectCatalog.SetScreenProfileEffects(profiles
            .OrderBy(p => p.Name, StringComparer.CurrentCultureIgnoreCase)
            .Select(p =>
            {
                string id = p.Id, name = "Spiel: " + p.Name;
                return new EffectDescriptor(CatalogId(id), name, () => new CanvasGameProfileEffect(id, name), isAnimated: false);
            }));
}
