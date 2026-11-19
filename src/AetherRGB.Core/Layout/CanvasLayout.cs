using System.Text.Json;
using System.Text.Json.Serialization;

namespace AetherRGB.Core.Layout;

/// <summary>Platzierung eines Geräts (genauer: einer Zone eines Geräts) auf der virtuellen 2D-Fläche,
/// die die Draufsicht ins PC-Gehäuse nachbildet (SignalRGB-Stil). Alle Koordinaten sind in
/// "Canvas-Einheiten" (0..CanvasWidth / 0..CanvasHeight der zugehörigen <see cref="CanvasLayout"/>),
/// nicht in Pixeln des UI-Editors — der Editor skaliert nur zur Anzeige.</summary>
public sealed class DeviceLayoutItem
{
    public required string DeviceId { get; set; }
    public required int ZoneIndex { get; set; }
    public required int LedCount { get; set; }
    public required string DisplayName { get; set; }

    /// <summary>Position der Rechteck-Mitte.</summary>
    public double CenterX { get; set; } = 500;
    public double CenterY { get; set; } = 500;

    /// <summary>Länge der langen Seite bestimmt, über welche Strecke die LEDs verteilt werden.</summary>
    public double Width { get; set; } = 300;
    public double Height { get; set; } = 40;

    /// <summary>Rotation um die Rechteck-Mitte, im Uhrzeigersinn, in Grad.</summary>
    public double RotationDegrees { get; set; } = 0;

    /// <summary>Berechnet die Canvas-Position jeder einzelnen LED entlang der langen Rechteckseite,
    /// mittig über die kurze Seite, unter Berücksichtigung der Rotation um die Rechteck-Mitte.
    /// Bei nur einer LED wird die Mitte des Rechtecks verwendet.</summary>
    public IReadOnlyList<(double X, double Y)> ComputeLedPositions()
    {
        var positions = new (double X, double Y)[Math.Max(0, LedCount)];
        if (LedCount <= 0) return positions;

        // Die LEDs laufen immer entlang der längeren Seite — bei quadratischen Geräten entlang der Breite.
        bool alongWidth = Width >= Height;
        double length = alongWidth ? Width : Height;

        double angleRad = RotationDegrees * Math.PI / 180.0;
        double cos = Math.Cos(angleRad);
        double sin = Math.Sin(angleRad);

        for (int i = 0; i < LedCount; i++)
        {
            // Gleichmäßig verteilt, mit halbem Abstand am Rand (keine LED exakt auf der Kante).
            double t = LedCount == 1 ? 0.5 : (i + 0.5) / LedCount;
            double offsetAlong = (t - 0.5) * length;

            double localX = alongWidth ? offsetAlong : 0;
            double localY = alongWidth ? 0 : offsetAlong;

            // Rotation um die Rechteck-Mitte (Standard-2D-Rotationsmatrix).
            double rotatedX = localX * cos - localY * sin;
            double rotatedY = localX * sin + localY * cos;

            positions[i] = (CenterX + rotatedX, CenterY + rotatedY);
        }
        return positions;
    }
}

/// <summary>Die komplette virtuelle Fläche mit allen platzierten Geräten — entspricht der
/// "Draufsicht auf das PC-Gehäuse" im Layout-Editor. Wird pro Nutzer persistiert.</summary>
public sealed class CanvasLayout
{
    public double CanvasWidth { get; set; } = 1000;
    public double CanvasHeight { get; set; } = 1000;
    public List<DeviceLayoutItem> Items { get; set; } = new();

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = true,
        DefaultIgnoreCondition = JsonIgnoreCondition.Never
    };

    public static string DefaultPath =>
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "AetherRGB", "layout.json");

    public static CanvasLayout LoadOrCreateDefault(string? path = null)
    {
        path ??= DefaultPath;
        try
        {
            if (File.Exists(path))
            {
                var json = File.ReadAllText(path);
                var loaded = JsonSerializer.Deserialize<CanvasLayout>(json, JsonOptions);
                if (loaded is not null) return loaded;
            }
        }
        catch
        {
            // Defektes/unlesbares Layout -> auf leeres Default zurückfallen, nie die App zum Absturz bringen.
        }
        return new CanvasLayout();
    }

    public void Save(string? path = null)
    {
        path ??= DefaultPath;
        var dir = Path.GetDirectoryName(path)!;
        Directory.CreateDirectory(dir);
        File.WriteAllText(path, JsonSerializer.Serialize(this, JsonOptions));
    }

    /// <summary>Entfernt Einträge, für die es laut aktueller Geräteliste keine Zone mehr gibt
    /// (Gerät abgesteckt/Treiber entfernt), und ergänzt neu gefundene Zonen automatisch als
    /// unplatzierte Standard-Rechtecke nebeneinander am oberen Rand, damit nichts "verschwindet".</summary>
    public void SyncWithDevices(IEnumerable<Devices.RgbDevice> devices)
    {
        var known = devices
            .SelectMany(d => d.Zones.Select(z => (d.Id, z.Index, z.LedCount, DisplayName: $"{d.Name} — {z.Name}")))
            .ToList();

        Items.RemoveAll(item => !known.Any(k => k.Id == item.DeviceId && k.Index == item.ZoneIndex));

        double nextX = 150;
        foreach (var (id, index, ledCount, displayName) in known)
        {
            if (Items.Any(i => i.DeviceId == id && i.ZoneIndex == index)) continue;
            Items.Add(new DeviceLayoutItem
            {
                DeviceId = id,
                ZoneIndex = index,
                LedCount = ledCount,
                DisplayName = displayName,
                CenterX = nextX,
                CenterY = 120,
                Width = 260,
                Height = 36
            });
            nextX += 300;
        }
    }
}
