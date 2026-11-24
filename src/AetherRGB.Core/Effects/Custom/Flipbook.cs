namespace AetherRGB.Core.Effects.Custom;

/// <summary>Bilder (Frames) einer <see cref="PaintLayer"/> als Daumenkino bearbeiten: anlegen, duplizieren,
/// löschen, verschieben und die Zeitpunkte der Bilder auf der Zeitleiste bestimmen. Hält die Länge der
/// Zeitleiste des Dokuments passend zur Summe der Bilddauern, damit das Daumenkino ohne Sprung in Schleife läuft.</summary>
public static class Flipbook
{
    /// <summary>Kleiner Versatz in ein Bild hinein, damit Rundungsfehler beim Aufsummieren der Dauern nicht
    /// beim vorigen Bild landen.</summary>
    private const double Epsilon = 1e-6;

    public static double FrameDuration(PaintLayer layer, int index) =>
        layer.Frames[index].DurationSeconds ?? layer.FrameDurationSeconds;

    public static double TotalDuration(PaintLayer layer)
    {
        double total = 0;
        for (int i = 0; i < layer.Frames.Count; i++) total += FrameDuration(layer, i);
        return total;
    }

    /// <summary>Zeitpunkt auf der Zeitleiste, an dem Bild <paramref name="index"/> ohne Überblenden voll zu sehen ist.</summary>
    public static double FrameStartTime(PaintLayer layer, int index)
    {
        double t = 0;
        for (int i = 0; i < index && i < layer.Frames.Count; i++) t += FrameDuration(layer, i);
        return t + Epsilon;
    }

    /// <summary>Neues Bild hinter <paramref name="afterIndex"/>: leer oder als Kopie des Bildes dort. Liefert den neuen Index.</summary>
    public static int Insert(CustomEffectDocument document, PaintLayer layer, int afterIndex, bool copy)
    {
        if (layer.Frames.Count >= CustomEffectSerializer.MaxFramesPerLayer)
            throw new InvalidOperationException($"Höchstens {CustomEffectSerializer.MaxFramesPerLayer} Bilder pro Ebene.");
        afterIndex = Math.Clamp(afterIndex, -1, layer.Frames.Count - 1);
        PaintFrame frame;
        if (copy && afterIndex >= 0)
        {
            var source = layer.Frames[afterIndex];
            frame = new PaintFrame { DurationSeconds = source.DurationSeconds, Pixels = (byte[])source.Pixels.Clone() };
        }
        else frame = PaintFrame.CreateEmpty(document.GridWidth, document.GridHeight);
        layer.Frames.Insert(afterIndex + 1, frame);
        SyncDuration(document);
        return afterIndex + 1;
    }

    /// <summary>Bild löschen. Das letzte verbleibende Bild wird nur geleert, eine Ebene ohne Bild gibt es nicht.
    /// Liefert den Index des danach gewählten Bildes.</summary>
    public static int Delete(CustomEffectDocument document, PaintLayer layer, int index)
    {
        if (index < 0 || index >= layer.Frames.Count) return Math.Clamp(index, 0, Math.Max(0, layer.Frames.Count - 1));
        if (layer.Frames.Count == 1)
        {
            Array.Clear(layer.Frames[0].Pixels);
            return 0;
        }
        layer.Frames.RemoveAt(index);
        SyncDuration(document);
        return Math.Min(index, layer.Frames.Count - 1);
    }

    /// <summary>Bild von <paramref name="from"/> an Position <paramref name="to"/> verschieben. Liefert die neue Position.</summary>
    public static int Move(CustomEffectDocument document, PaintLayer layer, int from, int to)
    {
        if (from < 0 || from >= layer.Frames.Count) return from;
        to = Math.Clamp(to, 0, layer.Frames.Count - 1);
        if (to == from) return from;
        var frame = layer.Frames[from];
        layer.Frames.RemoveAt(from);
        layer.Frames.Insert(to, frame);
        SyncDuration(document);
        return to;
    }

    public static bool IsEmpty(PaintFrame frame)
    {
        var px = frame.Pixels;
        for (int i = 3; i < px.Length; i += 4)
            if (px[i] != 0) return false;
        return true;
    }

    /// <summary>Besteht das Dokument nur aus Mal-Ebenen, ist die Zeitleiste genau so lang wie das längste
    /// Daumenkino — sonst würde <see cref="CustomEffectRenderer.MapTime"/> das Daumenkino mitten im Bild
    /// abschneiden. Mit Form-Ebenen (eigene Keyframes) bleibt die Länge, wie sie ist.</summary>
    public static void SyncDuration(CustomEffectDocument document)
    {
        if (document.Layers.Any(l => l is not PaintLayer)) return;
        double longest = document.Layers.OfType<PaintLayer>().Max(TotalDuration);
        if (longest <= 0 || !double.IsFinite(longest)) return;
        document.DurationSeconds = Math.Clamp(longest, CustomEffectSerializer.MinDurationSeconds, CustomEffectSerializer.MaxDurationSeconds);
    }
}
