using AetherRGB.Core.Devices;

namespace AetherRGB.Core.Effects;

/// <summary>Eine vom Nutzer wählbare Farbe eines Effekts, z. B. "Farbe 1" beim Farbverlauf.</summary>
public sealed record EffectColorSlot(string Label, RgbColor Default);

/// <summary>Ein auswählbarer Effekt — die Szene speichert nur die <see cref="Id"/> und die gewählten
/// Farben, die Instanz wird bei Bedarf neu erzeugt. <see cref="Create"/> bekommt genau so viele Farben,
/// wie <see cref="ColorSlots"/> Einträge hat. <see cref="IsAnimated"/> = Effekt bewegt sich und bekommt
/// einen Geschwindigkeitsregler.</summary>
public sealed record EffectDescriptor(string Id, string Name, IReadOnlyList<EffectColorSlot> ColorSlots,
    Func<IReadOnlyList<RgbColor>, ICanvasEffect> Create, bool IsAnimated = true)
{
    public EffectDescriptor(string id, string name, Func<ICanvasEffect> create, bool isAnimated = true)
        : this(id, name, Array.Empty<EffectColorSlot>(), _ => create(), isAnimated) { }

    public IReadOnlyList<RgbColor> DefaultColors => ColorSlots.Select(s => s.Default).ToArray();

    /// <summary>Im Effekt-Editor erstellt (ID <c>custom:…</c>).</summary>
    public bool IsCustom => Id.StartsWith(Custom.CustomEffectDocument.CatalogPrefix, StringComparison.Ordinal);

    public override string ToString() => Name;
}

/// <summary>Alle Canvas-Effekte, die in einem Effekt-Fenster gewählt werden können: die eingebauten und
/// die eigenen aus dem Effekt-Editor (<see cref="Custom.CustomEffectStore"/>, IDs <c>custom:…</c>).
/// Die Liste wird bei Änderungen als Ganzes ersetzt, Leser bekommen also immer einen festen Stand.</summary>
public static class EffectCatalog
{
    public static readonly IReadOnlyList<EffectDescriptor> BuiltIn = new[]
    {
        new EffectDescriptor("rainbow-wave", "Regenbogen-Welle", () => new CanvasRainbowWaveEffect()),
        new EffectDescriptor("gradient-sweep", "Farbverlauf",
            new[] { new EffectColorSlot("Farbe 1", RgbColor.FromHex("#3B82F6")), new EffectColorSlot("Farbe 2", RgbColor.FromHex("#EC4899")) },
            c => new CanvasGradientSweepEffect(c[0], c[1])),
        new EffectDescriptor("radial-pulse", "Radialer Puls",
            new[] { new EffectColorSlot("Farbe", RgbColor.FromHex("#10B981")) },
            c => new CanvasRadialPulseEffect(c[0])),
        new EffectDescriptor("static-red", "Statisch Rot", () => new CanvasStaticColorEffect(RgbColor.FromHex("#FF0000")), isAnimated: false),
        new EffectDescriptor("static-blue", "Statisch Blau", () => new CanvasStaticColorEffect(RgbColor.FromHex("#0000FF")), isAnimated: false),
        new EffectDescriptor("static-white", "Statisch Weiß", () => new CanvasStaticColorEffect(RgbColor.White), isAnimated: false),
    };

    private static volatile IReadOnlyList<EffectDescriptor> _all = BuiltIn;

    /// <summary>Eingebaute Effekte, danach die eigenen alphabetisch.</summary>
    public static IReadOnlyList<EffectDescriptor> All => _all;

    /// <summary>Die Liste hat sich geändert (eigener Effekt gespeichert, importiert, gelöscht). Kann von
    /// jedem Thread kommen.</summary>
    public static event Action? Changed;

    /// <summary>Ersetzt alle eigenen Effekte. Ein Effekt-Fenster mit einer geänderten Datei erzeugt dadurch
    /// beim nächsten Bild eine neue Instanz (neuer Descriptor).</summary>
    public static void SetCustomEffects(IEnumerable<EffectDescriptor> custom)
    {
        _all = BuiltIn.Concat(custom.OrderBy(e => e.Name, StringComparer.CurrentCultureIgnoreCase)).ToArray();
        Changed?.Invoke();
    }

    /// <summary>Geschwindigkeit in Prozent der Standardgeschwindigkeit.</summary>
    public const int MinSpeedPercent = 10, MaxSpeedPercent = 500, DefaultSpeedPercent = 100;

    /// <summary>Effekt zur ID; unbekannte IDs (z. B. gelöschter eigener Effekt) ergeben die Regenbogen-Welle,
    /// die ID im Effekt-Fenster bleibt aber erhalten — kommt die Datei zurück, läuft wieder der eigene Effekt.</summary>
    public static EffectDescriptor Get(string? id) => _all.FirstOrDefault(e => e.Id == id) ?? _all[0];

    public static bool Contains(string? id) => _all.Any(e => e.Id == id);
}
