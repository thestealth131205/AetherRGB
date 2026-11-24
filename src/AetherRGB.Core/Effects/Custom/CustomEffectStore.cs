using AetherRGB.Core.Diagnostics;

namespace AetherRGB.Core.Effects.Custom;

/// <summary>Eigene Effekte als <c>.aeffect</c>-Dateien in <c>%APPDATA%\AetherRGB\effects</c>. Lädt alle Dateien
/// in den <see cref="EffectCatalog"/> und lädt neu, wenn sich der Ordner ändert (Editor, Import, REST-API
/// oder eine von Hand hineinkopierte Datei). Ungültige Dateien werden übersprungen und im Log gemeldet.</summary>
public sealed class CustomEffectStore : IDisposable
{
    public const string Extension = ".aeffect";

    public static string DefaultDirectory =>
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "AetherRGB", "effects");

    public string Directory { get; }

    /// <summary>Lade-Meldungen für die Log-Leiste. Kann von jedem Thread kommen.</summary>
    public event Action<LogLevel, string>? Log;

    private readonly object _lock = new();
    private Dictionary<string, (CustomEffectDocument Document, string Path)> _effects = new();
    private FileSystemWatcher? _watcher;
    private Timer? _debounce;
    /// <summary>Letzter Stand pro Datei, damit beim Neuladen nur Änderungen gemeldet werden.</summary>
    private readonly Dictionary<string, DateTime> _seen = new(StringComparer.OrdinalIgnoreCase);

    public CustomEffectStore(string? directory = null) => Directory = directory ?? DefaultDirectory;

    /// <summary>Alle geladenen eigenen Effekte (Kopien — Änderungen daran wirken erst nach <see cref="Save"/>).</summary>
    public IReadOnlyList<CustomEffectDocument> Effects
    {
        get { lock (_lock) return _effects.Values.Select(e => e.Document.Clone()).ToList(); }
    }

    public CustomEffectDocument? Get(string id)
    {
        lock (_lock) return _effects.TryGetValue(StripPrefix(id), out var e) ? e.Document.Clone() : null;
    }

    public void LoadAll()
    {
        var loaded = new Dictionary<string, (CustomEffectDocument, string)>();
        if (System.IO.Directory.Exists(Directory))
        {
            foreach (var path in System.IO.Directory.EnumerateFiles(Directory, "*" + Extension).OrderBy(p => p, StringComparer.OrdinalIgnoreCase))
            {
                DateTime stamp;
                try { stamp = File.GetLastWriteTimeUtc(path); } catch { continue; }
                bool changed;
                lock (_lock) changed = !_seen.TryGetValue(path, out var old) || old != stamp;
                try
                {
                    var document = CustomEffectSerializer.Load(path, out var skipped);
                    if (loaded.ContainsKey(document.Id))
                    {
                        if (changed) Log?.Invoke(LogLevel.Warning, $"Eigener Effekt {Path.GetFileName(path)}: ID {document.Id} gibt es schon in einer anderen Datei — übersprungen.");
                    }
                    else
                    {
                        loaded[document.Id] = (document, path);
                        if (changed)
                            Log?.Invoke(skipped.Count > 0 ? LogLevel.Warning : LogLevel.Debug,
                                $"Eigener Effekt „{document.Name}“ geladen ({Path.GetFileName(path)}, {document.Layers.Count} Ebene(n), {document.DurationSeconds:0.##} s)" +
                                (skipped.Count > 0 ? $" — {skipped.Count} Ebene(n) eines neueren Typs übersprungen ({string.Join(", ", skipped.Distinct())}), AetherRGB aktualisieren." : ""));
                    }
                }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or InvalidDataException)
                {
                    // Datei wird evtl. gerade noch geschrieben — beim nächsten Änderungsereignis neu versuchen.
                    if (changed) Log?.Invoke(LogLevel.Warning, $"Eigener Effekt {Path.GetFileName(path)} konnte nicht geladen werden: {ex.Message}");
                }
                lock (_lock) _seen[path] = stamp;
            }
        }

        lock (_lock) _effects = loaded;
        EffectCatalog.SetCustomEffects(loaded.Values.Select(e => CreateDescriptor(e.Item1)).ToList());
    }

    /// <summary>Speichert den Effekt als <c>&lt;Id&gt;.aeffect</c> und lädt den Katalog neu. Gibt den Pfad zurück.</summary>
    /// <exception cref="InvalidDataException">Effekt ist ungültig.</exception>
    public string Save(CustomEffectDocument document)
    {
        var copy = document.Clone();
        string path;
        lock (_lock)
            path = _effects.TryGetValue(copy.Id, out var existing) ? existing.Path : Path.Combine(Directory, copy.Id + Extension);
        CustomEffectSerializer.Save(copy, path);
        LoadAll();
        return path;
    }

    public bool Delete(string id)
    {
        string? path;
        lock (_lock) path = _effects.TryGetValue(StripPrefix(id), out var e) ? e.Path : null;
        if (path is null) return false;
        File.Delete(path);
        LoadAll();
        return true;
    }

    /// <summary>Datei von außen übernehmen. Gibt es die ID schon, bekommt der importierte Effekt eine neue,
    /// damit nie unbemerkt ein vorhandener Effekt überschrieben wird.</summary>
    /// <exception cref="InvalidDataException">Datei ist kein gültiger Effekt.</exception>
    public CustomEffectDocument Import(string sourcePath)
    {
        var document = CustomEffectSerializer.Load(sourcePath, out _);
        lock (_lock)
            if (_effects.ContainsKey(document.Id)) document.Id = Guid.NewGuid().ToString("N");
        Save(document);
        return document;
    }

    public void StartWatching()
    {
        if (_watcher is not null) return;
        System.IO.Directory.CreateDirectory(Directory);
        _debounce = new Timer(_ =>
        {
            try { LoadAll(); }
            catch (Exception ex) { Log?.Invoke(LogLevel.Warning, "Eigene Effekte neu laden fehlgeschlagen: " + ExceptionText.Describe(ex)); }
        });
        _watcher = new FileSystemWatcher(Directory, "*" + Extension)
        {
            NotifyFilter = NotifyFilters.FileName | NotifyFilters.LastWrite | NotifyFilters.Size,
            IncludeSubdirectories = false
        };
        // Editor/Kopieren erzeugt mehrere Ereignisse pro Datei — gesammelt nach kurzer Ruhe einmal neu laden.
        FileSystemEventHandler changed = (_, _) => _debounce.Change(300, Timeout.Infinite);
        _watcher.Created += changed;
        _watcher.Changed += changed;
        _watcher.Deleted += changed;
        _watcher.Renamed += (_, _) => _debounce.Change(300, Timeout.Infinite);
        _watcher.EnableRaisingEvents = true;
    }

    public static string StripPrefix(string id) =>
        id.StartsWith(CustomEffectDocument.CatalogPrefix, StringComparison.Ordinal) ? id[CustomEffectDocument.CatalogPrefix.Length..] : id;

    /// <summary>Der Descriptor hält eine eigene Kopie, die danach niemand mehr ändert — alle Instanzen
    /// (mehrere Effekt-Fenster) lesen sie nur. Neu laden erzeugt einen neuen Descriptor mit neuer Kopie.</summary>
    public static EffectDescriptor CreateDescriptor(CustomEffectDocument document)
    {
        var snapshot = document.Clone();
        return new EffectDescriptor(snapshot.CatalogId, snapshot.Name, () => new CustomCanvasEffect(snapshot));
    }

    public void Dispose()
    {
        _watcher?.Dispose();
        _debounce?.Dispose();
    }
}
