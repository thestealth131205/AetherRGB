using AetherRGB.Core.Devices;

namespace AetherRGB.Core.Effects.Custom;

/// <summary>Ein fertig zusammengesetztes Bild eines eigenen Effekts im Raster des Dokuments
/// (RGB 0..1, ohne Gamma — die LEDs setzen die Werte linear in Licht um).</summary>
public sealed class CustomEffectFrame
{
    public int Width { get; }
    public int Height { get; }
    /// <summary>R, G, B je Zelle, Zeile für Zeile.</summary>
    public float[] Rgb { get; }

    public CustomEffectFrame(int width, int height)
    {
        Width = width;
        Height = height;
        Rgb = new float[width * height * 3];
    }

    /// <summary>Farbe an einer Canvas-Position, bilinear zwischen den Zellmitten interpoliert.</summary>
    public RgbColor Sample(double x, double y, double canvasWidth, double canvasHeight)
    {
        double gx = (canvasWidth <= 0 ? 0 : x / canvasWidth) * Width - 0.5;
        double gy = (canvasHeight <= 0 ? 0 : y / canvasHeight) * Height - 0.5;
        gx = Math.Clamp(gx, 0, Width - 1);
        gy = Math.Clamp(gy, 0, Height - 1);
        int x0 = (int)gx, y0 = (int)gy;
        int x1 = Math.Min(x0 + 1, Width - 1), y1 = Math.Min(y0 + 1, Height - 1);
        double fx = gx - x0, fy = gy - y0;

        int i00 = (y0 * Width + x0) * 3, i10 = (y0 * Width + x1) * 3, i01 = (y1 * Width + x0) * 3, i11 = (y1 * Width + x1) * 3;
        byte Channel(int c)
        {
            double top = Rgb[i00 + c] + (Rgb[i10 + c] - Rgb[i00 + c]) * fx;
            double bottom = Rgb[i01 + c] + (Rgb[i11 + c] - Rgb[i01 + c]) * fx;
            return ToByte(top + (bottom - top) * fy);
        }
        return new RgbColor(Channel(0), Channel(1), Channel(2));
    }

    /// <summary>Farbe einer Zelle (für die Vorschau im Editor).</summary>
    public RgbColor GetCell(int x, int y)
    {
        int i = (y * Width + x) * 3;
        return new RgbColor(ToByte(Rgb[i]), ToByte(Rgb[i + 1]), ToByte(Rgb[i + 2]));
    }

    private static byte ToByte(double v) => (byte)Math.Round(Math.Clamp(v, 0, 1) * 255);
}

/// <summary>Setzt die Ebenen eines <see cref="CustomEffectDocument"/> zu einem Zeitpunkt zu einem Bild
/// zusammen. Pro Bild werden alle animierten Werte einmal ausgewertet und dann jede Zelle des Rasters
/// (160 × 80 = 12 800) einmal pro Ebene berechnet — auch mit vielen Ebenen weit unter 1 ms pro Ebene.</summary>
public static class CustomEffectRenderer
{
    /// <summary>Effektzeit (inkl. Tempo) → Zeit auf der Zeitleiste gemäß <see cref="CustomEffectDocument.Loop"/>.</summary>
    public static double MapTime(CustomEffectDocument document, double effectSeconds)
    {
        double d = Math.Max(CustomEffectSerializer.MinDurationSeconds, document.DurationSeconds);
        if (effectSeconds < 0) effectSeconds = 0;
        switch (document.Loop)
        {
            case EffectLoopMode.PingPong:
                double p = effectSeconds % (2 * d);
                return p > d ? 2 * d - p : p;
            case EffectLoopMode.Once:
                return Math.Min(effectSeconds, d);
            default:
                return effectSeconds % d;
        }
    }

    /// <summary>Bild zum Zeitpunkt <paramref name="time"/> auf der Zeitleiste.</summary>
    public static CustomEffectFrame Render(CustomEffectDocument document, double time, CustomEffectFrame? target = null)
    {
        int w = document.GridWidth, h = document.GridHeight;
        var frame = target is not null && target.Width == w && target.Height == h ? target : new CustomEffectFrame(w, h);
        var rgb = frame.Rgb;
        Array.Clear(rgb);

        foreach (var layer in document.Layers)
        {
            if (!layer.Visible) continue;
            double opacity = Math.Clamp(layer.Opacity.Evaluate(time), 0, 1);
            if (opacity <= 0) continue;
            var sampler = CreateSampler(layer, document, time);
            if (sampler is null) continue;

            // Inverse Transformation Fläche → Ebene, in Rasterzellen (seitenverhältnistreu).
            double scale = layer.Scale.Evaluate(time);
            if (Math.Abs(scale) < 1e-4) continue;
            double rad = layer.Rotation.Evaluate(time) * Math.PI / 180.0;
            double cos = Math.Cos(-rad), sin = Math.Sin(-rad);
            double px = layer.PivotX * w, py = layer.PivotY * h;
            double ox = layer.OffsetX.Evaluate(time) * w, oy = layer.OffsetY.Evaluate(time) * h;
            var hue = HueMatrix(layer.HueShift.Evaluate(time));
            var blend = layer.Blend;
            bool wrapX = layer.WrapX, wrapY = layer.WrapY;

            for (int y = 0; y < h; y++)
            {
                for (int x = 0; x < w; x++)
                {
                    double qx = (x + 0.5 - px - ox) / scale, qy = (y + 0.5 - py - oy) / scale;
                    double lu = (qx * cos - qy * sin + px) / w;
                    double lv = (qx * sin + qy * cos + py) / h;
                    if (wrapX) lu -= Math.Floor(lu);
                    if (wrapY) lv -= Math.Floor(lv);

                    sampler(lu, lv, out float sr, out float sg, out float sb, out float sa);
                    float a = (float)(sa * opacity);
                    if (a <= 0) continue;
                    if (hue is not null)
                    {
                        float hr = hue[0] * sr + hue[1] * sg + hue[2] * sb;
                        float hg = hue[3] * sr + hue[4] * sg + hue[5] * sb;
                        float hb = hue[6] * sr + hue[7] * sg + hue[8] * sb;
                        sr = Math.Clamp(hr, 0, 1); sg = Math.Clamp(hg, 0, 1); sb = Math.Clamp(hb, 0, 1);
                    }

                    int i = (y * w + x) * 3;
                    Blend(blend, ref rgb[i], sr, a);
                    Blend(blend, ref rgb[i + 1], sg, a);
                    Blend(blend, ref rgb[i + 2], sb, a);
                }
            }
        }
        return frame;
    }

    private static void Blend(LayerBlendMode mode, ref float dst, float src, float a)
    {
        dst = mode switch
        {
            LayerBlendMode.Add => Math.Min(1f, dst + src * a),
            LayerBlendMode.Multiply => dst * (1 - a) + dst * src * a,
            LayerBlendMode.Screen => dst * (1 - a) + (1 - (1 - dst) * (1 - src)) * a,
            _ => dst * (1 - a) + src * a
        };
    }

    /// <summary>Farbe (nicht vormultipliziert) und Deckung einer Ebene an der Ebenen-Position (u, v) in 0..1.</summary>
    private delegate void LayerSampler(double u, double v, out float r, out float g, out float b, out float a);

    private static LayerSampler? CreateSampler(EffectLayer layer, CustomEffectDocument document, double time) => layer switch
    {
        PaintLayer paint => CreatePaintSampler(paint, document, time),
        ShapeLayer shape => CreateShapeSampler(shape, document, time),
        _ => null
    };

    // ---------- Gemalte Ebene ----------

    private static LayerSampler? CreatePaintSampler(PaintLayer layer, CustomEffectDocument document, double time)
    {
        if (layer.Frames.Count == 0) return null;
        int w = document.GridWidth, h = document.GridHeight;
        var (a, b, mix) = PickFrames(layer, time);
        var pa = layer.Frames[a].Pixels;
        var pb = layer.Frames[b].Pixels;
        if (pa.Length != w * h * 4 || pb.Length != w * h * 4) return null;
        bool wrapX = layer.WrapX, wrapY = layer.WrapY;

        return (double u, double v, out float r, out float g, out float bl, out float al) =>
        {
            r = g = bl = al = 0;
            if (u < 0 || u > 1 || v < 0 || v > 1) return;
            double gx = u * w - 0.5, gy = v * h - 0.5;
            int x0 = (int)Math.Floor(gx), y0 = (int)Math.Floor(gy);
            float fx = (float)(gx - x0), fy = (float)(gy - y0);
            // Vormultipliziert interpolieren, sonst bekommen Kanten einen dunklen Saum.
            float pr = 0, pg = 0, pbl = 0, pal = 0;
            for (int j = 0; j < 4; j++)
            {
                int cx = x0 + (j & 1), cy = y0 + (j >> 1);
                if (wrapX) cx = ((cx % w) + w) % w;
                if (wrapY) cy = ((cy % h) + h) % h;
                if (cx < 0 || cy < 0 || cx >= w || cy >= h) continue;
                float weight = ((j & 1) == 1 ? fx : 1 - fx) * ((j >> 1) == 1 ? fy : 1 - fy);
                if (weight <= 0) continue;
                int i = (cy * w + cx) * 4;
                Accumulate(pa, i, weight * (1 - mix), ref pr, ref pg, ref pbl, ref pal);
                if (mix > 0) Accumulate(pb, i, weight * mix, ref pr, ref pg, ref pbl, ref pal);
            }
            if (pal <= 1e-6f) return;
            r = pr / pal; g = pg / pal; bl = pbl / pal; al = Math.Min(1f, pal);
        };
    }

    private static void Accumulate(byte[] px, int i, float weight, ref float r, ref float g, ref float b, ref float a)
    {
        float alpha = px[i + 3] / 255f * weight;
        if (alpha <= 0) return;
        r += px[i] / 255f * alpha;
        g += px[i + 1] / 255f * alpha;
        b += px[i + 2] / 255f * alpha;
        a += alpha;
    }

    /// <summary>Welches Bild des Daumenkinos gerade gilt, und mit Überblenden wie weit das nächste schon
    /// eingeblendet ist. Das Daumenkino läuft in Schleife über die Summe seiner Bilddauern.</summary>
    public static (int Current, int Next, float Mix) PickFrames(PaintLayer layer, double time)
    {
        int n = layer.Frames.Count;
        if (n <= 1) return (0, 0, 0);
        double total = 0;
        foreach (var f in layer.Frames) total += f.DurationSeconds ?? layer.FrameDurationSeconds;
        if (total <= 0) return (0, 0, 0);
        double t = time % total;
        if (t < 0) t += total;
        for (int i = 0; i < n; i++)
        {
            double d = layer.Frames[i].DurationSeconds ?? layer.FrameDurationSeconds;
            if (t < d || i == n - 1)
                return layer.CrossFade ? (i, (i + 1) % n, (float)Math.Clamp(t / d, 0, 1)) : (i, i, 0);
            t -= d;
        }
        return (0, 0, 0);
    }

    // ---------- Form-Ebene ----------

    private static LayerSampler? CreateShapeSampler(ShapeLayer layer, CustomEffectDocument document, double time)
    {
        RgbColor color;
        try { color = RgbColor.FromHex(layer.Color); }
        catch (Exception e) when (e is FormatException or ArgumentException or OverflowException) { return null; }
        float cr = color.R / 255f, cg = color.G / 255f, cb = color.B / 255f;

        // Alles in Einheiten der Flächenhöhe, damit Kreise auch auf 2:1-Flächen rund bleiben.
        double aspect = (double)document.GridWidth / document.GridHeight;
        double cx = layer.CenterX.Evaluate(time) * aspect, cy = layer.CenterY.Evaluate(time);
        double radius = Math.Max(0, layer.Radius.Evaluate(time));
        double halfW = Math.Max(0, layer.Width.Evaluate(time)) * aspect / 2, halfH = Math.Max(0, layer.Height.Evaluate(time)) / 2;
        double halfThickness = Math.Max(0, layer.Thickness.Evaluate(time)) / 2;
        double soft = Math.Max(0, layer.Softness.Evaluate(time));
        // Mindestens eine halbe Zelle Kante, sonst flimmern bewegte Formen auf den LEDs.
        soft = Math.Max(soft, 0.5 / document.GridHeight);
        var kind = layer.Kind;
        bool filled = layer.Filled;
        bool wrapX = layer.WrapX, wrapY = layer.WrapY;

        double Distance(double x, double y)
        {
            double dx = x - cx, dy = y - cy;
            switch (kind)
            {
                case ShapeKind.Ring:
                    return Math.Abs(Math.Sqrt(dx * dx + dy * dy) - radius) - halfThickness;
                case ShapeKind.Rectangle:
                    double qx = Math.Abs(dx) - halfW, qy = Math.Abs(dy) - halfH;
                    double outside = Math.Sqrt(Math.Max(qx, 0) * Math.Max(qx, 0) + Math.Max(qy, 0) * Math.Max(qy, 0));
                    double box = outside + Math.Min(Math.Max(qx, qy), 0);
                    return filled ? box : Math.Abs(box + halfThickness) - halfThickness;
                default:
                    return Math.Sqrt(dx * dx + dy * dy) - radius;
            }
        }

        return (double u, double v, out float r, out float g, out float b, out float a) =>
        {
            r = cr; g = cg; b = cb;
            double x = u * aspect, y = v;
            double d = Distance(x, y);
            // Umlaufende Ebene: Form auch eine Flächenbreite/-höhe versetzt prüfen, damit sie am Rand
            // nahtlos auf der anderen Seite weiterläuft.
            if (wrapX) d = Math.Min(d, Math.Min(Distance(x - aspect, y), Distance(x + aspect, y)));
            if (wrapY) d = Math.Min(d, Math.Min(Distance(x, y - 1), Distance(x, y + 1)));
            a = (float)Math.Clamp(0.5 - d / soft, 0, 1);
            a = a * a * (3 - 2 * a); // weiche Kante
        };
    }

    // ---------- Farbton ----------

    /// <summary>Farbton-Drehung als 3×3-Matrix, null bei 0°. Gedreht wird um die Grauachse (1,1,1): Die
    /// Summe R+G+B bleibt dabei gleich — wie bei der Regenbogen-Welle geben die LEDs bei jedem Farbton
    /// gleich viel Licht ab, und 120°/240° treffen Rot→Grün→Blau genau.</summary>
    private static float[]? HueMatrix(double degrees)
    {
        degrees %= 360;
        if (Math.Abs(degrees) < 0.01) return null;
        double rad = degrees * Math.PI / 180, c = Math.Cos(rad), s = Math.Sin(rad) / Math.Sqrt(3);
        float d = (float)(c + (1 - c) / 3), p = (float)((1 - c) / 3 + s), m = (float)((1 - c) / 3 - s);
        return new[]
        {
            d, m, p,
            p, d, m,
            m, p, d
        };
    }
}

/// <summary>Ein eigener Effekt als normaler <see cref="ICanvasEffect"/>: Tempo-Regler, Helligkeit, mehrere
/// Effekt-Fenster und alle Geräte funktionieren damit ohne Sonderfall. Engine, Vorschau und der
/// MSI-Sende-Thread fragen gleichzeitig zu leicht verschiedenen Zeitpunkten — daher werden die letzten
/// Bilder zwischengespeichert, statt bei jedem einzelnen LED-Aufruf neu zu rechnen.</summary>
public sealed class CustomCanvasEffect : ICanvasEffect
{
    private readonly CustomEffectDocument _document;
    private readonly object _lock = new();
    private readonly (long Ticks, CustomEffectFrame? Frame)[] _cache = new (long, CustomEffectFrame?)[4];
    private int _nextSlot;
    private long? _onceStart;

    /// <param name="document">Wird nicht kopiert — der Aufrufer darf es danach nicht mehr ändern.</param>
    public CustomCanvasEffect(CustomEffectDocument document) => _document = document;

    public string Name => _document.Name;
    public CustomEffectDocument Document => _document;

    public RgbColor Sample(double x, double y, double canvasWidth, double canvasHeight, TimeSpan elapsed) =>
        GetFrame(elapsed).Sample(x, y, canvasWidth, canvasHeight);

    public CustomEffectFrame GetFrame(TimeSpan elapsed)
    {
        lock (_lock)
        {
            long ticks = elapsed.Ticks;
            foreach (var entry in _cache)
                if (entry.Frame is not null && entry.Ticks == ticks) return entry.Frame;

            // "Einmal abspielen" beginnt, wenn der Effekt gewählt wird, nicht beim App-Start.
            _onceStart ??= ticks;
            double seconds = _document.Loop == EffectLoopMode.Once
                ? TimeSpan.FromTicks(ticks - _onceStart.Value).TotalSeconds
                : elapsed.TotalSeconds;
            var time = CustomEffectRenderer.MapTime(_document, seconds);

            // Ältesten Eintrag wiederverwenden. Ein zurückgegebenes Bild kann noch von einem anderen Thread
            // gelesen werden — daher nie den gerade zurückgegebenen Puffer überschreiben, sondern neu anlegen.
            var frame = CustomEffectRenderer.Render(_document, time);
            _cache[_nextSlot] = (ticks, frame);
            _nextSlot = (_nextSlot + 1) % _cache.Length;
            return frame;
        }
    }
}
