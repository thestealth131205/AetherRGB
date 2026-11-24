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
    public string EffectId { get; set; } = EffectCatalog.BuiltIn[0].Id;
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

    /// <summary>Läuft synchron mit den anderen so markierten Fenstern: beim Einschalten, Fortsetzen und
    /// App-Start übernimmt es deren Effektzeit, statt eine eigene Phase zu haben. Mit gleichem Tempo
    /// bleiben die Fenster dadurch Bild für Bild gleich, mit unterschiedlichem laufen sie danach auseinander.</summary>
    public bool SyncWithOthers { get; set; }

    private readonly object _effectLock = new();
    private TimeSpan _effectTime, _lastGlobal;
    private bool _clockStarted;
    private ICanvasEffect? _effect;
    private EffectDescriptor? _effectDescriptor;
    private ICanvasEffect? _liveOverride;

    /// <summary>Effekt-Editor „Live auf LEDs“: solange gesetzt, zeigt dieses Fenster (Vorschau und LEDs)
    /// den übergebenen Effekt statt <see cref="EffectId"/>. Gespeichert wird das nie — null stellt den
    /// gewählten Effekt wieder her. Die Instanz darf danach nicht mehr verändert werden (Engine-Thread liest).</summary>
    public void SetLiveOverride(ICanvasEffect? effect)
    {
        lock (_effectLock) _liveOverride = effect;
    }

    [JsonIgnore]
    public bool HasLiveOverride
    {
        get { lock (_effectLock) return _liveOverride is not null; }
    }

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
            _effectDescriptor = null;
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

    /// <summary>Effektzeit zu <paramref name="globalElapsed"/>, ohne die Uhr weiterzustellen. Liegt der
    /// Zeitpunkt vor dem letzten Aufruf (der MSI-Sende-Thread fragt mit Vorlauf), wird zurückgerechnet.</summary>
    private TimeSpan PeekEffectTime(TimeSpan globalElapsed)
    {
        lock (_effectLock)
        {
            if (!_clockStarted) return globalElapsed * (ResolveSpeed(EffectId) / 100.0);
            return _effectTime + (globalElapsed - _lastGlobal) * (ResolveSpeed(EffectId) / 100.0);
        }
    }

    /// <summary>Übernimmt die Effektzeit von <paramref name="other"/> zum Zeitpunkt <paramref name="globalElapsed"/>.</summary>
    public void AlignClockTo(EffectScene other, TimeSpan globalElapsed)
    {
        if (ReferenceEquals(other, this)) return;
        var time = other.PeekEffectTime(globalElapsed); // außerhalb der eigenen Sperre, keine verschachtelten Locks
        SetClock(time < TimeSpan.Zero ? TimeSpan.Zero : time, globalElapsed);
    }

    /// <summary>Startet den Effekt dieses Fensters ab <paramref name="globalElapsed"/> von vorn.</summary>
    public void RestartClock(TimeSpan globalElapsed) => SetClock(TimeSpan.Zero, globalElapsed);

    private void SetClock(TimeSpan effectTime, TimeSpan globalElapsed)
    {
        lock (_effectLock)
        {
            _clockStarted = true;
            _effectTime = effectTime;
            _lastGlobal = globalElapsed;
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
    /// Wechsel neu erzeugt. Ist ein eigener Effekt nicht (mehr) vorhanden, läuft die Regenbogen-Welle. Wird gleichzeitig vom Engine-Timer und der UI gelesen, daher gesperrt.</summary>
    [JsonIgnore]
    public ICanvasEffect Effect
    {
        get
        {
            lock (_effectLock)
            {
                if (_liveOverride is not null) return _liveOverride;
                // Neu erzeugen bei anderem Effekt, anderen Farben oder neu geladenem eigenem Effekt
                // (der Katalog liefert dann einen neuen Descriptor).
                var descriptor = EffectCatalog.Get(EffectId);
                if (_effect is null || !ReferenceEquals(_effectDescriptor, descriptor))
                {
                    _effect = descriptor.Create(ResolveColors(descriptor));
                    _effectDescriptor = descriptor;
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

    /// <summary>Taktgeber der synchronen Gruppe: bevorzugt ein laufendes markiertes Fenster außer
    /// <paramref name="except"/>, sonst irgendein markiertes.</summary>
    private EffectScene? SyncReference(EffectScene? except) =>
        Scenes.FirstOrDefault(s => s.SyncWithOthers && s.IsRunning && s != except)
        ?? Scenes.FirstOrDefault(s => s.SyncWithOthers && s != except);

    /// <summary>Nimmt <paramref name="scene"/> in die synchrone Gruppe auf bzw. heraus. Beim Aufnehmen
    /// übernimmt es die Effektzeit der Gruppe — die anderen Fenster laufen dabei ohne Sprung weiter.</summary>
    public void SetSync(EffectScene scene, bool sync, TimeSpan globalElapsed)
    {
        scene.SyncWithOthers = sync;
        if (sync) ResyncScene(scene, globalElapsed);
    }

    /// <summary>Fenster wieder auf die Zeit der Gruppe setzen (z. B. nach dem Fortsetzen) — nur wenn es
    /// markiert ist und es noch ein anderes markiertes Fenster gibt.</summary>
    public void ResyncScene(EffectScene scene, TimeSpan globalElapsed)
    {
        if (!scene.SyncWithOthers) return;
        if (SyncReference(scene) is { } reference) scene.AlignClockTo(reference, globalElapsed);
    }

    /// <summary>Beim Start: alle markierten Fenster auf dieselbe Effektzeit bringen.</summary>
    public void AlignSynced(TimeSpan globalElapsed)
    {
        var reference = SyncReference(null);
        if (reference is null) return;
        foreach (var scene in Scenes.Where(s => s.SyncWithOthers && s != reference))
            scene.AlignClockTo(reference, globalElapsed);
    }

    /// <summary>Alle markierten Fenster gemeinsam von vorn starten. Anzahl der neu gestarteten Fenster.</summary>
    public int RestartSynced(TimeSpan globalElapsed)
    {
        var synced = Scenes.Where(s => s.SyncWithOthers).ToList();
        foreach (var scene in synced) scene.RestartClock(globalElapsed);
        return synced.Count;
    }

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
