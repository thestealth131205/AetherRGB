using System.Text.Json.Serialization;

namespace AetherRGB.Core.Effects.Custom;

/// <summary>Wie die Zeitleiste eines eigenen Effekts abläuft, wenn ihre Dauer erreicht ist.</summary>
public enum EffectLoopMode
{
    /// <summary>Von vorn beginnen.</summary>
    Loop,
    /// <summary>Rückwärts zurück zum Anfang, dann wieder vorwärts.</summary>
    PingPong,
    /// <summary>Einmal abspielen und das letzte Bild halten.</summary>
    Once
}

/// <summary>Wie eine Ebene mit den Ebenen darunter verrechnet wird.</summary>
public enum LayerBlendMode
{
    Normal,
    /// <summary>Licht addieren (Überlagerungen werden heller).</summary>
    Add,
    /// <summary>Darunterliegendes einfärben/abdunkeln.</summary>
    Multiply,
    /// <summary>Aufhellen ohne Übersteuern.</summary>
    Screen
}

/// <summary>Verlauf zwischen zwei Keyframes.</summary>
public enum Easing
{
    Linear,
    EaseIn,
    EaseOut,
    EaseInOut,
    /// <summary>Schießt leicht über das Ziel hinaus und federt zurück.</summary>
    Spring,
    /// <summary>Wert springt erst beim nächsten Keyframe (Stufen).</summary>
    Hold
}

/// <summary>Ein eigener, im Effekt-Editor erstellter Effekt: ein Stapel von Ebenen über einer Zeitleiste.
/// Gespeichert wird auflösungsunabhängig — Positionen in 0..1 der Fläche, gemalte Bilder im Raster
/// <see cref="GridWidth"/> × <see cref="GridHeight"/> — und auf jede Flächengröße skaliert.
/// Dateiformat: JSON (<c>.aeffect</c>), siehe <see cref="CustomEffectSerializer"/>.</summary>
public sealed class CustomEffectDocument
{
    public const int CurrentFormatVersion = 1;
    public const int DefaultGridWidth = 160, DefaultGridHeight = 80;

    public int FormatVersion { get; set; } = CurrentFormatVersion;

    /// <summary>Kurz-ID ohne Präfix; im Effekt-Katalog heißt der Effekt <c>custom:&lt;Id&gt;</c>.</summary>
    public string Id { get; set; } = Guid.NewGuid().ToString("N");
    public string Name { get; set; } = "Eigener Effekt";

    /// <summary>Länge der Zeitleiste in Sekunden (bei 100 % Tempo).</summary>
    public double DurationSeconds { get; set; } = 4;
    public EffectLoopMode Loop { get; set; } = EffectLoopMode.Loop;

    /// <summary>Länge der Zeitleiste automatisch so wählen, dass sich alle Animationen, Keyframes und
    /// Daumenkinos nahtlos wiederholen (<see cref="LayerAnimator.SeamlessDuration"/>).</summary>
    public bool AutoDuration { get; set; } = true;

    public int GridWidth { get; set; } = DefaultGridWidth;
    public int GridHeight { get; set; } = DefaultGridHeight;

    /// <summary>Unterste Ebene zuerst. Unter allen Ebenen ist die Fläche schwarz (= LED aus).</summary>
    public List<EffectLayer> Layers { get; set; } = new();

    [JsonIgnore] public string CatalogId => CatalogPrefix + Id;
    public const string CatalogPrefix = "custom:";

    public CustomEffectDocument Clone() => CustomEffectSerializer.Deserialize(CustomEffectSerializer.Serialize(this));
}

/// <summary>Gemeinsame Eigenschaften aller Ebenen. Die Transformation wirkt auf den Inhalt der Ebene:
/// zuerst um <see cref="PivotX"/>/<see cref="PivotY"/> skaliert und gedreht, dann um
/// <see cref="OffsetX"/>/<see cref="OffsetY"/> verschoben (in Anteilen der Flächenbreite bzw. -höhe —
/// Offset 1 = einmal komplett über die Fläche). Gedreht wird seitenverhältnistreu im Raster.</summary>
public abstract class EffectLayer
{
    public string Name { get; set; } = "Ebene";
    public bool Visible { get; set; } = true;
    public LayerBlendMode Blend { get; set; } = LayerBlendMode.Normal;

    /// <summary>Deckkraft 0..1.</summary>
    public AnimatedValue Opacity { get; set; } = 1;

    public AnimatedValue OffsetX { get; set; } = 0;
    public AnimatedValue OffsetY { get; set; } = 0;
    public AnimatedValue Scale { get; set; } = 1;
    /// <summary>Drehung in Grad, im Uhrzeigersinn.</summary>
    public AnimatedValue Rotation { get; set; } = 0;
    /// <summary>Farbton-Verschiebung in Grad (360 = einmal durch alle Farben).</summary>
    public AnimatedValue HueShift { get; set; } = 0;

    /// <summary>Dreh- und Skalierpunkt in 0..1 der Fläche.</summary>
    public double PivotX { get; set; } = 0.5;
    public double PivotY { get; set; } = 0.5;

    /// <summary>"Umlaufend": was rechts/unten aus der Fläche herausläuft, kommt links/oben wieder herein.</summary>
    public bool WrapX { get; set; }
    public bool WrapY { get; set; }

    /// <summary>Animations-Vorlagen (Wandern, Ausdehnen, Drehen …), wirken zusätzlich zu den Keyframes.</summary>
    public List<LayerAnimation> Animations { get; set; } = new();

    /// <summary>Alle animierbaren Werte, z. B. für die Zeitleiste und die Prüfung beim Laden.</summary>
    public virtual IEnumerable<(string Name, AnimatedValue Value)> AnimatedValues()
    {
        yield return (nameof(Opacity), Opacity);
        yield return (nameof(OffsetX), OffsetX);
        yield return (nameof(OffsetY), OffsetY);
        yield return (nameof(Scale), Scale);
        yield return (nameof(Rotation), Rotation);
        yield return (nameof(HueShift), HueShift);
    }
}

/// <summary>Gemalte Ebene: ein Bild im Raster des Effekts oder ein Daumenkino aus mehreren Bildern.</summary>
public sealed class PaintLayer : EffectLayer
{
    public List<PaintFrame> Frames { get; set; } = new();

    /// <summary>Standard-Anzeigedauer eines Bildes im Daumenkino (Sekunden), wenn das Bild keine eigene hat.</summary>
    public double FrameDurationSeconds { get; set; } = 0.25;

    /// <summary>Zwischen zwei Bildern weich überblenden statt hart umschalten.</summary>
    public bool CrossFade { get; set; }
}

/// <summary>Ein Bild einer <see cref="PaintLayer"/>.</summary>
public sealed class PaintFrame
{
    /// <summary>Eigene Anzeigedauer in Sekunden, sonst <see cref="PaintLayer.FrameDurationSeconds"/>.</summary>
    public double? DurationSeconds { get; set; }

    /// <summary>Pixel als RGBA (nicht vormultipliziert), Zeile für Zeile, im Raster des Dokuments.
    /// In der Datei Deflate-komprimiert und Base64-kodiert (Feld <c>pixels</c>).</summary>
    [JsonIgnore] public byte[] Pixels { get; set; } = Array.Empty<byte>();

    [JsonPropertyName("pixels")]
    public string PixelData
    {
        get => PixelCodec.Encode(Pixels);
        set => Pixels = PixelCodec.Decode(value);
    }

    public static PaintFrame CreateEmpty(int width, int height) => new() { Pixels = new byte[width * height * 4] };
}

public enum ShapeKind
{
    /// <summary>Gefüllter Kreis mit Radius.</summary>
    Circle,
    /// <summary>Ring mit Radius (Mitte des Rings) und Dicke.</summary>
    Ring,
    /// <summary>Rechteck mit Breite/Höhe, gefüllt oder als Rahmen (<see cref="ShapeLayer.Filled"/>).</summary>
    Rectangle
}

/// <summary>Vektorform, deren Werte (Position, Größe, Dicke, Kante) einzeln animierbar sind — Grundlage
/// z. B. für den "ausdehnenden Kreis". Maße sind Anteile der Flächenhöhe (Radius 0,5 = Kreis so hoch
/// wie die Fläche), Breite eines Rechtecks Anteil der Flächenbreite.</summary>
public sealed class ShapeLayer : EffectLayer
{
    public ShapeKind Kind { get; set; } = ShapeKind.Circle;
    public string Color { get; set; } = "#FFFFFF";
    /// <summary>Nur für Rechtecke: false = nur Rahmen mit <see cref="Thickness"/>.</summary>
    public bool Filled { get; set; } = true;

    public AnimatedValue CenterX { get; set; } = 0.5;
    public AnimatedValue CenterY { get; set; } = 0.5;
    public AnimatedValue Radius { get; set; } = 0.25;
    public AnimatedValue Width { get; set; } = 0.25;
    public AnimatedValue Height { get; set; } = 0.25;
    public AnimatedValue Thickness { get; set; } = 0.05;
    /// <summary>Breite der weichen Kante (Anteil der Flächenhöhe), 0 = hart.</summary>
    public AnimatedValue Softness { get; set; } = 0.02;

    public override IEnumerable<(string Name, AnimatedValue Value)> AnimatedValues()
    {
        foreach (var v in base.AnimatedValues()) yield return v;
        yield return (nameof(CenterX), CenterX);
        yield return (nameof(CenterY), CenterY);
        yield return (nameof(Radius), Radius);
        yield return (nameof(Width), Width);
        yield return (nameof(Height), Height);
        yield return (nameof(Thickness), Thickness);
        yield return (nameof(Softness), Softness);
    }
}
