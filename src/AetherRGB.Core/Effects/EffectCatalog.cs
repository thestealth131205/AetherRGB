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

    public override string ToString() => Name;
}

/// <summary>Alle Canvas-Effekte, die in einem Effekt-Fenster gewählt werden können.</summary>
public static class EffectCatalog
{
    public static readonly IReadOnlyList<EffectDescriptor> All = new[]
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

    /// <summary>Geschwindigkeit in Prozent der Standardgeschwindigkeit.</summary>
    public const int MinSpeedPercent = 10, MaxSpeedPercent = 500, DefaultSpeedPercent = 100;

    public static EffectDescriptor Get(string? id) => All.FirstOrDefault(e => e.Id == id) ?? All[0];
}
