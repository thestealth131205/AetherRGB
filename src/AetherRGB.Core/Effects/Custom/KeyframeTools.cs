namespace AetherRGB.Core.Effects.Custom;

/// <summary>Keyframes eines <see cref="AnimatedValue"/> im Editor setzen, verschieben und löschen.</summary>
public static class KeyframeTools
{
    /// <summary>Zwei Keyframes, die näher als das beieinander liegen, gelten als derselbe Zeitpunkt.</summary>
    public const double TimeTolerance = 0.005;

    /// <summary>Index des Keyframes bei <paramref name="time"/>, sonst -1.</summary>
    public static int IndexAt(AnimatedValue value, double time)
    {
        for (int i = 0; i < value.Keyframes.Count; i++)
            if (Math.Abs(value.Keyframes[i].Time - time) <= TimeTolerance) return i;
        return -1;
    }

    /// <summary>Wert ändern: Ist der Wert animiert, wird der Keyframe bei <paramref name="time"/> gesetzt
    /// bzw. angelegt („Auto-Key“) — sonst nur der feste Wert.</summary>
    public static void SetValue(AnimatedValue value, double time, double newValue)
    {
        if (!value.IsAnimated)
        {
            value.Value = newValue;
            return;
        }
        SetKeyframe(value, time, newValue);
    }

    /// <summary>Keyframe bei <paramref name="time"/> setzen (oder dessen Wert ändern). Liefert seinen Index.</summary>
    public static int SetKeyframe(AnimatedValue value, double time, double newValue, Easing? easing = null)
    {
        time = Math.Max(0, time);
        int i = IndexAt(value, time);
        if (i >= 0)
        {
            value.Keyframes[i].Value = newValue;
            if (easing is { } e) value.Keyframes[i].Easing = e;
            return i;
        }
        if (value.Keyframes.Count >= CustomEffectSerializer.MaxKeyframesPerValue)
            throw new InvalidOperationException($"Höchstens {CustomEffectSerializer.MaxKeyframesPerValue} Keyframes pro Wert.");
        // Easing vom vorigen Keyframe übernehmen, damit eingefügte Keyframes den Verlauf nicht ändern.
        var previous = value.Keyframes.LastOrDefault(k => k.Time < time);
        var key = new Keyframe(time, newValue, easing ?? previous?.Easing ?? Easing.EaseInOut);
        value.Keyframes.Add(key);
        value.SortKeyframes();
        return value.Keyframes.IndexOf(key);
    }

    /// <summary>Keyframe bei <paramref name="time"/> an/aus: fehlt er, wird er mit dem aktuellen Wert angelegt
    /// (die Animation sieht danach gleich aus), sonst gelöscht. true = jetzt vorhanden.</summary>
    public static bool Toggle(AnimatedValue value, double time)
    {
        int i = IndexAt(value, time);
        if (i >= 0)
        {
            Remove(value, i);
            return false;
        }
        SetKeyframe(value, time, value.Evaluate(time));
        return true;
    }

    /// <summary>Keyframe löschen. Der letzte verbliebene Keyframe wird zum festen Wert.</summary>
    public static void Remove(AnimatedValue value, int index)
    {
        if (index < 0 || index >= value.Keyframes.Count) return;
        if (value.Keyframes.Count == 1) value.Value = value.Keyframes[0].Value;
        value.Keyframes.RemoveAt(index);
    }

    /// <summary>Keyframe auf einen anderen Zeitpunkt schieben. Liegt dort schon einer, wird er ersetzt.
    /// Liefert den neuen Index.</summary>
    public static int Move(AnimatedValue value, int index, double newTime)
    {
        if (index < 0 || index >= value.Keyframes.Count) return index;
        var key = value.Keyframes[index];
        newTime = Math.Max(0, newTime);
        value.Keyframes.RemoveAll(k => k != key && Math.Abs(k.Time - newTime) <= TimeTolerance);
        key.Time = newTime;
        value.SortKeyframes();
        return value.Keyframes.IndexOf(key);
    }

    /// <summary>Alle Keyframes entfernen; der Wert bleibt so, wie er zum Zeitpunkt <paramref name="time"/> war.</summary>
    public static void ClearAnimation(AnimatedValue value, double time)
    {
        if (!value.IsAnimated) return;
        value.Value = value.Evaluate(time);
        value.Keyframes.Clear();
    }
}

/// <summary>Stand von Ebenen, Animationen und Keyframes für Rückgängig im Editor — ohne die Pixel der
/// gemalten Bilder zu kopieren: die Bild-Objekte werden nur referenziert, ihr Inhalt hat einen eigenen
/// Rückgängig-Verlauf pro Bild. So kostet ein Schritt wenige KB, auch bei vielen Bildern.</summary>
public sealed class DocumentStructureSnapshot
{
    private readonly string _json;
    private readonly List<List<PaintFrame>?> _frames;

    public string Description { get; }

    private DocumentStructureSnapshot(string json, List<List<PaintFrame>?> frames, string description)
    {
        _json = json;
        _frames = frames;
        Description = description;
    }

    public static DocumentStructureSnapshot Capture(CustomEffectDocument document, string description)
    {
        // Bilder kurz herausnehmen, damit sie nicht mit serialisiert werden, und danach zurücklegen.
        var frames = document.Layers.Select(l => l is PaintLayer p ? p.Frames : null).ToList();
        try
        {
            foreach (var p in document.Layers.OfType<PaintLayer>()) p.Frames = new List<PaintFrame>();
            return new DocumentStructureSnapshot(CustomEffectSerializer.Serialize(document), frames.Select(f => f?.ToList()).ToList(), description);
        }
        finally
        {
            for (int i = 0; i < document.Layers.Count; i++)
                if (document.Layers[i] is PaintLayer p && frames[i] is { } list) p.Frames = list;
        }
    }

    /// <summary>Legt den gespeicherten Stand in <paramref name="document"/> zurück (gleiches Objekt, damit
    /// Verweise darauf gültig bleiben). Name und ID bleiben, wie sie sind.</summary>
    public void Restore(CustomEffectDocument document)
    {
        var restored = CustomEffectSerializer.Deserialize(_json);
        for (int i = 0; i < restored.Layers.Count && i < _frames.Count; i++)
            if (restored.Layers[i] is PaintLayer p && _frames[i] is { } list) p.Frames = list.ToList();
        document.Layers = restored.Layers;
        document.DurationSeconds = restored.DurationSeconds;
        document.AutoDuration = restored.AutoDuration;
        document.Loop = restored.Loop;
    }
}
