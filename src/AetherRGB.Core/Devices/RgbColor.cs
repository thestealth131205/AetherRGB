namespace AetherRGB.Core.Devices;

/// <summary>Einfache 24-Bit-RGB-Farbe, keine Alpha-Komponente (ARGB-Hardware kennt kein Alpha).</summary>
public readonly record struct RgbColor(byte R, byte G, byte B)
{
    public static readonly RgbColor Black = new(0, 0, 0);
    public static readonly RgbColor White = new(255, 255, 255);

    public static RgbColor FromHex(string hex)
    {
        hex = hex.TrimStart('#');
        if (hex.Length != 6)
            throw new FormatException($"Ungültiger Hex-Farbcode: '{hex}'");
        return new RgbColor(
            Convert.ToByte(hex[..2], 16),
            Convert.ToByte(hex[2..4], 16),
            Convert.ToByte(hex[4..6], 16));
    }

    public string ToHex() => $"#{R:X2}{G:X2}{B:X2}";

    public static RgbColor Lerp(RgbColor a, RgbColor b, double t)
    {
        t = Math.Clamp(t, 0.0, 1.0);
        return new RgbColor(
            (byte)(a.R + (b.R - a.R) * t),
            (byte)(a.G + (b.G - a.G) * t),
            (byte)(a.B + (b.B - a.B) * t));
    }
}
