using AetherRGB.Core.Devices;

namespace AetherRGB.Core.Effects;

/// <summary>Ein auswählbarer Effekt — die Szene speichert nur die <see cref="Id"/>, die Instanz wird
/// bei Bedarf neu erzeugt.</summary>
public sealed record EffectDescriptor(string Id, string Name, Func<ICanvasEffect> Create)
{
    public override string ToString() => Name;
}

/// <summary>Alle Canvas-Effekte, die in einem Effekt-Fenster gewählt werden können.</summary>
public static class EffectCatalog
{
    public static readonly IReadOnlyList<EffectDescriptor> All = new[]
    {
        new EffectDescriptor("rainbow-wave", "Regenbogen-Welle", () => new CanvasRainbowWaveEffect()),
        new EffectDescriptor("gradient-sweep", "Farbverlauf", () => new CanvasGradientSweepEffect(RgbColor.FromHex("#3B82F6"), RgbColor.FromHex("#EC4899"))),
        new EffectDescriptor("radial-pulse", "Radialer Puls", () => new CanvasRadialPulseEffect(RgbColor.FromHex("#10B981"), RgbColor.FromHex("#0B0B0B"))),
        new EffectDescriptor("static-red", "Statisch Rot", () => new CanvasStaticColorEffect(RgbColor.FromHex("#FF0000"))),
        new EffectDescriptor("static-blue", "Statisch Blau", () => new CanvasStaticColorEffect(RgbColor.FromHex("#0000FF"))),
        new EffectDescriptor("static-white", "Statisch Weiß", () => new CanvasStaticColorEffect(RgbColor.White)),
    };

    public static EffectDescriptor Get(string? id) => All.FirstOrDefault(e => e.Id == id) ?? All[0];
}
