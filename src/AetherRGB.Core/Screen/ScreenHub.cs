using AetherRGB.Core.Diagnostics;

namespace AetherRGB.Core.Screen;

/// <summary>Treffpunkt zwischen Bildschirmaufnahme (App, Windows), Analyse und den Bildschirm-Effekten. Die Aufnahme
/// läuft nur, solange ein Effekt-Fenster einen Bildschirm-Effekt zeigt (<see cref="Demand"/>), sonst kostet sie nichts.</summary>
public sealed class ScreenHub
{
    public static ScreenHub Shared { get; } = new();

    /// <summary>Ohne Anfrage so lange wird die Aufnahme beendet.</summary>
    public const int DemandTimeoutMs = 2000;
    /// <summary>Kam so lange kein Bild bzw. Lebenszeichen der Aufnahme, gilt sie als ausgefallen.</summary>
    public const int StaleAfterMs = 1500;

    private long _lastDemand = long.MinValue / 2;
    private long _lastHeartbeat = long.MinValue / 2;
    private volatile ScreenAnalysis? _latest;
    private volatile ScreenImage? _latestImage;

    private readonly object _lock = new();
    private IReadOnlyList<ScreenProfile> _profiles = Array.Empty<ScreenProfile>();
    private string? _defaultProfileId;
    private string? _foregroundProcess;
    private ScreenProfile? _current;
    private ScreenAnalyzer _analyzer = new(null);

    public event Action<LogLevel, string>? Log;

    /// <summary>Das aktive Profil hat gewechselt (Spiel im Vordergrund, Einstellung geändert). Beliebiger Thread.</summary>
    public event Action<ScreenProfile?>? ProfileChanged;

    /// <summary>Von Bildschirm-Effekten bei jedem Abruf: hält die Aufnahme am Laufen.</summary>
    public void Demand() => Volatile.Write(ref _lastDemand, Environment.TickCount64);

    // ---- Feste Profile: Effekte „Spiel: …“ werten ihr Profil aus, egal welches Spiel im Vordergrund ist ----

    private sealed class FixedProfile
    {
        public long LastDemand;
        public ScreenAnalyzer? Analyzer;
    }

    private readonly System.Collections.Concurrent.ConcurrentDictionary<string, FixedProfile> _fixed = new();
    private volatile IReadOnlyDictionary<string, ScreenAnalysis> _fixedLatest = new Dictionary<string, ScreenAnalysis>();

    /// <summary>Wie <see cref="Demand()"/>, zusätzlich wird das Profil <paramref name="profileId"/> ausgewertet
    /// (<see cref="LatestFor"/>), solange danach gefragt wird.</summary>
    public void Demand(string profileId)
    {
        long now = Environment.TickCount64;
        Volatile.Write(ref _lastDemand, now);
        var entry = _fixed.GetOrAdd(profileId, _ => new FixedProfile());
        Volatile.Write(ref entry.LastDemand, now);
    }

    /// <summary>Letzte Auswertung nach dem Profil <paramref name="profileId"/> (null bis zum ersten Bild danach, oder
    /// wenn es das Profil nicht gibt).</summary>
    public ScreenAnalysis? LatestFor(string profileId) => _fixedLatest.TryGetValue(profileId, out var a) ? a : null;

    /// <summary>Gespeichertes Profil zur ID (dieselbe Instanz, die ausgewertet wird — nicht verändern).</summary>
    public ScreenProfile? FindProfile(string? profileId)
    {
        lock (_lock) return _profiles.FirstOrDefault(p => p.Id == profileId);
    }

    public IReadOnlyList<ScreenProfile> Profiles { get { lock (_lock) return _profiles; } }

    /// <summary>Verstärkung der Sättigung für Mischfarben und Ambilight (1 = unverändert), aus den Einstellungen.</summary>
    public double Saturation { get; set; } = 1.6;

    public bool IsDemanded => Environment.TickCount64 - Volatile.Read(ref _lastDemand) < DemandTimeoutMs;

    /// <summary>Aufnahme läuft und liefert (auch ein unverändertes Bild zählt — dann kommt nur ein Lebenszeichen).</summary>
    public bool IsLive => Environment.TickCount64 - Volatile.Read(ref _lastHeartbeat) < StaleAfterMs && _latest is not null;

    public ScreenAnalysis? Latest => _latest;
    public ScreenImage? LatestImage => _latestImage;

    /// <summary>Kurzer Zustandstext für die Einstellungen (Monitor, Methode, Bildrate).</summary>
    public string Status { get; set; } = "Aufnahme aus — kein Bildschirm-Effekt aktiv.";

    public ScreenProfile? CurrentProfile { get { lock (_lock) return _current; } }

    /// <summary>Alle Profile und das Standardprofil (ohne passendes Spiel im Vordergrund).</summary>
    public void SetProfiles(IReadOnlyList<ScreenProfile> profiles, string? defaultProfileId)
    {
        lock (_lock)
        {
            _profiles = profiles;
            _defaultProfileId = defaultProfileId;
        }
        SelectProfile();
    }

    /// <summary>Prozessname (ohne .exe) des Fensters im Vordergrund, von der Aufnahme regelmäßig gemeldet.</summary>
    public void SetForegroundProcess(string? processName)
    {
        lock (_lock)
        {
            if (string.Equals(_foregroundProcess, processName, StringComparison.OrdinalIgnoreCase)) return;
            _foregroundProcess = processName;
        }
        SelectProfile();
    }

    private void SelectProfile()
    {
        ScreenProfile? next, previous;
        lock (_lock)
        {
            previous = _current;
            next = _profiles.FirstOrDefault(p => p.ProcessName.Length > 0 && _foregroundProcess is not null &&
                                                 string.Equals(p.ProcessName, _foregroundProcess, StringComparison.OrdinalIgnoreCase))
                   ?? _profiles.FirstOrDefault(p => p.Id == _defaultProfileId);
            // Gleiches Profil (gleiche ID) aber neu gespeichert → neue Instanz übernehmen, sonst gilt die alte Fassung weiter.
            if (ReferenceEquals(next, previous)) return;
            _current = next;
            _analyzer = new ScreenAnalyzer(next);
        }
        if (previous?.Id != next?.Id)
            Log?.Invoke(LogLevel.Info, next is null ? "Bildschirm-Sync: kein Spiel-Profil aktiv." : $"Bildschirm-Sync: Profil „{next.Name}“ aktiv" +
                (next.ProcessName.Length > 0 && string.Equals(next.ProcessName, _foregroundProcess, StringComparison.OrdinalIgnoreCase) ? $" ({next.ProcessName} im Vordergrund)." : "."));
        ProfileChanged?.Invoke(next);
    }

    /// <summary>Neues Bild von der Aufnahme: auswerten und für die Effekte bereitstellen.</summary>
    public void Publish(ScreenImage image)
    {
        ScreenAnalysis analysis;
        var fixedLatest = new Dictionary<string, ScreenAnalysis>();
        long now = System.Diagnostics.Stopwatch.GetTimestamp();
        long tick = Environment.TickCount64;
        lock (_lock)
        {
            double saturation = Saturation;
            var overview = ScreenAnalyzer.Overview(image, saturation);
            _analyzer.Saturation = saturation;
            analysis = _analyzer.Analyze(image, now, overview);

            foreach (var (id, entry) in _fixed)
            {
                if (tick - Volatile.Read(ref entry.LastDemand) > DemandTimeoutMs)
                {
                    _fixed.TryRemove(id, out _);
                    continue;
                }
                var profile = _profiles.FirstOrDefault(p => p.Id == id);
                if (profile is null) continue;
                if (ReferenceEquals(profile, _current))
                {
                    // Dasselbe Profil wie das automatisch gewählte: nicht zweimal rechnen (und Blitze nicht doppelt erkennen).
                    fixedLatest[id] = analysis;
                    continue;
                }
                // Neu gespeichertes Profil = neue Instanz → neuer Analyzer, sonst gälte die alte Fassung weiter.
                if (entry.Analyzer is null || !ReferenceEquals(entry.Analyzer.Profile, profile)) entry.Analyzer = new ScreenAnalyzer(profile);
                entry.Analyzer.Saturation = saturation;
                fixedLatest[id] = entry.Analyzer.Analyze(image, now, overview);
            }
        }
        _latestImage = image;
        _latest = analysis;
        _fixedLatest = fixedLatest;
        Heartbeat();
    }

    /// <summary>Aufnahme läuft, Bild unverändert.</summary>
    public void Heartbeat() => Volatile.Write(ref _lastHeartbeat, Environment.TickCount64);

    /// <summary>Aufnahme beendet bzw. ausgefallen — Effekte zeigen dann nichts Veraltetes.</summary>
    public void Clear()
    {
        _latest = null;
        _latestImage = null;
        _fixedLatest = new Dictionary<string, ScreenAnalysis>();
    }

    public void Report(LogLevel level, string message) => Log?.Invoke(level, message);
}
