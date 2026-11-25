using System.Diagnostics;
using AetherRGB.Core.Devices;

namespace AetherRGB.Core.Screen;

/// <summary>Messergebnis eines Bereichs in einem Bild.</summary>
public sealed record RegionResult(
    /// <summary>Ungeglätteter Messwert 0..1 (Anteil, Füllstand; bei Average/Blitzen 0).</summary>
    double Raw,
    /// <summary>Geglätteter Wert 0..1, den die LEDs zeigen.</summary>
    double Value,
    /// <summary>Mischfarbe des Bereichs.</summary>
    RgbColor Average,
    /// <summary>Zeitpunkt des letzten Blitzes (<see cref="Stopwatch.GetTimestamp"/>), 0 = nie.</summary>
    long LastTrigger,
    /// <summary>HudCheck bestanden bzw. bei anderen Regeln immer true.</summary>
    bool Passed,
    /// <summary>Bild des Bereichs (Zeilen von oben, schon gesättigt), nur bei Darstellung <see cref="LayerDisplay.Mirror"/>.</summary>
    RgbColor[]? Pixels = null,
    int PixelCols = 0,
    int PixelRows = 0);

/// <summary>Auswertung eines Bildes nach einem Profil.</summary>
public sealed record ScreenAnalysis(
    ScreenProfile? Profile,
    RgbColor Average,
    RgbColor[] Thumbnail,
    int ThumbnailCols,
    int ThumbnailRows,
    bool HudVisible,
    IReadOnlyDictionary<string, RegionResult> Results,
    long Timestamp);

/// <summary>Wertet aufeinanderfolgende Bilder nach einem Profil aus. Hat Zustand (Glättung, Blitz-Erkennung),
/// also eine Instanz pro Profil und Bildfolge. Nicht threadsicher — läuft auf dem Aufnahme-Thread.</summary>
public sealed class ScreenAnalyzer
{
    public const int ThumbCols = 32, ThumbRows = 18;

    private sealed class State
    {
        public double Value = double.NaN;
        public double Baseline = double.NaN;
        public long LastTrigger;
        public readonly double[] History = new double[3];
        public int HistoryCount;
        public double? LastStable;
        /// <summary>ColorDistance: nächster Auslöser erlaubt (Farbe war seit dem letzten Auslöser lange genug weg).</summary>
        public bool Armed = true;
        /// <summary>ColorDistance: seit wann die Farbe wieder außer Reichweite ist (0 = ist sie nicht).</summary>
        public long AwaySince;
    }

    public ScreenProfile? Profile { get; }
    private readonly Dictionary<string, State> _states = new();
    private long _lastTimestamp;
    private bool _hudVisible = true;
    private int _hudFlipCount;

    public ScreenAnalyzer(ScreenProfile? profile) => Profile = profile;

    /// <summary>Verstärkung der Sättigung für Mischfarben, Ambilight-Bild und „Mischfarbe“-Bereiche
    /// (<see cref="ColorBoost.Saturate(RgbColor, double)"/>), 1 = unverändert.</summary>
    public double Saturation { get; set; } = 1;

    /// <summary>Übersichtsbild und Mischfarbe eines Bildes, schon verstärkt. Teilen sich mehrere Analyzer ein Bild
    /// (mehrere Spiel-Profile gleichzeitig), wird das nur einmal berechnet und an <see cref="Analyze"/> übergeben.</summary>
    public static (RgbColor[] Thumbnail, RgbColor Average) Overview(ScreenImage image, double saturation)
    {
        var thumb = image.Thumbnail(ThumbCols, ThumbRows);
        var average = ColorBoost.Saturate(AverageOf(thumb), saturation);
        ColorBoost.Saturate(thumb, saturation);
        return (thumb, average);
    }

    public ScreenAnalysis Analyze(ScreenImage image, long? timestamp = null, (RgbColor[] Thumbnail, RgbColor Average)? overview = null)
    {
        long now = timestamp ?? Stopwatch.GetTimestamp();
        double dtMs = _lastTimestamp == 0 ? 0 : (now - _lastTimestamp) * 1000.0 / Stopwatch.Frequency;
        _lastTimestamp = now;

        var results = new Dictionary<string, RegionResult>();
        var (thumb, average) = overview ?? Overview(image, Saturation);
        bool hud = true;

        if (Profile is not null)
        {
            // Erst alle messenden Regeln, danach die, die auf andere Bereiche zurückgreifen (ValueDrop).
            foreach (var region in Profile.Regions.Where(r => r.Enabled && r.Rule != RegionRule.ValueDrop))
            {
                var result = Measure(image, region, now, dtMs);
                if (region.IsMirror) result = WithPixels(result, image, region);
                results[region.Id] = result;
                if (region.Rule == RegionRule.HudCheck && !result.Passed) hud = false;
            }

            // Kurz verdecktes HUD (Explosion, Einblendung) soll die Regeln nicht gleich pausieren: Wechsel erst nach 3 Bildern.
            if (hud != _hudVisible)
            {
                if (++_hudFlipCount >= 3) { _hudVisible = hud; _hudFlipCount = 0; }
            }
            else _hudFlipCount = 0;

            foreach (var region in Profile.Regions.Where(r => r.Enabled && r.Rule == RegionRule.ValueDrop))
                results[region.Id] = MeasureDrop(region, results, now, hud && _hudVisible);
        }

        return new ScreenAnalysis(Profile, average, thumb, ThumbCols, ThumbRows, Profile is null || _hudVisible, results, now);
    }

    /// <summary>Höchstens so viele Zellen breit/hoch wird das Bild eines Bereichs für die Darstellung als Bild.</summary>
    public const int MirrorMaxCols = 128, MirrorMaxRows = 32;

    private RegionResult WithPixels(RegionResult result, ScreenImage image, ScreenRegion region)
    {
        var (pixels, cols, rows) = RegionPixels(image, region.Area, Saturation);
        return result with { Pixels = pixels, PixelCols = cols, PixelRows = rows };
    }

    /// <summary>Bild eines Bereichs: je Zelle der Mittelwert, höchstens <see cref="MirrorMaxCols"/> ×
    /// <see cref="MirrorMaxRows"/> Zellen, bei kleinen Bereichen 1 Zelle = 1 Pixel des Analysebildes.</summary>
    public static (RgbColor[] Pixels, int Cols, int Rows) RegionPixels(ScreenImage image, RelRect area, double saturation)
    {
        var (x0, y0, x1, y1) = image.ToPixels(area);
        int w = x1 - x0, h = y1 - y0;
        int cols = Math.Min(w, MirrorMaxCols), rows = Math.Min(h, MirrorMaxRows);
        var result = new RgbColor[cols * rows];
        var px = image.Bgra;
        for (int ry = 0; ry < rows; ry++)
        {
            int ya = y0 + ry * h / rows, yb = Math.Max(ya + 1, y0 + (ry + 1) * h / rows);
            for (int rx = 0; rx < cols; rx++)
            {
                int xa = x0 + rx * w / cols, xb = Math.Max(xa + 1, x0 + (rx + 1) * w / cols);
                long r = 0, g = 0, b = 0;
                for (int y = ya; y < yb; y++)
                {
                    int i = (y * image.Width + xa) * 4;
                    for (int x = xa; x < xb; x++, i += 4) { b += px[i]; g += px[i + 1]; r += px[i + 2]; }
                }
                long n = (long)(xb - xa) * (yb - ya);
                result[ry * cols + rx] = new RgbColor((byte)(r / n), (byte)(g / n), (byte)(b / n));
            }
        }
        ColorBoost.Saturate(result, saturation);
        return (result, cols, rows);
    }

    private static RgbColor AverageOf(RgbColor[] cells)
    {
        long r = 0, g = 0, b = 0;
        foreach (var c in cells) { r += c.R; g += c.G; b += c.B; }
        return new RgbColor((byte)(r / cells.Length), (byte)(g / cells.Length), (byte)(b / cells.Length));
    }

    private State StateOf(ScreenRegion region)
    {
        if (!_states.TryGetValue(region.Id, out var state)) _states[region.Id] = state = new State();
        return state;
    }

    private RegionResult Measure(ScreenImage image, ScreenRegion region, long now, double dtMs)
    {
        var state = StateOf(region);
        var avg = ColorBoost.Saturate(image.Average(region.Area), Saturation);
        switch (region.Rule)
        {
            case RegionRule.Average:
                return new RegionResult(0, 1, avg, 0, true);

            case RegionRule.ColorShare:
            {
                double share = Share(image, region);
                double raw = share < region.MinShare ? 0 : Math.Clamp(share / region.FullAt, 0, 1);
                return new RegionResult(raw, Smooth(state, raw, region.SmoothingMs, dtMs), avg, 0, true);
            }

            case RegionRule.BarFill:
            {
                double raw = BarFill(image, region);
                PushHistory(state, raw);
                return new RegionResult(raw, Smooth(state, raw, region.SmoothingMs, dtMs), avg, 0, true);
            }

            case RegionRule.ColorFlash:
            {
                double share = Share(image, region);
                if (double.IsNaN(state.Baseline)) state.Baseline = share;
                bool cooled = now - state.LastTrigger > region.FlashMs / 2.0 * Stopwatch.Frequency / 1000.0;
                if (cooled && share - state.Baseline >= region.Threshold && share >= region.Threshold && _hudVisible)
                    state.LastTrigger = now;
                // Grundpegel langsam nachführen (≈ 1 s), damit dauerhaft rote Szenen nicht ständig blitzen.
                double k = dtMs <= 0 ? 1 : 1 - Math.Exp(-dtMs / 1000.0);
                state.Baseline += (share - state.Baseline) * k;
                return new RegionResult(share, share, avg, state.LastTrigger, true);
            }

            case RegionRule.HudCheck:
            {
                double share = Share(image, region);
                return new RegionResult(share, share, avg, 0, share >= region.Threshold);
            }

            case RegionRule.ColorDistance:
            {
                double raw = Distance(image, region);
                double value = Smooth(state, raw, region.SmoothingMs, dtMs);
                // Auslöser fürs Lauflicht: Farbe kommt näher als TriggerBelow (z. B. Sturm taucht auf der Minimap auf).
                // Erneut erst, nachdem sie mindestens 1 s lang wieder weiter weg war — ein einzelnes Bild, in dem der
                // Sturm nicht erkannt wird, löst so kein zweites Lauflicht aus.
                if (raw >= region.TriggerBelow)
                {
                    if (state.AwaySince == 0) state.AwaySince = now;
                    if (now - state.AwaySince >= Stopwatch.Frequency) state.Armed = true;
                }
                else
                {
                    state.AwaySince = 0;
                    if (value < region.TriggerBelow && state.Armed && _hudVisible)
                    {
                        state.LastTrigger = now;
                        state.Armed = false;
                    }
                }
                return new RegionResult(raw, value, avg, state.LastTrigger, true);
            }

            default:
                return new RegionResult(0, 0, avg, 0, true);
        }
    }

    /// <param name="hudNow">HUD in diesem Bild sichtbar — schon das erste Bild ohne HUD (Menü, Tod) darf keinen
    /// „Abfall“ melden, auch bevor <see cref="ScreenAnalysis.HudVisible"/> nach 3 Bildern umschaltet.</param>
    private RegionResult MeasureDrop(ScreenRegion region, Dictionary<string, RegionResult> results, long now, bool hudNow)
    {
        var state = StateOf(region);
        var sources = region.Sources.Where(results.ContainsKey).ToList();
        if (sources.Count == 0) return new RegionResult(0, 0, RgbColor.Black, state.LastTrigger, true);

        // Median der letzten 3 Messungen je Quelle: ein einzelnes falsch gemessenes Bild löst keinen Blitz aus.
        double sum = 0;
        foreach (var id in sources)
        {
            var source = Profile!.Regions.First(r => r.Id == id);
            var s = StateOf(source);
            sum += s.HistoryCount == 0 ? results[id].Raw : Median(s);
        }

        if (!hudNow)
        {
            state.LastStable = null; // nach Menü/Tod neu einmessen statt einen „Abfall“ zu sehen
            return new RegionResult(sum, sum, RgbColor.Black, state.LastTrigger, true);
        }

        bool cooled = now - state.LastTrigger > 0.15 * Stopwatch.Frequency;
        if (state.LastStable is { } last && last - sum >= region.Threshold && cooled) state.LastTrigger = now;
        state.LastStable = sum;
        return new RegionResult(sum, sum, RgbColor.Black, state.LastTrigger, true);
    }

    private static void PushHistory(State state, double value)
    {
        state.History[state.HistoryCount % 3] = value;
        state.HistoryCount++;
    }

    private static double Median(State state)
    {
        int n = Math.Min(state.HistoryCount, 3);
        if (n < 3) return state.History[(state.HistoryCount - 1) % 3];
        double a = state.History[0], b = state.History[1], c = state.History[2];
        return Math.Max(Math.Min(a, b), Math.Min(Math.Max(a, b), c));
    }

    private static double Smooth(State state, double raw, double smoothingMs, double dtMs)
    {
        if (double.IsNaN(state.Value) || smoothingMs <= 0 || dtMs <= 0) return state.Value = raw;
        double k = 1 - Math.Exp(-dtMs / smoothingMs);
        return state.Value += (raw - state.Value) * k;
    }

    /// <summary>Anteil der Pixel im Bereich, die zu einer der Farben passen.</summary>
    public static double Share(ScreenImage image, ScreenRegion region)
    {
        if (region.Colors.Count == 0) return 0;
        var (x0, y0, x1, y1) = image.ToPixels(region.Area);
        var px = image.Bgra;
        int hits = 0;
        for (int y = y0; y < y1; y++)
        {
            int i = (y * image.Width + x0) * 4;
            for (int x = x0; x < x1; x++, i += 4)
                if (MatchesAny(region.Colors, px[i + 2], px[i + 1], px[i])) hits++;
        }
        return (double)hits / ((x1 - x0) * (y1 - y0));
    }

    /// <summary>Füllstand eines Balkens 0..1. Eine Spalte (bzw. Zeile bei senkrechten Balken) zählt als gefüllt, wenn
    /// mindestens die Hälfte ihrer Pixel zur Füllfarbe passt. Ergebnis ist die weiteste gefüllte Spalte, bis zu der
    /// mindestens 60 % der Spalten gefüllt sind — Zahlen oder Glanzlichter auf dem Balken stören so nicht, ein
    /// einzelner passender Pixel weit hinten aber auch nicht. Die Füllung muss am Anfang (erste 10 %) beginnen.</summary>
    public static double BarFill(ScreenImage image, ScreenRegion region)
    {
        if (region.Colors.Count == 0) return 0;
        var (x0, y0, x1, y1) = image.ToPixels(region.Area);
        bool vertical = region.Direction is BarDirection.BottomToTop or BarDirection.TopToBottom;
        int length = vertical ? y1 - y0 : x1 - x0;
        int depth = vertical ? x1 - x0 : y1 - y0;
        var px = image.Bgra;

        int filled = 0, best = 0, first = -1;
        for (int step = 0; step < length; step++)
        {
            int along = region.Direction switch
            {
                BarDirection.LeftToRight => x0 + step,
                BarDirection.RightToLeft => x1 - 1 - step,
                BarDirection.TopToBottom => y0 + step,
                _ => y1 - 1 - step,
            };
            int hits = 0;
            for (int d = 0; d < depth; d++)
            {
                int x = vertical ? x0 + d : along, y = vertical ? along : y0 + d;
                int i = (y * image.Width + x) * 4;
                if (MatchesAny(region.Colors, px[i + 2], px[i + 1], px[i])) hits++;
            }
            if (hits * 2 >= depth)
            {
                if (first < 0)
                {
                    first = step;
                    if (first > Math.Max(1, length / 10)) return 0;
                }
                filled++;
                if (filled >= 0.6 * (step + 1)) best = step + 1;
            }
        }
        return (double)best / length;
    }

    /// <summary>Raster für <see cref="Distance"/>: so viele Zellen über die längere Seite des Bereichs.</summary>
    public const int DistanceGrid = 32;

    /// <summary>Abstand von der Bereichsmitte zur nächsten Stelle in einer der Farben, 0..1 (1 = nichts in Reichweite
    /// <see cref="ScreenRegion.Range"/>, gemessen in halben Bereichsgrößen). Gezählt werden nur Rasterzellen, die zu
    /// mindestens 40 % passen — einzelne Pixel (Symbole, Mitspieler-Marker) ergeben so keinen „nahen Sturm“. Die
    /// Zellen direkt um die Mitte (Spielerpfeil) zählen als Abstand 0.</summary>
    public static double Distance(ScreenImage image, ScreenRegion region)
    {
        if (region.Colors.Count == 0) return 1;
        var (x0, y0, x1, y1) = image.ToPixels(region.Area);
        int w = x1 - x0, h = y1 - y0;
        if (w <= 0 || h <= 0) return 1;
        double cell = Math.Max(1.0, Math.Max(w, h) / (double)DistanceGrid);
        int cols = Math.Max(1, (int)Math.Ceiling(w / cell)), rows = Math.Max(1, (int)Math.Ceiling(h / cell));
        double cx = x0 + w / 2.0, cy = y0 + h / 2.0;
        double halfX = w / 2.0, halfY = h / 2.0;
        var px = image.Bgra;
        double best = double.MaxValue;

        for (int row = 0; row < rows; row++)
        {
            int ya = y0 + (int)(row * cell), yb = Math.Min(y1, y0 + (int)((row + 1) * cell));
            if (yb <= ya) continue;
            for (int col = 0; col < cols; col++)
            {
                int xa = x0 + (int)(col * cell), xb = Math.Min(x1, x0 + (int)((col + 1) * cell));
                if (xb <= xa) continue;
                int hits = 0, total = 0;
                for (int y = ya; y < yb; y++)
                {
                    int i = (y * image.Width + xa) * 4;
                    for (int x = xa; x < xb; x++, i += 4, total++)
                        if (MatchesAny(region.Colors, px[i + 2], px[i + 1], px[i])) hits++;
                }
                if (hits * 5 < total * 2) continue;
                // Nächster Punkt der Zelle zur Mitte, relativ zur halben Bereichsgröße (Kreis bei quadratischer Minimap).
                double dx = Math.Max(0, Math.Max(xa - cx, cx - xb)) / halfX;
                double dy = Math.Max(0, Math.Max(ya - cy, cy - yb)) / halfY;
                double d = Math.Sqrt(dx * dx + dy * dy);
                if (d < best) best = d;
            }
        }
        if (best == double.MaxValue) return 1;
        // Mitte ± eine Zelle zählt als „drin“ (die Spielfigur verdeckt die Farbe genau in der Mitte).
        double inside = 1.5 * cell / Math.Min(halfX, halfY);
        if (best <= inside) return 0;
        return Math.Clamp(best / region.Range, 0, 1);
    }

    private static bool MatchesAny(List<ColorMatcher> colors, byte r, byte g, byte b)
    {
        foreach (var c in colors)
            if (c.Matches(r, g, b)) return true;
        return false;
    }
}
