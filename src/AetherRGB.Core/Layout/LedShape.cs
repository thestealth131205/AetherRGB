using System.Text.Json;
using System.Text.Json.Serialization;

namespace AetherRGB.Core.Layout;

public enum LedShapeKind
{
    /// <summary>Gerade Schnur/Streifen — LEDs gleichmäßig entlang der Breite.</summary>
    Line,
    /// <summary>Ring (z. B. Lüfter) — LEDs gleichmäßig auf einer Ellipse, Start oben, im Uhrzeigersinn.</summary>
    Circle,
    /// <summary>Rahmen (z. B. Monitor-Backlight) — LEDs gleichmäßig auf dem Rechteckumfang, Start oben links.</summary>
    Square,
    /// <summary>Frei auf der 100x100-Pixelmatrix gezeichnet — Reihenfolge der Klicks = LED-Reihenfolge.</summary>
    Custom
}

/// <summary>Ein Punkt auf der 100x100-Zeichenmatrix des Form-Editors.</summary>
public readonly record struct LedPoint(int X, int Y);

/// <summary>Die physische Anordnung der LEDs eines Geräts (einer Zone). Gespeichert wird nur die Form
/// und die Anzahl — nie eine absolute Position: wo das Gerät auf einer Effekt-Fläche liegt und wie groß
/// es dort ist, bestimmt allein der jeweilige <see cref="DeviceLayoutItem"/>. Die Form liefert nur
/// normierte Positionen (0..1) innerhalb dieses Rechtecks.</summary>
public sealed class LedShape
{
    public const int MatrixSize = 100;

    public LedShapeKind Kind { get; set; } = LedShapeKind.Line;

    /// <summary>Anzahl der LEDs. Bei <see cref="LedShapeKind.Custom"/> immer gleich <c>Points.Count</c>.</summary>
    public int LedCount { get; set; }

    /// <summary>Nur bei <see cref="LedShapeKind.Custom"/>: Matrixpunkte in LED-Reihenfolge (Index 0 = LED 1),
    /// bereits auf die linke obere Ecke der Bounding-Box verschoben (Position auf der Matrix ist egal).</summary>
    public List<LedPoint> Points { get; set; } = new();

    public static LedShape DefaultFor(int ledCount) => new() { Kind = LedShapeKind.Line, LedCount = Math.Max(1, ledCount) };

    /// <summary>Übernimmt frei gezeichnete Matrixpunkte; verschiebt sie so, dass die Bounding-Box bei (0,0)
    /// beginnt — dadurch ist egal, wo auf der Matrix gezeichnet wurde.</summary>
    public static LedShape FromPoints(IReadOnlyList<LedPoint> points)
    {
        if (points.Count == 0) return DefaultFor(1);
        int minX = points.Min(p => p.X), minY = points.Min(p => p.Y);
        return new LedShape
        {
            Kind = LedShapeKind.Custom,
            LedCount = points.Count,
            Points = points.Select(p => new LedPoint(p.X - minX, p.Y - minY)).ToList()
        };
    }

    /// <summary>Seitenverhältnis (Breite/Höhe) der Form — für eine sinnvolle Startgröße beim Platzieren.</summary>
    [JsonIgnore]
    public double AspectRatio => Kind switch
    {
        LedShapeKind.Line => Math.Max(1, LedCount) * 1.2,
        LedShapeKind.Custom when Points.Count > 0 =>
            (Points.Max(p => p.X) + 1.0) / (Points.Max(p => p.Y) + 1.0),
        _ => 1.0
    };

    /// <summary>Normierte LED-Positionen (0..1 in beiden Achsen) in LED-Reihenfolge.</summary>
    public IReadOnlyList<(double U, double V)> ComputeNormalizedPositions()
    {
        int count = Kind == LedShapeKind.Custom ? Points.Count : Math.Max(0, LedCount);
        var result = new (double U, double V)[count];
        if (count == 0) return result;

        switch (Kind)
        {
            case LedShapeKind.Line:
                for (int i = 0; i < count; i++)
                    result[i] = ((i + 0.5) / count, 0.5);
                break;

            case LedShapeKind.Circle:
                for (int i = 0; i < count; i++)
                {
                    // Start oben (12 Uhr), im Uhrzeigersinn — wie die meisten Lüfterringe verdrahtet sind.
                    double angle = -Math.PI / 2 + 2 * Math.PI * i / count;
                    result[i] = (0.5 + 0.5 * Math.Cos(angle), 0.5 + 0.5 * Math.Sin(angle));
                }
                break;

            case LedShapeKind.Square:
                for (int i = 0; i < count; i++)
                {
                    // Umfang in 4 gleich lange Kanten (normiertes Quadrat), Start oben links, im Uhrzeigersinn.
                    double t = 4.0 * i / count;
                    int edge = (int)t;
                    double f = t - edge;
                    result[i] = edge switch
                    {
                        0 => (f, 0.0),
                        1 => (1.0, f),
                        2 => (1.0 - f, 1.0),
                        _ => (0.0, 1.0 - f)
                    };
                }
                break;

            case LedShapeKind.Custom:
            {
                int maxX = Points.Max(p => p.X), maxY = Points.Max(p => p.Y);
                for (int i = 0; i < count; i++)
                {
                    var p = Points[i];
                    // Zellmitte statt Zellecke, damit eine einzelne Zeile/Spalte mittig im Rechteck liegt.
                    result[i] = ((p.X + 0.5) / (maxX + 1.0), (p.Y + 0.5) / (maxY + 1.0));
                }
                break;
            }
        }
        return result;
    }

    /// <summary>Punkte der Form auf der 100x100-Matrix, zur Vorschau im Editor (bei Vorlagen gerundet,
    /// mehrere LEDs können sich dann eine Zelle teilen).</summary>
    public IReadOnlyList<LedPoint> ToMatrixPreview(int margin = 10)
    {
        if (Kind == LedShapeKind.Custom) return Points.Select(p => new LedPoint(p.X + margin, p.Y + margin)).ToList();
        double span = MatrixSize - 1 - 2 * margin;
        return ComputeNormalizedPositions()
            .Select(p => new LedPoint((int)Math.Round(margin + p.U * span), (int)Math.Round(margin + p.V * span)))
            .ToList();
    }
}

/// <summary>Alle vom Nutzer festgelegten LED-Formen, pro Gerät+Zone — gilt für jedes Effekt-Fenster,
/// in dem das Gerät platziert ist. Persistiert in <c>%APPDATA%\AetherRGB\shapes.json</c>.</summary>
public sealed class LedShapeStore
{
    public Dictionary<string, LedShape> Shapes { get; set; } = new();

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = true,
        Converters = { new JsonStringEnumConverter() }
    };

    public static string DefaultPath =>
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "AetherRGB", "shapes.json");

    public static string KeyFor(string deviceId, int zoneIndex) => $"{deviceId}#{zoneIndex}";

    /// <summary>Gespeicherte Form oder — falls nie festgelegt — eine gerade Schnur mit der vom Gerät gemeldeten LED-Anzahl.</summary>
    public LedShape Get(string deviceId, int zoneIndex, int reportedLedCount) =>
        Shapes.TryGetValue(KeyFor(deviceId, zoneIndex), out var shape) ? shape : LedShape.DefaultFor(reportedLedCount);

    public bool HasCustom(string deviceId, int zoneIndex) => Shapes.ContainsKey(KeyFor(deviceId, zoneIndex));

    public void Set(string deviceId, int zoneIndex, LedShape shape) => Shapes[KeyFor(deviceId, zoneIndex)] = shape;

    public static LedShapeStore LoadOrCreateDefault(string? path = null)
    {
        path ??= DefaultPath;
        try
        {
            if (File.Exists(path))
            {
                var loaded = JsonSerializer.Deserialize<LedShapeStore>(File.ReadAllText(path), JsonOptions);
                if (loaded is not null) return loaded;
            }
        }
        catch
        {
            // Defekte Datei -> alle Geräte fallen auf die Standard-Schnur zurück, App läuft weiter.
        }
        return new LedShapeStore();
    }

    public void Save(string? path = null)
    {
        path ??= DefaultPath;
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllText(path, JsonSerializer.Serialize(this, JsonOptions));
    }
}
