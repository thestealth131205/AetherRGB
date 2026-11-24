using System.Globalization;
using System.IO.MemoryMappedFiles;
using System.Text;
using System.Xml;

namespace AetherRGB.Core.Sensors;

/// <summary>Liest die Werte, die AIDA64 im gemeinsamen Speicher bereitstellt (Einstellungen → Hardwareüberwachung →
/// Externe Programme → „Gemeinsamen Speicher aktivieren“). Inhalt ist ein XML-Fragment ohne Wurzel, z. B.
/// <c>&lt;sys&gt;&lt;id&gt;SCPUUTI&lt;/id&gt;&lt;label&gt;CPU Utilization&lt;/label&gt;&lt;value&gt;12&lt;/value&gt;&lt;/sys&gt;&lt;temp&gt;…</c>.
/// Nur lesen, nichts schreiben — AIDA64 bleibt der einzige, der die Sensoren selbst abfragt.</summary>
public sealed class Aida64SensorSource : ISensorSource
{
    public const string MappingName = "AIDA64_SensorValues";
    private const int MaxBytes = 4 * 1024 * 1024;

    private MemoryMappedFile? _map;
    private readonly Func<string?>? _reader;
    private string _status = "noch nicht abgefragt";
    private int _missingPolls;

    public string Name => "AIDA64";
    public string Status => _status;
    public bool IsAvailable { get; private set; }

    /// <param name="reader">Nur für Tests: liefert den Speicherinhalt statt ihn zu öffnen.</param>
    public Aida64SensorSource(Func<string?>? reader = null) => _reader = reader;

    public IReadOnlyList<SensorReading> Poll()
    {
        string? text = _reader is not null ? _reader() : ReadShared();
        if (text is null)
        {
            IsAvailable = false;
            _status = "nicht gefunden — läuft AIDA64 und ist „Gemeinsamen Speicher aktivieren“ eingeschaltet?";
            return Array.Empty<SensorReading>();
        }
        var readings = Aida64Parser.Parse(text);
        IsAvailable = readings.Count > 0;
        _status = readings.Count > 0 ? $"verbunden, {readings.Count} Werte" : "gemeinsamer Speicher leer (AIDA64 startet noch?)";
        return readings;
    }

    private string? ReadShared()
    {
        if (!OperatingSystem.IsWindows()) return null;
        try
        {
            // Nicht bei jedem Abruf neu suchen, wenn AIDA64 nicht läuft: nur jede 5. Abfrage.
            if (_map is null)
            {
                if (_missingPolls++ % 5 != 0) return null;
                _map = Open(MappingName) ?? Open(@"Global\" + MappingName);
                if (_map is null) return null;
            }
            using var view = _map.CreateViewStream(0, 0, MemoryMappedFileAccess.Read);
            int len = (int)Math.Min(view.Length, MaxBytes);
            var buffer = new byte[len];
            int read = 0;
            while (read < len)
            {
                int n = view.Read(buffer, read, len - read);
                if (n <= 0) break;
                read += n;
            }
            int end = Array.IndexOf(buffer, (byte)0, 0, read);
            if (end < 0) end = read;
            // AIDA64 schreibt in der ANSI-Codepage (bei deutschem Windows 1252) — Latin-1 trifft die Umlaute.
            return Encoding.Latin1.GetString(buffer, 0, end);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // AIDA64 beendet → Mapping weg; beim nächsten Mal neu öffnen.
            _map?.Dispose();
            _map = null;
            return null;
        }
    }

    private static MemoryMappedFile? Open(string name)
    {
        try
        {
#pragma warning disable CA1416 // nur unter Windows erreichbar (oben geprüft)
            return MemoryMappedFile.OpenExisting(name, MemoryMappedFileRights.Read);
#pragma warning restore CA1416
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or PlatformNotSupportedException) { return null; }
    }

    public void Dispose()
    {
        _map?.Dispose();
        _map = null;
    }
}

public static class Aida64Parser
{
    public const string IdPrefix = "aida/";

    /// <summary>Zerlegt den Inhalt des gemeinsamen Speichers. Unvollständige Einträge (AIDA64 schreibt gerade) und
    /// kaputtes XML am Ende werden übersprungen.</summary>
    public static IReadOnlyList<SensorReading> Parse(string content)
    {
        var result = new List<SensorReading>();
        if (string.IsNullOrWhiteSpace(content)) return result;
        var settings = new XmlReaderSettings { ConformanceLevel = ConformanceLevel.Fragment, DtdProcessing = DtdProcessing.Prohibit, XmlResolver = null, IgnoreWhitespace = true };
        var seen = new HashSet<string>();
        try
        {
            using var reader = XmlReader.Create(new StringReader(content), settings);
            while (reader.Read())
            {
                if (reader.NodeType != XmlNodeType.Element || reader.Depth != 0) continue;
                string kind = reader.Name;
                string? id = null, label = null, value = null;
                using (var sub = reader.ReadSubtree())
                {
                    sub.Read(); // Wurzel des Eintrags (<sys>, <temp> …)
                    sub.Read();
                    // ReadElementContentAsString steht danach schon auf dem nächsten Knoten — dann nicht noch einmal Read(),
                    // sonst wird das folgende Feld übersprungen.
                    while (!sub.EOF)
                    {
                        if (sub.NodeType != XmlNodeType.Element || sub.Depth != 1) { sub.Read(); continue; }
                        switch (sub.Name)
                        {
                            case "id": id = sub.ReadElementContentAsString().Trim(); break;
                            case "label": label = sub.ReadElementContentAsString().Trim(); break;
                            case "value": value = sub.ReadElementContentAsString().Trim(); break;
                            default: sub.Skip(); break;
                        }
                    }
                }
                if (string.IsNullOrEmpty(id) || value is null || !seen.Add(id)) continue;
                result.Add(Create(kind, id, string.IsNullOrEmpty(label) ? id : label, value));
            }
        }
        catch (XmlException)
        {
            // Rest abgeschnitten — was bis hierher kam, gilt.
        }
        return result;
    }

    private static SensorReading Create(string kind, string id, string label, string value)
    {
        double? number = ParseNumber(value);
        (string group, string unit) = kind switch
        {
            "temp" => (SensorGroups.Temperature, "°C"),
            "fan" => (SensorGroups.Fan, "RPM"),
            "duty" => (SensorGroups.Fan, "%"),
            "volt" => (SensorGroups.Voltage, "V"),
            "curr" => (SensorGroups.Current, "A"),
            "pwr" => (SensorGroups.Power, "W"),
            _ => (SystemGroup(id), SystemUnit(id)),
        };
        if (number is null) unit = "";
        return new SensorReading(IdPrefix + id, label, group, unit, number, number is null ? value : null);
    }

    public static double? ParseNumber(string value)
    {
        var v = value.Trim();
        if (v.Length == 0) return null;
        if (v.Contains(',') && !v.Contains('.')) v = v.Replace(',', '.');
        return double.TryParse(v, NumberStyles.Float, CultureInfo.InvariantCulture, out var d) && double.IsFinite(d) ? d : null;
    }

    private static string SystemGroup(string id)
    {
        var u = id.ToUpperInvariant();
        if (u.StartsWith("SCPU") || u.StartsWith("SCC") || u.StartsWith("SHTT")) return SensorGroups.Cpu;
        if (u.StartsWith("SGPU") || u.StartsWith("SVMEM") || u.Contains("FPS")) return SensorGroups.Gpu;
        if (u.Contains("MEM") || u.StartsWith("SVIRT") || u.StartsWith("SPAGE")) return SensorGroups.Memory;
        if (u.StartsWith("SDSK") || u.StartsWith("SDRV") || u.StartsWith("SHDD")) return SensorGroups.Storage;
        if (u.StartsWith("SNIC")) return SensorGroups.Network;
        return SensorGroups.System;
    }

    private static string SystemUnit(string id)
    {
        var u = id.ToUpperInvariant();
        if (u.Contains("UTI") || u.EndsWith("ACT") || u.Contains("USAGE") || u.EndsWith("PCT")) return "%";
        if (u.EndsWith("CLK")) return "MHz";
        if (u.Contains("RATE") || u.Contains("SPD") || u.Contains("DL") && u.StartsWith("SNIC") || u.Contains("UL") && u.StartsWith("SNIC")) return "KB/s";
        if (u.Contains("FPS")) return "FPS";
        if (u.Contains("MEM") || u.Contains("FREE") || u.Contains("USED")) return "MB";
        return "";
    }
}
