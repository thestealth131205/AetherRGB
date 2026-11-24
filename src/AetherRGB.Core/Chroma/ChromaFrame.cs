using AetherRGB.Core.Devices;
using AetherRGB.Core.Effects;

namespace AetherRGB.Core.Chroma;

/// <summary>Gerätearten des Razer Chroma SDK. Die Zahlen sind die Gerätenummern im Bridge-Protokoll.</summary>
public enum ChromaDevice : byte
{
    Keyboard = 1,
    Mouse = 2,
    Headset = 3,
    Mousepad = 4,
    Keypad = 5,
    ChromaLink = 6,
}

public enum ChromaFrameKind
{
    /// <summary>Feste Farben in einem Raster (<see cref="ChromaFrame.Width"/> × <see cref="ChromaFrame.Height"/>),
    /// auch eine einzelne statische Farbe (1 × 1).</summary>
    Grid,
    /// <summary>Farbkreis, alle LEDs gleich (CHROMA_SPECTRUMCYCLING).</summary>
    Spectrum,
    /// <summary>Regenbogen, der über das Gerät läuft (CHROMA_WAVE).</summary>
    Wave,
    /// <summary>Zwischen <see cref="ChromaFrame.Color1"/> und <see cref="ChromaFrame.Color2"/> auf- und abblenden.</summary>
    Breathing,
    /// <summary>Blinken in <see cref="ChromaFrame.Color1"/>.</summary>
    Blinking,
}

/// <summary>Was ein Spiel zuletzt für eine Geräteart gesetzt hat — unveränderlich, wird bei jedem neuen
/// Effekt komplett ersetzt.</summary>
public sealed class ChromaFrame
{
    public ChromaFrameKind Kind { get; }
    public int Width { get; }
    public int Height { get; }
    /// <summary>Zeilenweise, Länge Width × Height (nur bei <see cref="ChromaFrameKind.Grid"/>).</summary>
    public RgbColor[] Colors { get; }
    public RgbColor Color1 { get; }
    public RgbColor Color2 { get; }
    /// <summary>Wellenrichtung: +1 nach rechts, -1 nach links.</summary>
    public int Direction { get; }

    private ChromaFrame(ChromaFrameKind kind, int width, int height, RgbColor[] colors, RgbColor c1, RgbColor c2, int direction)
    {
        Kind = kind; Width = width; Height = height; Colors = colors; Color1 = c1; Color2 = c2; Direction = direction;
    }

    public static readonly ChromaFrame Off = Solid(RgbColor.Black);

    public static ChromaFrame Solid(RgbColor color) => new(ChromaFrameKind.Grid, 1, 1, new[] { color }, color, color, 1);

    public static ChromaFrame Grid(int width, int height, RgbColor[] colors)
    {
        if (width <= 0 || height <= 0 || colors.Length != width * height)
            throw new ArgumentException("Rastergröße passt nicht zu den Farben.");
        return new(ChromaFrameKind.Grid, width, height, colors, RgbColor.Black, RgbColor.Black, 1);
    }

    public static ChromaFrame Spectrum() => new(ChromaFrameKind.Spectrum, 1, 1, Array.Empty<RgbColor>(), RgbColor.Black, RgbColor.Black, 1);
    public static ChromaFrame Wave(int direction) => new(ChromaFrameKind.Wave, 1, 1, Array.Empty<RgbColor>(), RgbColor.Black, RgbColor.Black, direction < 0 ? -1 : 1);
    public static ChromaFrame Breathing(RgbColor c1, RgbColor c2) => new(ChromaFrameKind.Breathing, 1, 1, Array.Empty<RgbColor>(), c1, c2, 1);
    public static ChromaFrame Blinking(RgbColor c) => new(ChromaFrameKind.Blinking, 1, 1, Array.Empty<RgbColor>(), c, RgbColor.Black, 1);

    /// <summary>Farbe an der Stelle (u, v) ∈ [0, 1]² des Geräts. Raster werden bilinear interpoliert, damit
    /// ein Strip weiche Übergänge zwischen den Tasten zeigt statt Treppenstufen.</summary>
    public RgbColor Sample(double u, double v, TimeSpan time)
    {
        double t = time.TotalSeconds;
        switch (Kind)
        {
            case ChromaFrameKind.Spectrum:
                return CanvasRainbowWaveEffect.SmoothHueToRgb(t * 36.0);
            case ChromaFrameKind.Wave:
                return CanvasRainbowWaveEffect.SmoothHueToRgb(t * 90.0 - Direction * u * 360.0);
            case ChromaFrameKind.Breathing:
                return RgbColor.Lerp(Color1, Color2, 0.5 - 0.5 * Math.Cos(t * Math.PI / 2.0)); // 4 s pro Atemzug
            case ChromaFrameKind.Blinking:
                return t % 1.0 < 0.5 ? Color1 : RgbColor.Black;
        }

        if (Colors.Length == 1) return Colors[0];
        double fx = Math.Clamp(u, 0, 1) * Width - 0.5, fy = Math.Clamp(v, 0, 1) * Height - 0.5;
        int x0 = (int)Math.Floor(fx), y0 = (int)Math.Floor(fy);
        double ax = fx - x0, ay = fy - y0;
        var top = RgbColor.Lerp(At(x0, y0), At(x0 + 1, y0), ax);
        var bottom = RgbColor.Lerp(At(x0, y0 + 1), At(x0 + 1, y0 + 1), ax);
        return RgbColor.Lerp(top, bottom, ay);
    }

    private RgbColor At(int x, int y) => Colors[Math.Clamp(y, 0, Height - 1) * Width + Math.Clamp(x, 0, Width - 1)];

    /// <summary>Mittelwert über das ganze Gerät (für die Mischfarbe).</summary>
    public RgbColor Average(TimeSpan time)
    {
        if (Kind != ChromaFrameKind.Grid) return Kind == ChromaFrameKind.Wave ? Sample(0.5, 0.5, time) : Sample(0, 0, time);
        long r = 0, g = 0, b = 0;
        foreach (var c in Colors) { r += c.R; g += c.G; b += c.B; }
        int n = Colors.Length;
        return new RgbColor((byte)(r / n), (byte)(g / n), (byte)(b / n));
    }

    /// <summary>COLORREF (0x00BBGGRR), wie Chroma Farben übergibt — im SDK und in der REST-API.</summary>
    public static RgbColor FromColorRef(uint c) => new((byte)(c & 0xFF), (byte)((c >> 8) & 0xFF), (byte)((c >> 16) & 0xFF));
}
