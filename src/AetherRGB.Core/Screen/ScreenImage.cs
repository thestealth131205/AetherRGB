using AetherRGB.Core.Devices;

namespace AetherRGB.Core.Screen;

/// <summary>Ein aufgenommenes Bild eines Monitors (BGRA, 4 Byte pro Pixel, Zeilen ohne Lücken), meist
/// verkleinert. Unveränderlich nach dem Erzeugen — Aufnahme-Thread, Analyse und Effekte teilen es sich.</summary>
public sealed class ScreenImage
{
    public int Width { get; }
    public int Height { get; }

    /// <summary>Größe des Monitors in Pixeln, von dem das Bild stammt (vor dem Verkleinern).</summary>
    public int SourceWidth { get; }
    public int SourceHeight { get; }

    /// <summary>Pixel in BGRA-Reihenfolge, <see cref="Width"/> × <see cref="Height"/> × 4 Byte.</summary>
    public byte[] Bgra { get; }

    public ScreenImage(int width, int height, byte[] bgra, int sourceWidth = 0, int sourceHeight = 0)
    {
        if (width <= 0 || height <= 0) throw new ArgumentOutOfRangeException(nameof(width));
        if (bgra.Length < width * height * 4) throw new ArgumentException("Pixelpuffer zu klein.", nameof(bgra));
        Width = width;
        Height = height;
        Bgra = bgra;
        SourceWidth = sourceWidth > 0 ? sourceWidth : width;
        SourceHeight = sourceHeight > 0 ? sourceHeight : height;
    }

    public RgbColor GetPixel(int x, int y)
    {
        int i = (y * Width + x) * 4;
        return new RgbColor(Bgra[i + 2], Bgra[i + 1], Bgra[i]);
    }

    /// <summary>Verkleinert einen BGRA-Puffer (z. B. die gemappte Textur der Bildschirmaufnahme) um den Faktor
    /// <paramref name="factor"/> per Mittelwert über factor × factor Pixel. Faktor 1 kopiert nur.</summary>
    public static ScreenImage Downsample(ReadOnlySpan<byte> source, int width, int height, int stride, int factor)
    {
        factor = Math.Max(1, factor);
        int w = Math.Max(1, width / factor), h = Math.Max(1, height / factor);
        var dst = new byte[w * h * 4];
        if (factor == 1)
        {
            for (int y = 0; y < h; y++)
                source.Slice(y * stride, w * 4).CopyTo(dst.AsSpan(y * w * 4, w * 4));
            return new ScreenImage(w, h, dst, width, height);
        }

        int count = factor * factor;
        var sums = new int[w * 3];
        for (int y = 0; y < h; y++)
        {
            Array.Clear(sums);
            for (int sy = 0; sy < factor; sy++)
            {
                var row = source.Slice((y * factor + sy) * stride, w * factor * 4);
                for (int x = 0, si = 0; x < w; x++)
                {
                    int b = 0, g = 0, r = 0;
                    for (int sx = 0; sx < factor; sx++, si += 4)
                    {
                        b += row[si];
                        g += row[si + 1];
                        r += row[si + 2];
                    }
                    sums[x * 3] += b;
                    sums[x * 3 + 1] += g;
                    sums[x * 3 + 2] += r;
                }
            }
            int di = y * w * 4;
            for (int x = 0; x < w; x++, di += 4)
            {
                dst[di] = (byte)(sums[x * 3] / count);
                dst[di + 1] = (byte)(sums[x * 3 + 1] / count);
                dst[di + 2] = (byte)(sums[x * 3 + 2] / count);
                dst[di + 3] = 255;
            }
        }
        return new ScreenImage(w, h, dst, width, height);
    }

    /// <summary>Verkleinerungsfaktor, damit das Analysebild höchstens <paramref name="maxWidth"/> breit ist.
    /// 1920 → 2 (960 Pixel), 2560 → 3, 3840 → 4.</summary>
    public static int FactorFor(int width, int maxWidth = 960) => Math.Max(1, (width + maxWidth - 1) / maxWidth);

    /// <summary>Pixelbereich zu einem relativen Rechteck (0..1), mindestens 1 × 1 Pixel, im Bild geklemmt.</summary>
    public (int X0, int Y0, int X1, int Y1) ToPixels(RelRect rect)
    {
        int x0 = Math.Clamp((int)Math.Floor(rect.X * Width), 0, Width - 1);
        int y0 = Math.Clamp((int)Math.Floor(rect.Y * Height), 0, Height - 1);
        int x1 = Math.Clamp((int)Math.Ceiling((rect.X + rect.W) * Width), x0 + 1, Width);
        int y1 = Math.Clamp((int)Math.Ceiling((rect.Y + rect.H) * Height), y0 + 1, Height);
        return (x0, y0, x1, y1);
    }

    /// <summary>Mittelwert aller Pixel in einem relativen Rechteck.</summary>
    public RgbColor Average(RelRect rect)
    {
        var (x0, y0, x1, y1) = ToPixels(rect);
        long r = 0, g = 0, b = 0;
        for (int y = y0; y < y1; y++)
        {
            int i = (y * Width + x0) * 4;
            for (int x = x0; x < x1; x++, i += 4)
            {
                b += Bgra[i];
                g += Bgra[i + 1];
                r += Bgra[i + 2];
            }
        }
        long n = (long)(x1 - x0) * (y1 - y0);
        return new RgbColor((byte)(r / n), (byte)(g / n), (byte)(b / n));
    }

    /// <summary>Kleines Übersichtsbild (je Zelle der Mittelwert) für den Effekt „Bildschirm – Bild“.</summary>
    public RgbColor[] Thumbnail(int cols, int rows)
    {
        var result = new RgbColor[cols * rows];
        for (int ry = 0; ry < rows; ry++)
            for (int rx = 0; rx < cols; rx++)
                result[ry * cols + rx] = Average(new RelRect((double)rx / cols, (double)ry / rows, 1.0 / cols, 1.0 / rows));
        return result;
    }

    /// <summary>Ausschnitt als eigenes Bild (z. B. ein Monitor aus einem Screenshot über zwei Monitore).</summary>
    public ScreenImage Crop(int x, int y, int width, int height)
    {
        x = Math.Clamp(x, 0, Width - 1);
        y = Math.Clamp(y, 0, Height - 1);
        width = Math.Clamp(width, 1, Width - x);
        height = Math.Clamp(height, 1, Height - y);
        var dst = new byte[width * height * 4];
        for (int row = 0; row < height; row++)
            Array.Copy(Bgra, ((y + row) * Width + x) * 4, dst, row * width * 4, width * 4);
        return new ScreenImage(width, height, dst);
    }
}

/// <summary>Rechteck relativ zum Monitor bzw. zur Effekt-Fläche, alle Werte 0..1.</summary>
public readonly record struct RelRect(double X, double Y, double W, double H)
{
    public static readonly RelRect Full = new(0, 0, 1, 1);

    public bool Contains(double u, double v) => u >= X && u <= X + W && v >= Y && v <= Y + H;

    /// <summary>Auf den gültigen Bereich geklemmt, mindestens 0,1 % groß.</summary>
    public RelRect Normalized()
    {
        double x = Math.Clamp(X, 0, 0.999), y = Math.Clamp(Y, 0, 0.999);
        return new RelRect(x, y, Math.Clamp(W, 0.001, 1 - x), Math.Clamp(H, 0.001, 1 - y));
    }

    /// <summary>Aus Pixelkoordinaten eines Bildes der Größe <paramref name="width"/> × <paramref name="height"/>.</summary>
    public static RelRect FromPixels(double x0, double y0, double x1, double y1, double width, double height) =>
        new RelRect(Math.Min(x0, x1) / width, Math.Min(y0, y1) / height, Math.Abs(x1 - x0) / width, Math.Abs(y1 - y0) / height).Normalized();
}
