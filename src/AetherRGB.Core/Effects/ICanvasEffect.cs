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
        return SmoothHueToRgb(phase + normalizedX * _scalePerAxis);
    }

    /// <summary>Regenbogenfarbe für einen Farbton (0..360°) mit gleichbleibender Lichtmenge. Jeder Kanal
    /// hat sein Maximum bei seinem Farbton (Rot 0°, Grün 120°, Blau 240°) und blendet über cos² zum
    /// Nachbarn über — zwei benachbarte Kanäle ergeben dabei immer zusammen 100 %. Das klassische HSV
    /// schaltet dagegen bei Gelb/Cyan/Magenta zwei Kanäle voll ein (doppelte Lichtmenge an der LED,
    /// sichtbar als heller Streifen) und hat alle 60° einen Knick, der auf dem Strip als harte Kante
    /// auffällt. Die LEDs setzen die Werte linear in Licht um, deshalb wird ohne Gamma gerechnet.</summary>
    internal static RgbColor SmoothHueToRgb(double hue)
    {
        hue %= 360.0;
        if (hue < 0) hue += 360.0;
        return new RgbColor(Channel(hue, 0), Channel(hue, 120), Channel(hue, 240));
    }

    private static byte Channel(double hue, double center)
    {
        double d = Math.Abs(hue - center);
        if (d > 180) d = 360 - d;
        if (d >= 120) return 0;
        double w = Math.Cos(d * 0.75 * Math.PI / 180.0); // 0° → 1, 120° → 0
        return (byte)Math.Round(w * w * 255.0);
    }
}

/// <summary>Ein weicher, kreisförmiger "Puls" von der Canvas-Mitte aus — ein Ring in der gewählten Farbe
/// läuft nach außen, dazwischen ist es dunkel (bzw. <c>background</c>).</summary>
public sealed class CanvasRadialPulseEffect : ICanvasEffect
{
    public string Name => "Radialer Puls";
    private readonly RgbColor _color;
    private readonly RgbColor _background;
    private readonly double _speedHz;

    /// <summary>Halbe Ringbreite relativ zum Abstand Mitte–Ecke. Schmal genug, dass auf einem
    /// Strip sichtbar ein Ring durchläuft statt dass alle LEDs gemeinsam auf- und abblenden.</summary>
    private const double RingHalfWidth = 0.18;

    public CanvasRadialPulseEffect(RgbColor color, RgbColor? background = null, double speedHz = 0.3)
    {
        _color = color;
        _background = background ?? RgbColor.Black;
        _speedHz = speedHz;
    }

    public RgbColor Sample(double x, double y, double canvasWidth, double canvasHeight, TimeSpan elapsed)
    {
        double cx = canvasWidth / 2.0, cy = canvasHeight / 2.0;
        double maxDist = Math.Sqrt(cx * cx + cy * cy);
        double dist = maxDist <= 0 ? 0 : Math.Sqrt((x - cx) * (x - cx) + (y - cy) * (y - cy)) / maxDist;

        double phase = (elapsed.TotalSeconds * _speedHz) % 1.0;
        // Ring, der nach außen läuft. Er startet eine Ringbreite vor der Mitte und endet eine
        // Ringbreite hinter dem Rand, so blendet er weich ein und aus — sonst springt beim
        // Phasenwechsel jede LED nahe der Mitte schlagartig von dunkel auf hell.
        double ringPos = phase * (1.0 + 2.0 * RingHalfWidth) - RingHalfWidth;
        double ringDist = Math.Abs(dist - ringPos);
        double t = 1.0 - Math.Clamp(ringDist / RingHalfWidth, 0.0, 1.0);
        t = 0.5 - 0.5 * Math.Cos(t * Math.PI); // weiche Ringkante statt linearem Dreieck
        return RgbColor.Lerp(_background, _color, t);
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
        double phase = ((projection + shift) % 1.0 + 1.0) % 1.0;
        // Hin und zurück (Farbe 1 → 2 → 1) statt Sägezahn — sonst gäbe es eine harte Kante, wo der
        // Verlauf von Farbe 2 wieder auf Farbe 1 springt.
        double t = 1.0 - Math.Abs(2.0 * phase - 1.0);
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
