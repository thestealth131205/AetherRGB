using System.IO.Compression;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.Json.Serialization;
using AetherRGB.Core.Devices;

namespace AetherRGB.Core.Effects.Custom;

/// <summary>Liest und schreibt <c>.aeffect</c>-Dateien (JSON, versioniert über <c>formatVersion</c>).
/// Dateien können von anderen stammen (Import, REST-API) — daher werden Größe, Raster, Ebenen-,
/// Bild- und Keyframe-Anzahl begrenzt und alle Zahlen geprüft. Unbekannte Felder und Ebenen-Typen
/// neuerer Versionen werden übersprungen statt die ganze Datei abzulehnen.</summary>
public static class CustomEffectSerializer
{
    public const long MaxFileBytes = 32L * 1024 * 1024;
    public const int MaxGridSize = 512;
    public const int MaxLayers = 64;
    public const int MaxFramesPerLayer = 1000;
    public const int MaxKeyframesPerValue = 10_000;
    public const int MaxAnimationsPerLayer = 32;
    public const long MaxTotalPixelBytes = 64L * 1024 * 1024;
    public const double MinDurationSeconds = 0.1, MaxDurationSeconds = 600;

    internal static readonly JsonSerializerOptions Options = CreateOptions();

    private static JsonSerializerOptions CreateOptions()
    {
        var options = new JsonSerializerOptions
        {
            WriteIndented = true,
            PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
            PropertyNameCaseInsensitive = true,
            DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
            NumberHandling = JsonNumberHandling.Strict
        };
        options.Converters.Add(new JsonStringEnumConverter(JsonNamingPolicy.CamelCase));
        options.Converters.Add(new EffectLayerConverter());
        options.Converters.Add(new AnimationListConverter());
        return options;
    }

    public static string Serialize(CustomEffectDocument document) => JsonSerializer.Serialize(document, Options);

    /// <summary>Unbekannte Ebenen-Typen, die beim letzten <see cref="Deserialize"/> im aktuellen Thread
    /// übersprungen wurden — für einen Hinweis im Log.</summary>
    [ThreadStatic] private static List<string>? _skippedLayerTypes;

    /// <exception cref="InvalidDataException">Datei ist kein gültiger Effekt.</exception>
    public static CustomEffectDocument Deserialize(string json) => Deserialize(json, out _);

    public static CustomEffectDocument Deserialize(string json, out IReadOnlyList<string> skippedLayerTypes)
    {
        if (json.Length > MaxFileBytes) throw new InvalidDataException("Datei ist größer als 32 MB.");
        _skippedLayerTypes = new List<string>();
        CustomEffectDocument? document;
        try
        {
            document = JsonSerializer.Deserialize<CustomEffectDocument>(json, Options);
        }
        catch (Exception e) when (e is JsonException or FormatException or InvalidDataException or NotSupportedException or InvalidOperationException)
        {
            throw new InvalidDataException("Kein gültiger AetherRGB-Effekt: " + e.Message, e);
        }
        finally
        {
            skippedLayerTypes = _skippedLayerTypes ?? new List<string>();
            _skippedLayerTypes = null;
        }
        if (document is null) throw new InvalidDataException("Datei ist leer.");
        document.Layers.RemoveAll(l => l is null);
        Validate(document);
        return document;
    }

    public static CustomEffectDocument Load(string path, out IReadOnlyList<string> skippedLayerTypes)
    {
        var info = new FileInfo(path);
        if (info.Length > MaxFileBytes) throw new InvalidDataException("Datei ist größer als 32 MB.");
        return Deserialize(File.ReadAllText(path), out skippedLayerTypes);
    }

    /// <summary>Schreibt erst in eine Temp-Datei und benennt dann um — ein Absturz mitten im Speichern
    /// lässt so nie eine halbe Datei zurück.</summary>
    public static void Save(CustomEffectDocument document, string path)
    {
        Validate(document);
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        var temp = path + ".tmp";
        File.WriteAllText(temp, Serialize(document));
        File.Move(temp, path, overwrite: true);
    }

    /// <exception cref="InvalidDataException"></exception>
    public static void Validate(CustomEffectDocument d)
    {
        if (d.FormatVersion < 1) throw new InvalidDataException("Unbekannte Formatversion.");
        if (string.IsNullOrWhiteSpace(d.Id) || d.Id.Length > 64 || !d.Id.All(c => char.IsAsciiLetterOrDigit(c) || c is '-' or '_'))
            throw new InvalidDataException("Ungültige Effekt-ID (nur Buchstaben, Ziffern, - und _, höchstens 64 Zeichen).");
        d.Name = string.IsNullOrWhiteSpace(d.Name) ? "Eigener Effekt" : d.Name.Trim();
        if (d.Name.Length > 100) d.Name = d.Name[..100];
        if (!double.IsFinite(d.DurationSeconds)) throw new InvalidDataException("Ungültige Dauer.");
        d.DurationSeconds = Math.Clamp(d.DurationSeconds, MinDurationSeconds, MaxDurationSeconds);
        if (d.GridWidth < 1 || d.GridHeight < 1 || d.GridWidth > MaxGridSize || d.GridHeight > MaxGridSize)
            throw new InvalidDataException($"Raster {d.GridWidth}×{d.GridHeight} ist ungültig (höchstens {MaxGridSize}×{MaxGridSize}).");
        if (d.Layers.Count > MaxLayers) throw new InvalidDataException($"Mehr als {MaxLayers} Ebenen.");

        long frameBytes = (long)d.GridWidth * d.GridHeight * 4, totalBytes = 0;
        foreach (var layer in d.Layers)
        {
            foreach (var (name, value) in layer.AnimatedValues())
            {
                if (value is null) throw new InvalidDataException($"Ebene „{layer.Name}“: Wert {name} fehlt.");
                if (value.Keyframes.Count > MaxKeyframesPerValue) throw new InvalidDataException($"Ebene „{layer.Name}“: zu viele Keyframes bei {name}.");
                if (!double.IsFinite(value.Value) || value.Keyframes.Any(k => !double.IsFinite(k.Time) || !double.IsFinite(k.Value)))
                    throw new InvalidDataException($"Ebene „{layer.Name}“: ungültige Zahl bei {name}.");
                value.SortKeyframes();
            }
            if (!double.IsFinite(layer.PivotX) || !double.IsFinite(layer.PivotY))
                throw new InvalidDataException($"Ebene „{layer.Name}“: ungültiger Drehpunkt.");
            layer.Animations ??= new();
            layer.Animations.RemoveAll(a => a is null);
            if (layer.Animations.Count > MaxAnimationsPerLayer)
                throw new InvalidDataException($"Ebene „{layer.Name}“: mehr als {MaxAnimationsPerLayer} Animationen.");
            foreach (var animation in layer.Animations) animation.Normalize();

            switch (layer)
            {
                case PaintLayer paint:
                    if (paint.Frames.Count > MaxFramesPerLayer) throw new InvalidDataException($"Ebene „{layer.Name}“: mehr als {MaxFramesPerLayer} Bilder.");
                    if (!double.IsFinite(paint.FrameDurationSeconds) || paint.FrameDurationSeconds <= 0) paint.FrameDurationSeconds = 0.25;
                    foreach (var frame in paint.Frames)
                    {
                        if (frame.Pixels.Length != frameBytes)
                            throw new InvalidDataException($"Ebene „{layer.Name}“: Bild passt nicht zum Raster {d.GridWidth}×{d.GridHeight}.");
                        if (frame.DurationSeconds is { } fd && (!double.IsFinite(fd) || fd <= 0)) frame.DurationSeconds = null;
                        totalBytes += frameBytes;
                    }
                    if (totalBytes > MaxTotalPixelBytes) throw new InvalidDataException("Bilder sind zusammen zu groß.");
                    break;
                case ShapeLayer shape:
                    try { RgbColor.FromHex(shape.Color); }
                    catch (Exception e) when (e is FormatException or ArgumentException or OverflowException)
                    {
                        throw new InvalidDataException($"Ebene „{layer.Name}“: ungültige Farbe „{shape.Color}“.");
                    }
                    break;
            }
        }
    }

    /// <summary>Schreibt Ebenen mit <c>"type": "paint" | "shape"</c> und liest unbekannte Typen als null
    /// (werden danach entfernt), damit Dateien aus neueren Versionen trotzdem laden.</summary>
    private sealed class EffectLayerConverter : JsonConverter<EffectLayer>
    {
        private static readonly Dictionary<string, Type> Types = new(StringComparer.OrdinalIgnoreCase)
        {
            ["paint"] = typeof(PaintLayer),
            ["shape"] = typeof(ShapeLayer)
        };

        public override bool HandleNull => false;

        public override EffectLayer? Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options)
        {
            var node = JsonNode.Parse(ref reader) as JsonObject ?? throw new JsonException("Ebene muss ein Objekt sein.");
            var typeName = node.FirstOrDefault(p => string.Equals(p.Key, "type", StringComparison.OrdinalIgnoreCase)).Value?.GetValue<string>();
            if (typeName is null || !Types.TryGetValue(typeName, out var type))
            {
                _skippedLayerTypes?.Add(typeName ?? "(ohne Typ)");
                return null;
            }
            return (EffectLayer?)node.Deserialize(type, options);
        }

        public override void Write(Utf8JsonWriter writer, EffectLayer value, JsonSerializerOptions options)
        {
            var typeName = Types.First(t => t.Value == value.GetType()).Key;
            var node = JsonSerializer.SerializeToNode(value, value.GetType(), options)!.AsObject();
            writer.WriteStartObject();
            writer.WriteString("type", typeName);
            foreach (var (key, child) in node)
            {
                writer.WritePropertyName(key);
                if (child is null) writer.WriteNullValue();
                else child.WriteTo(writer, options);
            }
            writer.WriteEndObject();
        }
    }
    /// <summary>Liest Animationen einzeln: eine unbekannte Art aus einer neueren Version wird übersprungen
    /// (und gemeldet), statt die ganze Datei abzulehnen.</summary>
    private sealed class AnimationListConverter : JsonConverter<List<LayerAnimation>>
    {
        public override List<LayerAnimation> Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options)
        {
            var array = JsonNode.Parse(ref reader) as JsonArray ?? throw new JsonException("Animationen müssen eine Liste sein.");
            if (array.Count > MaxAnimationsPerLayer * 4) throw new JsonException("Zu viele Animationen.");
            var result = new List<LayerAnimation>();
            foreach (var item in array)
            {
                if (item is not JsonObject obj) continue;
                var kind = obj.FirstOrDefault(p => string.Equals(p.Key, "kind", StringComparison.OrdinalIgnoreCase)).Value;
                string? kindName = kind is JsonValue v && v.TryGetValue<string>(out var str) ? str : null;
                if (kindName is null || !Enum.TryParse<AnimationKind>(kindName, ignoreCase: true, out var parsed) || !Enum.IsDefined(parsed))
                {
                    _skippedLayerTypes?.Add("Animation " + (kindName ?? "(ohne Art)"));
                    continue;
                }
                var animation = obj.Deserialize<LayerAnimation>(options);
                if (animation is not null) result.Add(animation);
            }
            return result;
        }

        public override void Write(Utf8JsonWriter writer, List<LayerAnimation> value, JsonSerializerOptions options)
        {
            writer.WriteStartArray();
            foreach (var animation in value) JsonSerializer.Serialize(writer, animation, options);
            writer.WriteEndArray();
        }
    }
}

/// <summary>RGBA-Pixel ↔ Base64 von Deflate-komprimierten Rohdaten. Beim Entpacken begrenzt, damit eine
/// präparierte Datei nicht beliebig viel Speicher belegt.</summary>
internal static class PixelCodec
{
    private const int MaxDecodedBytes = CustomEffectSerializer.MaxGridSize * CustomEffectSerializer.MaxGridSize * 4;

    public static string Encode(byte[] pixels)
    {
        if (pixels.Length == 0) return "";
        using var output = new MemoryStream();
        using (var deflate = new DeflateStream(output, CompressionLevel.Optimal, leaveOpen: true))
            deflate.Write(pixels);
        return Convert.ToBase64String(output.GetBuffer(), 0, (int)output.Length);
    }

    public static byte[] Decode(string? data)
    {
        if (string.IsNullOrEmpty(data)) return Array.Empty<byte>();
        byte[] compressed;
        try { compressed = Convert.FromBase64String(data); }
        catch (FormatException) { throw new InvalidDataException("Bilddaten sind kein gültiges Base64."); }

        using var deflate = new DeflateStream(new MemoryStream(compressed), CompressionMode.Decompress);
        using var output = new MemoryStream();
        var buffer = new byte[81920];
        int read;
        try
        {
            while ((read = deflate.Read(buffer, 0, buffer.Length)) > 0)
            {
                if (output.Length + read > MaxDecodedBytes) throw new InvalidDataException("Bilddaten sind zu groß.");
                output.Write(buffer, 0, read);
            }
        }
        catch (Exception e) when (e is not InvalidDataException)
        {
            throw new InvalidDataException("Bilddaten sind beschädigt.", e);
        }
        return output.ToArray();
    }
}
