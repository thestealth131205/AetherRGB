using AetherRGB.Core.Devices;

namespace AetherRGB.Core.Effects;

/// <summary>Ein Lichteffekt berechnet pro Tick die Farben für eine gegebene Pixelanzahl.
/// Die Engine kümmert sich um das Verteilen auf die tatsächlichen Geräte/Zonen.</summary>
public interface IEffect
{
    string Name { get; }

    /// <param name="pixelCount">Anzahl der LEDs der Zone, für die der nächste Frame berechnet wird.</param>
    /// <param name="elapsed">Zeit seit Effektstart, für Animationen.</param>
    IReadOnlyList<RgbColor> RenderFrame(int pixelCount, TimeSpan elapsed);
}

public sealed class StaticColorEffect : IEffect
{
    private readonly RgbColor _color;
    public StaticColorEffect(RgbColor color) => _color = color;
    public string Name => "Statische Farbe";

    public IReadOnlyList<RgbColor> RenderFrame(int pixelCount, TimeSpan elapsed) =>
        Enumerable.Repeat(_color, pixelCount).ToArray();
}

public sealed class RainbowWaveEffect : IEffect
{
    public string Name => "Regenbogen-Welle";
    private readonly double _speed;

    public RainbowWaveEffect(double speedHz = 0.2) => _speed = speedHz;

    public IReadOnlyList<RgbColor> RenderFrame(int pixelCount, TimeSpan elapsed)
    {
        var colors = new RgbColor[pixelCount];
        double phase = elapsed.TotalSeconds * _speed * 360.0;
        for (int i = 0; i < pixelCount; i++)
        {
            double hue = (phase + (pixelCount == 0 ? 0 : (360.0 * i / pixelCount))) % 360.0;
            colors[i] = HsvToRgb(hue, 1.0, 1.0);
        }
        return colors;
    }

    private static RgbColor HsvToRgb(double h, double s, double v)
    {
        double c = v * s;
        double x = c * (1 - Math.Abs((h / 60.0 % 2) - 1));
        double m = v - c;
        double r, g, b;
        if (h < 60) { r = c; g = x; b = 0; }
        else if (h < 120) { r = x; g = c; b = 0; }
        else if (h < 180) { r = 0; g = c; b = x; }
        else if (h < 240) { r = 0; g = x; b = c; }
        else if (h < 300) { r = x; g = 0; b = c; }
        else { r = c; g = 0; b = x; }
        return new RgbColor((byte)((r + m) * 255), (byte)((g + m) * 255), (byte)((b + m) * 255));
    }
}

/// <summary>Spiegelt eine externe Lichtquelle (z. B. eine Home-Assistant-Lampe) 1:1 auf alle Pixel.
/// Die aktuelle Farbe wird von außen (per <see cref="SetColor"/>) gefüttert, nicht selbst berechnet.</summary>
public sealed class ExternalSourceEffect : IEffect
{
    public string Name => "Externe Quelle";
    private RgbColor _current = RgbColor.Black;

    public void SetColor(RgbColor color) => _current = color;

    public IReadOnlyList<RgbColor> RenderFrame(int pixelCount, TimeSpan elapsed) =>
        Enumerable.Repeat(_current, pixelCount).ToArray();
}
