using System.IO;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;

namespace AetherRGB.App.Panels;

/// <summary>Bild fürs Sensor-Panel. Animierte GIFs werden beim Laden einmal zu vollständigen Einzelbildern zusammengesetzt
/// (GIF-Frames sind oft nur Ausschnitte mit Versatz und Entsorgungsregel), danach ist das Abspielen nur noch Auswahl nach Zeit.</summary>
internal sealed class AnimatedImage
{
    private const int MaxFrames = 300, MaxSide = 2048;

    public IReadOnlyList<BitmapSource> Frames { get; }
    /// <summary>Endzeit jedes Bildes in Millisekunden ab Start.</summary>
    private readonly double[] _ends;
    public double DurationMs => _ends.Length == 0 ? 0 : _ends[^1];

    private AnimatedImage(List<BitmapSource> frames, List<double> delays)
    {
        Frames = frames;
        _ends = new double[delays.Count];
        double t = 0;
        for (int i = 0; i < delays.Count; i++) { t += delays[i]; _ends[i] = t; }
    }

    public BitmapSource FrameAt(TimeSpan elapsed)
    {
        if (Frames.Count == 1 || DurationMs <= 0) return Frames[0];
        double t = elapsed.TotalMilliseconds % DurationMs;
        int i = Array.BinarySearch(_ends, t);
        if (i < 0) i = ~i;
        return Frames[Math.Min(i, Frames.Count - 1)];
    }

    public static AnimatedImage Load(string path)
    {
        using var stream = File.OpenRead(path);
        if (!path.EndsWith(".gif", StringComparison.OrdinalIgnoreCase))
        {
            var single = BitmapFrame.Create(stream, BitmapCreateOptions.None, BitmapCacheOption.OnLoad);
            single.Freeze();
            return new AnimatedImage(new List<BitmapSource> { single }, new List<double> { 0 });
        }

        var decoder = new GifBitmapDecoder(stream, BitmapCreateOptions.PreservePixelFormat, BitmapCacheOption.OnLoad);
        int width = Meta<ushort>(decoder.Metadata, "/logscrdesc/Width", 0);
        int height = Meta<ushort>(decoder.Metadata, "/logscrdesc/Height", 0);
        if (width <= 0 || height <= 0) { width = decoder.Frames[0].PixelWidth; height = decoder.Frames[0].PixelHeight; }
        width = Math.Min(width, MaxSide);
        height = Math.Min(height, MaxSide);

        var frames = new List<BitmapSource>();
        var delays = new List<double>();
        BitmapSource? baseImage = null;
        foreach (var frame in decoder.Frames.Take(MaxFrames))
        {
            var meta = frame.Metadata as BitmapMetadata;
            int left = Meta<ushort>(meta, "/imgdesc/Left", 0), top = Meta<ushort>(meta, "/imgdesc/Top", 0);
            int delay = Meta<ushort>(meta, "/grctlext/Delay", 10);
            int disposal = Meta<byte>(meta, "/grctlext/Disposal", 0);

            var composed = Compose(baseImage, frame, left, top, width, height, clearRect: null);
            frames.Add(composed);
            delays.Add(Math.Max(20, delay * 10)); // Browser-Verhalten: < 20 ms gilt als 100 ms bzw. zu schnell → mind. 20 ms

            baseImage = disposal switch
            {
                2 => Compose(baseImage, null, 0, 0, width, height, new Rect(left, top, frame.PixelWidth, frame.PixelHeight)), // Hintergrund
                3 => baseImage, // vorheriges Bild
                _ => composed,
            };
        }
        return new AnimatedImage(frames, delays);
    }

    private static BitmapSource Compose(BitmapSource? under, BitmapSource? frame, int left, int top, int width, int height, Rect? clearRect)
    {
        var visual = new DrawingVisual();
        using (var dc = visual.RenderOpen())
        {
            if (under is not null)
            {
                if (clearRect is { } clear)
                {
                    var keep = new CombinedGeometry(GeometryCombineMode.Exclude, new RectangleGeometry(new Rect(0, 0, width, height)), new RectangleGeometry(clear));
                    dc.PushClip(keep);
                    dc.DrawImage(under, new Rect(0, 0, width, height));
                    dc.Pop();
                }
                else dc.DrawImage(under, new Rect(0, 0, width, height));
            }
            if (frame is not null) dc.DrawImage(frame, new Rect(left, top, frame.PixelWidth, frame.PixelHeight));
        }
        var target = new RenderTargetBitmap(width, height, 96, 96, PixelFormats.Pbgra32);
        target.Render(visual);
        target.Freeze();
        return target;
    }

    private static T Meta<T>(BitmapMetadata? meta, string query, T fallback)
    {
        try { return meta?.GetQuery(query) is T v ? v : fallback; }
        catch { return fallback; }
    }
}
