using AetherRGB.Core.Devices;
using AetherRGB.Core.Diagnostics;

namespace AetherRGB.Core.Screen;

/// <summary>Spiel-Profile für den Bildschirm-Sync als JSON-Dateien in <c>%APPDATA%\AetherRGB\screen-profiles</c>.</summary>
public sealed class ScreenProfileStore
{
    public static string DefaultDirectory =>
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "AetherRGB", "screen-profiles");

    public const string Extension = ".json";
    public const long MaxFileBytes = 1024 * 1024;

    public string Directory { get; }
    public event Action<LogLevel, string>? Log;

    private readonly object _lock = new();
    private List<ScreenProfile> _profiles = new();

    public ScreenProfileStore(string? directory = null) => Directory = directory ?? DefaultDirectory;

    /// <summary>Geladene Profile (dieselben Instanzen, die der <see cref="ScreenHub"/> bekommt — nicht verändern, sondern
    /// eine Kopie bearbeiten und <see cref="Save"/> aufrufen).</summary>
    public IReadOnlyList<ScreenProfile> Profiles { get { lock (_lock) return _profiles.ToList(); } }

    public event Action? Changed;

    /// <summary>Lädt alle Profile. Beim allerersten Start (Ordner fehlt) wird die Fortnite-Vorlage angelegt.</summary>
    public void LoadAll()
    {
        bool firstRun = !System.IO.Directory.Exists(Directory);
        if (firstRun)
        {
            System.IO.Directory.CreateDirectory(Directory);
            foreach (var template in ScreenProfileTemplates.All) WriteFile(template());
        }

        var loaded = new List<ScreenProfile>();
        foreach (var path in System.IO.Directory.EnumerateFiles(Directory, "*" + Extension).OrderBy(p => p, StringComparer.OrdinalIgnoreCase))
        {
            try
            {
                if (new FileInfo(path).Length > MaxFileBytes) throw new InvalidDataException("Datei größer als 1 MB.");
                var profile = ScreenProfileSerializer.Deserialize(File.ReadAllText(path));
                if (ScreenProfileTemplates.Upgrade(profile) is { } note)
                {
                    WriteFile(profile);
                    Log?.Invoke(LogLevel.Info, $"Bildschirm-Profil „{profile.Name}“ auf den neuen Stand der Vorlage gebracht: {note}");
                }
                if (loaded.Any(p => p.Id == profile.Id))
                {
                    Log?.Invoke(LogLevel.Warning, $"Bildschirm-Profil {Path.GetFileName(path)}: ID {profile.Id} doppelt — übersprungen.");
                    continue;
                }
                loaded.Add(profile);
            }
            catch (Exception ex)
            {
                Log?.Invoke(LogLevel.Warning, $"Bildschirm-Profil {Path.GetFileName(path)} ungültig: {ExceptionText.Describe(ex)}");
            }
        }
        lock (_lock) _profiles = loaded.OrderBy(p => p.Name, StringComparer.CurrentCultureIgnoreCase).ToList();
        Changed?.Invoke();
    }

    public void Save(ScreenProfile profile)
    {
        ScreenProfileSerializer.Sanitize(profile);
        var copy = profile.Clone();
        WriteFile(copy);
        lock (_lock)
        {
            _profiles.RemoveAll(p => p.Id == copy.Id);
            _profiles.Add(copy);
            _profiles = _profiles.OrderBy(p => p.Name, StringComparer.CurrentCultureIgnoreCase).ToList();
        }
        Changed?.Invoke();
    }

    public void Delete(string id)
    {
        var path = PathFor(id);
        if (File.Exists(path)) File.Delete(path);
        lock (_lock) _profiles.RemoveAll(p => p.Id == id);
        Changed?.Invoke();
    }

    public ScreenProfile? Get(string? id)
    {
        lock (_lock) return _profiles.FirstOrDefault(p => p.Id == id)?.Clone();
    }

    public string PathFor(string id) => Path.Combine(Directory, id + Extension);

    private void WriteFile(ScreenProfile profile)
    {
        System.IO.Directory.CreateDirectory(Directory);
        var path = PathFor(profile.Id);
        var tmp = path + ".tmp";
        File.WriteAllText(tmp, ScreenProfileSerializer.Serialize(profile));
        File.Move(tmp, path, overwrite: true);
    }
}

/// <summary>Mitgelieferte Vorlagen. Werte gemessen an einem Fortnite-Screenshot in 1920 × 1080 (Standard-HUD).</summary>
public static class ScreenProfileTemplates
{
    public const string FortniteId = "fortnite";

    public static readonly IReadOnlyList<Func<ScreenProfile>> All = new Func<ScreenProfile>[] { Fortnite };

    public static ScreenProfile? Get(string id) => All.Select(t => t()).FirstOrDefault(p => p.Id == id);

    /// <summary>Stand der mitgelieferten Vorlagen. 2 = Farbstufen für die Anzeige auf Geräten, Sturm-Abstand.
    /// 3 = Leben und Schild als Bild 1:1 über die volle Effekt-Breite, Sturm als Lauflicht von rechts nach links.</summary>
    public const int CurrentVersion = 3;

    /// <summary>Bringt ein aus einer Vorlage entstandenes Profil auf deren aktuellen Stand: fehlende Bereiche kommen
    /// dazu, Bereiche ohne Farbstufen bekommen die der Vorlage, der alte Sturm-„Farbanteil“ wird zum Sturm-Abstand.
    /// Verschobene Bereiche, eigene Farben und Schwellen bleiben, wie sie sind. Rückgabe: was geändert wurde, sonst null.</summary>
    public static string? Upgrade(ScreenProfile profile)
    {
        var templateId = profile.TemplateId.Length > 0 ? profile.TemplateId : profile.Id;
        var template = Get(templateId);
        if (template is null || profile.TemplateVersion >= template.TemplateVersion) return null;

        var changes = new List<string>();
        for (int i = 0; i < template.Regions.Count; i++)
        {
            var t = template.Regions[i];
            var existing = profile.Regions.FirstOrDefault(r => r.Id == t.Id);
            if (existing is null)
            {
                profile.Regions.Insert(Math.Min(i, profile.Regions.Count), t.Clone());
                changes.Add($"Bereich „{t.Name}“ neu");
                continue;
            }
            if (t.Rule == RegionRule.ColorDistance && existing.Rule == RegionRule.ColorShare)
            {
                existing.Rule = RegionRule.ColorDistance;
                existing.Range = t.Range;
                if (existing.Name == "Sturm (Minimap)") existing.Name = t.Name;
                changes.Add($"„{existing.Name}“ misst jetzt den Abstand zum Sturm");
            }
            // v3: Darstellung der Vorlage übernehmen, solange der Bereich noch die alte Standard-Darstellung hat.
            if (profile.TemplateVersion < 3 && t.Display is LayerDisplay.Mirror or LayerDisplay.Sweep
                && existing.Display == LayerDisplay.Fill && existing.Target == RelRect.Full && existing.Rule == t.Rule)
            {
                existing.Display = t.Display;
                existing.Opacity = t.Opacity;
                if (t.Display == LayerDisplay.Sweep)
                {
                    existing.FlashMs = t.FlashMs;
                    existing.TriggerBelow = t.TriggerBelow;
                }
                changes.Add(t.Display == LayerDisplay.Mirror ? $"„{existing.Name}“ als Bild 1:1" : $"„{existing.Name}“ als Lauflicht");
            }
            if (existing.Steps.Count == 0 && t.Steps.Count > 0)
            {
                existing.Steps = t.Steps.ToList();
                existing.DeviceFill = t.DeviceFill;
                existing.BlinkHz = t.BlinkHz;
                changes.Add($"Farbstufen für „{existing.Name}“");
            }
        }
        profile.TemplateId = template.TemplateId;
        profile.TemplateVersion = template.TemplateVersion;
        return changes.Count == 0 ? "keine Änderung an den Bereichen." : string.Join(", ", changes) + ".";
    }

    // Pixelkoordinaten aus dem Screenshot (1920 × 1080) → relativ.
    private static RelRect Px(double x0, double y0, double x1, double y1) => RelRect.FromPixels(x0, y0, x1, y1, 1920, 1080);

    private static readonly ColorMatcher HealthGreen = new() { Hue = 110, HueTolerance = 32, MinSaturation = 0.45, MinValue = 0.45 };
    private static readonly ColorMatcher ShieldBlue = new() { Hue = 200, HueTolerance = 15, MinSaturation = 0.7, MinValue = 0.5 };
    private static readonly ColorMatcher BarEmpty = new() { Hue = 354, HueTolerance = 15, MinSaturation = 0.4, MinValue = 0.12, MaxValue = 0.35 };
    private static readonly ColorMatcher StormPurple = new() { Hue = 305, HueTolerance = 18, MinSaturation = 0.45, MinValue = 0.35 };

    public static ScreenProfile Fortnite() => new()
    {
        Id = FortniteId,
        TemplateId = FortniteId,
        TemplateVersion = CurrentVersion,
        Name = "Fortnite (Vorlage)",
        ProcessName = "FortniteClient-Win64-Shipping",
        WhenHudHidden = HudHiddenMode.ScreenAverage,
        BaseColor = RgbColor.Black,
        Regions =
        {
            new ScreenRegion
            {
                Id = "hud", Name = "HUD sichtbar (Lebens-/Schildbalken)", Rule = RegionRule.HudCheck,
                Area = Px(57, 980, 280, 1023), Colors = { HealthGreen, ShieldBlue, BarEmpty }, Threshold = 0.35,
            },
            new ScreenRegion
            {
                Id = "health", Name = "Leben", Rule = RegionRule.BarFill, Area = Px(57, 1000, 280, 1021),
                Colors = { HealthGreen }, SmoothingMs = 150,
                ColorLow = new RgbColor(255, 0, 0), ColorMid = new RgbColor(255, 170, 0), ColorHigh = new RgbColor(0, 255, 40),
                PulseBelow = 0.25,
                // Auf der Effekt-Fläche: der Balken aus dem Spiel 1:1 über die volle Breite (unter dem Schild-Band).
                Display = LayerDisplay.Mirror,
                // Auf einem Gerät: Füllstand über die LEDs, Farbe nach Stufe.
                Steps =
                {
                    new GaugeStep(1.00, new RgbColor(0, 255, 40)),
                    new GaugeStep(0.50, new RgbColor(255, 210, 0)),
                    new GaugeStep(0.30, new RgbColor(255, 100, 0)),
                    new GaugeStep(0.15, new RgbColor(255, 0, 0), blink: true),
                },
            },
            new ScreenRegion
            {
                Id = "shield", Name = "Schild", Rule = RegionRule.BarFill, Area = Px(57, 982, 280, 992),
                Colors = { ShieldBlue }, SmoothingMs = 150,
                ColorLow = RgbColor.Black, ColorHigh = new RgbColor(0, 140, 255), ValueAsOpacity = true, Opacity = 1,
                Display = LayerDisplay.Mirror,
                Steps =
                {
                    new GaugeStep(1.00, new RgbColor(0, 120, 255)),
                    new GaugeStep(0.50, new RgbColor(0, 190, 255)),
                    new GaugeStep(0.30, new RgbColor(0, 255, 230)),
                },
            },
            new ScreenRegion
            {
                // Die Spielfigur steht in der Fortnite-Minimap immer in der Mitte: Abstand Mitte → nächste Sturmfläche bzw.
                // pink-rote Sturmgrenze. 1 = kein Sturm auf der Minimap, 0 = mitten im Sturm.
                Id = "storm", Name = "Sturm-Abstand (Minimap)", Rule = RegionRule.ColorDistance, Area = Px(1733, 24, 1895, 186),
                Colors = { StormPurple }, FullAt = 0.5, MinShare = 0.03, SmoothingMs = 400, Range = 1.0,
                ColorHigh = new RgbColor(176, 38, 255), Opacity = 1,
                // Auf der Effekt-Fläche: sobald der Sturm auf der Minimap auftaucht, läuft einmal ein Licht von rechts nach links.
                Display = LayerDisplay.Sweep, TriggerBelow = 0.999, FlashMs = 900,
                DeviceFill = DeviceFill.Inverse, BlinkHz = 3,
                Steps =
                {
                    new GaugeStep(1.00, new RgbColor(110, 20, 255)),
                    new GaugeStep(0.50, new RgbColor(176, 38, 255)),
                    new GaugeStep(0.25, new RgbColor(255, 0, 200), blink: true),
                },
            },
            new ScreenRegion
            {
                Id = "damage", Name = "Schaden (Leben/Schild fällt)", Rule = RegionRule.ValueDrop,
                Area = Px(57, 980, 280, 1023), Sources = { "health", "shield" }, Threshold = 0.025,
                ColorHigh = new RgbColor(255, 0, 0), FlashMs = 400,
            },
        },
    };
}
