using System.Diagnostics;
using System.Globalization;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using AetherRGB.Core.Devices;
using AetherRGB.Core.Effects;
using AetherRGB.Core.Panels;
using AetherRGB.Core.Sensors;

namespace AetherRGB.App.Panels;

/// <summary>Zeichnet ein Sensor-Panel mit den aktuellen Werten des <see cref="SensorHub"/>. Wird vom Editor und vom
/// Zweitdisplay-Fenster gleichermaßen benutzt, die Anzeige ist also im Editor genau die auf dem Display.
/// Hält je Element einen kleinen Zustand (Effekt-Instanz und Bildpuffer der Animation, geladene Bilder).</summary>
public sealed class SensorPanelRenderer
{
    private readonly SensorHub _hub;
    private readonly Func<string?, string?> _resolveAsset;
    private readonly Dictionary<string, AnimationState> _animations = new();
    private readonly Dictionary<string, (AnimatedImage? Image, DateTime Loaded)> _images = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<(RgbColor, double), SolidColorBrush> _brushes = new();
    private readonly Dictionary<string, Typeface> _typefaces = new();
    private readonly Stopwatch _clock = Stopwatch.StartNew();
    private TimeSpan _lastFrame;
    /// <summary>Zählt Änderungen am Effekt-Katalog (eigener Effekt gespeichert) — einmal für alle Renderer abonniert.</summary>
    private static int _catalogVersion;
    static SensorPanelRenderer() => EffectCatalog.Changed += () => Interlocked.Increment(ref _catalogVersion);

    public double PixelsPerDip { get; set; } = 1;

    public SensorPanelRenderer(SensorHub hub, Func<string?, string?> resolveAsset)
    {
        _hub = hub;
        _resolveAsset = resolveAsset;
    }

    private sealed class AnimationState
    {
        public string Key = "";
        public int CatalogVersion;
        public ICanvasEffect? Effect;
        public WriteableBitmap? Bitmap;
        public int[] Pixels = Array.Empty<int>();
        public double EffectSeconds;
    }

    public void Render(DrawingContext dc, SensorPanelDocument doc)
    {
        var now = _clock.Elapsed;
        double dt = Math.Clamp((now - _lastFrame).TotalSeconds, 0, 0.5);
        _lastFrame = now;

        var full = new Rect(0, 0, doc.Width, doc.Height);
        dc.PushClip(new RectangleGeometry(full));
        dc.DrawRectangle(Brush(doc.Background), null, full);
        if (Image(doc.BackgroundImage) is { } bg) DrawStretched(dc, bg.FrameAt(now), full, PanelStretch.UniformToFill);

        foreach (var item in doc.Items)
        {
            if (!item.Visible) continue;
            var rect = new Rect(item.X, item.Y, Math.Max(1, item.W), Math.Max(1, item.H));
            bool opaque = item.Opacity >= 0.999;
            if (!opaque) dc.PushOpacity(item.Opacity);
            try
            {
                switch (item.Kind)
                {
                    case PanelItemKind.Box: DrawBox(dc, item, rect); break;
                    case PanelItemKind.Text: DrawText(dc, item.Text, item, rect, item.Color, item.FontSize, item.Align, wrap: true); break;
                    case PanelItemKind.Clock: DrawText(dc, PanelLogic.ClockText(item, DateTime.Now), item, rect, item.Color, item.FontSize, item.Align, wrap: false); break;
                    case PanelItemKind.Value: DrawValue(dc, item, rect); break;
                    case PanelItemKind.Bar: DrawBar(dc, item, rect); break;
                    case PanelItemKind.Gauge: DrawGauge(dc, item, rect); break;
                    case PanelItemKind.Graph: DrawGraph(dc, item, rect); break;
                    case PanelItemKind.Animation: DrawAnimation(dc, item, rect, dt); break;
                    case PanelItemKind.Image: DrawImage(dc, item, rect, now); break;
                }
            }
            catch (Exception ex)
            {
                // Ein kaputtes Element (Effekt wirft, Bild unlesbar) darf das ganze Panel nicht leer lassen.
                DrawText(dc, "⚠ " + ex.Message, item, rect, item.CritColor, 12, PanelAlign.Left, wrap: true);
            }
            if (!opaque) dc.Pop();
        }
        dc.Pop();
    }

    /// <summary>Zustände gelöschter Elemente verwerfen.</summary>
    public void Prune(SensorPanelDocument doc)
    {
        var ids = doc.Items.Select(i => i.Id).ToHashSet();
        foreach (var id in _animations.Keys.Where(k => !ids.Contains(k)).ToList()) _animations.Remove(id);
    }

    /// <summary>Geänderte Bilddateien neu laden.</summary>
    public void ForgetImages() => _images.Clear();

    // ===================== Elemente =====================

    private void DrawBox(DrawingContext dc, PanelItem item, Rect rect)
    {
        var pen = item.BorderWidth > 0 ? new Pen(Brush(item.BorderColor), item.BorderWidth) : null;
        double inset = item.BorderWidth / 2;
        var r = new Rect(rect.X + inset, rect.Y + inset, Math.Max(0, rect.Width - item.BorderWidth), Math.Max(0, rect.Height - item.BorderWidth));
        dc.DrawRoundedRectangle(Brush(item.BackColor, item.BackOpacity), pen, r, item.CornerRadius, item.CornerRadius);
    }

    private SensorReading? Reading(PanelItem item) => PanelLogic.Resolve(item.SensorId, _hub.Get);

    private void DrawValue(DrawingContext dc, PanelItem item, Rect rect)
    {
        var reading = Reading(item);
        var valueColor = PanelLogic.ColorFor(item, reading, item.Color);
        string value = PanelLogic.ValueText(item, reading);
        string label = item.ShowLabel ? PanelLogic.LabelText(item, reading) : "";
        if (label.Length == 0)
        {
            DrawText(dc, value, item, rect, valueColor, item.FontSize, item.Align, wrap: false);
            return;
        }
        if (rect.Height >= item.FontSize * 2.0)
        {
            // Zweizeilig: Beschriftung klein oben, Wert groß darunter.
            double labelSize = Math.Max(8, item.FontSize * 0.55);
            double labelH = labelSize * 1.35;
            DrawText(dc, label, item, new Rect(rect.X, rect.Y, rect.Width, labelH), item.LabelColor, labelSize, item.Align, wrap: false, bold: false);
            DrawText(dc, value, item, new Rect(rect.X, rect.Y + labelH, rect.Width, rect.Height - labelH), valueColor, item.FontSize, item.Align, wrap: false);
        }
        else
        {
            // Einzeilig: Beschriftung links, Wert rechts.
            DrawText(dc, label, item, rect, item.LabelColor, item.FontSize * 0.8, PanelAlign.Left, wrap: false, bold: false);
            DrawText(dc, value, item, rect, valueColor, item.FontSize, PanelAlign.Right, wrap: false);
        }
    }

    private void DrawBar(DrawingContext dc, PanelItem item, Rect rect)
    {
        var reading = Reading(item);
        double frac = PanelLogic.Fraction(item, reading);
        var fill = PanelLogic.ColorFor(item, reading, item.AccentColor);
        bool text = (item.ShowLabel || item.ShowValue) && !item.Vertical;
        Rect bar = rect;
        if (text)
        {
            double textH = Math.Min(rect.Height * 0.6, item.FontSize * 1.35);
            var row = new Rect(rect.X, rect.Y, rect.Width, textH);
            if (item.ShowLabel) DrawText(dc, PanelLogic.LabelText(item, reading), item, row, item.LabelColor, item.FontSize, PanelAlign.Left, wrap: false, bold: false);
            if (item.ShowValue) DrawText(dc, PanelLogic.ValueText(item, reading), item, row, PanelLogic.ColorFor(item, reading, item.Color), item.FontSize, PanelAlign.Right, wrap: false);
            bar = new Rect(rect.X, rect.Y + textH + 2, rect.Width, Math.Max(2, rect.Height - textH - 2));
        }
        else if (item.Vertical && item.ShowValue)
        {
            double textH = item.FontSize * 1.35;
            DrawText(dc, PanelLogic.ValueText(item, reading), item, new Rect(rect.X - 20, rect.Y, rect.Width + 40, textH), PanelLogic.ColorFor(item, reading, item.Color), item.FontSize, PanelAlign.Center, wrap: false);
            bar = new Rect(rect.X, rect.Y + textH + 2, rect.Width, Math.Max(2, rect.Height - textH - 2));
        }
        if (double.IsNaN(frac)) frac = 0;

        double radius = Math.Min(item.CornerRadius, Math.Min(bar.Width, bar.Height) / 2);
        if (item.Segments > 1)
        {
            int n = item.Segments;
            int lit = (int)Math.Round(frac * n);
            double gap = Math.Max(1, (item.Vertical ? bar.Height : bar.Width) / n * 0.18);
            double seg = ((item.Vertical ? bar.Height : bar.Width) - gap * (n - 1)) / n;
            for (int i = 0; i < n; i++)
            {
                var r = item.Vertical
                    ? new Rect(bar.X, bar.Bottom - (i + 1) * seg - i * gap, bar.Width, seg)
                    : new Rect(bar.X + i * (seg + gap), bar.Y, seg, bar.Height);
                dc.DrawRoundedRectangle(i < lit ? Brush(fill) : Brush(item.BackColor, item.BackOpacity), null, r, radius, radius);
            }
            return;
        }
        dc.DrawRoundedRectangle(Brush(item.BackColor, item.BackOpacity), null, bar, radius, radius);
        if (frac <= 0) return;
        var filled = item.Vertical
            ? new Rect(bar.X, bar.Bottom - bar.Height * frac, bar.Width, bar.Height * frac)
            : new Rect(bar.X, bar.Y, bar.Width * frac, bar.Height);
        dc.PushClip(new RectangleGeometry(filled));
        dc.DrawRoundedRectangle(Brush(fill), null, bar, radius, radius);
        dc.Pop();
    }

    private void DrawGauge(DrawingContext dc, PanelItem item, Rect rect)
    {
        var reading = Reading(item);
        double frac = PanelLogic.Fraction(item, reading);
        double sweep = item.GaugeSweep, t = item.Thickness;
        double half = sweep / 2;
        // Unterkante des Bogens relativ zur Mitte (bei > 180° reicht der Bogen unter die Mitte).
        double below = Math.Max(0, -Math.Cos(half * Math.PI / 180));
        bool full = sweep >= 359.5;
        if (full) below = 1;
        double r = Math.Max(4, Math.Min((rect.Width - t) / 2, (rect.Height - t) / (1 + Math.Max(below, 0.25))));
        var center = new Point(rect.X + rect.Width / 2, rect.Y + t / 2 + r);

        var trackPen = new Pen(Brush(item.BackColor, item.BackOpacity), t) { StartLineCap = PenLineCap.Round, EndLineCap = PenLineCap.Round };
        dc.DrawGeometry(null, trackPen, Arc(center, r, -half, half));
        if (!double.IsNaN(frac) && frac > 0.002)
        {
            var color = PanelLogic.ColorFor(item, reading, item.AccentColor);
            var pen = new Pen(Brush(color), t) { StartLineCap = PenLineCap.Round, EndLineCap = PenLineCap.Round };
            dc.DrawGeometry(null, pen, Arc(center, r, -half, -half + sweep * frac));
        }

        if (item.ShowValue)
        {
            double size = Math.Min(item.FontSize, r * 0.6);
            var valueRect = new Rect(center.X - r, center.Y - size * 0.75, 2 * r, size * 1.5);
            DrawText(dc, PanelLogic.ValueText(item, reading), item, valueRect, PanelLogic.ColorFor(item, reading, item.Color), size, PanelAlign.Center, wrap: false);
        }
        if (item.ShowLabel)
        {
            double size = Math.Max(8, Math.Min(item.FontSize * 0.42, r * 0.3));
            double y = item.ShowValue ? center.Y + item.FontSize * 0.6 : center.Y - size * 0.7;
            DrawText(dc, PanelLogic.LabelText(item, reading), item, new Rect(center.X - r, y, 2 * r, size * 1.4), item.LabelColor, size, PanelAlign.Center, wrap: false, bold: false);
        }
    }

    /// <summary>Bogen um <paramref name="center"/>, Winkel in Grad, 0° = oben, im Uhrzeigersinn.</summary>
    private static Geometry Arc(Point center, double r, double fromDeg, double toDeg)
    {
        if (toDeg - fromDeg >= 359.9)
            return new EllipseGeometry(center, r, r);
        Point P(double deg) => new(center.X + r * Math.Sin(deg * Math.PI / 180), center.Y - r * Math.Cos(deg * Math.PI / 180));
        var g = new StreamGeometry();
        using (var ctx = g.Open())
        {
            ctx.BeginFigure(P(fromDeg), false, false);
            ctx.ArcTo(P(toDeg), new Size(r, r), 0, toDeg - fromDeg > 180, SweepDirection.Clockwise, true, false);
        }
        g.Freeze();
        return g;
    }

    private void DrawGraph(DrawingContext dc, PanelItem item, Rect rect)
    {
        var reading = Reading(item);
        double radius = Math.Min(item.CornerRadius, Math.Min(rect.Width, rect.Height) / 2);
        dc.DrawRoundedRectangle(Brush(item.BackColor, item.BackOpacity), null, rect, radius, radius);
        dc.PushClip(new RectangleGeometry(rect, radius, radius));

        int samples = Math.Max(2, (int)Math.Round(item.GraphSeconds * 1000.0 / Math.Max(1, _hub.IntervalMs)));
        var history = _hub.GetHistory(reading?.Id, samples);
        var (min, max) = PanelLogic.GraphRange(item, reading, history);

        if (item.GraphGrid)
        {
            var gridPen = new Pen(Brush(item.Color, 0.08), 1);
            for (int i = 1; i < 4; i++)
            {
                double y = Math.Round(rect.Y + rect.Height * i / 4) + 0.5;
                dc.DrawLine(gridPen, new Point(rect.X, y), new Point(rect.Right, y));
            }
        }

        if (history.Length >= 2)
        {
            double step = rect.Width / (samples - 1);
            double x0 = rect.Right - (history.Length - 1) * step;
            var color = PanelLogic.ColorFor(item, reading, item.AccentColor);
            var line = new StreamGeometry();
            var area = new StreamGeometry();
            using (var lc = line.Open())
            using (var ac = area.Open())
            {
                bool open = false;
                Point first = default, last = default;
                for (int i = 0; i < history.Length; i++)
                {
                    double v = history[i];
                    if (!double.IsFinite(v))
                    {
                        if (open) { ac.LineTo(new Point(last.X, rect.Bottom), false, false); open = false; }
                        continue;
                    }
                    double f = Math.Clamp((v - min) / (max - min), 0, 1);
                    var p = new Point(x0 + i * step, rect.Bottom - 2 - f * (rect.Height - 4));
                    if (!open)
                    {
                        lc.BeginFigure(p, false, false);
                        ac.BeginFigure(new Point(p.X, rect.Bottom), true, true);
                        ac.LineTo(p, false, false);
                        first = p;
                        open = true;
                    }
                    else
                    {
                        lc.LineTo(p, true, true);
                        ac.LineTo(p, false, false);
                    }
                    last = p;
                }
                if (open) ac.LineTo(new Point(last.X, rect.Bottom), false, false);
                _ = first;
            }
            line.Freeze();
            area.Freeze();
            if (item.GraphFill) dc.DrawGeometry(Brush(color, 0.22), null, area);
            dc.DrawGeometry(null, new Pen(Brush(color), item.Thickness) { LineJoin = PenLineJoin.Round }, line);
        }

        double textSize = item.FontSize;
        var top = new Rect(rect.X + 8, rect.Y + 4, rect.Width - 16, textSize * 1.4);
        if (item.ShowLabel) DrawText(dc, PanelLogic.LabelText(item, reading), item, top, item.LabelColor, textSize, PanelAlign.Left, wrap: false, bold: false);
        if (item.ShowValue) DrawText(dc, PanelLogic.ValueText(item, reading), item, top, PanelLogic.ColorFor(item, reading, item.Color), textSize, PanelAlign.Right, wrap: false);
        dc.Pop();
    }

    private void DrawAnimation(DrawingContext dc, PanelItem item, Rect rect, double dt)
    {
        int cols = Math.Clamp((int)Math.Ceiling(rect.Width / item.CellSize), 1, 512);
        int rows = Math.Clamp((int)Math.Ceiling(rect.Height / item.CellSize), 1, 512);
        if (!_animations.TryGetValue(item.Id, out var state)) _animations[item.Id] = state = new AnimationState();

        var descriptor = EffectCatalog.Get(item.EffectId);
        string key = descriptor.Id + "|" + string.Join(",", item.EffectColors.Select(c => c.ToHex())) + "|" + RuntimeHelpers(descriptor);
        int version = Volatile.Read(ref _catalogVersion);
        if (state.Effect is null || state.Key != key || state.CatalogVersion != version)
        {
            var colors = item.EffectColors.Count == descriptor.ColorSlots.Count ? item.EffectColors : descriptor.DefaultColors;
            state.Effect = descriptor.Create(colors);
            state.Key = key;
            state.CatalogVersion = version;
        }
        if (state.Bitmap is null || state.Bitmap.PixelWidth != cols || state.Bitmap.PixelHeight != rows)
        {
            state.Bitmap = new WriteableBitmap(cols, rows, 96, 96, PixelFormats.Bgr32, null);
            state.Pixels = new int[cols * rows];
        }
        state.EffectSeconds += dt * item.SpeedPercent / 100.0;
        var elapsed = TimeSpan.FromSeconds(state.EffectSeconds);
        var effect = state.Effect;
        var px = state.Pixels;
        for (int y = 0; y < rows; y++)
        for (int x = 0; x < cols; x++)
        {
            var c = effect.Sample(x + 0.5, y + 0.5, cols, rows, elapsed);
            px[y * cols + x] = (c.R << 16) | (c.G << 8) | c.B;
        }
        state.Bitmap.WritePixels(new Int32Rect(0, 0, cols, rows), px, cols * 4, 0);

        double radius = Math.Min(item.CornerRadius, Math.Min(rect.Width, rect.Height) / 2);
        bool clip = radius > 0;
        if (clip) dc.PushClip(new RectangleGeometry(rect, radius, radius));
        dc.DrawImage(state.Bitmap, rect);
        if (clip) dc.Pop();
        if (item.BorderWidth > 0)
            dc.DrawRoundedRectangle(null, new Pen(Brush(item.BorderColor), item.BorderWidth), rect, radius, radius);
    }

    /// <summary>Eigene Effekte bekommen bei jeder Dateiänderung einen neuen Descriptor — die Instanz dann neu erzeugen.</summary>
    private static string RuntimeHelpers(EffectDescriptor d) => System.Runtime.CompilerServices.RuntimeHelpers.GetHashCode(d).ToString(CultureInfo.InvariantCulture);

    private void DrawImage(DrawingContext dc, PanelItem item, Rect rect, TimeSpan now)
    {
        var image = Image(item.ImagePath);
        double radius = Math.Min(item.CornerRadius, Math.Min(rect.Width, rect.Height) / 2);
        if (image is null)
        {
            dc.DrawRoundedRectangle(Brush(item.BackColor, 0.6), new Pen(Brush(item.BorderColor), 1), rect, radius, radius);
            DrawText(dc, string.IsNullOrWhiteSpace(item.ImagePath) ? "🖼 Bild wählen" : "🖼 Bild fehlt", item, rect, item.LabelColor, 14, PanelAlign.Center, wrap: true, bold: false);
            return;
        }
        dc.PushClip(new RectangleGeometry(rect, radius, radius));
        DrawStretched(dc, image.FrameAt(now), rect, item.Stretch);
        dc.Pop();
    }

    private static void DrawStretched(DrawingContext dc, BitmapSource bmp, Rect rect, PanelStretch stretch)
    {
        double iw = bmp.Width, ih = bmp.Height;
        if (iw <= 0 || ih <= 0) return;
        Rect target = rect;
        if (stretch != PanelStretch.Fill)
        {
            double s = stretch == PanelStretch.Uniform ? Math.Min(rect.Width / iw, rect.Height / ih) : Math.Max(rect.Width / iw, rect.Height / ih);
            double w = iw * s, h = ih * s;
            target = new Rect(rect.X + (rect.Width - w) / 2, rect.Y + (rect.Height - h) / 2, w, h);
        }
        dc.DrawImage(bmp, target);
    }

    private AnimatedImage? Image(string? path)
    {
        var full = _resolveAsset(path);
        if (full is null) return null;
        if (_images.TryGetValue(full, out var cached))
        {
            // Nicht ladbare Dateien nur alle 10 s erneut versuchen.
            if (cached.Image is not null || DateTime.UtcNow - cached.Loaded < TimeSpan.FromSeconds(10)) return cached.Image;
        }
        AnimatedImage? img = null;
        try { img = AnimatedImage.Load(full); }
        catch { /* unlesbar → Platzhalter */ }
        _images[full] = (img, DateTime.UtcNow);
        return img;
    }

    // ===================== Text, Farben =====================

    private void DrawText(DrawingContext dc, string text, PanelItem item, Rect rect, RgbColor color, double size, PanelAlign align, bool wrap, bool? bold = null)
    {
        if (string.IsNullOrEmpty(text) || rect.Width < 1 || rect.Height < 1) return;
        var ft = new FormattedText(text, CultureInfo.CurrentCulture, FlowDirection.LeftToRight, Typeface(item.FontFamily, bold ?? item.Bold),
            Math.Max(1, size), Brush(color), PixelsPerDip)
        {
            MaxTextWidth = rect.Width,
            Trimming = TextTrimming.CharacterEllipsis,
            TextAlignment = align switch { PanelAlign.Center => TextAlignment.Center, PanelAlign.Right => TextAlignment.Right, _ => TextAlignment.Left },
        };
        if (!wrap) ft.MaxLineCount = 1;
        else ft.MaxTextHeight = Math.Max(size * 1.3, rect.Height);
        double y = rect.Y + Math.Max(0, (rect.Height - ft.Height) / 2);
        dc.DrawText(ft, new Point(rect.X, y));
    }

    private Typeface Typeface(string family, bool bold)
    {
        string key = family + (bold ? "|b" : "");
        if (!_typefaces.TryGetValue(key, out var tf))
            _typefaces[key] = tf = new Typeface(new FontFamily(family), FontStyles.Normal, bold ? FontWeights.Bold : FontWeights.Normal, FontStretches.Normal);
        return tf;
    }

    private SolidColorBrush Brush(RgbColor c, double opacity = 1)
    {
        opacity = Math.Round(Math.Clamp(opacity, 0, 1), 3);
        if (!_brushes.TryGetValue((c, opacity), out var b))
        {
            if (_brushes.Count > 2000) _brushes.Clear();
            b = new SolidColorBrush(Color.FromArgb((byte)Math.Round(opacity * 255), c.R, c.G, c.B));
            b.Freeze();
            _brushes[(c, opacity)] = b;
        }
        return b;
    }
}

/// <summary>Zeichenfläche für ein Panel in Panel-Pixeln (1:1); skaliert wird außen (Viewbox bzw. Fenstergröße).</summary>
public sealed class SensorPanelSurface : FrameworkElement
{
    private readonly SensorPanelRenderer _renderer;
    private SensorPanelDocument? _document;

    public SensorPanelSurface(SensorPanelRenderer renderer)
    {
        _renderer = renderer;
        RenderOptions.SetBitmapScalingMode(this, BitmapScalingMode.Linear);
        TextOptions.SetTextFormattingMode(this, TextFormattingMode.Ideal);
        TextOptions.SetTextRenderingMode(this, TextRenderingMode.Grayscale);
    }

    public SensorPanelRenderer Renderer => _renderer;

    public SensorPanelDocument? Document
    {
        get => _document;
        set
        {
            _document = value;
            Width = value?.Width ?? 0;
            Height = value?.Height ?? 0;
            if (value is not null) _renderer.Prune(value);
            InvalidateVisual();
        }
    }

    protected override void OnRender(DrawingContext dc)
    {
        if (_document is null) return;
        _renderer.PixelsPerDip = VisualTreeHelper.GetDpi(this).PixelsPerDip;
        _renderer.Render(dc, _document);
    }
}
