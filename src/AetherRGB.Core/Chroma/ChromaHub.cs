using AetherRGB.Core.Diagnostics;

namespace AetherRGB.Core.Chroma;

/// <summary>Was das gerade aktive Spiel zuletzt pro Geräteart gesetzt hat. Unveränderlich.</summary>
public sealed class ChromaSnapshot
{
    public required string AppName { get; init; }
    public required string Source { get; init; }
    public required IReadOnlyDictionary<ChromaDevice, ChromaFrame> Frames { get; init; }
}

/// <summary>Sammelstelle für alle Chroma-Spiele, egal ob sie über die Bridge-DLL (natives SDK) oder die
/// REST-API kommen. Wie bei Razer Synapse gewinnt das zuletzt gestartete Spiel; endet es, ist wieder das
/// vorige dran. Der Effekt „Spiel (Razer Chroma)“ liest nur <see cref="Active"/> — ein Lesezugriff ohne Sperre.</summary>
public sealed class ChromaHub
{
    public static ChromaHub Shared { get; } = new();

    /// <summary>Höchstzahl gleichzeitiger Sitzungen — schützt vor einem Programm, das endlos neue anlegt.</summary>
    public const int MaxSessions = 32;

    private sealed class Session
    {
        public required string Key;
        public required string AppName;
        public required string Source;
        public int? Pid;
        public long Order;
        public DateTime LastSeenUtc;
        public readonly Dictionary<ChromaDevice, ChromaFrame> Frames = new();
    }

    private readonly object _lock = new();
    private readonly Dictionary<string, Session> _sessions = new();
    private long _order;
    private volatile ChromaSnapshot? _active;

    /// <summary>Aktives Spiel oder null (kein Spiel verbunden — der Effekt zeigt dann seine Leerlauf-Animation).</summary>
    public ChromaSnapshot? Active => _active;

    /// <summary>Spiel verbunden/getrennt, für die Log-Leiste. Kann von jedem Thread kommen.</summary>
    public event Action<LogLevel, string>? Log;

    /// <summary>Meldet ein Spiel an (oder frischt den Namen auf). Gibt false zurück, wenn schon
    /// <see cref="MaxSessions"/> offen sind.</summary>
    public bool Open(string key, string appName, string source, int? pid = null)
    {
        appName = Sanitize(appName);
        bool isNew;
        lock (_lock)
        {
            isNew = !_sessions.TryGetValue(key, out var session);
            if (isNew)
            {
                if (_sessions.Count >= MaxSessions) return false;
                session = new Session { Key = key, AppName = appName, Source = source, Pid = pid };
                _sessions[key] = session;
            }
            session!.AppName = appName;
            session.Order = ++_order;
            session.LastSeenUtc = DateTime.UtcNow;
            Publish();
        }
        if (isNew) Log?.Invoke(LogLevel.Info, $"Spiel verbunden: {appName} ({source}{(pid is { } p ? $", PID {p}" : "")})");
        return true;
    }

    public void Close(string key, string reason = "beendet")
    {
        string? name = null;
        lock (_lock)
        {
            if (_sessions.Remove(key, out var session)) { name = session.AppName; Publish(); }
        }
        if (name is not null) Log?.Invoke(LogLevel.Info, $"Spiel getrennt: {name} ({reason})");
    }

    public bool IsOpen(string key)
    {
        lock (_lock) return _sessions.ContainsKey(key);
    }

    public void Touch(string key)
    {
        lock (_lock) if (_sessions.TryGetValue(key, out var s)) s.LastSeenUtc = DateTime.UtcNow;
    }

    public void SetFrame(string key, ChromaDevice device, ChromaFrame frame)
    {
        lock (_lock)
        {
            if (!_sessions.TryGetValue(key, out var session)) return;
            session.Frames[device] = frame;
            session.LastSeenUtc = DateTime.UtcNow;
            if (_active is null || ReferenceEquals(CurrentSession(), session)) Publish();
        }
    }

    /// <summary>Schließt Sitzungen, deren Prozess nicht mehr läuft oder die zu lange stumm waren.</summary>
    public void Prune(Func<int, bool> isProcessAlive, Func<string, TimeSpan?> timeoutFor)
    {
        List<(string Key, string Reason)> expired = new();
        var now = DateTime.UtcNow;
        lock (_lock)
        {
            foreach (var s in _sessions.Values)
            {
                if (s.Pid is { } pid && !isProcessAlive(pid)) expired.Add((s.Key, "Prozess beendet"));
                else if (timeoutFor(s.Source) is { } timeout && now - s.LastSeenUtc > timeout) expired.Add((s.Key, "keine Rückmeldung mehr"));
            }
        }
        foreach (var (key, reason) in expired) Close(key, reason);
    }

    public void CloseAll(string source)
    {
        List<string> keys;
        lock (_lock) keys = _sessions.Values.Where(s => s.Source == source).Select(s => s.Key).ToList();
        foreach (var key in keys) Close(key, "Empfang beendet");
    }

    private Session? CurrentSession() => _sessions.Values.OrderByDescending(s => s.Order).FirstOrDefault();

    private void Publish()
    {
        var s = CurrentSession();
        _active = s is null ? null : new ChromaSnapshot
        {
            AppName = s.AppName,
            Source = s.Source,
            Frames = new Dictionary<ChromaDevice, ChromaFrame>(s.Frames),
        };
    }

    /// <summary>Namen kommen vom Spiel bzw. von beliebigen lokalen Programmen — für das Log kürzen und
    /// Steuerzeichen entfernen.</summary>
    private static string Sanitize(string name)
    {
        var clean = new string(name.Where(c => !char.IsControl(c)).Take(80).ToArray()).Trim();
        return clean.Length == 0 ? "Unbekanntes Spiel" : clean;
    }
}
