using System.Globalization;

namespace AetherRGB.Core.Sensors;

/// <summary>Formatiert Messwerte für die Anzeige (deutsches Zahlenformat, Byte-Einheiten automatisch skaliert).</summary>
public static class SensorFormat
{
    private static readonly string[] ByteUnits = { "B", "KB", "MB", "GB", "TB" };

    /// <summary>Wert mit Einheit, z. B. "45 °C", "3,2 GB", "12,5 MB/s". <paramref name="decimals"/> &lt; 0 = automatisch.</summary>
    public static string Format(SensorReading? reading, int decimals = -1, bool withUnit = true, CultureInfo? culture = null)
    {
        culture ??= CultureInfo.CurrentCulture;
        if (reading is null) return "–";
        if (!reading.IsNumeric) return reading.Text ?? "–";
        var (value, unit) = Scale(reading.Value!.Value, reading.Unit);
        string number = value.ToString("F" + (decimals >= 0 ? Math.Min(decimals, 6) : AutoDecimals(value, unit)), culture);
        return withUnit && unit.Length > 0 ? $"{number} {unit}" : number;
    }

    /// <summary>Byte-Einheiten (B, KB, MB, GB, auch …/s) so umrechnen, dass der Wert unter 1024 bleibt.</summary>
    public static (double Value, string Unit) Scale(double value, string unit)
    {
        bool perSecond = unit.EndsWith("/s", StringComparison.Ordinal);
        string baseUnit = perSecond ? unit[..^2] : unit;
        int index = Array.IndexOf(ByteUnits, baseUnit);
        if (index < 0) return (value, unit);
        while (Math.Abs(value) >= 1024 && index < ByteUnits.Length - 1) { value /= 1024; index++; }
        return (value, ByteUnits[index] + (perSecond ? "/s" : ""));
    }

    private static int AutoDecimals(double value, string unit)
    {
        if (unit is "%" or "RPM" or "MHz" or "°C" or "FPS" or "B" or "B/s") return 0;
        double a = Math.Abs(value);
        return a >= 100 ? 0 : a >= 10 ? 1 : 2;
    }

    /// <summary>Sinnvoller Standardbereich für Balken/Instrumente, wenn im Panel kein fester Bereich eingetragen ist.</summary>
    public static (double Min, double Max) DefaultRange(string unit) => unit switch
    {
        "%" => (0, 100),
        "°C" => (20, 100),
        "RPM" => (0, 3000),
        "MHz" => (0, 6000),
        "V" => (0, 13),
        "W" => (0, 300),
        "A" => (0, 30),
        "FPS" => (0, 240),
        _ => (0, 100),
    };
}
