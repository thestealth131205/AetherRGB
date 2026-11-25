using AetherRGB.Core.Config;

namespace AetherRGB.Core.Cooling;

/// <summary>Rechnet eine Temperatur-Kurve (Stützpunkte Temperatur → Prozent) in eine Ansteuerung um.</summary>
public static class FanCurve
{
    /// <summary>Lineare Interpolation zwischen den Stützpunkten; unter/über dem ersten/letzten Punkt gilt
    /// dessen Wert. Ohne Punkte 50 % (sicherer Mittelwert statt 0, falls eine Kurve mal leer ist).</summary>
    public static int Evaluate(IReadOnlyList<FanCurvePointConfig> points, double tempC)
    {
        if (points.Count == 0) return 50;
        var sorted = points.OrderBy(p => p.TempC).ToList();
        if (tempC <= sorted[0].TempC) return Clamp(sorted[0].Percent);
        for (int i = 0; i < sorted.Count - 1; i++)
        {
            var a = sorted[i];
            var b = sorted[i + 1];
            if (tempC > b.TempC) continue;
            if (b.TempC <= a.TempC) return Clamp(b.Percent);
            double t = (tempC - a.TempC) / (b.TempC - a.TempC);
            return Clamp((int)Math.Round(a.Percent + t * (b.Percent - a.Percent)));
        }
        return Clamp(sorted[^1].Percent);
    }

    private static int Clamp(int percent) => Math.Clamp(percent, 0, 100);

    /// <summary>Grundprofil für Lüfter ohne Drehzahlsignal (lässt sich nicht kalibrieren): bis 40 °C 50 %,
    /// linear bis 70 °C auf 100 %.</summary>
    public static List<FanCurvePointConfig> Default() => new()
    {
        new FanCurvePointConfig { TempC = 0, Percent = 50 },
        new FanCurvePointConfig { TempC = 40, Percent = 50 },
        new FanCurvePointConfig { TempC = 70, Percent = 100 },
        new FanCurvePointConfig { TempC = 100, Percent = 100 },
    };

    /// <summary>Auf Effizienz ausgelegt: dreht früher hoch, hält die Temperatur niedriger. <paramref name="minPercent"/>
    /// ist die gemessene (oder angenommene) niedrigste Ansteuerung mit Drehzahlsignal.</summary>
    public static List<FanCurvePointConfig> GenerateEfficient(int minPercent, double safeCeilingC)
    {
        int floor = Math.Clamp(minPercent, 0, 90);
        double start = Math.Max(35, safeCeilingC - 30);
        double end = Math.Max(start + 10, safeCeilingC - 5);
        return new List<FanCurvePointConfig>
        {
            new() { TempC = 0, Percent = floor },
            new() { TempC = start, Percent = floor },
            new() { TempC = end, Percent = 100 },
            new() { TempC = 100, Percent = 100 },
        };
    }

    /// <summary>Auf Lautstärke ausgelegt: bleibt länger auf dem Minimum, holt die Drehzahl dafür steiler nach,
    /// sobald es wirklich nötig ist.</summary>
    public static List<FanCurvePointConfig> GenerateQuiet(int minPercent, double safeCeilingC)
    {
        int floor = Math.Clamp(minPercent, 0, 90);
        double start = Math.Max(45, safeCeilingC - 15);
        double end = Math.Max(start + 5, safeCeilingC);
        return new List<FanCurvePointConfig>
        {
            new() { TempC = 0, Percent = floor },
            new() { TempC = start, Percent = floor },
            new() { TempC = end, Percent = 100 },
            new() { TempC = 100, Percent = 100 },
        };
    }
}
