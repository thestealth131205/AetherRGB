using System.Globalization;
using AetherRGB.Core.Devices;
using AetherRGB.Core.Sensors;

namespace AetherRGB.Core.Panels;

/// <summary>Rechenteil der Panel-Anzeige, unabhängig von WPF (testbar): welcher Sensor gilt, Skala, Füllstand, Farbe, Text.</summary>
public static class PanelLogic
{
    /// <summary>Erster vorhandene Sensor aus der „|“-Liste; bevorzugt einen mit Zahl.</summary>
    public static SensorReading? Resolve(string? sensorIds, Func<string, SensorReading?> lookup)
    {
        if (string.IsNullOrWhiteSpace(sensorIds)) return null;
        SensorReading? textOnly = null;
        foreach (var raw in sensorIds.Split('|'))
        {
            var id = raw.Trim();
            if (id.Length == 0) continue;
            var r = lookup(id);
            if (r is null) continue;
            if (r.IsNumeric) return r;
            textOnly ??= r;
        }
        return textOnly;
    }

    /// <summary>ID des tatsächlich verwendeten Sensors (für den Verlauf).</summary>
    public static string? ResolveId(string? sensorIds, Func<string, SensorReading?> lookup) => Resolve(sensorIds, lookup)?.Id;

    public static (double Min, double Max) Range(PanelItem item, SensorReading? reading)
    {
        var (dMin, dMax) = SensorFormat.DefaultRange(reading?.Unit ?? "%");
        double min = item.Min ?? dMin, max = item.Max ?? dMax;
        if (max <= min) max = min + 1;
        return (min, max);
    }

    /// <summary>Füllstand 0..1 (NaN ohne Wert).</summary>
    public static double Fraction(PanelItem item, SensorReading? reading)
    {
        if (reading is null || !reading.IsNumeric) return double.NaN;
        var (min, max) = Range(item, reading);
        return Math.Clamp((reading.Value!.Value - min) / (max - min), 0, 1);
    }

    public enum Level { Normal, Warn, Critical }

    public static Level LevelOf(PanelItem item, double value)
    {
        if (!double.IsFinite(value)) return Level.Normal;
        bool lowIsBad = item.CritAt is { } c && item.WarnAt is { } w && c < w;
        if (lowIsBad)
        {
            if (value <= item.CritAt!.Value) return Level.Critical;
            if (value <= item.WarnAt!.Value) return Level.Warn;
            return Level.Normal;
        }
        if (item.CritAt is { } crit && value >= crit) return Level.Critical;
        if (item.WarnAt is { } warn && value >= warn) return Level.Warn;
        return Level.Normal;
    }

    /// <summary>Farbe für Wert/Füllung je nach Schwelle.</summary>
    public static RgbColor ColorFor(PanelItem item, SensorReading? reading, RgbColor normal)
    {
        if (reading is null || !reading.IsNumeric) return normal;
        return LevelOf(item, reading.Value!.Value) switch
        {
            Level.Critical => item.CritColor,
            Level.Warn => item.WarnColor,
            _ => normal,
        };
    }

    /// <summary>Anzeigetext des Werts (ohne Beschriftung).</summary>
    public static string ValueText(PanelItem item, SensorReading? reading) =>
        SensorFormat.Format(reading, item.Decimals, item.ShowUnit);

    /// <summary>Beschriftung: eigener Text oder der Sensorname.</summary>
    public static string LabelText(PanelItem item, SensorReading? reading) =>
        !string.IsNullOrWhiteSpace(item.Text) ? item.Text : reading?.Name ?? (string.IsNullOrWhiteSpace(item.SensorId) ? "" : item.SensorId.Split('|')[0]);

    public static string ClockText(PanelItem item, DateTime now, CultureInfo? culture = null)
    {
        culture ??= CultureInfo.CurrentCulture;
        var format = string.IsNullOrWhiteSpace(item.Text) ? "HH:mm:ss" : item.Text;
        try { return now.ToString(format, culture); }
        catch (FormatException) { return now.ToString("HH:mm:ss", culture); }
    }

    /// <summary>Skala für den Verlauf: feste Grenzen, sonst aus der Einheit; bei Byte-Raten u. ä. ohne festen Bereich
    /// wächst das Maximum mit dem größten Wert im Fenster (auf eine runde Zahl).</summary>
    public static (double Min, double Max) GraphRange(PanelItem item, SensorReading? reading, IReadOnlyList<double> history)
    {
        string unit = reading?.Unit ?? "";
        bool fixedUnit = unit is "%" or "°C";
        if (item.Min is not null && item.Max is not null || fixedUnit && item.Max is null && item.Min is null) return Range(item, reading);
        double min = item.Min ?? 0;
        double peak = history.Where(double.IsFinite).DefaultIfEmpty(0).Max();
        double max = item.Max ?? NiceCeiling(Math.Max(peak, min + 1e-9));
        if (max <= min) max = min + 1;
        return (min, max);
    }

    /// <summary>Nächstgrößere „runde“ Zahl (1, 2, 5 × 10ⁿ).</summary>
    public static double NiceCeiling(double v)
    {
        if (v <= 0 || !double.IsFinite(v)) return 1;
        double exp = Math.Pow(10, Math.Floor(Math.Log10(v)));
        foreach (var m in new[] { 1.0, 2.0, 5.0, 10.0 })
            if (m * exp >= v) return m * exp;
        return 10 * exp;
    }

    /// <summary>Oberstes sichtbares Element unter dem Punkt (für den Editor).</summary>
    public static PanelItem? HitTest(SensorPanelDocument doc, double x, double y)
    {
        for (int i = doc.Items.Count - 1; i >= 0; i--)
        {
            var it = doc.Items[i];
            if (x >= it.X && x <= it.X + it.W && y >= it.Y && y <= it.Y + it.H) return it;
        }
        return null;
    }

    /// <summary>Auf ein Raster runden (Editor, „Am Raster ausrichten“).</summary>
    public static double Snap(double v, double grid) => grid <= 1 ? Math.Round(v) : Math.Round(v / grid) * grid;
}
