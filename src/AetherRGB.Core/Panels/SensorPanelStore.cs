using AetherRGB.Core.Devices;
using AetherRGB.Core.Diagnostics;

namespace AetherRGB.Core.Panels;

/// <summary>Sensor-Panels als JSON-Dateien in <c>%APPDATA%\AetherRGB\panels</c>, Bilder in <c>panels\assets</c>.</summary>
public sealed class SensorPanelStore
{
    public static string DefaultDirectory =>
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "AetherRGB", "panels");

    public const string Extension = ".json";
    public const long MaxFileBytes = 2 * 1024 * 1024;

    public string Directory { get; }
    public string AssetDirectory => Path.Combine(Directory, "assets");
    public event Action<LogLevel, string>? Log;
    public event Action? Changed;

    private readonly object _lock = new();
    private List<SensorPanelDocument> _panels = new();

    public SensorPanelStore(string? directory = null) => Directory = directory ?? DefaultDirectory;

    public IReadOnlyList<SensorPanelDocument> Panels { get { lock (_lock) return _panels.ToList(); } }

    /// <summary>Lädt alle Panels; beim ersten Start (Ordner fehlt) werden die Vorlagen angelegt.</summary>
    public void LoadAll()
    {
        if (!System.IO.Directory.Exists(Directory))
        {
            System.IO.Directory.CreateDirectory(Directory);
            foreach (var t in SensorPanelTemplates.All) WriteFile(t());
        }
        var loaded = new List<SensorPanelDocument>();
        foreach (var path in System.IO.Directory.EnumerateFiles(Directory, "*" + Extension).OrderBy(p => p, StringComparer.OrdinalIgnoreCase))
        {
            try
            {
                if (new FileInfo(path).Length > MaxFileBytes) throw new InvalidDataException("Datei größer als 2 MB.");
                var doc = SensorPanelSerializer.Deserialize(File.ReadAllText(path));
                if (loaded.Any(p => p.Id == doc.Id)) { Log?.Invoke(LogLevel.Warning, $"Sensor-Panel {Path.GetFileName(path)}: ID {doc.Id} doppelt — übersprungen."); continue; }
                loaded.Add(doc);
            }
            catch (Exception ex) { Log?.Invoke(LogLevel.Warning, $"Sensor-Panel {Path.GetFileName(path)} ungültig: {ExceptionText.Describe(ex)}"); }
        }
        lock (_lock) _panels = Sorted(loaded);
        Changed?.Invoke();
    }

    public void Save(SensorPanelDocument doc)
    {
        SensorPanelSerializer.Sanitize(doc);
        var copy = doc.Clone();
        WriteFile(copy);
        lock (_lock)
        {
            _panels.RemoveAll(p => p.Id == copy.Id);
            _panels.Add(copy);
            _panels = Sorted(_panels);
        }
        Changed?.Invoke();
    }

    public void Delete(string id)
    {
        var path = PathFor(id);
        if (File.Exists(path)) File.Delete(path);
        lock (_lock) _panels.RemoveAll(p => p.Id == id);
        Changed?.Invoke();
    }

    public SensorPanelDocument? Get(string? id)
    {
        lock (_lock) return _panels.FirstOrDefault(p => p.Id == id)?.Clone();
    }

    public string PathFor(string id) => Path.Combine(Directory, SensorPanelSerializer.SafeId(id) + Extension);

    /// <summary>Kopiert ein Bild in den Asset-Ordner (damit das Panel nicht vom Originalpfad abhängt) und liefert den Dateinamen.</summary>
    public string ImportAsset(string sourcePath)
    {
        System.IO.Directory.CreateDirectory(AssetDirectory);
        var name = Path.GetFileName(sourcePath);
        var target = Path.Combine(AssetDirectory, name);
        if (File.Exists(target) && !SameFile(sourcePath, target))
        {
            var stem = Path.GetFileNameWithoutExtension(name);
            var ext = Path.GetExtension(name);
            for (int n = 2; File.Exists(target); n++) target = Path.Combine(AssetDirectory, $"{stem}-{n}{ext}");
            name = Path.GetFileName(target);
        }
        if (!SameFile(sourcePath, target)) File.Copy(sourcePath, target, overwrite: false);
        return name;
    }

    /// <summary>Voller Pfad zu einem Bild-Eintrag (Dateiname im Asset-Ordner oder absoluter Pfad).</summary>
    public string? ResolveAsset(string? path)
    {
        if (string.IsNullOrWhiteSpace(path)) return null;
        if (Path.IsPathRooted(path)) return File.Exists(path) ? path : null;
        var p = Path.Combine(AssetDirectory, Path.GetFileName(path));
        return File.Exists(p) ? p : null;
    }

    private static bool SameFile(string a, string b) =>
        string.Equals(Path.GetFullPath(a), Path.GetFullPath(b), StringComparison.OrdinalIgnoreCase);

    private static List<SensorPanelDocument> Sorted(IEnumerable<SensorPanelDocument> list) =>
        list.OrderBy(p => p.Name, StringComparer.CurrentCultureIgnoreCase).ToList();

    private void WriteFile(SensorPanelDocument doc)
    {
        System.IO.Directory.CreateDirectory(Directory);
        var path = PathFor(doc.Id);
        var tmp = path + ".tmp";
        File.WriteAllText(tmp, SensorPanelSerializer.Serialize(doc));
        File.Move(tmp, path, overwrite: true);
    }
}
