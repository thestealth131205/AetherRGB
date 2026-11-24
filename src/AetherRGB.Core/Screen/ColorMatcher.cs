using AetherRGB.Core.Devices;

namespace AetherRGB.Core.Screen;

/// <summary>Erkennt eine Farbe über Farbton, Sättigung und Helligkeit statt über RGB-Abstand — so passt z. B. die
/// Sturmfläche in der Fortnite-Minimap, egal ob sie hell oder im Schatten liegt.</summary>
public sealed record ColorMatcher
{
    /// <summary>Farbton 0..360°.</summary>
    public double Hue { get; init; }
    /// <summary>Erlaubte Abweichung vom Farbton in Grad (beidseitig).</summary>
    public double HueTolerance { get; init; } = 20;
    /// <summary>Sättigung 0..1, darunter (zu grau) passt die Farbe nicht.</summary>
    public double MinSaturation { get; init; } = 0.35;
    /// <summary>Helligkeit 0..1 (HSV-Value).</summary>
    public double MinValue { get; init; } = 0.25;
    public double MaxValue { get; init; } = 1.0;

    public bool Matches(byte r, byte g, byte b)
    {
        var (h, s, v) = ToHsv(r, g, b);
        if (s < MinSaturation || v < MinValue || v > MaxValue) return false;
        double d = Math.Abs(h - Hue) % 360;
        if (d > 180) d = 360 - d;
        return d <= HueTolerance;
    }

    public bool Matches(RgbColor c) => Matches(c.R, c.G, c.B);

    /// <summary>Beispielfarbe (für die Vorschau im Bereichs-Editor): reiner Farbton bei mittlerer Helligkeit.</summary>
    public RgbColor Example()
    {
        double v = Math.Clamp((MinValue + MaxValue) / 2 + 0.15, 0.2, 1), s = Math.Clamp(Math.Max(MinSaturation, 0.75), 0, 1);
        return FromHsv(Hue, s, v);
    }

    /// <summary>Passende Grenzen für eine mit der Pipette aufgenommene Farbe: etwas Spielraum bei Farbton,
    /// Sättigung und Helligkeit, damit Schattierungen derselben Farbe auch zählen.</summary>
    public static ColorMatcher FromSample(RgbColor c, double hueTolerance = 20)
    {
        var (h, s, v) = ToHsv(c.R, c.G, c.B);
        return new ColorMatcher
        {
            Hue = Math.Round(h, 1),
            HueTolerance = hueTolerance,
            MinSaturation = Math.Round(Math.Clamp(s * 0.6, 0.15, 0.9), 2),
            MinValue = Math.Round(Math.Clamp(v * 0.5, 0.05, 0.9), 2),
            MaxValue = Math.Round(v < 0.5 ? Math.Clamp(v * 1.6 + 0.05, 0.2, 1) : 1.0, 2),
        };
    }

    public static (double H, double S, double V) ToHsv(byte r, byte g, byte b)
    {
        int max = Math.Max(r, Math.Max(g, b)), min = Math.Min(r, Math.Min(g, b));
        double v = max / 255.0;
        if (max == 0) return (0, 0, 0);
        double delta = max - min;
        double s = delta / max;
        if (delta == 0) return (0, 0, v);
        double h;
        if (max == r) h = 60 * (((g - b) / delta) % 6);
        else if (max == g) h = 60 * ((b - r) / delta + 2);
        else h = 60 * ((r - g) / delta + 4);
        if (h < 0) h += 360;
        return (h, s, v);
    }

    public static RgbColor FromHsv(double h, double s, double v)
    {
        h = ((h % 360) + 360) % 360;
        double c = v * s, x = c * (1 - Math.Abs(h / 60 % 2 - 1)), m = v - c;
        var (r, g, b) = h switch
        {
            < 60 => (c, x, 0.0),
            < 120 => (x, c, 0.0),
            < 180 => (0.0, c, x),
            < 240 => (0.0, x, c),
            < 300 => (x, 0.0, c),
            _ => (c, 0.0, x),
        };
        return new RgbColor((byte)Math.Round((r + m) * 255), (byte)Math.Round((g + m) * 255), (byte)Math.Round((b + m) * 255));
    }
}
