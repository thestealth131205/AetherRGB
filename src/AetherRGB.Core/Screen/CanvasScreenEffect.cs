using System.Diagnostics;
using AetherRGB.Core.Devices;
using AetherRGB.Core.Effects;

namespace AetherRGB.Core.Screen;

/// <summary>Effekte aus der Bildschirmaufnahme (<see cref="ScreenHub"/>). Nur das fertige Bild wird gelesen — kein Code im
/// Spiel, kein Speicherzugriff. Ohne laufende Aufnahme bleiben die LEDs dunkel.</summary>
public sealed class CanvasScreenEffect : ICanvasEffect
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
            if (!region.Enabled || !region.HasLayer || !region.Target.Contains(u, v)) continue;
            if (!analysis.Results.TryGetValue(region.Id, out var r)) continue;
            var (layer, alpha) = Layer(region, r, u, elapsed, now);
            if (alpha > 0) color = RgbColor.Lerp(color, layer, alpha * region.Opacity);
        }
        return color;
    }

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
            {
                if (r.LastTrigger == 0) return (RgbColor.Black, 0);
                double ms = (now - r.LastTrigger) * 1000.0 / Stopwatch.Frequency;
                if (ms < 0 || ms >= region.FlashMs) return (RgbColor.Black, 0);
                double t = 1 - ms / region.FlashMs;
                return (region.ColorHigh, t * t);
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
