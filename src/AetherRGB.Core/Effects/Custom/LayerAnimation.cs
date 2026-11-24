namespace AetherRGB.Core.Effects.Custom;

/// <summary>Art einer Animations-Vorlage (Bewegungsvorlage) auf einer Ebene.</summary>
public enum AnimationKind
{
    /// <summary>Wandern in eine Richtung (Winkel), optional umlaufend.</summary>
    Move,
    /// <summary>Ausdehnender Kreis: Größe wächst von <see cref="LayerAnimation.StartSize"/> auf
    /// <see cref="LayerAnimation.EndSize"/>, optional ausblendend und als mehrere Wellen.</summary>
    Expand,
    /// <summary>Zusammenziehen: wie <see cref="Expand"/>, nur von außen zur Mitte.</summary>
    Contract,
    /// <summary>Größe schwingt zwischen <see cref="LayerAnimation.StartSize"/> und <see cref="LayerAnimation.EndSize"/>.</summary>
    Pulse,
    /// <summary>Drehen um den Mittelpunkt.</summary>
    Rotate,
    /// <summary>Atmen / Blinken: Deckkraft schwingt weich oder hart.</summary>
    Breathe,
    /// <summary>Farbe wechseln: Farbton dreht sich.</summary>
    HueCycle
}

/// <summary>Wie oft eine Animation läuft.</summary>
public enum AnimationRepeat
{
    /// <summary>Immer wieder von vorn.</summary>
    Loop,
    /// <summary>Hin und zurück.</summary>
    PingPong,
    /// <summary>Einmal nach der Verzögerung, danach bleibt der Endzustand.</summary>
    Once
}

/// <summary>Kurvenform beim Atmen/Blinken.</summary>
public enum AnimationWave
{
    /// <summary>Weich (Sinus) — „Atmen“.</summary>
    Sine,
    /// <summary>Linear auf und ab.</summary>
    Triangle,
    /// <summary>Hart an/aus mit Tastverhältnis — „Blinken“.</summary>
    Square
}

/// <summary>Eine Animations-Vorlage auf einer Ebene. Statt Bild für Bild zu malen, beschreibt sie eine
/// Bewegung mit wenigen Werten (Richtung, Dauer, Größe …). Mehrere Animationen auf einer Ebene werden
/// kombiniert (z. B. Wandern + Farbe wechseln) und wirken zusätzlich zu den Keyframes der Ebene.
/// Nicht jede Eigenschaft gilt für jede Art — die nicht benutzten werden ignoriert und mitgespeichert,
/// damit ein Wechsel der Art die Einstellungen nicht verliert.</summary>
public sealed class LayerAnimation
{
    public AnimationKind Kind { get; set; } = AnimationKind.Move;
    public bool Enabled { get; set; } = true;

    /// <summary>Länge eines Durchlaufs in Sekunden (bei 100 % Tempo).</summary>
    public double DurationSeconds { get; set; } = 2;

    /// <summary>Bei Schleife/Hin und zurück ein Versatz im Durchlauf (z. B. um zwei Ebenen gegeneinander
    /// zu versetzen), bei „einmal“ die Wartezeit bis zum Start.</summary>
    public double DelaySeconds { get; set; }

    public AnimationRepeat Repeat { get; set; } = AnimationRepeat.Loop;
    public Easing Easing { get; set; } = Easing.Linear;

    // ---------- Wandern ----------

    /// <summary>Richtung in Grad: 0 = nach rechts, 90 = nach unten, 180 = nach links, 270 = nach oben.</summary>
    public double AngleDegrees { get; set; }

    /// <summary>Strecke pro Durchlauf in Anteilen der Fläche in Bewegungsrichtung (1 = einmal über die Fläche).</summary>
    public double Distance { get; set; } = 1;

    /// <summary>Was an einer Seite hinausläuft, kommt an der anderen wieder herein.</summary>
    public bool Wrap { get; set; } = true;

    // ---------- Größe (Ausdehnen, Zusammenziehen, Pulsieren) ----------

    /// <summary>Größe am Anfang des Durchlaufs (1 = Originalgröße der Ebene/Form).</summary>
    public double StartSize { get; set; }

    /// <summary>Größe am Ende des Durchlaufs.</summary>
    public double EndSize { get; set; } = 1;

    /// <summary>Ausdehnen/Zusammenziehen: zum Ende hin ausblenden, damit die Schleife ohne Sprung neu beginnt.</summary>
    public bool FadeOut { get; set; } = true;

    /// <summary>Ausdehnen/Zusammenziehen: so viele Wellen gleichzeitig, gleichmäßig über den Durchlauf verteilt.</summary>
    public int Waves { get; set; } = 1;

    // ---------- Mittelpunkt (Größe, Drehen) ----------

    /// <summary>Mittelpunkt für Größe/Drehung in 0..1 der Fläche. Bei Form-Ebenen gilt stattdessen der
    /// Mittelpunkt der Form, damit eine Kreis-Welle immer von ihrer Mitte ausgeht.</summary>
    public double CenterX { get; set; } = 0.5;
    public double CenterY { get; set; } = 0.5;

    // ---------- Drehen / Farbe ----------

    /// <summary>Drehen: Grad pro Durchlauf (negativ = gegen den Uhrzeigersinn). Farbe wechseln: Farbton-Grad pro Durchlauf.</summary>
    public double Degrees { get; set; } = 360;

    // ---------- Atmen / Blinken ----------

    public double MinOpacity { get; set; }
    public double MaxOpacity { get; set; } = 1;
    public AnimationWave Wave { get; set; } = AnimationWave.Sine;
    /// <summary>Blinken: Anteil des Durchlaufs, in dem die Ebene an ist (0,05..0,95).</summary>
    public double DutyCycle { get; set; } = 0.5;

    /// <summary>Animationen mit Mittelpunkt (für Anfasser auf der Fläche).</summary>
    public bool HasCenter => Kind is AnimationKind.Expand or AnimationKind.Contract or AnimationKind.Pulse or AnimationKind.Rotate;

    /// <summary>Anzeigename der Art für Listen.</summary>
    public static string KindName(AnimationKind kind) => kind switch
    {
        AnimationKind.Move => "Wandern",
        AnimationKind.Expand => "Ausdehnender Kreis",
        AnimationKind.Contract => "Zusammenziehen",
        AnimationKind.Pulse => "Pulsieren",
        AnimationKind.Rotate => "Drehen",
        AnimationKind.Breathe => "Atmen / Blinken",
        AnimationKind.HueCycle => "Farbe wechseln",
        _ => kind.ToString()
    };

    /// <summary>Kurzbeschreibung mit den wichtigsten Werten, z. B. „Wandern → · 2 s · umlaufend“.</summary>
    public string Describe()
    {
        string repeat = Repeat switch { AnimationRepeat.PingPong => " · hin und zurück", AnimationRepeat.Once => " · einmal", _ => "" };
        string detail = Kind switch
        {
            AnimationKind.Move => $" {DirectionArrow(AngleDegrees)}{(Wrap ? " · umlaufend" : "")}",
            AnimationKind.Expand or AnimationKind.Contract => $" {StartSize * 100:0}→{EndSize * 100:0} %{(Waves > 1 ? $" · {Waves} Wellen" : "")}",
            AnimationKind.Pulse => $" {StartSize * 100:0}–{EndSize * 100:0} %",
            AnimationKind.Rotate => $" {Degrees:0}°",
            AnimationKind.Breathe => Wave == AnimationWave.Square ? " (Blinken)" : "",
            AnimationKind.HueCycle => $" {Degrees:0}°",
            _ => ""
        };
        return $"{KindName(Kind)}{detail} · {DurationSeconds:0.##} s{repeat}";
    }

    /// <summary>Pfeil für die Richtung (nächste der 8 Hauptrichtungen).</summary>
    public static string DirectionArrow(double angle)
    {
        int i = (int)Math.Round(((angle % 360) + 360) % 360 / 45) % 8;
        return new[] { "→", "↘", "↓", "↙", "←", "↖", "↑", "↗" }[i];
    }

    /// <summary>Vorlage mit sinnvollen Startwerten für eine Art.</summary>
    public static LayerAnimation CreatePreset(AnimationKind kind) => kind switch
    {
        AnimationKind.Move => new LayerAnimation { Kind = kind, DurationSeconds = 4, Easing = Easing.Linear, Wrap = true, Distance = 1 },
        AnimationKind.Expand => new LayerAnimation { Kind = kind, DurationSeconds = 2, Easing = Easing.EaseOut, StartSize = 0, EndSize = 1, FadeOut = true },
        AnimationKind.Contract => new LayerAnimation { Kind = kind, DurationSeconds = 2, Easing = Easing.EaseIn, StartSize = 1, EndSize = 0, FadeOut = true },
        AnimationKind.Pulse => new LayerAnimation { Kind = kind, DurationSeconds = 1.5, Easing = Easing.Linear, StartSize = 0.7, EndSize = 1.2 },
        AnimationKind.Rotate => new LayerAnimation { Kind = kind, DurationSeconds = 4, Easing = Easing.Linear, Degrees = 360 },
        AnimationKind.Breathe => new LayerAnimation { Kind = kind, DurationSeconds = 3, Easing = Easing.Linear, MinOpacity = 0.1, MaxOpacity = 1, Wave = AnimationWave.Sine },
        AnimationKind.HueCycle => new LayerAnimation { Kind = kind, DurationSeconds = 6, Easing = Easing.Linear, Degrees = 360 },
        _ => new LayerAnimation { Kind = kind }
    };

    public LayerAnimation Clone() => (LayerAnimation)MemberwiseClone();

    /// <summary>Zahlen auf gültige Bereiche bringen (nach dem Laden und nach Eingaben im Editor).</summary>
    public void Normalize()
    {
        static double Finite(double v, double fallback) => double.IsFinite(v) ? v : fallback;
        DurationSeconds = Math.Clamp(Finite(DurationSeconds, 2), 0.05, CustomEffectSerializer.MaxDurationSeconds);
        DelaySeconds = Math.Clamp(Finite(DelaySeconds, 0), 0, CustomEffectSerializer.MaxDurationSeconds);
        AngleDegrees = Finite(AngleDegrees, 0) % 360;
        if (AngleDegrees < 0) AngleDegrees += 360;
        Distance = Math.Clamp(Finite(Distance, 1), -20, 20);
        StartSize = Math.Clamp(Finite(StartSize, 0), 0, 20);
        EndSize = Math.Clamp(Finite(EndSize, 1), 0, 20);
        Waves = Math.Clamp(Waves, 1, 8);
        CenterX = Math.Clamp(Finite(CenterX, 0.5), -1, 2);
        CenterY = Math.Clamp(Finite(CenterY, 0.5), -1, 2);
        Degrees = Math.Clamp(Finite(Degrees, 360), -3600, 3600);
        MinOpacity = Math.Clamp(Finite(MinOpacity, 0), 0, 1);
        MaxOpacity = Math.Clamp(Finite(MaxOpacity, 1), 0, 1);
        DutyCycle = Math.Clamp(Finite(DutyCycle, 0.5), 0.05, 0.95);
        if (!Enum.IsDefined(Kind)) Kind = AnimationKind.Move;
        if (!Enum.IsDefined(Repeat)) Repeat = AnimationRepeat.Loop;
        if (!Enum.IsDefined(Easing)) Easing = Easing.Linear;
        if (!Enum.IsDefined(Wave)) Wave = AnimationWave.Sine;
    }

    // ---------- Auswertung ----------

    /// <summary>Fortschritt im Durchlauf 0..1 zum Zeitpunkt <paramref name="time"/> (ohne Easing).
    /// <paramref name="phase"/> verschiebt um einen Anteil des Durchlaufs (für mehrere Wellen).</summary>
    public double Progress(double time, double phase = 0)
    {
        double d = Math.Max(0.05, DurationSeconds);
        if (Repeat == AnimationRepeat.Once)
            return Math.Clamp((time - DelaySeconds) / d + phase, 0, 1);
        double cycles = (time - DelaySeconds) / d + phase;
        if (Repeat == AnimationRepeat.PingPong)
        {
            double p = cycles % 2;
            if (p < 0) p += 2;
            return p > 1 ? 2 - p : p;
        }
        double f = cycles - Math.Floor(cycles);
        return f;
    }

    /// <summary>Wie lange es dauert, bis die Animation genau wieder gleich aussieht (für eine nahtlose
    /// Schleife des ganzen Effekts). Bei „einmal“: bis sie fertig ist.</summary>
    public double PeriodSeconds => Repeat switch
    {
        AnimationRepeat.PingPong => 2 * DurationSeconds,
        AnimationRepeat.Once => DelaySeconds + DurationSeconds,
        // Wandern ohne Umlaufen springt am Ende zurück — trotzdem ein Durchlauf.
        _ => DurationSeconds
    };
}

/// <summary>Was alle Animationen einer Ebene zu einem Zeitpunkt zusammen bewirken.</summary>
public struct AnimationState
{
    /// <summary>Zusätzliche Verschiebung in Anteilen der Flächenbreite/-höhe.</summary>
    public double OffsetX, OffsetY;
    /// <summary>Faktor auf die Größe (Form-Maße bzw. Skalierung des Bildes).</summary>
    public double SizeFactor;
    /// <summary>Mittelpunkt der Größenänderung (nur für Mal-Ebenen), 0..1.</summary>
    public double SizeCenterX, SizeCenterY;
    public bool HasSizeCenter;
    /// <summary>Zusätzliche Drehung in Grad um <see cref="RotateCenterX"/>/<see cref="RotateCenterY"/>.</summary>
    public double Rotation, RotateCenterX, RotateCenterY;
    public double OpacityFactor;
    public double HueShift;
    public bool WrapX, WrapY;

    public static AnimationState Identity => new() { SizeFactor = 1, OpacityFactor = 1, SizeCenterX = 0.5, SizeCenterY = 0.5, RotateCenterX = 0.5, RotateCenterY = 0.5 };
}

/// <summary>Wertet die Animationen einer Ebene aus.</summary>
public static class LayerAnimator
{
    /// <summary>Wie oft die Ebene gezeichnet wird: mehr als einmal bei Kreis-Wellen
    /// (<see cref="LayerAnimation.Waves"/>) — jede Welle ist eine eigene, zeitversetzte Kopie.</summary>
    public static int InstanceCount(EffectLayer layer)
    {
        int waves = 1;
        foreach (var a in layer.Animations)
            if (a.Enabled && a.Kind is AnimationKind.Expand or AnimationKind.Contract && a.Repeat != AnimationRepeat.Once)
                waves = Math.Max(waves, Math.Clamp(a.Waves, 1, 8));
        return waves;
    }

    /// <summary>Zustand aller Animationen der Ebene zum Zeitpunkt <paramref name="time"/> für Kopie
    /// <paramref name="instance"/> von <paramref name="instances"/> (Wellen).</summary>
    public static AnimationState Evaluate(EffectLayer layer, double time, int instance = 0, int instances = 1)
    {
        var s = AnimationState.Identity;
        foreach (var a in layer.Animations)
        {
            if (!a.Enabled) continue;
            bool waveAnim = a.Kind is AnimationKind.Expand or AnimationKind.Contract && a.Repeat != AnimationRepeat.Once;
            int waves = waveAnim ? Math.Clamp(a.Waves, 1, 8) : 1;
            // Kopien über die Wellen verteilen; Animationen mit weniger Wellen als Kopien lassen die
            // überzähligen Kopien unsichtbar, statt sie doppelt übereinander zu zeichnen.
            if (waveAnim && instance >= waves) { s.OpacityFactor = 0; continue; }
            double phase = waveAnim ? (double)instance / waves : 0;
            double p = a.Progress(time, phase);
            double e = AnimatedValue.Ease(a.Easing, p);

            switch (a.Kind)
            {
                case AnimationKind.Move:
                {
                    double rad = a.AngleDegrees * Math.PI / 180;
                    double dx = Math.Cos(rad), dy = Math.Sin(rad);
                    if (Math.Abs(dx) < 1e-9) dx = 0;
                    if (Math.Abs(dy) < 1e-9) dy = 0;
                    s.OffsetX += dx * a.Distance * e;
                    s.OffsetY += dy * a.Distance * e;
                    if (a.Wrap)
                    {
                        if (dx != 0) s.WrapX = true;
                        if (dy != 0) s.WrapY = true;
                    }
                    break;
                }
                case AnimationKind.Expand or AnimationKind.Contract:
                {
                    s.SizeFactor *= a.StartSize + (a.EndSize - a.StartSize) * e;
                    SetSizeCenter(ref s, a);
                    if (a.FadeOut)
                    {
                        // Am Ende ganz aus, damit die nächste Welle ohne Sprung beginnt; kurz am Anfang
                        // weich einblenden, damit eine Welle nicht schlagartig auftaucht.
                        double fadeIn = Math.Clamp(p / 0.08, 0, 1);
                        s.OpacityFactor *= (1 - e) * fadeIn * fadeIn * (3 - 2 * fadeIn);
                    }
                    break;
                }
                case AnimationKind.Pulse:
                {
                    // Eine volle Schwingung pro Durchlauf, weich (Kosinus) — beginnt und endet bei Start-Größe.
                    double wave = (1 - Math.Cos(2 * Math.PI * e)) / 2;
                    s.SizeFactor *= a.StartSize + (a.EndSize - a.StartSize) * wave;
                    SetSizeCenter(ref s, a);
                    break;
                }
                case AnimationKind.Rotate:
                    s.Rotation += a.Degrees * e;
                    s.RotateCenterX = a.CenterX;
                    s.RotateCenterY = a.CenterY;
                    break;
                case AnimationKind.Breathe:
                    s.OpacityFactor *= a.MinOpacity + (a.MaxOpacity - a.MinOpacity) * WaveValue(a, e);
                    break;
                case AnimationKind.HueCycle:
                    s.HueShift += a.Degrees * e;
                    break;
            }
        }
        return s;
    }

    private static void SetSizeCenter(ref AnimationState s, LayerAnimation a)
    {
        s.SizeCenterX = a.CenterX;
        s.SizeCenterY = a.CenterY;
        s.HasSizeCenter = true;
    }

    /// <summary>Atmen/Blinken: Helligkeit 0..1 über den Durchlauf, beginnt hell.</summary>
    private static double WaveValue(LayerAnimation a, double p) => a.Wave switch
    {
        AnimationWave.Square => p < a.DutyCycle ? 1 : 0,
        AnimationWave.Triangle => Math.Abs(1 - 2 * p),
        _ => (1 + Math.Cos(2 * Math.PI * p)) / 2
    };

    /// <summary>Gesamtlänge, bei der sich alle Animationen, Keyframes und Daumenkinos nahtlos wiederholen:
    /// das kleinste gemeinsame Vielfache der Durchlauf-Längen (auf 10 ms gerastert), begrenzt auf
    /// <see cref="CustomEffectSerializer.MaxDurationSeconds"/>. Gibt es kein gemeinsames Vielfaches in
    /// dieser Grenze, gilt die längste Durchlauf-Länge.</summary>
    public static double SeamlessDuration(CustomEffectDocument document)
    {
        var periods = new List<double>();
        double minimum = 0;
        foreach (var layer in document.Layers)
        {
            foreach (var a in layer.Animations)
            {
                if (!a.Enabled) continue;
                if (a.Repeat == AnimationRepeat.Once) minimum = Math.Max(minimum, a.PeriodSeconds);
                else periods.Add(a.PeriodSeconds);
            }
            if (layer is PaintLayer paint && paint.Frames.Count > 1) periods.Add(Flipbook.TotalDuration(paint));
            foreach (var (_, value) in layer.AnimatedValues())
                if (value.IsAnimated) minimum = Math.Max(minimum, value.Keyframes[^1].Time);
        }
        const double max = CustomEffectSerializer.MaxDurationSeconds;
        double result;
        if (periods.Count == 0) result = minimum > 0 ? minimum : document.DurationSeconds;
        else
        {
            // In 10-ms-Schritten rechnen, damit 0,25 s und 1,5 s ein exaktes Vielfaches haben.
            long lcm = 1;
            bool overflow = false;
            foreach (var period in periods)
            {
                long steps = Math.Max(1, (long)Math.Round(period * 100));
                lcm = lcm / Gcd(lcm, steps) * steps;
                if (lcm > max * 100) { overflow = true; break; }
            }
            result = overflow ? periods.Max() : lcm / 100.0;
            // Keyframes/einmalige Animationen brauchen mindestens so viel Zeit: auf das nächste Vielfache aufrunden.
            if (minimum > result && !overflow) result = Math.Ceiling(minimum / result - 1e-9) * result;
            else if (minimum > result) result = minimum;
        }
        return Math.Clamp(result, CustomEffectSerializer.MinDurationSeconds, max);
    }

    private static long Gcd(long a, long b)
    {
        while (b != 0) (a, b) = (b, a % b);
        return a;
    }
}
