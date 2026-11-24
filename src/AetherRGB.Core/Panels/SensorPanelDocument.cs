using System.Text.Json;
using System.Text.Json.Serialization;
using AetherRGB.Core.Devices;
using AetherRGB.Core.Screen;

namespace AetherRGB.Core.Panels;

public enum PanelItemKind
{
    /// <summary>Fester Text.</summary>
    Text,
    /// <summary>Messwert als Zahl, optional mit Beschriftung und Einheit.</summary>
    Value,
    /// <summary>Balken (waagerecht/senkrecht, durchgehend oder in Segmenten).</summary>
    Bar,
    /// <summary>Rundinstrument (Bogen).</summary>
    Gauge,
    /// <summary>Verlaufsgrafik der letzten Sekunden.</summary>
    Graph,
    /// <summary>Uhrzeit/Datum mit Formatangabe.</summary>
    Clock,
    /// <summary>Fläche, auf der ein Effekt läuft (eingebaut oder aus dem Effekt-Editor).</summary>
    Animation,
    /// <summary>Bild (PNG/JPG/BMP, animiertes GIF).</summary>
    Image,
    /// <summary>Rechteck als Hintergrund/Rahmen für Gruppen.</summary>
    Box,
}

public enum PanelAlign { Left, Center, Right }

public enum PanelStretch { Uniform, UniformToFill, Fill }

/// <summary>Ein Element im Sensor-Panel. Bewusst eine flache Klasse für alle Arten: der Editor zeigt je Art nur die
/// passenden Felder, Dateien bleiben bei neuen Feldern lesbar. Koordinaten in Panel-Pixeln.</summary>
public sealed class PanelItem
{
    public string Id { get; set; } = Guid.NewGuid().ToString("N")[..8];

    [JsonConverter(typeof(JsonStringEnumConverter))]
    public PanelItemKind Kind { get; set; } = PanelItemKind.Text;

    public string Name { get; set; } = "";
    public bool Visible { get; set; } = true;

    public double X { get; set; }
    public double Y { get; set; }
    public double W { get; set; } = 200;
    public double H { get; set; } = 40;

    /// <summary>Sensor-ID, mehrere mit „|“ getrennt = erster verfügbarer (z. B. <c>aida/TCPU|nvml/0/temp</c>).</summary>
    public string SensorId { get; set; } = "";

    /// <summary>Text (Text), Beschriftung (Wert, Balken, Instrument, Verlauf) bzw. Format (Uhr, .NET-Format wie <c>HH:mm:ss</c>).</summary>
    public string Text { get; set; } = "";

    public bool ShowLabel { get; set; } = true;
    public bool ShowUnit { get; set; } = true;
    public bool ShowValue { get; set; } = true;
    /// <summary>Nachkommastellen, -1 = automatisch.</summary>
    public int Decimals { get; set; } = -1;

    public double FontSize { get; set; } = 22;
    public bool Bold { get; set; }
    public string FontFamily { get; set; } = "Segoe UI";

    [JsonConverter(typeof(JsonStringEnumConverter))]
    public PanelAlign Align { get; set; } = PanelAlign.Left;

    /// <summary>Text-/Wertfarbe.</summary>
    public RgbColor Color { get; set; } = new(231, 232, 236);
    public RgbColor LabelColor { get; set; } = new(140, 143, 160);
    /// <summary>Füllfarbe von Balken, Bogen, Verlaufslinie.</summary>
    public RgbColor AccentColor { get; set; } = new(124, 92, 255);
    /// <summary>Hintergrund (Box) bzw. leerer Teil von Balken/Bogen.</summary>
    public RgbColor BackColor { get; set; } = new(32, 33, 42);
    public double BackOpacity { get; set; } = 1;
    public RgbColor BorderColor { get; set; } = new(42, 43, 53);
    public double BorderWidth { get; set; }
    public double CornerRadius { get; set; } = 6;
    public double Opacity { get; set; } = 1;

    /// <summary>Feste Skala; null = aus der Einheit (z. B. % → 0–100).</summary>
    public double? Min { get; set; }
    public double? Max { get; set; }

    /// <summary>Schwellen für Warn-/Kritisch-Farbe. Ist <see cref="CritAt"/> kleiner als <see cref="WarnAt"/>, gelten kleine
    /// Werte als schlecht (z. B. freier Speicher).</summary>
    public double? WarnAt { get; set; }
    public double? CritAt { get; set; }
    public RgbColor WarnColor { get; set; } = new(245, 158, 11);
    public RgbColor CritColor { get; set; } = new(239, 68, 68);

    /// <summary>Balken senkrecht (füllt von unten).</summary>
    public bool Vertical { get; set; }
    /// <summary>0 = durchgehend, sonst Anzahl Segmente (AIDA64-Stil).</summary>
    public int Segments { get; set; }
    /// <summary>Instrument: Bogen in Grad (Mitte unten offen), Linienstärke für Bogen und Verlauf.</summary>
    public double GaugeSweep { get; set; } = 260;
    public double Thickness { get; set; } = 12;

    public int GraphSeconds { get; set; } = 60;
    public bool GraphFill { get; set; } = true;
    public bool GraphGrid { get; set; } = true;

    /// <summary>Animation: Effekt-ID aus dem Katalog (eingebaut oder <c>custom:…</c>).</summary>
    public string EffectId { get; set; } = "rainbow-wave";
    public List<RgbColor> EffectColors { get; set; } = new();
    public int SpeedPercent { get; set; } = 100;
    /// <summary>Panel-Pixel je berechnetem Punkt; größer = gröber, aber sparsamer.</summary>
    public int CellSize { get; set; } = 6;

    /// <summary>Bild: Dateiname im Ordner <c>panels\assets</c> oder absoluter Pfad.</summary>
    public string ImagePath { get; set; } = "";

    [JsonConverter(typeof(JsonStringEnumConverter))]
    public PanelStretch Stretch { get; set; } = PanelStretch.Uniform;

    public PanelItem Clone()
    {
        var c = (PanelItem)MemberwiseClone();
        c.EffectColors = EffectColors.ToList();
        return c;
    }

    public override string ToString() => string.IsNullOrWhiteSpace(Name) ? Kind.ToString() : Name;

    public static string KindName(PanelItemKind kind) => kind switch
    {
        PanelItemKind.Text => "Text",
        PanelItemKind.Value => "Wert",
        PanelItemKind.Bar => "Balken",
        PanelItemKind.Gauge => "Rundinstrument",
        PanelItemKind.Graph => "Verlauf",
        PanelItemKind.Clock => "Uhr / Datum",
        PanelItemKind.Animation => "Animation",
        PanelItemKind.Image => "Bild",
        PanelItemKind.Box => "Rechteck",
        _ => kind.ToString(),
    };
}

/// <summary>Ein Sensor-Panel für ein Zweitdisplay (Datei <c>%APPDATA%\AetherRGB\panels\&lt;id&gt;.json</c>).</summary>
public sealed class SensorPanelDocument
{
    public const int CurrentVersion = 1;

    public int Version { get; set; } = CurrentVersion;
    public string Id { get; set; } = Guid.NewGuid().ToString("N")[..12];
    public string Name { get; set; } = "Neues Panel";
    public int Width { get; set; } = 1024;
    public int Height { get; set; } = 600;
    public RgbColor Background { get; set; } = new(7, 8, 11);
    public string BackgroundImage { get; set; } = "";

    /// <summary>Zeichenreihenfolge: erstes Element ganz unten.</summary>
    public List<PanelItem> Items { get; set; } = new();

    public SensorPanelDocument Clone()
    {
        var c = (SensorPanelDocument)MemberwiseClone();
        c.Items = Items.Select(i => i.Clone()).ToList();
        return c;
    }

    public override string ToString() => Name;
}

public static class SensorPanelSerializer
{
    public static readonly JsonSerializerOptions Options = new()
    {
        WriteIndented = true,
        Converters = { new RgbColorHexConverter() },
    };

    public const int MaxItems = 400;
    public const int MinSize = 64, MaxSize = 7680;

    public static string Serialize(SensorPanelDocument doc) => JsonSerializer.Serialize(doc, Options);

    public static SensorPanelDocument Deserialize(string json)
    {
        var doc = JsonSerializer.Deserialize<SensorPanelDocument>(json, Options) ?? throw new JsonException("Leeres Panel.");
        Sanitize(doc);
        return doc;
    }

    /// <summary>Klemmt alle Werte auf gültige Bereiche (Dateien können von Hand bearbeitet oder importiert sein).</summary>
    public static void Sanitize(SensorPanelDocument doc)
    {
        doc.Id = string.IsNullOrWhiteSpace(doc.Id) ? Guid.NewGuid().ToString("N")[..12] : SafeId(doc.Id);
        doc.Name = string.IsNullOrWhiteSpace(doc.Name) ? "Panel" : doc.Name.Trim()[..Math.Min(80, doc.Name.Trim().Length)];
        doc.Width = Math.Clamp(doc.Width, MinSize, MaxSize);
        doc.Height = Math.Clamp(doc.Height, MinSize, MaxSize);
        doc.BackgroundImage ??= "";
        doc.Items ??= new();
        doc.Items.RemoveAll(i => i is null);
        if (doc.Items.Count > MaxItems) doc.Items = doc.Items.Take(MaxItems).ToList();
        var ids = new HashSet<string>();
        foreach (var i in doc.Items)
        {
            if (string.IsNullOrWhiteSpace(i.Id) || !ids.Add(i.Id)) { i.Id = Guid.NewGuid().ToString("N")[..8]; ids.Add(i.Id); }
            if (!Enum.IsDefined(i.Kind)) i.Kind = PanelItemKind.Text;
            i.Name ??= "";
            i.SensorId = (i.SensorId ?? "").Trim();
            i.Text ??= "";
            if (i.Text.Length > 2000) i.Text = i.Text[..2000];
            i.FontFamily = string.IsNullOrWhiteSpace(i.FontFamily) ? "Segoe UI" : i.FontFamily.Trim();
            i.W = Finite(i.W, 200, 2, MaxSize);
            i.H = Finite(i.H, 40, 2, MaxSize);
            i.X = Finite(i.X, 0, -MaxSize, MaxSize);
            i.Y = Finite(i.Y, 0, -MaxSize, MaxSize);
            i.FontSize = Finite(i.FontSize, 22, 4, 400);
            i.Decimals = Math.Clamp(i.Decimals, -1, 6);
            i.Opacity = Finite(i.Opacity, 1, 0, 1);
            i.BackOpacity = Finite(i.BackOpacity, 1, 0, 1);
            i.BorderWidth = Finite(i.BorderWidth, 0, 0, 50);
            i.CornerRadius = Finite(i.CornerRadius, 6, 0, 500);
            i.Segments = Math.Clamp(i.Segments, 0, 100);
            i.GaugeSweep = Finite(i.GaugeSweep, 260, 30, 360);
            i.Thickness = Finite(i.Thickness, 12, 1, 200);
            i.GraphSeconds = Math.Clamp(i.GraphSeconds, 5, Sensors.SensorHub.HistoryLength);
            i.EffectId = string.IsNullOrWhiteSpace(i.EffectId) ? "rainbow-wave" : i.EffectId.Trim();
            i.EffectColors ??= new();
            if (i.EffectColors.Count > 8) i.EffectColors = i.EffectColors.Take(8).ToList();
            i.SpeedPercent = Math.Clamp(i.SpeedPercent, Effects.EffectCatalog.MinSpeedPercent, Effects.EffectCatalog.MaxSpeedPercent);
            i.CellSize = Math.Clamp(i.CellSize, 1, 64);
            i.ImagePath ??= "";
            if (i.Min is { } mn && !double.IsFinite(mn)) i.Min = null;
            if (i.Max is { } mx && !double.IsFinite(mx)) i.Max = null;
            if (i.WarnAt is { } wa && !double.IsFinite(wa)) i.WarnAt = null;
            if (i.CritAt is { } ca && !double.IsFinite(ca)) i.CritAt = null;
        }
    }

    private static double Finite(double v, double fallback, double min, double max) => double.IsFinite(v) ? Math.Clamp(v, min, max) : fallback;

    public static string SafeId(string id)
    {
        var chars = id.Where(c => char.IsAsciiLetterOrDigit(c) || c is '-' or '_').Take(40).ToArray();
        return chars.Length == 0 ? Guid.NewGuid().ToString("N")[..12] : new string(chars);
    }
}
