using AetherRGB.Core.Devices;
using AetherRGB.Core.Layout;

namespace AetherRGB.Core.Effects;

/// <summary>Treibt alle laufenden Effekt-Fenster (<see cref="EffectScene"/>): für jede LED jedes dort
/// platzierten Geräts wird anhand ihrer Position (Rechteck + <see cref="LedShape"/>) die Effektfarbe
/// gesampelt und an den zuständigen Provider gesendet. Die ältere, rein pro Zone arbeitende
/// <see cref="EffectEngine"/> bleibt für einfache "ein Effekt pro Zone ohne Layout"-Fälle bestehen.</summary>
public sealed class CanvasEffectEngine : IDisposable
{
    private DeviceRegistry _registry;
    private SceneCollection _scenes = new();
    private LedShapeStore _shapes = new();
    private Timer? _timer;
    private readonly DateTime _start = DateTime.UtcNow;
    private readonly int _fps;
    private int _ticking;

    public CanvasEffectEngine(DeviceRegistry registry, int fps = 30)
    {
        _registry = registry;
        _fps = fps;
    }

    /// <summary>Gemeinsame Effektzeit — die UI-Vorschau nutzt sie, damit Vorschau und echte LEDs synchron laufen.</summary>
    public TimeSpan Elapsed => DateTime.UtcNow - _start;

    public bool IsRunning => _timer is not null;

    public void SetRegistry(DeviceRegistry registry) => _registry = registry;
    public void SetScenes(SceneCollection scenes) => _scenes = scenes;
    public void SetShapes(LedShapeStore shapes) => _shapes = shapes;

    public void Start()
    {
        _timer?.Dispose();
        int intervalMs = Math.Max(10, 1000 / Math.Max(1, _fps));
        _timer = new Timer(_ => Tick(), null, 0, intervalMs);
    }

    public void Stop()
    {
        _timer?.Dispose();
        _timer = null;
    }

    /// <summary>Farben aller LEDs eines platzierten Geräts für den aktuellen Zeitpunkt, inkl. Helligkeit.
    /// Hat die Form weniger LEDs als das Gerät meldet, bleiben die übrigen schwarz.
    /// <paramref name="elapsed"/> ist die gemeinsame Engine-Uhr, die Szene rechnet daraus ihre
    /// Effektzeit inkl. Geschwindigkeit.</summary>
    public static RgbColor[] RenderItem(EffectScene scene, DeviceLayoutItem item, LedShape shape, TimeSpan elapsed)
    {
        var positions = item.ComputeLedPositions(shape);
        var colors = new RgbColor[Math.Max(positions.Count, item.LedCount)];
        double brightness = Math.Clamp(scene.Brightness, 0, 100) / 100.0 * Math.Clamp(item.Brightness, 0, 100) / 100.0;
        var effect = scene.Effect;
        var effectTime = scene.GetEffectTime(elapsed);
        for (int i = 0; i < positions.Count; i++)
        {
            var (x, y) = positions[i];
            colors[i] = Scale(effect.Sample(x, y, scene.CanvasWidth, scene.CanvasHeight, effectTime), brightness);
        }
        return colors;
    }

    private static RgbColor Scale(RgbColor c, double f) =>
        f >= 1.0 ? c : new RgbColor((byte)(c.R * f), (byte)(c.G * f), (byte)(c.B * f));

    private void Tick()
    {
        // Ein langsamer Frame (z. B. viele Geräte) darf sich nie mit dem nächsten überlappen.
        if (Interlocked.Exchange(ref _ticking, 1) == 1) return;
        try
        {
            var elapsed = Elapsed;
            foreach (var scene in _scenes.Scenes.ToArray())
            {
                if (!scene.IsRunning) continue;
                foreach (var item in scene.Items.ToArray())
                {
                    var provider = _registry.FindProviderFor(item.DeviceId);
                    if (provider is null || !provider.IsConnected) continue;

                    var colors = RenderItem(scene, item, _shapes.Get(item.DeviceId, item.ZoneIndex, item.LedCount), elapsed);

                    // Bewusst "fire and forget" mit Fehlerschlucken pro Tick — ein einzelner fehlgeschlagener
                    // Frame (z. B. kurzer WLED-Netzwerkhänger) darf die laufende Animation auf allen anderen
                    // Geräten nie unterbrechen.
                    var deviceId = item.DeviceId;
                    _ = provider.SetZoneColorsAsync(deviceId, item.ZoneIndex, colors).ContinueWith(t =>
                    {
                        if (t.Exception is not null) ReportSendError(deviceId, t.Exception);
                    }, TaskScheduler.Default);
                }
            }
        }
        catch
        {
            // Die UI kann Listen genau während des Kopierens ändern — dann einfach diesen Frame auslassen.
        }
        finally
        {
            Volatile.Write(ref _ticking, 0);
        }
    }

    /// <summary>Fehler beim Senden an ein Gerät: Geräte-ID, Fehlertext. Pro Gerät höchstens alle
    /// <see cref="SendErrorInterval"/> gemeldet (mit Anzahl der unterdrückten), sonst flutet ein
    /// offline gegangener WLED-Strip bei 30+ FPS das Log.</summary>
    public event Action<string, string>? SendError;

    private static readonly TimeSpan SendErrorInterval = TimeSpan.FromSeconds(30);
    private readonly System.Collections.Concurrent.ConcurrentDictionary<string, (DateTime LastReported, int Suppressed)> _sendErrors = new();

    private void ReportSendError(string deviceId, Exception ex)
    {
        var now = DateTime.UtcNow;
        bool report = false;
        int suppressed = 0;
        _sendErrors.AddOrUpdate(deviceId,
            _ => { report = true; return (now, 0); },
            (_, s) =>
            {
                if (now - s.LastReported < SendErrorInterval) { report = false; return (s.LastReported, s.Suppressed + 1); }
                report = true; suppressed = s.Suppressed;
                return (now, 0);
            });
        if (!report) return;
        SendError?.Invoke(deviceId, Diagnostics.ExceptionText.Describe(ex) +
            (suppressed > 0 ? $" ({suppressed} weitere Fehler in den letzten {SendErrorInterval.TotalSeconds:0} s)" : ""));
    }

    public void Dispose() => Stop();
}
