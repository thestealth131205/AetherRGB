using System.Text.Json;
using System.Text.Json.Serialization;
using AetherRGB.Core.Devices;

namespace AetherRGB.Core.Screen;

/// <summary>Was ein Bereich auf dem Bildschirm misst.</summary>
public enum RegionRule
{
    /// <summary>Mischfarbe des Bereichs.</summary>
    Average,
    /// <summary>Anteil der Pixel, die zu einer der Farben passen (z. B. Sturm in der Minimap).</summary>
    ColorShare,
    /// <summary>Füllstand eines Balkens (z. B. Leben, Schild): wie weit die Füllfarbe reicht.</summary>
    BarFill,
    /// <summary>Blitz, wenn der Anteil einer Farbe plötzlich steigt (z. B. roter Bildschirmrand).</summary>
    ColorFlash,
    /// <summary>Blitz, wenn die Werte anderer Bereiche fallen (z. B. Leben + Schild = Schaden).</summary>
    ValueDrop,
    /// <summary>Kein Licht, nur Prüfung: Ist das Spiel-HUD zu sehen? Sonst pausieren die Regeln.</summary>
    HudCheck,
    /// <summary>Abstand von der Mitte des Bereichs zur nächsten Stelle in einer der Farben, 0 = Farbe in der Mitte,
    /// 1 = keine Farbe in der Nähe — z. B. Sturm in der Minimap (die Spielfigur steht immer in der Mitte).</summary>
    ColorDistance,
}

public enum BarDirection { LeftToRight, RightToLeft, BottomToTop, TopToBottom }

/// <summary>Wie viele LEDs eines zugewiesenen Geräts leuchten.</summary>
public enum DeviceFill
{
    /// <summary>Anteil der LEDs = Wert (voller Lebensbalken = alle LEDs).</summary>
    Value,
    /// <summary>Anteil der LEDs = 1 − Wert (Sturm-Abstand: je näher der Sturm, desto mehr LEDs).</summary>
    Inverse,
    /// <summary>Immer alle LEDs, nur die Farbe der Stufe zählt.</summary>
    All,
}

/// <summary>Farbstufe für die Anzeige auf einem Gerät: gilt, solange der Wert höchstens <see cref="UpTo"/> ist.
/// Von allen passenden Stufen gilt die mit dem kleinsten <see cref="UpTo"/> — z. B. Leben ≤ 100 % grün,
/// ≤ 50 % gelb, ≤ 30 % orange, ≤ 15 % rot blinkend.</summary>
public sealed record GaugeStep
{
    public double UpTo { get; init; } = 1;
    public RgbColor Color { get; init; } = new(0, 255, 0);
    /// <summary>Blinken — gilt zugleich als „kritisch“: dann überdeckt der Bereich auf einem Gerät mit mehreren
    /// Bereichen die Hauptanzeige.</summary>
    public bool Blink { get; init; }

    public GaugeStep() { }
    public GaugeStep(double upTo, RgbColor color, bool blink = false) { UpTo = upTo; Color = color; Blink = blink; }
}

/// <summary>Wie ein Wert auf den LEDs erscheint.</summary>
public enum LayerDisplay
{
    /// <summary>Der ganze Zielbereich der Effekt-Fläche bekommt die Farbe.</summary>
    Fill,
    /// <summary>Als Balken über den Zielbereich (von links nach rechts) — z. B. das Leben auf einem Strip.</summary>
    Bar,
    /// <summary>Das Bild des Bereichs 1:1, nur vergrößert auf die Zielfläche — der Lebensbalken aus dem Spiel erscheint
    /// so, wie er ist, über die volle Effekt-Breite. Bereiche mit Zielfläche „ganze Fläche“ werden dabei automatisch
    /// untereinander gestapelt (in der Reihenfolge, in der sie auf dem Monitor liegen).</summary>
    Mirror,
    /// <summary>Lauflicht: bei einem Auslöser leuchtet die Zielfläche einmal von rechts nach links auf (Blitze, und bei
    /// „Abstand zur Farbe“, sobald die Farbe näher als <see cref="ScreenRegion.TriggerBelow"/> kommt — z. B. Sturm taucht
    /// auf der Minimap auf).</summary>
    Sweep,
}

/// <summary>Was die Lichter zeigen, solange das HUD nicht zu sehen ist (Menü, Lobby, Zuschauen).</summary>
public enum HudHiddenMode
{
    /// <summary>Mischfarbe des ganzen Monitors.</summary>
    ScreenAverage,
    /// <summary>LEDs aus.</summary>
    Off,
    /// <summary>Regeln laufen trotzdem weiter.</summary>
    KeepRunning,
}

/// <summary>Ein Bereich auf dem Monitor mit einer Messregel und der Wirkung auf die LEDs.</summary>
public sealed class ScreenRegion
{
    public string Id { get; set; } = Guid.NewGuid().ToString("N")[..8];
    public string Name { get; set; } = "Bereich";
    public bool Enabled { get; set; } = true;

    /// <summary>Position auf dem Monitor, relativ (0..1) — bleibt bei anderer Auflösung mit gleichem Seitenverhältnis gültig.</summary>
    public RelRect Area { get; set; } = new(0.4, 0.4, 0.2, 0.2);

    [JsonConverter(typeof(JsonStringEnumConverter))]
    public RegionRule Rule { get; set; } = RegionRule.Average;

    /// <summary>Gesuchte Farben (ColorShare, BarFill, ColorFlash, HudCheck) — eine passende reicht.</summary>
    public List<ColorMatcher> Colors { get; set; } = new();

    // ---- Regel-Einstellungen ----

    /// <summary>ColorShare: Anteil, bei dem der Wert 100 % erreicht (0,5 = halber Bereich in der Farbe).</summary>
    public double FullAt { get; set; } = 0.5;

    /// <summary>ColorShare: Anteile darunter zählen als 0 (gegen einzelne passende Pixel).</summary>
    public double MinShare { get; set; } = 0.02;

    [JsonConverter(typeof(JsonStringEnumConverter))]
    public BarDirection Direction { get; set; } = BarDirection.LeftToRight;

    /// <summary>ColorFlash: Anstieg des Farbanteils, ab dem geblitzt wird. ValueDrop: Abfall (Summe der Quellen,
    /// 0..1 je Quelle), ab dem geblitzt wird. HudCheck: Mindestanteil der Farben, damit das HUD als sichtbar gilt.</summary>
    public double Threshold { get; set; } = 0.05;

    /// <summary>ValueDrop: IDs der Bereiche, deren Werte zusammengezählt werden.</summary>
    public List<string> Sources { get; set; } = new();

    /// <summary>Glättung der Messwerte in Millisekunden (0 = keine).</summary>
    public double SmoothingMs { get; set; } = 120;

    // ---- Wirkung auf die LEDs ----

    /// <summary>Farbe bei Wert 0 (BarFill) — bzw. ungenutzt.</summary>
    public RgbColor ColorLow { get; set; } = new(255, 0, 0);
    /// <summary>Optionale Zwischenfarbe bei Wert 0,5 (BarFill).</summary>
    public RgbColor? ColorMid { get; set; }
    /// <summary>Farbe bei Wert 1 bzw. Farbe des Blitzes/des Farbanteils.</summary>
    public RgbColor ColorHigh { get; set; } = new(0, 255, 0);

    /// <summary>Deckkraft der Ebene 0..1.</summary>
    public double Opacity { get; set; } = 1.0;

    [JsonConverter(typeof(JsonStringEnumConverter))]
    public LayerDisplay Display { get; set; } = LayerDisplay.Fill;

    /// <summary>BarFill mit Fill: Deckkraft folgt dem Wert (leerer Schild = unsichtbar) statt der Farbskala.</summary>
    public bool ValueAsOpacity { get; set; }

    /// <summary>Wo auf der Effekt-Fläche die Ebene wirkt (relativ, Standard ganze Fläche).</summary>
    public RelRect Target { get; set; } = RelRect.Full;

    /// <summary>BarFill: unter diesem Wert pulsiert die Ebene (0 = nie), z. B. 0,25 bei wenig Leben.</summary>
    public double PulseBelow { get; set; }

    /// <summary>Blitzdauer in Millisekunden (ColorFlash, ValueDrop).</summary>
    public int FlashMs { get; set; } = 450;

    // ---- Anzeige auf zugewiesenen Geräten (Effekt „Spiel: …“, Gerät im Effekt-Fenster zugewiesen) ----

    /// <summary>Farbstufen auf dem Gerät. Leer = Farbskala <see cref="ColorLow"/> → <see cref="ColorHigh"/>, Blinken
    /// unter <see cref="PulseBelow"/>.</summary>
    public List<GaugeStep> Steps { get; set; } = new();

    [JsonConverter(typeof(JsonStringEnumConverter))]
    public DeviceFill DeviceFill { get; set; } = DeviceFill.Value;

    /// <summary>Blinkfrequenz kritischer Stufen in Hz.</summary>
    public double BlinkHz { get; set; } = 2.5;

    /// <summary>ColorDistance: so weit (relativ zur halben Breite/Höhe des Bereichs) zählt eine Farbe noch als „nah“;
    /// weiter weg ergibt Wert 1.</summary>
    public double Range { get; set; } = 1.0;

    /// <summary>ColorDistance mit Darstellung <see cref="LayerDisplay.Sweep"/>: Auslöser, sobald der Abstand unter diesen
    /// Wert fällt (0,999 = die Farbe taucht überhaupt im Bereich auf). Erneut erst, nachdem er wieder darüber lag.</summary>
    public double TriggerBelow { get; set; } = 0.999;

    /// <summary>Darstellung als Bild des Bereichs: Die Analyse liefert dafür dessen Pixel mit (<see cref="RegionResult.Pixels"/>).</summary>
    [JsonIgnore]
    public bool IsMirror => Display == LayerDisplay.Mirror && Rule is not (RegionRule.HudCheck or RegionRule.ValueDrop);

    /// <summary>Kann die Regel ein Lauflicht auslösen?</summary>
    [JsonIgnore]
    public bool CanSweep => Rule is RegionRule.ColorFlash or RegionRule.ValueDrop or RegionRule.ColorDistance;

    /// <summary>Kann auf einem Gerät angezeigt werden (HudCheck nicht).</summary>
    [JsonIgnore]
    public bool CanShowOnDevice => Rule != RegionRule.HudCheck;

    /// <summary>Anzeige als Füllstand über die LEDs — braucht dafür mindestens <see cref="MinGaugeLeds"/> LEDs.</summary>
    [JsonIgnore]
    public bool IsGauge => (Rule is RegionRule.BarFill or RegionRule.ColorShare or RegionRule.ColorDistance) && DeviceFill != DeviceFill.All;

    public const int MinGaugeLeds = 4;

    /// <summary>Erzeugt die Ebene ein Licht? HudCheck nicht.</summary>
    [JsonIgnore]
    public bool HasLayer => Rule != RegionRule.HudCheck;

    public ScreenRegion Clone() => ScreenProfileSerializer.Clone(this);
}

/// <summary>Bereiche und Regeln für ein Spiel. Gespeichert in <c>%APPDATA%\AetherRGB\screen-profiles\*.json</c>.</summary>
public sealed class ScreenProfile
{
    public string Id { get; set; } = Guid.NewGuid().ToString("N")[..12];
    public string Name { get; set; } = "Neues Profil";

    /// <summary>Optional: Prozessname des Spiels ohne „.exe“. Läuft er im Vordergrund, wird dieses Profil automatisch benutzt.</summary>
    public string ProcessName { get; set; } = "";

    [JsonConverter(typeof(JsonStringEnumConverter))]
    public HudHiddenMode WhenHudHidden { get; set; } = HudHiddenMode.ScreenAverage;

    /// <summary>Grundfarbe unter allen Ebenen.</summary>
    public RgbColor BaseColor { get; set; } = RgbColor.Black;

    /// <summary>Ebenen von unten nach oben.</summary>
    public List<ScreenRegion> Regions { get; set; } = new();

    /// <summary>Aus einer mitgelieferten Vorlage angelegt: deren Stand. Neuere Vorlagen ergänzen beim Laden fehlende
    /// Bereiche und Farbstufen, ohne Geändertes zu überschreiben (<see cref="ScreenProfileTemplates.Upgrade"/>).</summary>
    public int TemplateVersion { get; set; }

    /// <summary>ID der Vorlage, aus der das Profil stammt (bleibt bei „Kopie“ erhalten). Fehlt in Dateien bis 0.3.11 —
    /// dann gilt ein Profil mit der ID einer Vorlage als deren Abkömmling.</summary>
    public string TemplateId { get; set; } = "";

    public ScreenProfile Clone() => ScreenProfileSerializer.Clone(this);
}

public static class ScreenProfileSerializer
{
    public static readonly JsonSerializerOptions Options = new()
    {
        WriteIndented = true,
        Converters = { new RgbColorHexConverter() },
    };

    public const int MaxRegions = 64;

    public static string Serialize(ScreenProfile profile) => JsonSerializer.Serialize(profile, Options);

    /// <summary>Liest ein Profil und klemmt alle Werte auf gültige Bereiche (Dateien können von Hand bearbeitet sein).</summary>
    public static ScreenProfile Deserialize(string json)
    {
        var profile = JsonSerializer.Deserialize<ScreenProfile>(json, Options) ?? throw new JsonException("Leeres Profil.");
        Sanitize(profile);
        return profile;
    }

    public static void Sanitize(ScreenProfile profile)
    {
        profile.Id = string.IsNullOrWhiteSpace(profile.Id) ? Guid.NewGuid().ToString("N")[..12] : SafeId(profile.Id);
        profile.Name = string.IsNullOrWhiteSpace(profile.Name) ? "Profil" : profile.Name.Trim()[..Math.Min(80, profile.Name.Trim().Length)];
        profile.ProcessName = (profile.ProcessName ?? "").Trim();
        profile.TemplateId ??= "";
        if (profile.ProcessName.EndsWith(".exe", StringComparison.OrdinalIgnoreCase)) profile.ProcessName = profile.ProcessName[..^4];
        profile.Regions ??= new();
        if (profile.Regions.Count > MaxRegions) profile.Regions = profile.Regions.Take(MaxRegions).ToList();
        profile.Regions.RemoveAll(r => r is null);
        var ids = new HashSet<string>();
        foreach (var r in profile.Regions)
        {
            if (string.IsNullOrWhiteSpace(r.Id) || !ids.Add(r.Id)) { r.Id = Guid.NewGuid().ToString("N")[..8]; ids.Add(r.Id); }
            r.Name = string.IsNullOrWhiteSpace(r.Name) ? "Bereich" : r.Name.Trim();
            r.Area = r.Area.Normalized();
            r.Target = r.Target.Normalized();
            r.Colors ??= new();
            if (r.Colors.Count > 16) r.Colors = r.Colors.Take(16).ToList();
            r.Colors.RemoveAll(c => c is null);
            r.Sources ??= new();
            r.FullAt = Math.Clamp(r.FullAt, 0.01, 1);
            r.MinShare = Math.Clamp(r.MinShare, 0, 0.9);
            r.Threshold = Math.Clamp(r.Threshold, 0.001, 2);
            r.SmoothingMs = Math.Clamp(r.SmoothingMs, 0, 5000);
            r.Opacity = Math.Clamp(r.Opacity, 0, 1);
            r.PulseBelow = Math.Clamp(r.PulseBelow, 0, 1);
            r.FlashMs = Math.Clamp(r.FlashMs, 50, 5000);
            r.Steps ??= new();
            r.Steps.RemoveAll(st => st is null);
            if (r.Steps.Count > 12) r.Steps = r.Steps.Take(12).ToList();
            r.Steps = r.Steps.Select(st => st with { UpTo = Math.Clamp(double.IsFinite(st.UpTo) ? st.UpTo : 1, 0, 1) }).ToList();
            r.BlinkHz = Math.Clamp(double.IsFinite(r.BlinkHz) ? r.BlinkHz : 2.5, 0.2, 10);
            r.Range = Math.Clamp(double.IsFinite(r.Range) ? r.Range : 1, 0.05, 1.5);
            if (!Enum.IsDefined(r.DeviceFill)) r.DeviceFill = DeviceFill.Value;
            if (!Enum.IsDefined(r.Display)) r.Display = LayerDisplay.Fill;
            r.TriggerBelow = Math.Clamp(double.IsFinite(r.TriggerBelow) ? r.TriggerBelow : 0.999, 0.01, 0.999);
        }
        foreach (var r in profile.Regions) r.Sources.RemoveAll(s => s == r.Id || !ids.Contains(s));
    }

    private static string SafeId(string id) => new string(id.Where(c => char.IsLetterOrDigit(c) || c is '-' or '_').Take(40).ToArray()) is { Length: > 0 } s ? s : Guid.NewGuid().ToString("N")[..12];

    internal static T Clone<T>(T value) => JsonSerializer.Deserialize<T>(JsonSerializer.Serialize(value, Options), Options)!;
}

/// <summary>Farben als „#RRGGBB“ in den Profil-Dateien — lesbar und von Hand änderbar.</summary>
public sealed class RgbColorHexConverter : JsonConverter<RgbColor>
{
    public override RgbColor Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options)
    {
        var text = reader.GetString() ?? throw new JsonException("Farbe fehlt.");
        try { return RgbColor.FromHex(text); }
        catch (Exception ex) when (ex is FormatException or ArgumentException or OverflowException) { throw new JsonException($"Ungültige Farbe '{text}'.", ex); }
    }

    public override void Write(Utf8JsonWriter writer, RgbColor value, JsonSerializerOptions options) => writer.WriteStringValue(value.ToHex());
}
