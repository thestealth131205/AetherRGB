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
        lock (_lock) analysis = _analyzer.Analyze(image);
        _latestImage = image;
        _latest = analysis;
        Heartbeat();
    }

    /// <summary>Aufnahme läuft, Bild unverändert.</summary>
    public void Heartbeat() => Volatile.Write(ref _lastHeartbeat, Environment.TickCount64);

    /// <summary>Aufnahme beendet bzw. ausgefallen — Effekte zeigen dann nichts Veraltetes.</summary>
    public void Clear()
    {
        _latest = null;
        _latestImage = null;
    }

    public void Report(LogLevel level, string message) => Log?.Invoke(level, message);
}
