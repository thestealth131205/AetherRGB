using System.Text.Json;
using System.Text.Json.Serialization;
using AetherRGB.Core.Devices;
using AetherRGB.Core.Effects;

namespace AetherRGB.Core.Layout;

/// <summary>Ein Effekt-Fenster: eine eigene Fläche mit eigenem Effekt und eigenen platzierten Geräten.
/// Mehrere Szenen laufen gleichzeitig (z. B. "PC-Gehäuse" mit Regenbogen und "Schreibtisch-WLED"
/// mit statischer Farbe). Liegt dieselbe Zone in mehreren laufenden Szenen, gewinnt die spätere Szene
/// in der Liste.</summary>
public sealed class EffectScene
{
    public string Id { get; set; } = Guid.NewGuid().ToString("N");
    public string Name { get; set; } = "Effekt";
    public string EffectId { get; set; } = EffectCatalog.All[0].Id;
    public bool IsRunning { get; set; } = true;

    /// <summary>Layout-Helligkeit 0..100 % — gilt für alle Geräte dieser Szene.</summary>
    public int Brightness { get; set; } = 100;

    public double CanvasWidth { get; set; } = 1600;
    public double CanvasHeight { get; set; } = 800;
    public List<DeviceLayoutItem> Items { get; set; } = new();

    /// <summary>Vom Nutzer gewählte Farben als Hex (<c>#RRGGBB</c>), pro Effekt-ID — so bleiben die Farben
    /// des Farbverlaufs erhalten, wenn man kurz auf einen anderen Effekt wechselt und zurück.</summary>
    public Dictionary<string, List<string>> EffectColors { get; set; } = new();

    /// <summary>Gewählte Geschwindigkeit in Prozent (<see cref="EffectCatalog.MinSpeedPercent"/> ..
    /// <see cref="EffectCatalog.MaxSpeedPercent"/>), pro Effekt-ID wie <see cref="EffectColors"/>.</summary>
    public Dictionary<string, int> EffectSpeeds { get; set; } = new();

    private readonly object _effectLock = new();
    private TimeSpan _effectTime, _lastGlobal;
    private bool _clockStarted;
    private ICanvasEffect? _effect;
    private string? _effectInstanceKey;

    /// <summary>Die Farben für <see cref="EffectId"/>: gespeicherte Werte, fehlende/ungültige durch die
    /// Standardfarben des Effekts ersetzt.</summary>
    public IReadOnlyList<RgbColor> GetEffectColors()
    {
        lock (_effectLock) return ResolveColors(EffectCatalog.Get(EffectId));
    }

    public void SetEffectColor(int slot, RgbColor color)
    {
        lock (_effectLock)
        {
            var descriptor = EffectCatalog.Get(EffectId);
            if (slot < 0 || slot >= descriptor.ColorSlots.Count) return;
            var colors = ResolveColors(descriptor).Select(c => c.ToHex()).ToList();
            colors[slot] = color.ToHex();
            EffectColors[descriptor.Id] = colors;
            _effectInstanceKey = null;
        }
    }

    public int GetEffectSpeed()
    {
        lock (_effectLock) return ResolveSpeed(EffectId);
    }

    public void SetEffectSpeed(int percent)
    {
        lock (_effectLock)
            EffectSpeeds[EffectCatalog.Get(EffectId).Id] =
                Math.Clamp(percent, EffectCatalog.MinSpeedPercent, EffectCatalog.MaxSpeedPercent);
    }

    private int ResolveSpeed(string effectId) =>
        EffectSpeeds.TryGetValue(EffectCatalog.Get(effectId).Id, out var p)
            ? Math.Clamp(p, EffectCatalog.MinSpeedPercent, EffectCatalog.MaxSpeedPercent)
            : EffectCatalog.DefaultSpeedPercent;

    /// <summary>Effektzeit dieses Fensters zur gemeinsamen Uhr <paramref name="globalElapsed"/>: die
    /// Zeitdifferenzen werden mit der Geschwindigkeit aufsummiert statt die Uhr einfach zu
    /// multiplizieren — so läuft die Animation beim Verschieben des Reglers ohne Sprung weiter.
    /// Engine und Vorschau fragen mit derselben Uhr, daher sehen beide dieselbe Effektzeit.</summary>
    public TimeSpan GetEffectTime(TimeSpan globalElapsed)
    {
        lock (_effectLock)
        {
            if (!_clockStarted)
            {
                // Beim ersten Aufruf auf der gemeinsamen Uhr aufsetzen, damit Fenster mit 100 %
                // genau wie bisher synchron zur Engine-Zeit laufen.
                _clockStarted = true;
                _effectTime = globalElapsed * (ResolveSpeed(EffectId) / 100.0);
                _lastGlobal = globalElapsed;
            }
            else if (globalElapsed > _lastGlobal)
            {
                _effectTime += (globalElapsed - _lastGlobal) * (ResolveSpeed(EffectId) / 100.0);
                _lastGlobal = globalElapsed;
            }
            return _effectTime;
        }
    }

    private IReadOnlyList<RgbColor> ResolveColors(EffectDescriptor descriptor)
    {
        EffectColors.TryGetValue(descriptor.Id, out var stored);
        var result = new RgbColor[descriptor.ColorSlots.Count];
        for (int i = 0; i < result.Length; i++)
        {
            result[i] = descriptor.ColorSlots[i].Default;
            if (stored is not null && i < stored.Count)
            {
                try { result[i] = RgbColor.FromHex(stored[i]); }
                catch (Exception e) when (e is FormatException or ArgumentException or OverflowException) { }
            }
        }
        return result;
    }

    /// <summary>Laufende Effektinstanz passend zu <see cref="EffectId"/> und den gewählten Farben — wird bei
    /// Wechsel neu erzeugt. Wird gleichzeitig vom Engine-Timer und der UI gelesen, daher gesperrt.</summary>
    [JsonIgnore]
    public ICanvasEffect Effect
    {
        get
        {
            lock (_effectLock)
            {
                if (_effect is null || _effectInstanceKey != EffectId)
                {
                    var descriptor = EffectCatalog.Get(EffectId);
                    _effect = descriptor.Create(ResolveColors(descriptor));
                    _effectInstanceKey = EffectId;
                }
                return _effect;
            }
        }
    }
}

/// <summary>Alle Effekt-Fenster. Persistiert in <c>%APPDATA%\AetherRGB\scenes.json</c>.</summary>
public sealed class SceneCollection
{
    public List<EffectScene> Scenes { get; set; } = new();
    public string? ActiveSceneId { get; set; }

    private static readonly JsonSerializerOptions JsonOptions = new() { WriteIndented = true };

    public static string DefaultPath =>
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "AetherRGB", "scenes.json");

    public static SceneCollection LoadOrCreateDefault(string? path = null)
    {
        path ??= DefaultPath;
        SceneCollection? result = null;
        try
        {
            if (File.Exists(path))
                result = JsonSerializer.Deserialize<SceneCollection>(File.ReadAllText(path), JsonOptions);
        }
        catch
        {
            // Defekte Datei -> mit einem leeren Effekt-Fenster neu beginnen statt abzustürzen.
        }

        result ??= FromLegacyLayout() ?? new SceneCollection();
        if (result.Scenes.Count == 0)
            result.Scenes.Add(new EffectScene { Name = "Effekt 1" });
        return result;
    }

    /// <summary>Übernimmt das alte Ein-Flächen-Layout (layout.json, bis v0.2) als erstes Effekt-Fenster.
    /// Alte Rechtecke liefen bei Hochformat entlang der Höhe — die werden in Breite + 90° umgewandelt,
    /// weil Formen jetzt immer entlang der Breite verlaufen.</summary>
    private static SceneCollection? FromLegacyLayout()
    {
        var legacy = CanvasLayout.TryLoadLegacy();
        if (legacy is null || legacy.Items.Count == 0) return null;

        double sx = 1600 / Math.Max(1, legacy.CanvasWidth), sy = 800 / Math.Max(1, legacy.CanvasHeight);
        var scene = new EffectScene { Name = "Effekt 1" };
        foreach (var old in legacy.Items)
        {
            var item = old.Clone();
            if (item.Height > item.Width)
            {
                (item.Width, item.Height) = (item.Height, item.Width);
                item.RotationDegrees += 90;
            }
            item.CenterX *= sx;
            item.CenterY *= sy;
            scene.Items.Add(item);
        }
        return new SceneCollection { Scenes = { scene }, ActiveSceneId = scene.Id };
    }

    public void Save(string? path = null)
    {
        path ??= DefaultPath;
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllText(path, JsonSerializer.Serialize(this, JsonOptions));
    }
}
