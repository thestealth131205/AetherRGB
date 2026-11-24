namespace AetherRGB.Core.Sensors;

/// <summary>Ein Messwert zu einem Zeitpunkt. <see cref="Id"/> ist stabil über Neustarts (z. B. <c>win/cpu/load</c>,
/// <c>aida/TCPU</c>, <c>nvml/0/temp</c>) und wird so in Panels gespeichert. Werte ohne Zahl (Datum, Name eines
/// Laufwerks) stehen in <see cref="Text"/>.</summary>
public sealed record SensorReading(string Id, string Name, string Group, string Unit, double? Value, string? Text = null)
{
    /// <summary>Quelle, z. B. "Windows", "AIDA64", "NVIDIA" — vom <see cref="SensorHub"/> gesetzt.</summary>
    public string Source { get; init; } = "";

    public bool IsNumeric => Value is { } v && double.IsFinite(v);
}

/// <summary>Gruppen für die Sensorliste im Editor (Reihenfolge = Anzeige).</summary>
public static class SensorGroups
{
    public const string System = "System";
    public const string Cpu = "CPU";
    public const string Gpu = "Grafikkarte";
    public const string Memory = "Arbeitsspeicher";
    public const string Storage = "Laufwerke";
    public const string Network = "Netzwerk";
    public const string Temperature = "Temperaturen";
    public const string Fan = "Lüfter";
    public const string Voltage = "Spannungen";
    public const string Power = "Leistung";
    public const string Current = "Ströme";

    public static readonly IReadOnlyList<string> Order = new[] { System, Cpu, Gpu, Memory, Storage, Network, Temperature, Fan, Voltage, Current, Power };

    public static int Rank(string group)
    {
        int i = Order.ToList().IndexOf(group);
        return i < 0 ? Order.Count : i;
    }
}

/// <summary>Liefert Messwerte. <see cref="Poll"/> wird vom Hub-Thread aufgerufen (nie gleichzeitig) und darf werfen —
/// dann gilt die Quelle bis zum nächsten Versuch als nicht verfügbar.</summary>
public interface ISensorSource : IDisposable
{
    /// <summary>Kurzname, z. B. "AIDA64".</summary>
    string Name { get; }

    /// <summary>Zustand für Einstellungen und Editor, z. B. "verbunden, 143 Werte" oder "nicht gefunden".</summary>
    string Status { get; }

    /// <summary>true = hat beim letzten Abruf Werte geliefert.</summary>
    bool IsAvailable { get; }

    IReadOnlyList<SensorReading> Poll();
}
