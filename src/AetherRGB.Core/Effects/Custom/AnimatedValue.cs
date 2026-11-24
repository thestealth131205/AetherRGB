using System.Text.Json;
using System.Text.Json.Serialization;

namespace AetherRGB.Core.Effects.Custom;

/// <summary>Ein Keyframe: zum Zeitpunkt <see cref="Time"/> (Sekunden auf der Zeitleiste) hat der Wert
/// <see cref="Value"/>. <see cref="Easing"/> bestimmt den Verlauf bis zum nächsten Keyframe.</summary>
public sealed class Keyframe
{
    public double Time { get; set; }
    public double Value { get; set; }
    public Easing Easing { get; set; } = Easing.EaseInOut;

    public Keyframe() { }
    public Keyframe(double time, double value, Easing easing = Easing.EaseInOut)
    {
        Time = time;
        Value = value;
        Easing = easing;
    }
}

/// <summary>Ein Wert, der fest ist oder über Keyframes animiert wird. Vor dem ersten Keyframe gilt dessen
/// Wert, nach dem letzten der des letzten. In der Datei steht ein fester Wert als Zahl, ein animierter
/// als <c>{ "value": …, "keyframes": [ … ] }</c>.</summary>
[JsonConverter(typeof(AnimatedValueConverter))]
public sealed class AnimatedValue
{
    public double Value { get; set; }

    /// <summary>Nach <see cref="Keyframe.Time"/> sortiert halten (<see cref="SortKeyframes"/>).</summary>
    public List<Keyframe> Keyframes { get; set; } = new();

    public bool IsAnimated => Keyframes.Count > 0;

    public AnimatedValue() { }
    public AnimatedValue(double value) => Value = value;

    public static implicit operator AnimatedValue(double value) => new(value);

    public void SortKeyframes() => Keyframes.Sort((a, b) => a.Time.CompareTo(b.Time));

    public double Evaluate(double time)
    {
        var keys = Keyframes;
        if (keys.Count == 0) return Value;
        if (time <= keys[0].Time) return keys[0].Value;
        if (time >= keys[^1].Time) return keys[^1].Value;

        // Binäre Suche: letzter Keyframe mit Time <= time.
        int lo = 0, hi = keys.Count - 1;
        while (hi - lo > 1)
        {
            int mid = (lo + hi) / 2;
            if (keys[mid].Time <= time) lo = mid; else hi = mid;
        }
        var a = keys[lo];
        var b = keys[hi];
        double span = b.Time - a.Time;
        double t = span <= 0 ? 1 : (time - a.Time) / span;
        return a.Value + (b.Value - a.Value) * Ease(a.Easing, t);
    }

    public static double Ease(Easing easing, double t)
    {
        t = Math.Clamp(t, 0, 1);
        return easing switch
        {
            Easing.Linear => t,
            Easing.EaseIn => t * t * t,
            Easing.EaseOut => 1 - Math.Pow(1 - t, 3),
            Easing.EaseInOut => t < 0.5 ? 4 * t * t * t : 1 - Math.Pow(-2 * t + 2, 3) / 2,
            // "Back out": überschießt um ~10 % und kehrt zurück.
            Easing.Spring => 1 + 2.70158 * Math.Pow(t - 1, 3) + 1.70158 * Math.Pow(t - 1, 2),
            Easing.Hold => t >= 1 ? 1 : 0,
            _ => t
        };
    }

    public AnimatedValue Clone() => new()
    {
        Value = Value,
        Keyframes = Keyframes.Select(k => new Keyframe(k.Time, k.Value, k.Easing)).ToList()
    };
}

internal sealed class AnimatedValueConverter : JsonConverter<AnimatedValue>
{
    public override AnimatedValue Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options)
    {
        if (reader.TokenType == JsonTokenType.Number) return new AnimatedValue(reader.GetDouble());
        if (reader.TokenType != JsonTokenType.StartObject) throw new JsonException("Zahl oder Objekt mit value/keyframes erwartet.");

        var result = new AnimatedValue();
        while (reader.Read())
        {
            if (reader.TokenType == JsonTokenType.EndObject)
            {
                result.SortKeyframes();
                return result;
            }
            if (reader.TokenType != JsonTokenType.PropertyName) throw new JsonException();
            var name = reader.GetString();
            reader.Read();
            if (string.Equals(name, "value", StringComparison.OrdinalIgnoreCase))
                result.Value = reader.GetDouble();
            else if (string.Equals(name, "keyframes", StringComparison.OrdinalIgnoreCase))
                result.Keyframes = JsonSerializer.Deserialize<List<Keyframe>>(ref reader, options) ?? new();
            else
                reader.Skip(); // Unbekannte Felder neuerer Versionen ignorieren.
        }
        throw new JsonException("Unerwartetes Dateiende.");
    }

    public override void Write(Utf8JsonWriter writer, AnimatedValue value, JsonSerializerOptions options)
    {
        if (!value.IsAnimated)
        {
            writer.WriteNumberValue(value.Value);
            return;
        }
        writer.WriteStartObject();
        writer.WriteNumber("value", value.Value);
        writer.WritePropertyName("keyframes");
        JsonSerializer.Serialize(writer, value.Keyframes, options);
        writer.WriteEndObject();
    }
}
