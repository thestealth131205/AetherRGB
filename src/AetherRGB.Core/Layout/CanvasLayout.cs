using System.Text.Json;

namespace AetherRGB.Core.Layout;

/// <summary>Platzierung eines Geräts (genauer: einer Zone eines Geräts) auf der Fläche eines
/// <see cref="EffectScene"/> (SignalRGB-Stil). Alle Koordinaten sind in "Canvas-Einheiten"
/// (0..CanvasWidth / 0..CanvasHeight der Szene), nicht in Pixeln des UI-Editors — der Editor
/// skaliert nur zur Anzeige.</summary>
public sealed class DeviceLayoutItem
{
    public required string DeviceId { get; set; }
    public required int ZoneIndex { get; set; }

    /// <summary>Vom Gerät/Provider gemeldete LED-Anzahl der Zone (nicht die der Form — siehe <see cref="LedShape"/>).</summary>
    public required int LedCount { get; set; }
    public required string DisplayName { get; set; }

    /// <summary>Position der Rechteck-Mitte.</summary>
    public double CenterX { get; set; } = 500;
    public double CenterY { get; set; } = 500;

    /// <summary>Größe des Rechtecks, in das die <see cref="LedShape"/> des Geräts eingepasst wird.</summary>
    public double Width { get; set; } = 300;
    public double Height { get; set; } = 40;

    /// <summary>Rotation um die Rechteck-Mitte, im Uhrzeigersinn, in Grad.</summary>
    public double RotationDegrees { get; set; } = 0;

    /// <summary>Geräte-Helligkeit 0..100 %, zusätzlich zur Helligkeit der Szene.</summary>
    public int Brightness { get; set; } = 100;

    /// <summary>Spiel-Anzeige (Effekte „Spiel: …“ und „Bildschirm – Spiel-Profil“): IDs der Bereiche des Profils, die
    /// dieses Gerät zeigt. Der erste ist die Hauptanzeige (leer = Effekt-Fläche wie bei jedem anderen Effekt), die
    /// übrigen überdecken sie nur, solange sie kritisch sind (blinkende Stufe, Blitz). Leere Liste = nur Effekt-Fläche.</summary>
    public List<string> ScreenRegions { get => _screenRegions; set => _screenRegions = value ?? new(); }
    private List<string> _screenRegions = new();

    /// <summary>Canvas-Position jeder LED: die normierten Positionen der Form werden ins Rechteck
    /// eingepasst und um die Rechteck-Mitte gedreht. Reihenfolge = LED-Reihenfolge am Gerät.</summary>
    public IReadOnlyList<(double X, double Y)> ComputeLedPositions(LedShape shape)
    {
        var normalized = shape.ComputeNormalizedPositions();
        var positions = new (double X, double Y)[normalized.Count];

        double angleRad = RotationDegrees * Math.PI / 180.0;
        double cos = Math.Cos(angleRad);
        double sin = Math.Sin(angleRad);

        for (int i = 0; i < normalized.Count; i++)
        {
            double localX = (normalized[i].U - 0.5) * Width;
            double localY = (normalized[i].V - 0.5) * Height;

            // Rotation um die Rechteck-Mitte (Standard-2D-Rotationsmatrix).
            positions[i] = (CenterX + localX * cos - localY * sin, CenterY + localX * sin + localY * cos);
        }
        return positions;
    }

    public DeviceLayoutItem Clone() => new()
    {
        DeviceId = DeviceId,
        ZoneIndex = ZoneIndex,
        LedCount = LedCount,
        DisplayName = DisplayName,
        CenterX = CenterX,
        CenterY = CenterY,
        Width = Width,
        Height = Height,
        RotationDegrees = RotationDegrees,
        Brightness = Brightness,
        ScreenRegions = ScreenRegions.ToList()
    };
}

/// <summary>Altes Ein-Flächen-Layout (bis v0.2, <c>layout.json</c>). Wird nur noch gelesen, um es beim
/// ersten Start in das erste Effekt-Fenster (<see cref="EffectScene"/>) zu übernehmen.</summary>
public sealed class CanvasLayout
{
    public double CanvasWidth { get; set; } = 1000;
    public double CanvasHeight { get; set; } = 1000;
    public List<DeviceLayoutItem> Items { get; set; } = new();

    public static string DefaultPath =>
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "AetherRGB", "layout.json");

    public static CanvasLayout? TryLoadLegacy(string? path = null)
    {
        path ??= DefaultPath;
        try
        {
            if (!File.Exists(path)) return null;
            return JsonSerializer.Deserialize<CanvasLayout>(File.ReadAllText(path));
        }
        catch
        {
            return null;
        }
    }
}
