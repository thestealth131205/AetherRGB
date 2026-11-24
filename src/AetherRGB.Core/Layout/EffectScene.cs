using System.Text.Json;
using System.Text.Json.Serialization;
using AetherRGB.Core.Effects;

namespace AetherRGB.Core.Layout;

/// <summary>Ein Effekt-Fenster: eine eigene Fläche mit eigenem Effekt und eigenen platzierten Geräten.
/// Mehrere Szenen laufen gleichzeitig (z. B. "PC-Gehäuse" mit Regenbogen und "Schreibtisch-WLED"
/// mit statischer Farbe). Liegt dieselbe Zone in mehreren laufenden Szenen, gewinnt die spätere Szene
/// in der Liste.</summary>
public sealed class EffectScene
{
    public string Id { get; set; } = Guid.NewGuid().ToString("N");
    public string Name { get; set; } = "Effekt";
    public string EffectId { get; set; } = EffectCatalog.All[0].Id;
    public bool IsRunning { get; set; } = true;

    /// <summary>Layout-Helligkeit 0..100 % — gilt für alle Geräte dieser Szene.</summary>
    public int Brightness { get; set; } = 100;

    public double CanvasWidth { get; set; } = 1600;
    public double CanvasHeight { get; set; } = 800;
    public List<DeviceLayoutItem> Items { get; set; } = new();

    private ICanvasEffect? _effect;
    private string? _effectInstanceId;

    /// <summary>Laufende Effektinstanz passend zu <see cref="EffectId"/> — wird bei Wechsel neu erzeugt.</summary>
    [JsonIgnore]
    public ICanvasEffect Effect
    {
        get
        {
            if (_effect is null || _effectInstanceId != EffectId)
            {
                _effect = EffectCatalog.Get(EffectId).Create();
                _effectInstanceId = EffectId;
            }
            return _effect;
        }
    }
}

/// <summary>Alle Effekt-Fenster. Persistiert in <c>%APPDATA%\AetherRGB\scenes.json</c>.</summary>
public sealed class SceneCollection
{
    public List<EffectScene> Scenes { get; set; } = new();
    public string? ActiveSceneId { get; set; }

    private static readonly JsonSerializerOptions JsonOptions = new() { WriteIndented = true };

    public static string DefaultPath =>
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "AetherRGB", "scenes.json");

    public static SceneCollection LoadOrCreateDefault(string? path = null)
    {
        path ??= DefaultPath;
        SceneCollection? result = null;
        try
        {
            if (File.Exists(path))
                result = JsonSerializer.Deserialize<SceneCollection>(File.ReadAllText(path), JsonOptions);
        }
        catch
        {
            // Defekte Datei -> mit einem leeren Effekt-Fenster neu beginnen statt abzustürzen.
        }

        result ??= FromLegacyLayout() ?? new SceneCollection();
        if (result.Scenes.Count == 0)
            result.Scenes.Add(new EffectScene { Name = "Effekt 1" });
        return result;
    }

    /// <summary>Übernimmt das alte Ein-Flächen-Layout (layout.json, bis v0.2) als erstes Effekt-Fenster.
    /// Alte Rechtecke liefen bei Hochformat entlang der Höhe — die werden in Breite + 90° umgewandelt,
    /// weil Formen jetzt immer entlang der Breite verlaufen.</summary>
    private static SceneCollection? FromLegacyLayout()
    {
        var legacy = CanvasLayout.TryLoadLegacy();
        if (legacy is null || legacy.Items.Count == 0) return null;

        double sx = 1600 / Math.Max(1, legacy.CanvasWidth), sy = 800 / Math.Max(1, legacy.CanvasHeight);
        var scene = new EffectScene { Name = "Effekt 1" };
        foreach (var old in legacy.Items)
        {
            var item = old.Clone();
            if (item.Height > item.Width)
            {
                (item.Width, item.Height) = (item.Height, item.Width);
                item.RotationDegrees += 90;
            }
            item.CenterX *= sx;
            item.CenterY *= sy;
            scene.Items.Add(item);
        }
        return new SceneCollection { Scenes = { scene }, ActiveSceneId = scene.Id };
    }

    public void Save(string? path = null)
    {
        path ??= DefaultPath;
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllText(path, JsonSerializer.Serialize(this, JsonOptions));
    }
}
