using AetherRGB.Core.Devices;

namespace AetherRGB.Core.Effects.Custom;

/// <summary>Rechteck in Rasterzellen (Ende exklusiv).</summary>
public readonly record struct CellRect(int X, int Y, int Width, int Height)
{
    public bool IsEmpty => Width <= 0 || Height <= 0;
    public int Right => X + Width;
    public int Bottom => Y + Height;

    public static readonly CellRect Empty = new(0, 0, 0, 0);

    public CellRect Union(CellRect other)
    {
        if (IsEmpty) return other;
        if (other.IsEmpty) return this;
        int x = Math.Min(X, other.X), y = Math.Min(Y, other.Y);
        return new CellRect(x, y, Math.Max(Right, other.Right) - x, Math.Max(Bottom, other.Bottom) - y);
    }

    public CellRect Clip(int width, int height)
    {
        int x = Math.Max(0, X), y = Math.Max(0, Y);
        int r = Math.Min(width, Right), b = Math.Min(height, Bottom);
        return r <= x || b <= y ? Empty : new CellRect(x, y, r - x, b - y);
    }
}

public enum BrushTip
{
    Round,
    Square
}

/// <summary>Pinsel-Einstellungen. <see cref="Size"/> = Durchmesser in Rasterzellen,
/// <see cref="Hardness"/> 0..1 (1 = harte Kante, 0 = ganz weich auslaufend), <see cref="Opacity"/> 0..1.</summary>
public sealed record BrushSettings(int Size = 3, double Hardness = 1, double Opacity = 1, BrushTip Tip = BrushTip.Round)
{
    public const int MinSize = 1, MaxSize = 50;
}

/// <summary>Spiegeln beim Malen: jeder Pinselabdruck wird zusätzlich an der Mittelachse gespiegelt.</summary>
[Flags]
public enum PaintSymmetry
{
    None = 0,
    Horizontal = 1,
    Vertical = 2
}

/// <summary>Ein Bild im Raster eines eigenen Effekts (RGBA, nicht vormultipliziert, wie
/// <see cref="PaintFrame.Pixels"/>), auf dem die Mal-Werkzeuge arbeiten.</summary>
public sealed class PaintSurface
{
    public int Width { get; }
    public int Height { get; }
    public byte[] Pixels { get; }

    public PaintSurface(int width, int height, byte[] pixels)
    {
        if (pixels.Length != width * height * 4) throw new ArgumentException("Pixelpuffer passt nicht zum Raster.", nameof(pixels));
        Width = width;
        Height = height;
        Pixels = pixels;
    }

    public bool Contains(int x, int y) => x >= 0 && y >= 0 && x < Width && y < Height;

    /// <summary>Farbe einer Zelle; null = (fast) durchsichtig.</summary>
    public RgbColor? GetColor(int x, int y)
    {
        if (!Contains(x, y)) return null;
        int i = (y * Width + x) * 4;
        return Pixels[i + 3] < 8 ? null : new RgbColor(Pixels[i], Pixels[i + 1], Pixels[i + 2]);
    }

    public byte[] CopyRect(CellRect rect)
    {
        var result = new byte[rect.Width * rect.Height * 4];
        for (int y = 0; y < rect.Height; y++)
            Buffer.BlockCopy(Pixels, ((rect.Y + y) * Width + rect.X) * 4, result, y * rect.Width * 4, rect.Width * 4);
        return result;
    }

    public void WriteRect(CellRect rect, byte[] data)
    {
        for (int y = 0; y < rect.Height; y++)
            Buffer.BlockCopy(data, y * rect.Width * 4, Pixels, ((rect.Y + y) * Width + rect.X) * 4, rect.Width * 4);
    }

    /// <summary>Farbe mit Deckung <paramref name="alpha"/> über die Zelle legen ("über"-Verknüpfung).
    /// Grundlage ist <paramref name="basePixels"/> (Stand vor dem Strich), damit sich überlappende
    /// Abdrücke desselben Strichs nicht aufsummieren.</summary>
    internal void Composite(byte[] basePixels, int index, RgbColor color, float alpha, bool erase)
    {
        float da = basePixels[index + 3] / 255f;
        if (erase)
        {
            Pixels[index] = basePixels[index];
            Pixels[index + 1] = basePixels[index + 1];
            Pixels[index + 2] = basePixels[index + 2];
            Pixels[index + 3] = ToByte(da * (1 - alpha));
            return;
        }
        float outA = alpha + da * (1 - alpha);
        if (outA <= 0)
        {
            Pixels[index] = Pixels[index + 1] = Pixels[index + 2] = Pixels[index + 3] = 0;
            return;
        }
        float keep = da * (1 - alpha);
        Pixels[index] = ToByte((color.R / 255f * alpha + basePixels[index] / 255f * keep) / outA);
        Pixels[index + 1] = ToByte((color.G / 255f * alpha + basePixels[index + 1] / 255f * keep) / outA);
        Pixels[index + 2] = ToByte((color.B / 255f * alpha + basePixels[index + 2] / 255f * keep) / outA);
        Pixels[index + 3] = ToByte(outA);
    }

    internal static byte ToByte(float v) => (byte)Math.Round(Math.Clamp(v, 0, 1) * 255);
}

/// <summary>Ein laufender Mal-Vorgang (Pinselstrich, Linie, Rechteck …). Sammelt die Deckung jeder Zelle
/// als Maximum aller Abdrücke und setzt die betroffenen Zellen jedes Mal neu aus dem Stand vor dem
/// Strich zusammen — so wird ein Strich mit 50 % Deckkraft auch dort nicht dunkler, wo er sich selbst
/// kreuzt, und Formen lassen sich beim Ziehen mit <see cref="Reset"/> beliebig oft neu aufziehen.
/// <see cref="Finish"/> liefert den Rückgängig-Schritt.</summary>
public sealed class PaintStroke
{
    private readonly PaintSurface _surface;
    private readonly byte[] _base;
    private readonly float[] _coverage;
    private readonly RgbColor _color;
    private readonly bool _erase;
    private CellRect _dirty = CellRect.Empty;
    private (double X, double Y)? _last;

    public BrushSettings Brush { get; }
    public PaintSymmetry Symmetry { get; }

    /// <summary>Bereich, den der Strich bisher verändert hat (für Neuzeichnen der Anzeige).</summary>
    public CellRect Dirty => _dirty;

    public PaintStroke(PaintSurface surface, BrushSettings brush, RgbColor color, bool erase, PaintSymmetry symmetry = PaintSymmetry.None)
    {
        _surface = surface;
        _base = (byte[])surface.Pixels.Clone();
        _coverage = new float[surface.Width * surface.Height];
        _color = color;
        _erase = erase;
        Brush = brush with { Size = Math.Clamp(brush.Size, BrushSettings.MinSize, BrushSettings.MaxSize) };
        Symmetry = symmetry;
    }

    /// <summary>Freihand: Abdruck an der Mausposition (in Zellen, 0..Width) und Lücke zum vorigen Punkt füllen.</summary>
    public void MoveTo(double x, double y)
    {
        if (_last is { } last) StampLine(last.X, last.Y, x, y, skipFirst: true);
        else Stamp(x, y);
        _last = (x, y);
    }

    /// <summary>Gerade Linie mit der Pinselgröße als Strichstärke.</summary>
    public void Line(double x0, double y0, double x1, double y1) => StampLine(x0, y0, x1, y1, skipFirst: false);

    /// <summary>Rechteck zwischen zwei Zellen (beide eingeschlossen); Rahmen mit Pinselgröße oder gefüllt.</summary>
    public void Rectangle(int x0, int y0, int x1, int y1, bool filled)
    {
        if (x0 > x1) (x0, x1) = (x1, x0);
        if (y0 > y1) (y0, y1) = (y1, y0);
        if (filled)
        {
            float a = (float)Brush.Opacity;
            for (int y = y0; y <= y1; y++)
                for (int x = x0; x <= x1; x++)
                    Cover(x, y, a);
            return;
        }
        double ax = x0 + 0.5, ay = y0 + 0.5, bx = x1 + 0.5, by = y1 + 0.5;
        StampLine(ax, ay, bx, ay, false);
        StampLine(bx, ay, bx, by, false);
        StampLine(bx, by, ax, by, false);
        StampLine(ax, by, ax, ay, false);
    }

    /// <summary>Ellipse, die das Rechteck zwischen zwei Zellen ausfüllt; Rahmen mit Pinselgröße oder gefüllt.</summary>
    public void Ellipse(int x0, int y0, int x1, int y1, bool filled)
    {
        if (x0 > x1) (x0, x1) = (x1, x0);
        if (y0 > y1) (y0, y1) = (y1, y0);
        double cx = (x0 + x1 + 1) / 2.0, cy = (y0 + y1 + 1) / 2.0;
        double rx = (x1 - x0 + 1) / 2.0, ry = (y1 - y0 + 1) / 2.0;
        if (filled)
        {
            float a = (float)Brush.Opacity;
            for (int y = y0; y <= y1; y++)
                for (int x = x0; x <= x1; x++)
                {
                    double dx = (x + 0.5 - cx) / rx, dy = (y + 0.5 - cy) / ry;
                    if (dx * dx + dy * dy <= 1.0) Cover(x, y, a);
                }
            return;
        }
        // Mittellinie des Rahmens eine halbe Strichstärke nach innen, damit er im Rechteck bleibt.
        double inset = Brush.Size / 2.0;
        double mx = Math.Max(0, rx - inset), my = Math.Max(0, ry - inset);
        int steps = Math.Max(16, (int)(Math.PI * (mx + my) * 2));
        double px = cx + mx, py = cy;
        for (int i = 1; i <= steps; i++)
        {
            double t = 2 * Math.PI * i / steps;
            double nx = cx + mx * Math.Cos(t), ny = cy + my * Math.Sin(t);
            StampLine(px, py, nx, ny, skipFirst: i > 1);
            px = nx; py = ny;
        }
    }

    /// <summary>Alles verwerfen, was dieser Strich bisher gemalt hat (Form wird beim Ziehen neu aufgezogen).</summary>
    public void Reset()
    {
        if (!_dirty.IsEmpty)
        {
            for (int y = _dirty.Y; y < _dirty.Bottom; y++)
            {
                int row = y * _surface.Width;
                Array.Clear(_coverage, row + _dirty.X, _dirty.Width);
                Buffer.BlockCopy(_base, (row + _dirty.X) * 4, _surface.Pixels, (row + _dirty.X) * 4, _dirty.Width * 4);
            }
        }
        // Dirty bleibt stehen: die Anzeige muss den zurückgesetzten Bereich noch neu zeichnen.
        _last = null;
    }

    /// <summary>Rückgängig-Schritt mit Vorher/Nachher des veränderten Bereichs; null, wenn sich nichts geändert hat.</summary>
    public PaintEdit? Finish(string description)
    {
        if (_dirty.IsEmpty) return null;
        var before = new byte[_dirty.Width * _dirty.Height * 4];
        for (int y = 0; y < _dirty.Height; y++)
            Buffer.BlockCopy(_base, ((_dirty.Y + y) * _surface.Width + _dirty.X) * 4, before, y * _dirty.Width * 4, _dirty.Width * 4);
        var after = _surface.CopyRect(_dirty);
        return before.AsSpan().SequenceEqual(after) ? null : new PaintEdit(description, _dirty, before, after);
    }

    private void StampLine(double x0, double y0, double x1, double y1, bool skipFirst)
    {
        double dx = x1 - x0, dy = y1 - y0;
        double length = Math.Sqrt(dx * dx + dy * dy);
        // Abstand der Abdrücke: ein Viertel der Pinselgröße, höchstens eine halbe Zelle bei kleinen
        // Pinseln — sonst bekommt ein schneller Strich Löcher.
        double spacing = Math.Max(0.35, Math.Min(0.5, Brush.Size * 0.25));
        int steps = Math.Max(1, (int)Math.Ceiling(length / spacing));
        for (int i = skipFirst ? 1 : 0; i <= steps; i++)
        {
            double t = (double)i / steps;
            Stamp(x0 + dx * t, y0 + dy * t);
        }
    }

    private void Stamp(double x, double y)
    {
        StampOne(x, y);
        double mx = _surface.Width - x, my = _surface.Height - y;
        if (Symmetry.HasFlag(PaintSymmetry.Horizontal)) StampOne(mx, y);
        if (Symmetry.HasFlag(PaintSymmetry.Vertical)) StampOne(x, my);
        if (Symmetry.HasFlag(PaintSymmetry.Horizontal) && Symmetry.HasFlag(PaintSymmetry.Vertical)) StampOne(mx, my);
    }

    /// <summary>Ein Pinselabdruck. Die Mitte rastet auf das Zellraster ein (ungerade Größe: Zellmitte,
    /// gerade: Zellecke), damit Größe 1 genau eine Zelle und Größe 3 genau 3 × 3 Zellen trifft.</summary>
    private void StampOne(double x, double y)
    {
        int size = Brush.Size;
        double cx = size % 2 == 1 ? Math.Floor(x) + 0.5 : Math.Round(x);
        double cy = size % 2 == 1 ? Math.Floor(y) + 0.5 : Math.Round(y);
        double radius = size / 2.0;
        double hardness = Math.Clamp(Brush.Hardness, 0, 1);
        // Weicher Pinsel: voller Kern bis radius·hardness, danach weicher Auslauf bis eine halbe Zelle
        // über den Rand, damit auch Größe 1–2 noch weich wirkt.
        double inner = radius * hardness;
        double outer = hardness >= 1 ? radius : radius + 0.5;
        float opacity = (float)Math.Clamp(Brush.Opacity, 0, 1);

        int minX = (int)Math.Floor(cx - outer), maxX = (int)Math.Ceiling(cx + outer);
        int minY = (int)Math.Floor(cy - outer), maxY = (int)Math.Ceiling(cy + outer);
        for (int py = minY; py <= maxY; py++)
        {
            for (int px = minX; px <= maxX; px++)
            {
                double ddx = px + 0.5 - cx, ddy = py + 0.5 - cy;
                double d = Brush.Tip == BrushTip.Square ? Math.Max(Math.Abs(ddx), Math.Abs(ddy)) : Math.Sqrt(ddx * ddx + ddy * ddy);
                double a;
                if (hardness >= 1) a = d <= radius + 1e-9 ? 1 : 0;
                else if (d <= inner) a = 1;
                else if (d >= outer) a = 0;
                else
                {
                    double t = (outer - d) / (outer - inner);
                    a = t * t * (3 - 2 * t);
                }
                if (a > 0) Cover(px, py, (float)a * opacity);
            }
        }
    }

    private void Cover(int x, int y, float alpha)
    {
        if (!_surface.Contains(x, y)) return;
        int c = y * _surface.Width + x;
        if (alpha <= _coverage[c]) return;
        _coverage[c] = alpha;
        _surface.Composite(_base, c * 4, _color, alpha, _erase);
        _dirty = _dirty.Union(new CellRect(x, y, 1, 1));
    }
}

/// <summary>Werkzeuge, die in einem Schritt wirken (kein Ziehen).</summary>
public static class PaintTools
{
    /// <summary>Füllen ab Zelle (x, y): alle Zellen, deren Farbe und Deckung höchstens
    /// <paramref name="tolerance"/> (0..255 je Kanal) von der Startzelle abweichen — zusammenhängend
    /// (4er-Nachbarschaft) oder auf der ganzen Fläche. Gibt den Rückgängig-Schritt zurück.</summary>
    public static PaintEdit? Fill(PaintSurface surface, int x, int y, RgbColor color, double opacity, int tolerance, bool contiguous)
    {
        if (!surface.Contains(x, y)) return null;
        int w = surface.Width, h = surface.Height;
        var px = surface.Pixels;
        var original = (byte[])px.Clone();
        int seed = (y * w + x) * 4;
        byte sr = px[seed], sg = px[seed + 1], sb = px[seed + 2], sa = px[seed + 3];
        tolerance = Math.Clamp(tolerance, 0, 255);

        bool Matches(int i)
        {
            // Durchsichtige Zellen gelten unabhängig von ihrer (unsichtbaren) Restfarbe als gleich.
            if (sa == 0 && original[i + 3] == 0) return true;
            return Math.Abs(original[i] - sr) <= tolerance && Math.Abs(original[i + 1] - sg) <= tolerance &&
                   Math.Abs(original[i + 2] - sb) <= tolerance && Math.Abs(original[i + 3] - sa) <= tolerance;
        }

        var mark = new bool[w * h];
        if (contiguous)
        {
            var stack = new Stack<int>();
            stack.Push(y * w + x);
            mark[y * w + x] = true;
            while (stack.Count > 0)
            {
                int c = stack.Pop();
                int cx = c % w, cy = c / w;
                void Try(int nx, int ny)
                {
                    if (nx < 0 || ny < 0 || nx >= w || ny >= h) return;
                    int n = ny * w + nx;
                    if (mark[n] || !Matches(n * 4)) return;
                    mark[n] = true;
                    stack.Push(n);
                }
                Try(cx - 1, cy); Try(cx + 1, cy); Try(cx, cy - 1); Try(cx, cy + 1);
            }
        }
        else
        {
            for (int c = 0; c < w * h; c++) mark[c] = Matches(c * 4);
        }

        float alpha = (float)Math.Clamp(opacity, 0, 1);
        var dirty = CellRect.Empty;
        for (int c = 0; c < w * h; c++)
        {
            if (!mark[c]) continue;
            surface.Composite(original, c * 4, color, alpha, erase: false);
            dirty = dirty.Union(new CellRect(c % w, c / w, 1, 1));
        }
        return MakeEdit("Füllen", surface, original, dirty);
    }

    /// <summary>Ganze Fläche leeren (durchsichtig = LEDs aus) oder mit einer Farbe füllen.</summary>
    public static PaintEdit? Clear(PaintSurface surface, RgbColor? color = null)
    {
        var original = (byte[])surface.Pixels.Clone();
        for (int i = 0; i < surface.Pixels.Length; i += 4)
        {
            surface.Pixels[i] = color?.R ?? 0;
            surface.Pixels[i + 1] = color?.G ?? 0;
            surface.Pixels[i + 2] = color?.B ?? 0;
            surface.Pixels[i + 3] = (byte)(color is null ? 0 : 255);
        }
        return MakeEdit(color is null ? "Leeren" : "Fläche füllen", surface, original, new CellRect(0, 0, surface.Width, surface.Height));
    }

    private static PaintEdit? MakeEdit(string description, PaintSurface surface, byte[] original, CellRect dirty)
    {
        if (dirty.IsEmpty) return null;
        var before = new PaintSurface(surface.Width, surface.Height, original).CopyRect(dirty);
        var after = surface.CopyRect(dirty);
        return before.AsSpan().SequenceEqual(after) ? null : new PaintEdit(description, dirty, before, after);
    }
}

/// <summary>Ein Rückgängig-Schritt: nur der veränderte Bereich, vorher und nachher.</summary>
public sealed record PaintEdit(string Description, CellRect Rect, byte[] Before, byte[] After)
{
    public long Bytes => Before.LongLength + After.LongLength;

    /// <summary>Wird von <see cref="PaintHistory.Push"/> vergeben.</summary>
    public long Sequence { get; set; }
}

/// <summary>Fortlaufende Nummer für Rückgängig-Schritte aus verschiedenen Verläufen (Malen pro Bild,
/// Ebenen/Animationen), damit Strg+Z immer den zuletzt gemachten Schritt zurücknimmt.</summary>
public static class EditSequence
{
    private static long _next;
    public static long Next() => Interlocked.Increment(ref _next);
}

/// <summary>Rückgängig/Wiederholen für ein Bild. Hält mindestens <see cref="MinSteps"/> Schritte und
/// darüber hinaus so viele, wie in <see cref="MaxBytes"/> passen.</summary>
public sealed class PaintHistory
{
    public const int MinSteps = 100;
    public const long MaxBytes = 64L * 1024 * 1024;

    private readonly List<PaintEdit> _undo = new();
    private readonly Stack<PaintEdit> _redo = new();
    private long _bytes;

    public bool CanUndo => _undo.Count > 0;
    public bool CanRedo => _redo.Count > 0;
    public string? UndoDescription => _undo.Count > 0 ? _undo[^1].Description : null;
    public string? RedoDescription => _redo.Count > 0 ? _redo.Peek().Description : null;

    /// <summary>Reihenfolge-Nummer (<see cref="EditSequence"/>) des nächsten Rückgängig-/Wiederholen-Schritts,
    /// -1 = keiner. Damit lässt sich dieser Verlauf mit anderen (z. B. Ebenen) in eine gemeinsame Reihenfolge bringen.</summary>
    public long UndoSequence => _undo.Count > 0 ? _undo[^1].Sequence : -1;
    public long RedoSequence => _redo.Count > 0 ? _redo.Peek().Sequence : -1;

    public void Push(PaintEdit? edit)
    {
        if (edit is null) return;
        edit.Sequence = EditSequence.Next();
        _undo.Add(edit);
        _bytes += edit.Bytes;
        _redo.Clear();
        while (_undo.Count > MinSteps && _bytes > MaxBytes)
        {
            _bytes -= _undo[0].Bytes;
            _undo.RemoveAt(0);
        }
    }

    public CellRect? Undo(PaintSurface surface)
    {
        if (_undo.Count == 0) return null;
        var edit = _undo[^1];
        _undo.RemoveAt(_undo.Count - 1);
        _bytes -= edit.Bytes;
        surface.WriteRect(edit.Rect, edit.Before);
        _redo.Push(edit);
        return edit.Rect;
    }

    public CellRect? Redo(PaintSurface surface)
    {
        if (_redo.Count == 0) return null;
        var edit = _redo.Pop();
        surface.WriteRect(edit.Rect, edit.After);
        _undo.Add(edit);
        _bytes += edit.Bytes;
        return edit.Rect;
    }

    public void Clear()
    {
        _undo.Clear();
        _redo.Clear();
        _bytes = 0;
    }

    /// <summary>Wiederholen verwerfen — z. B. wenn anderswo (Ebenen) ein neuer Schritt gemacht wurde.</summary>
    public void ClearRedo() => _redo.Clear();
}
