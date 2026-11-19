using AetherRGB.Core.Devices;
using AetherRGB.Core.Layout;

namespace AetherRGB.Core.Effects;

/// <summary>Treibt einen <see cref="ICanvasEffect"/> über das platzierte 2D-Layout: für jede LED jedes
/// Geräts wird anhand ihrer im Layout-Editor festgelegten Position die Effektfarbe gesampelt und an
/// den zuständigen Provider gesendet. Ersetzt für den SignalRGB-Stil die ältere, rein pro-Zone arbeitende
/// <see cref="EffectEngine"/> — die bleibt für einfache "ein Effekt pro Zone ohne Layout"-Fälle bestehen.</summary>
public sealed class CanvasEffectEngine : IDisposable
{
    private readonly DeviceRegistry _registry;
    private CanvasLayout _layout = new();
    private Timer? _timer;
    private ICanvasEffect _effect = new CanvasStaticColorEffect(RgbColor.Black);
    private readonly DateTime _start = DateTime.UtcNow;
    private readonly int _fps;

    public CanvasEffectEngine(DeviceRegistry registry, int fps = 30)
    {
        _registry = registry;
        _fps = fps;
    }

    public void SetLayout(CanvasLayout layout) => _layout = layout;

    public void SetEffect(ICanvasEffect effect) => _effect = effect;

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

    private void Tick()
    {
        var elapsed = DateTime.UtcNow - _start;
        foreach (var item in _layout.Items)
        {
            var provider = _registry.FindProviderFor(item.DeviceId);
            if (provider is null || !provider.IsConnected) continue;

            var positions = item.ComputeLedPositions();
            var colors = new RgbColor[positions.Count];
            for (int i = 0; i < positions.Count; i++)
            {
                var (x, y) = positions[i];
                colors[i] = _effect.Sample(x, y, _layout.CanvasWidth, _layout.CanvasHeight, elapsed);
            }

            // Bewusst "fire and forget" mit Fehlerschlucken pro Tick — ein einzelner fehlgeschlagener
            // Frame (z. B. kurzer WLED-Netzwerkhänger) darf die laufende Animation auf allen anderen
            // Geräten nie unterbrechen.
            _ = provider.SetZoneColorsAsync(item.DeviceId, item.ZoneIndex, colors).ContinueWith(t =>
            {
                if (t.Exception is not null) { /* bewusst ignoriert, siehe Kommentar oben */ }
            }, TaskScheduler.Default);
        }
    }

    public void Dispose() => Stop();
}
