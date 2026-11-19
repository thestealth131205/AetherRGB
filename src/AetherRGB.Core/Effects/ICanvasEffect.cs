using AetherRGB.Core.Devices;

namespace AetherRGB.Core.Effects;

/// <summary>Ein Canvas-Effekt kennt kein einzelnes Gerät — er berechnet die Farbe für einen beliebigen
/// Punkt auf der virtuellen 2D-Fläche (SignalRGB-Stil). Die <see cref="CanvasEffectEngine"/> sampelt
/// diese Funktion für die tatsächliche Pixelposition jeder LED, wie sie im <see cref="Layout.CanvasLayout"/>
/// platziert wurde — das Geräte-Layout entscheidet nur, WO gesampelt wird, nie WAS der Effekt zeigt.</summary>
public interface ICanvasEffect
{
    string Name { get; }

    /// <param name="x">X-Position in Canvas-Einheiten (0..canvasWidth).</param>
    /// <param name="y">Y-Position in Canvas-Einheiten (0..canvasHeight).</param>
    /// <param name="canvasWidth">Breite der Fläche, zum Normalisieren innerhalb des Effekts.</param>
    /// <param name="canvasHeight">Höhe der Fläche, zum Normalisieren innerhalb des Effekts.</param>
    /// <param name="elapsed">Zeit seit Effektstart, für Animationen.</param>
    RgbColor Sample(double x, double y, double canvasWidth, double canvasHeight, TimeSpan elapsed);
}

public sealed class CanvasStaticColorEffect : ICanvasEffect
{
    private readonly RgbColor _color;
    public CanvasStaticColorEffect(RgbColor color) => _color = color;
    public string Name => "Statische Farbe";

    public RgbColor Sample(double x, double y, double canvasWidth, double canvasHeight, TimeSpan elapsed) => _color;
}

/// <summary>Ein Regenbogen-Farbverlauf, der horizontal über die ganze Fläche läuft — Geräte, die
/// weiter rechts liegen, zeigen zu jedem Zeitpunkt einen späteren Punkt der Welle als Geräte links,
/// genau wie bei SignalRGB/OpenRGB "Wave"-Effekten.</summary>
public sealed class CanvasRainbowWaveEffect : ICanvasEffect
{
    public string Name => "Regenbogen-Welle";
    private readonly double _speedHz;
    private readonly double _scalePerAxis;

    public CanvasRainbowWaveEffect(double speedHz = 0.15, double scalePerAxis = 360)
    {
        _speedHz = speedHz;
        _scalePerAxis = scalePerAxis;
    }

    public RgbColor Sample(double x, double y, double canvasWidth, double canvasHeight, TimeSpan elapsed)
    {
        double normalizedX = canvasWidth <= 0 ? 0 : x / canvasWidth;
        double phase = elapsed.TotalSeconds * _speedHz * 360.0;
        double hue = (phase + normalizedX * _scalePerAxis) % 360.0;
        if (hue < 0) hue += 360.0;
        return HsvToRgb(hue, 1.0, 1.0);
    }

    internal static RgbColor HsvToRgb(double h, double s, double v)
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

/// <summary>Ein weicher, kreisförmiger "Puls" von der Canvas-Mitte aus — expandiert/zieht sich
/// zusammen und wird dabei über eine konfigurierbare Zweifarb-Palette gefärbt.</summary>
public sealed class CanvasRadialPulseEffect : ICanvasEffect
{
    public string Name => "Radialer Puls";
    private readonly RgbColor _from;
    private readonly RgbColor _to;
    private readonly double _speedHz;

    public CanvasRadialPulseEffect(RgbColor from, RgbColor to, double speedHz = 0.3)
    {
        _from = from;
        _to = to;
        _speedHz = speedHz;
    }

    public RgbColor Sample(double x, double y, double canvasWidth, double canvasHeight, TimeSpan elapsed)
    {
        double cx = canvasWidth / 2.0, cy = canvasHeight / 2.0;
        double maxDist = Math.Sqrt(cx * cx + cy * cy);
        double dist = maxDist <= 0 ? 0 : Math.Sqrt((x - cx) * (x - cx) + (y - cy) * (y - cy)) / maxDist;

        double phase = (elapsed.TotalSeconds * _speedHz) % 1.0;
        // Ring, der nach außen läuft: Abstand zur aktuellen "Pulswelle" bestimmt die Mischung.
        double ringDist = Math.Abs(dist - phase);
        double t = 1.0 - Math.Clamp(ringDist * 2.5, 0.0, 1.0);
        return RgbColor.Lerp(_from, _to, t);
    }
}

/// <summary>Diagonaler, linearer Farbverlauf zwischen zwei Farben über die gesamte Fläche —
/// das einfachste "male die Fläche an"-Muster, auf dem jedes Gerät seinen Ausschnitt zeigt.</summary>
public sealed class CanvasGradientSweepEffect : ICanvasEffect
{
    public string Name => "Farbverlauf";
    private readonly RgbColor _from;
    private readonly RgbColor _to;
    private readonly double _angleDegrees;
    private readonly double _speedHz;

    public CanvasGradientSweepEffect(RgbColor from, RgbColor to, double angleDegrees = 45, double speedHz = 0.1)
    {
        _from = from;
        _to = to;
        _angleDegrees = angleDegrees;
        _speedHz = speedHz;
    }

    public RgbColor Sample(double x, double y, double canvasWidth, double canvasHeight, TimeSpan elapsed)
    {
        double angleRad = _angleDegrees * Math.PI / 180.0;
        double dirX = Math.Cos(angleRad), dirY = Math.Sin(angleRad);

        double nx = canvasWidth <= 0 ? 0 : x / canvasWidth;
        double ny = canvasHeight <= 0 ? 0 : y / canvasHeight;

        double projection = nx * dirX + ny * dirY;
        double shift = elapsed.TotalSeconds * _speedHz;
        double t = ((projection + shift) % 1.0 + 1.0) % 1.0;
        return RgbColor.Lerp(_from, _to, t);
    }
}

/// <summary>Spiegelt eine externe Lichtquelle (z. B. eine Home-Assistant-Lampe) 1:1 auf die gesamte
/// Fläche — unabhängig von Position, jedes Gerät zeigt dieselbe Farbe.</summary>
public sealed class CanvasExternalSourceEffect : ICanvasEffect
{
    public string Name => "Externe Quelle";
    private RgbColor _current = RgbColor.Black;

    public void SetColor(RgbColor color) => _current = color;

    public RgbColor Sample(double x, double y, double canvasWidth, double canvasHeight, TimeSpan elapsed) => _current;
}
