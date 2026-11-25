using System.Diagnostics;
using AetherRGB.Core.Devices;
using AetherRGB.Core.Layout;
using AetherRGB.Core.Timing;

namespace AetherRGB.Core.Effects;

/// <summary>Treibt alle laufenden Effekt-Fenster (<see cref="EffectScene"/>): für jede LED jedes dort
/// platzierten Geräts wird anhand ihrer Position (Rechteck + <see cref="LedShape"/>) die Effektfarbe
/// gesampelt und an den zuständigen Provider gesendet. Die ältere, rein pro Zone arbeitende
/// <see cref="EffectEngine"/> bleibt für einfache "ein Effekt pro Zone ohne Layout"-Fälle bestehen.
///
/// Der Takt läuft in einem eigenen Thread auf festen Terminen (t0 + n · Intervall) mit
/// <see cref="PreciseWaiter"/> — ein <c>System.Threading.Timer</c> springt unter Windows in 15,6-ms-
/// Schritten, die Bilder kämen dann abwechselnd nach 31 und 47 ms. Provider mit eigenem, langsamerem
/// Sendetakt (<see cref="IFrameSourceProvider"/>, z. B. MSI SDK) bekommen statt Bildern eine
/// Render-Funktion und rechnen die Farben selbst zum Sendezeitpunkt.</summary>
public sealed class CanvasEffectEngine : IDisposable
{
    private DeviceRegistry _registry;
    private SceneCollection _scenes = new();
    private LedShapeStore _shapes = new();
    private readonly Stopwatch _clock = Stopwatch.StartNew();
    private readonly int _fps;
    private Thread? _thread;
    private ManualResetEvent? _stop;

    /// <summary>Zonen, die gerade per <see cref="IFrameSourceProvider"/> laufen, mit dem Fenster und
    /// Element, aus dem ihre Render-Funktion rechnet. Nur im Takt-Thread benutzt.</summary>
    private readonly Dictionary<(IFrameSourceProvider Provider, string DeviceId, int Zone), (EffectScene Scene, DeviceLayoutItem Item)> _sources = new();

    public CanvasEffectEngine(DeviceRegistry registry, int fps = 30)
    {
        _registry = registry;
        _fps = fps;
    }

    /// <summary>Gemeinsame Effektzeit — die UI-Vorschau nutzt sie, damit Vorschau und echte LEDs synchron laufen.</summary>
    public TimeSpan Elapsed => _clock.Elapsed;

    public bool IsRunning => _thread is not null;

    public void SetRegistry(DeviceRegistry registry) => _registry = registry;
    public void SetScenes(SceneCollection scenes) => _scenes = scenes;
    public void SetShapes(LedShapeStore shapes) => _shapes = shapes;

    public void Start()
    {
        Stop();
        _stop = new ManualResetEvent(false);
        _thread = new Thread(() => Run(_stop)) { IsBackground = true, Name = "AetherRGB Effekt-Engine", Priority = ThreadPriority.AboveNormal };
        _thread.Start();
    }

    public void Stop()
    {
        if (_thread is null) return;
        _stop!.Set();
        _thread.Join(2000);
        _stop.Dispose();
        _thread = null;
        _stop = null;
    }

    /// <summary>Alle 60 s: tatsächliche Bildrate der Engine und wie gleichmäßig die Abstände sind.</summary>
    public event Action<string>? Statistics;
    private static readonly TimeSpan StatsInterval = TimeSpan.FromSeconds(60);

    private void Run(ManualResetEvent stop)
    {
        using var waiter = new PreciseWaiter();
        var interval = TimeSpan.FromSeconds(1.0 / Math.Max(1, _fps));
        var next = _clock.Elapsed;
        var statsStart = next;
        var lastTick = next;
        int frames = 0;
        double sumGap = 0, sumGapSq = 0, maxGap = 0;

        while (!stop.WaitOne(0))
        {
            var now = _clock.Elapsed;
            if (frames > 0 || now > statsStart)
            {
                double gap = (now - lastTick).TotalMilliseconds;
                sumGap += gap; sumGapSq += gap * gap; maxGap = Math.Max(maxGap, gap);
            }
            lastTick = now;
            frames++;
            Tick(now);

            if (now - statsStart >= StatsInterval)
            {
                double mean = sumGap / frames, sd = Math.Sqrt(Math.Max(0, sumGapSq / frames - mean * mean));
                Statistics?.Invoke($"Effekt-Engine: {frames / (now - statsStart).TotalSeconds:0.0} Bilder/s (Ziel {_fps}), " +
                                   $"Abstand Ø {mean:0.0} ms, Streuung {sd:0.0} ms, max. {maxGap:0} ms, " +
                                   $"Takt {(waiter.IsHighResolution ? "hochauflösend" : "timeBeginPeriod(1)")}");
                statsStart = now; frames = 0; sumGap = sumGapSq = maxGap = 0;
            }

            // Feste Termine statt "Intervall nach dem letzten Bild", damit sich nichts aufsummiert. Liegt
            // die Engine mehr als ein Bild zurück (System hing), nicht nachholen, sondern neu aufsetzen.
            next += interval;
            if (_clock.Elapsed - next > interval) next = _clock.Elapsed;
            waiter.WaitUntil(_clock, next, stop);
        }
        ClearSources();
    }

    /// <summary>Farben aller LEDs eines platzierten Geräts für den aktuellen Zeitpunkt, inkl. Helligkeit.
    /// Hat die Form weniger LEDs als das Gerät meldet, bleiben die übrigen schwarz.
    /// <paramref name="elapsed"/> ist die gemeinsame Engine-Uhr, die Szene rechnet daraus ihre
    /// Effektzeit inkl. Geschwindigkeit.</summary>
    /// <param name="padToLedCount">false = nur die laut Form belegten LEDs zurückgeben (für
    /// <see cref="IFrameSourceProvider"/>, die die übrigen dann gar nicht erst senden).</param>
    public static RgbColor[] RenderItem(EffectScene scene, DeviceLayoutItem item, LedShape shape, TimeSpan elapsed, bool padToLedCount = true)
    {
        var positions = item.ComputeLedPositions(shape);
        var colors = new RgbColor[padToLedCount ? Math.Max(positions.Count, item.LedCount) : positions.Count];
        double brightness = Math.Clamp(scene.Brightness, 0, 100) / 100.0 * Math.Clamp(item.Brightness, 0, 100) / 100.0;
        var effect = scene.Effect;
        var effectTime = scene.GetEffectTime(elapsed);
        var sampled = new RgbColor[positions.Count];
        for (int i = 0; i < positions.Count; i++)
        {
            var (x, y) = positions[i];
            sampled[i] = effect.Sample(x, y, scene.CanvasWidth, scene.CanvasHeight, effectTime);
        }
        // Spiel-Anzeige: Gerät mit zugewiesenen Bereichen zeigt z. B. den Lebensbalken über seine LEDs.
        if (effect is IDeviceAwareCanvasEffect deviceAware && item.ScreenRegions.Count > 0)
            deviceAware.ApplyToDevice(item, sampled, effectTime);
        // Strip andersherum angeschlossen: LED 1 bekommt die Farbe am Ende der Form (Füllstand läuft mit).
        if (item.ReverseLeds) Array.Reverse(sampled);
        for (int i = 0; i < sampled.Length; i++) colors[i] = Scale(sampled[i], brightness);
        return colors;
    }

    private static RgbColor Scale(RgbColor c, double f) =>
        f >= 1.0 ? c : new RgbColor((byte)(c.R * f), (byte)(c.G * f), (byte)(c.B * f));

    private void Tick(TimeSpan elapsed)
    {
        var activeSources = new HashSet<(IFrameSourceProvider, string, int)>();
        try
        {
            foreach (var scene in _scenes.Scenes.ToArray())
            {
                if (!scene.IsRunning) continue;
                foreach (var item in scene.Items.ToArray())
                {
                    var provider = _registry.FindProviderFor(item.DeviceId);
                    if (provider is null || !provider.IsConnected) continue;

                    if (provider is IFrameSourceProvider pull)
                    {
                        // Spätere Fenster gewinnen, wie beim Pushen (das zuletzt gesendete Bild bleibt stehen).
                        var key = (pull, item.DeviceId, item.ZoneIndex);
                        activeSources.Add(key);
                        if (!_sources.TryGetValue(key, out var current) || current.Scene != scene || current.Item != item)
                        {
                            _sources[key] = (scene, item);
                            pull.SetZoneFrameSource(item.DeviceId, item.ZoneIndex, CreateSource(scene, item));
                        }
                        continue;
                    }

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
            // Die UI kann Listen genau während des Kopierens ändern — dann einfach diesen Frame auslassen,
            // auch ohne Zonen abzumelden.
            return;
        }

        foreach (var key in _sources.Keys.Where(k => !activeSources.Contains(k)).ToList())
        {
            _sources.Remove(key);
            try { key.Provider.SetZoneFrameSource(key.DeviceId, key.Zone, null); }
            catch { /* Provider evtl. schon entsorgt (Einstellungen neu gespeichert) */ }
        }
    }

    /// <summary>Render-Funktion für eine Zone: rechnet mit dem aktuellen Stand von Fenster, Element und
    /// LED-Form zum Zeitpunkt "Engine-Uhr + lead" — läuft im Sende-Thread des Providers.</summary>
    private ZoneFrameSource CreateSource(EffectScene scene, DeviceLayoutItem item) => lead =>
        RenderItem(scene, item, _shapes.Get(item.DeviceId, item.ZoneIndex, item.LedCount), Elapsed + lead, padToLedCount: false);

    private void ClearSources()
    {
        foreach (var key in _sources.Keys)
        {
            try { key.Provider.SetZoneFrameSource(key.DeviceId, key.Zone, null); }
            catch { /* siehe oben */ }
        }
        _sources.Clear();
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
