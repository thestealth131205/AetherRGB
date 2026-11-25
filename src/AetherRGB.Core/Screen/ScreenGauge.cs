using System.Diagnostics;
using AetherRGB.Core.Devices;

namespace AetherRGB.Core.Screen;

/// <summary>Zeigt Bereiche eines Spiel-Profils direkt auf einem Gerät statt auf der Effekt-Fläche: der Lebensbalken
/// als Füllstand über die LEDs eines Strips (4 von 4 LEDs grün = volles Leben, darunter gelb, orange, rot blinkend),
/// der Sturm-Abstand als wachsender Balken, Schaden als Blitz über alle LEDs.
///
/// Einem Gerät können mehrere Bereiche zugewiesen sein (<see cref="Layout.DeviceLayoutItem.ScreenRegions"/>): der
/// erste ist die Hauptanzeige, die übrigen überdecken sie nur, solange sie kritisch sind — so zeigt ein einziger Strip
/// das Leben und blinkt nur dann lila, wenn der Sturm ganz nah ist. Wer getrennte Geräte zuweist, braucht das nicht.</summary>
public static class ScreenGauge
{
    /// <summary>Anteil einer Blinkperiode, in dem die LEDs an sind.</summary>
    public const double BlinkDuty = 0.55;

    /// <summary>Wert, der über die LEDs angezeigt wird, und die daraus folgende Füllung (0..1).</summary>
    public static double FillFor(ScreenRegion region, double value, int ledCount)
    {
        if (!region.IsGauge || ledCount < ScreenRegion.MinGaugeLeds) return 1;
        value = Math.Clamp(value, 0, 1);
        return region.DeviceFill == DeviceFill.Inverse ? 1 - value : value;
    }

    /// <summary>Farbstufe zum Wert: von allen Stufen mit <c>UpTo ≥ Wert</c> die mit dem kleinsten <c>UpTo</c>.
    /// Ohne Stufen die Farbskala des Bereichs, Blinken unter <see cref="ScreenRegion.PulseBelow"/>.</summary>
    public static GaugeStep StepFor(ScreenRegion region, double value)
    {
        value = Math.Clamp(value, 0, 1);
        if (region.Steps.Count == 0)
        {
            var color = region.Rule == RegionRule.BarFill ? CanvasScreenEffect.Gradient(region, value) : region.ColorHigh;
            return new GaugeStep(1, color, region.PulseBelow > 0 && value < region.PulseBelow && value > 0.005);
        }
        GaugeStep? best = null;
        foreach (var step in region.Steps)
            if (step.UpTo >= value - 1e-9 && (best is null || step.UpTo < best.UpTo)) best = step;
        // Wert über allen Stufen (z. B. nur Stufen bis 50 % angelegt): die höchste Stufe gilt.
        return best ?? region.Steps.MaxBy(s => s.UpTo)!;
    }

    /// <summary>Ist der Bereich gerade kritisch? Füllstände: Stufe blinkt (und es leuchtet überhaupt etwas). Blitze: der
    /// Blitz läuft gerade. Nur kritische Bereiche überdecken die Hauptanzeige eines Geräts.</summary>
    public static bool IsCritical(ScreenRegion region, RegionResult result, long now)
    {
        switch (region.Rule)
        {
            case RegionRule.ColorFlash:
            case RegionRule.ValueDrop:
                return FlashLevel(region, result, now) > 0;
            case RegionRule.BarFill:
            case RegionRule.ColorShare:
            case RegionRule.ColorDistance:
                if (FillFor(region, result.Value, int.MaxValue) <= 0.005)
                    return false; // nichts zu zeigen (kein Sturm in Reichweite, leerer Balken)
                return StepFor(region, result.Value).Blink;
            default:
                return false;
        }
    }

    /// <summary>Helligkeit eines laufenden Blitzes 1 → 0, 0 = kein Blitz.</summary>
    public static double FlashLevel(ScreenRegion region, RegionResult result, long now)
    {
        if (result.LastTrigger == 0) return 0;
        double ms = (now - result.LastTrigger) * 1000.0 / Stopwatch.Frequency;
        if (ms < 0 || ms >= region.FlashMs) return 0;
        double t = 1 - ms / region.FlashMs;
        return t * t;
    }

    /// <summary>An oder aus im Blinktakt.</summary>
    public static bool BlinkOn(ScreenRegion region, TimeSpan elapsed)
    {
        double phase = elapsed.TotalSeconds * Math.Clamp(region.BlinkHz, 0.2, 10) % 1.0;
        return phase < BlinkDuty;
    }

    /// <summary>Farben eines Bereichs über <paramref name="colors"/>.Length LEDs. Blitze blenden über den bisherigen
    /// Inhalt von <paramref name="colors"/>, alle anderen Regeln überschreiben ihn.</summary>
    public static void RenderRegion(ScreenRegion region, RegionResult result, RgbColor[] colors, TimeSpan elapsed, long now)
    {
        int n = colors.Length;
        switch (region.Rule)
        {
            case RegionRule.Average:
                Array.Fill(colors, result.Average);
                return;

            case RegionRule.ColorFlash:
            case RegionRule.ValueDrop:
            {
                double level = FlashLevel(region, result, now);
                for (int i = 0; i < n; i++) colors[i] = RgbColor.Lerp(colors[i], region.ColorHigh, level);
                return;
            }

            case RegionRule.BarFill:
            case RegionRule.ColorShare:
            case RegionRule.ColorDistance:
            {
                var step = StepFor(region, result.Value);
                bool on = !step.Blink || BlinkOn(region, elapsed);
                double fill = FillFor(region, result.Value, n);
                // Auf Viertel-LEDs gerundet: Messrauschen (Leben 98 statt 100 %) lässt die letzte LED nicht flackern.
                double lit = Math.Round(fill * n * 4) / 4;
                // Etwas sichtbar, aber weniger als eine LED: die erste LED trotzdem ganz, sonst glimmt bei 10 % Leben nur ein Hauch.
                if (fill > 0.005 && lit < 1) lit = 1;
                for (int i = 0; i < n; i++)
                {
                    double level = on ? Math.Clamp(lit - i, 0, 1) : 0;
                    colors[i] = level >= 1 ? step.Color : level <= 0 ? RgbColor.Black : Scale(step.Color, level);
                }
                return;
            }

            default:
                Array.Fill(colors, RgbColor.Black);
                return;
        }
    }

    /// <summary>Spiel-Anzeige eines Geräts: überschreibt <paramref name="colors"/> (vorher die Farben der Effekt-Fläche)
    /// nach den zugewiesenen Bereichen <paramref name="regionIds"/>. Erster Eintrag = Hauptanzeige (leer = Effekt-Fläche
    /// bleibt), weitere nur, solange sie kritisch sind — der letzte kritische gewinnt.</summary>
    public static void Apply(ScreenAnalysis? analysis, IReadOnlyList<string> regionIds, RgbColor[] colors, TimeSpan elapsed, long now)
    {
        if (regionIds.Count == 0 || colors.Length == 0) return;
        var profile = analysis?.Profile;
        if (analysis is null || profile is null)
        {
            // Keine Aufnahme / kein Profil: Hauptanzeige aus, statt veraltete Werte stehen zu lassen.
            if (regionIds[0].Length > 0) Array.Fill(colors, RgbColor.Black);
            return;
        }

        if (!analysis.HudVisible && profile.WhenHudHidden != HudHiddenMode.KeepRunning)
        {
            // Menü, Lobby, Zuschauen: wie die Effekt-Fläche — Mischfarbe des Monitors bzw. aus.
            Array.Fill(colors, profile.WhenHudHidden == HudHiddenMode.ScreenAverage ? analysis.Average : RgbColor.Black);
            return;
        }

        if (Find(profile, analysis, regionIds[0]) is { } main)
            RenderRegion(main.Region, main.Result, colors, elapsed, now);

        for (int i = 1; i < regionIds.Count; i++)
        {
            if (Find(profile, analysis, regionIds[i]) is not { } overlay) continue;
            if (!IsCritical(overlay.Region, overlay.Result, now)) continue;
            if (overlay.Region.Rule is RegionRule.ColorFlash or RegionRule.ValueDrop)
            {
                RenderRegion(overlay.Region, overlay.Result, colors, elapsed, now); // blendet über
                continue;
            }
            // Kritischer Füllstand übernimmt das ganze Gerät (z. B. Sturm ganz nah: alle LEDs blinken lila).
            var full = new RgbColor[colors.Length];
            RenderRegion(overlay.Region, overlay.Result, full, elapsed, now);
            Array.Copy(full, colors, colors.Length);
        }
    }

    private static (ScreenRegion Region, RegionResult Result)? Find(ScreenProfile profile, ScreenAnalysis analysis, string id)
    {
        if (id.Length == 0) return null;
        var region = profile.Regions.FirstOrDefault(r => r.Id == id);
        if (region is null || !region.Enabled || !region.CanShowOnDevice) return null;
        return analysis.Results.TryGetValue(id, out var result) ? (region, result) : null;
    }

    /// <summary>Darf der Bereich einem Gerät mit <paramref name="ledCount"/> LEDs zugewiesen werden? Füllstände brauchen
    /// mindestens <see cref="ScreenRegion.MinGaugeLeds"/> LEDs, sonst lässt sich der Wert nicht ablesen.</summary>
    public static bool CanAssign(ScreenRegion region, int ledCount) =>
        region.CanShowOnDevice && (!region.IsGauge || ledCount >= ScreenRegion.MinGaugeLeds);

    private static RgbColor Scale(RgbColor c, double f) => new((byte)(c.R * f), (byte)(c.G * f), (byte)(c.B * f));
}
