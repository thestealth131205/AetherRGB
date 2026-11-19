using AetherRGB.Core.Devices;

namespace AetherRGB.Core.Effects;

/// <summary>Treibt einen Effekt mit fester Framerate auf eine Menge von (Geräte-, Zonen)-Zielen.
/// Läuft komplett lokal in einem Hintergrund-Timer, keine Netzwerkabhängigkeit außer den
/// Geräte-Providern selbst.</summary>
public sealed class EffectEngine : IDisposable
{
    private readonly DeviceRegistry _registry;
    private readonly List<(string DeviceId, int ZoneIndex, int LedCount)> _targets = new();
    private Timer? _timer;
    private IEffect _effect = new StaticColorEffect(RgbColor.Black);
    private readonly DateTime _start = DateTime.UtcNow;
    private readonly int _fps;

    public EffectEngine(DeviceRegistry registry, int fps = 30)
    {
        _registry = registry;
        _fps = fps;
    }

    public void SetTargets(IEnumerable<(string DeviceId, int ZoneIndex, int LedCount)> targets)
    {
        _targets.Clear();
        _targets.AddRange(targets);
    }

    public void SetEffect(IEffect effect) => _effect = effect;

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
        foreach (var (deviceId, zoneIndex, ledCount) in _targets)
        {
            var provider = _registry.FindProviderFor(deviceId);
            if (provider is null || !provider.IsConnected) continue;

            var colors = _effect.RenderFrame(ledCount, elapsed);
            // Bewusst "fire and forget" mit Fehlerschlucken pro Tick — ein einzelner
            // fehlgeschlagener Frame (z. B. kurzer WLED-Netzwerkhänger) darf die laufende
            // Animation auf allen anderen Geräten nie unterbrechen.
            _ = provider.SetZoneColorsAsync(deviceId, zoneIndex, colors).ContinueWith(t =>
            {
                if (t.Exception is not null) { /* bewusst ignoriert, siehe Kommentar oben */ }
            }, TaskScheduler.Default);
        }
    }

    public void Dispose() => Stop();
}
