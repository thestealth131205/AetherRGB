using AetherRGB.Core.Devices;

namespace AetherRGB.Core.Effects;

/// <summary>Eine vom Nutzer wählbare Farbe eines Effekts, z. B. "Farbe 1" beim Farbverlauf.</summary>
public sealed record EffectColorSlot(string Label, RgbColor Default);

/// <summary>Ein auswählbarer Effekt — die Szene speichert nur die <see cref="Id"/> und die gewählten
/// Farben, die Instanz wird bei Bedarf neu erzeugt. <see cref="Create"/> bekommt genau so viele Farben,
/// wie <see cref="ColorSlots"/> Einträge hat.</summary>
public sealed record EffectDescriptor(string Id, string Name, IReadOnlyList<EffectColorSlot> ColorSlots,
    Func<IReadOnlyList<RgbColor>, ICanvasEffect> Create)
{
    public EffectDescriptor(string id, string name, Func<ICanvasEffect> create)
        : this(id, name, Array.Empty<EffectColorSlot>(), _ => create()) { }

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
        new EffectDescriptor("static-red", "Statisch Rot", () => new CanvasStaticColorEffect(RgbColor.FromHex("#FF0000"))),
        new EffectDescriptor("static-blue", "Statisch Blau", () => new CanvasStaticColorEffect(RgbColor.FromHex("#0000FF"))),
        new EffectDescriptor("static-white", "Statisch Weiß", () => new CanvasStaticColorEffect(RgbColor.White)),
    };

    public static EffectDescriptor Get(string? id) => All.FirstOrDefault(e => e.Id == id) ?? All[0];
}
