using AetherRGB.Core.Devices;

namespace AetherRGB.Core.Screen;

/// <summary>Macht gemittelte Bildschirmfarben für LEDs kräftiger. Mittelwerte über viele Pixel sind fast immer blass
/// (Farben mischen sich zu Grau), auf einem Strip wirkt das verwaschen.</summary>
public static class ColorBoost
{
    /// <param name="amount">1 = unverändert, 1,6 = Sättigung × 1,6. Die Helligkeit wird dabei leicht angehoben, sonst
    /// werden kräftigere dunkle Farben matschig. Fast graue Farben (Sättigung unter etwa 4 %) bleiben grau — sonst
    /// färbt Bildrauschen einen grauen Himmel bunt.</param>
    public static RgbColor Saturate(RgbColor c, double amount)
    {
        if (amount <= 1.0001) return c;
        var (h, s, v) = ColorMatcher.ToHsv(c.R, c.G, c.B);
        if (v < 0.02) return c;
        double ramp = Math.Clamp((s - 0.04) / 0.08, 0, 1);
        if (ramp <= 0) return c;
        double s2 = Math.Min(1, s * (1 + (amount - 1) * ramp));
        double v2 = Math.Pow(v, 1 / (1 + (amount - 1) * 0.4 * ramp));
        return ColorMatcher.FromHsv(h, s2, v2);
    }

    public static void Saturate(RgbColor[] colors, double amount)
    {
        if (amount <= 1.0001) return;
        for (int i = 0; i < colors.Length; i++) colors[i] = Saturate(colors[i], amount);
    }
}
