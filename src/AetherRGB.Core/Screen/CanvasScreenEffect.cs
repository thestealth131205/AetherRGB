using System.Diagnostics;
using AetherRGB.Core.Devices;
using AetherRGB.Core.Effects;

namespace AetherRGB.Core.Screen;

/// <summary>Effekte aus der Bildschirmaufnahme (<see cref="ScreenHub"/>). Nur das fertige Bild wird gelesen — kein Code im
/// Spiel, kein Speicherzugriff. Ohne laufende Aufnahme bleiben die LEDs dunkel.</summary>
public sealed class CanvasScreenEffect : IDeviceAwareCanvasEffect
{
    public enum Mode
    {
        /// <summary>Mischfarbe des ganzen Monitors für alle LEDs.</summary>
        Average,
        /// <summary>Das Bild auf die Effekt-Fläche gelegt (Ambilight) — jede LED zeigt die Farbe an ihrer Stelle.</summary>
        Picture,
        /// <summary>Spiel-Profil: Bereiche und Regeln (Leben, Schild, Sturm, Schaden …).</summary>
        Profile,
    }

    private readonly ScreenHub _hub;
    private readonly Mode _mode;

    public CanvasScreenEffect(Mode mode, ScreenHub? hub = null)
    {
        _mode = mode;
        _hub = hub ?? ScreenHub.Shared;
    }

    public string Name => _mode switch
    {
        Mode.Average => "Bildschirm – Mischfarbe",
        Mode.Picture => "Bildschirm – Bild (Ambilight)",
        _ => "Bildschirm – Spiel-Profil",
    };

    public RgbColor Sample(double x, double y, double canvasWidth, double canvasHeight, TimeSpan elapsed)
    {
        _hub.Demand();
        var analysis = _hub.Latest;
        if (analysis is null || !_hub.IsLive) return RgbColor.Black;
        double u = canvasWidth <= 0 ? 0 : x / canvasWidth, v = canvasHeight <= 0 ? 0 : y / canvasHeight;
        return _mode switch
        {
            Mode.Average => analysis.Average,
            Mode.Picture => SampleThumbnail(analysis, u, v),
            _ => SampleProfile(analysis, u, v, elapsed, Stopwatch.GetTimestamp()),
        };
    }

    /// <summary>Spiel-Profil: Geräte mit zugewiesenen Bereichen zeigen deren Wert (<see cref="ScreenGauge"/>).</summary>
    public void ApplyToDevice(Layout.DeviceLayoutItem item, RgbColor[] colors, TimeSpan elapsed)
    {
        if (_mode != Mode.Profile) return;
        ScreenGauge.Apply(_hub.IsLive ? _hub.Latest : null, item.ScreenRegions, colors, elapsed, Stopwatch.GetTimestamp());
    }

    private static RgbColor SampleThumbnail(ScreenAnalysis a, double u, double v)
    {
        double fx = Math.Clamp(u * a.ThumbnailCols - 0.5, 0, a.ThumbnailCols - 1);
        double fy = Math.Clamp(v * a.ThumbnailRows - 0.5, 0, a.ThumbnailRows - 1);
        int x0 = (int)fx, y0 = (int)fy;
        int x1 = Math.Min(x0 + 1, a.ThumbnailCols - 1), y1 = Math.Min(y0 + 1, a.ThumbnailRows - 1);
        double tx = fx - x0, ty = fy - y0;
        var top = RgbColor.Lerp(a.Thumbnail[y0 * a.ThumbnailCols + x0], a.Thumbnail[y0 * a.ThumbnailCols + x1], tx);
        var bottom = RgbColor.Lerp(a.Thumbnail[y1 * a.ThumbnailCols + x0], a.Thumbnail[y1 * a.ThumbnailCols + x1], tx);
        return RgbColor.Lerp(top, bottom, ty);
    }

    /// <summary>Farbe an einer Stelle der Fläche nach dem Profil: Grundfarbe, darüber die Ebenen in Profil-Reihenfolge.
    /// Öffentlich für die Vorschau im Bereichs-Editor.</summary>
    public static RgbColor SampleProfile(ScreenAnalysis analysis, double u, double v, TimeSpan elapsed, long now)
    {
        var profile = analysis.Profile;
        if (profile is null) return analysis.Average;
        if (!analysis.HudVisible)
        {
            if (profile.WhenHudHidden == HudHiddenMode.ScreenAverage) return analysis.Average;
            if (profile.WhenHudHidden == HudHiddenMode.Off) return RgbColor.Black;
        }

        var color = profile.BaseColor;
        foreach (var region in profile.Regions)
        {
            if (!region.Enabled || !region.HasLayer) continue;
            var target = TargetOf(profile, region);
            if (!target.Contains(u, v)) continue;
            if (!analysis.Results.TryGetValue(region.Id, out var r)) continue;
            double lu = target.W <= 0 ? 0 : (u - target.X) / target.W, lv = target.H <= 0 ? 0 : (v - target.Y) / target.H;
            var (layer, alpha) = region.IsMirror ? (SamplePixels(r, lu, lv), r.Pixels is null ? 0 : 1)
                : region.Display == LayerDisplay.Sweep && region.CanSweep ? (SweepColor(region, r), SweepLevel(region, r, lu, now))
                : Layer(region, r, u, elapsed, now);
            if (alpha > 0) color = RgbColor.Lerp(color, layer, alpha * region.Opacity);
        }
        return color;
    }

    /// <summary>Zielfläche eines Bereichs. Als Bild dargestellte Bereiche mit Zielfläche „ganze Fläche“ teilen sich die
    /// Fläche: jeder bekommt die volle Breite und ein gleich hohes Band, sortiert nach ihrer Lage auf dem Monitor
    /// (oben liegender Schildbalken oben, Lebensbalken darunter).</summary>
    public static RelRect TargetOf(ScreenProfile profile, ScreenRegion region)
    {
        if (!region.IsMirror || region.Target != RelRect.Full) return region.Target;
        int index = 0, count = 0;
        foreach (var other in profile.Regions)
        {
            if (!other.Enabled || !other.IsMirror || other.Target != RelRect.Full) continue;
            count++;
            if (other != region && Before(other, region)) index++;
        }
        if (count <= 1) return RelRect.Full;
        return new RelRect(0, (double)index / count, 1, 1.0 / count);
    }

    private static bool Before(ScreenRegion a, ScreenRegion b) =>
        a.Area.Y < b.Area.Y - 1e-9 || (Math.Abs(a.Area.Y - b.Area.Y) <= 1e-9 && a.Area.X < b.Area.X);

    /// <summary>Bild des Bereichs an der Stelle (lu, lv) der Zielfläche, weich zwischen den Zellen überblendet.</summary>
    public static RgbColor SamplePixels(RegionResult r, double lu, double lv)
    {
        if (r.Pixels is not { } px || r.PixelCols <= 0 || r.PixelRows <= 0) return RgbColor.Black;
        int cols = r.PixelCols, rows = r.PixelRows;
        double fx = Math.Clamp(lu * cols - 0.5, 0, cols - 1), fy = Math.Clamp(lv * rows - 0.5, 0, rows - 1);
        int x0 = (int)fx, y0 = (int)fy, x1 = Math.Min(x0 + 1, cols - 1), y1 = Math.Min(y0 + 1, rows - 1);
        var top = RgbColor.Lerp(px[y0 * cols + x0], px[y0 * cols + x1], fx - x0);
        var bottom = RgbColor.Lerp(px[y1 * cols + x0], px[y1 * cols + x1], fx - x0);
        return RgbColor.Lerp(top, bottom, fy - y0);
    }

    /// <summary>Breite des hellen Kopfes und Länge des Nachleuchtens des Lauflichts (Anteil der Zielfläche).</summary>
    private const double SweepHead = 0.08, SweepTail = 0.55;

    /// <summary>Lauflicht von rechts nach links: Der Kopf wandert in <see cref="ScreenRegion.FlashMs"/> über die
    /// Zielfläche (lu 1 → 0) und zieht ein abklingendes Nachleuchten hinter sich her.</summary>
    public static double SweepLevel(ScreenRegion region, RegionResult r, double lu, long now)
    {
        if (r.LastTrigger == 0) return 0;
        double ms = (now - r.LastTrigger) * 1000.0 / Stopwatch.Frequency;
        if (ms < 0 || ms >= region.FlashMs) return 0;
        double t = ms / region.FlashMs;
        // Kopf startet rechts außerhalb und verlässt die Fläche links, damit Anfang und Ende ganz zu sehen sind.
        double head = 1 + SweepHead - t * (1 + SweepHead + SweepTail);
        double d = lu - head;
        double level = d < 0 ? 1 - -d / SweepHead : 1 - d / SweepTail;
        return Math.Clamp(level, 0, 1);
    }

    /// <summary>Farbe des Lauflichts: bei Abstand zur Farbe die Farbstufe zum aktuellen Wert, sonst die Blitzfarbe.</summary>
    private static RgbColor SweepColor(ScreenRegion region, RegionResult r) =>
        region.Rule == RegionRule.ColorDistance ? ScreenGauge.StepFor(region, r.Value).Color : region.ColorHigh;

    private static (RgbColor Color, double Alpha) Layer(ScreenRegion region, RegionResult r, double u, TimeSpan elapsed, long now)
    {
        switch (region.Rule)
        {
            case RegionRule.Average:
                return (r.Average, 1);

            case RegionRule.ColorShare:
                return Display(region, r.Value, u, region.ColorHigh, r.Value);

            case RegionRule.BarFill:
            {
                double alpha = 1;
                if (region.PulseBelow > 0 && r.Value < region.PulseBelow && r.Value > 0.005)
                    alpha = 0.3 + 0.7 * (0.5 + 0.5 * Math.Cos(elapsed.TotalSeconds * 2 * Math.PI * 1.5));
                return Display(region, r.Value, u, Gradient(region, r.Value), region.ValueAsOpacity ? alpha * r.Value : alpha);
            }

            case RegionRule.ColorFlash:
            case RegionRule.ValueDrop:
                return (region.ColorHigh, ScreenGauge.FlashLevel(region, r, now));

            case RegionRule.ColorDistance:
            {
                // Je näher die Farbe (Sturm), desto kräftiger; Farbe und Blinken aus den Stufen.
                if (r.Value >= 0.999) return (RgbColor.Black, 0);
                var step = ScreenGauge.StepFor(region, r.Value);
                if (step.Blink && !ScreenGauge.BlinkOn(region, elapsed)) return (RgbColor.Black, 0);
                return Display(region, 1 - r.Value, u, step.Color, 1 - r.Value);
            }

            default:
                return (RgbColor.Black, 0);
        }
    }

    /// <summary>Fill: ganzer Zielbereich. Bar: Zielbereich bis zum Wert in der Farbe, der Rest dunkel.</summary>
    private static (RgbColor, double) Display(ScreenRegion region, double value, double u, RgbColor color, double fillAlpha)
    {
        if (region.Display == LayerDisplay.Fill) return (color, fillAlpha);
        double local = region.Target.W <= 0 ? 0 : (u - region.Target.X) / region.Target.W;
        return local <= value ? (color, region.Rule == RegionRule.ColorShare ? 1 : fillAlpha) : (RgbColor.Black, 0.85);
    }

    /// <summary>Farbe zum Wert: Low → (Mid →) High.</summary>
    public static RgbColor Gradient(ScreenRegion region, double value)
    {
        value = Math.Clamp(value, 0, 1);
        if (region.ColorMid is not { } mid) return RgbColor.Lerp(region.ColorLow, region.ColorHigh, value);
        return value < 0.5 ? RgbColor.Lerp(region.ColorLow, mid, value * 2) : RgbColor.Lerp(mid, region.ColorHigh, (value - 0.5) * 2);
    }
}
