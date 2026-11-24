using System.Diagnostics;
using AetherRGB.Core.Diagnostics;

namespace AetherRGB.Core.Sensors;

/// <summary>Sammelt Systemwerte aus allen Quellen (Windows-Leistungsindikatoren, AIDA64, NVIDIA …) in einem eigenen
/// Thread. Abgefragt wird nur, solange jemand die Werte braucht (<see cref="Demand"/> — Sensor-Panel, Editor, REST-API),
/// danach ruht der Thread. Für Verlaufsgrafiken hält der Hub zu jedem Sensor die letzten Werte.</summary>
public sealed class SensorHub : IDisposable
{
    public static SensorHub Shared { get; } = new();

    /// <summary>Ohne Anfrage so lange wird weiter abgefragt (danach ruht der Thread).</summary>
    public const int DemandTimeoutMs = 10_000;
    /// <summary>So viele Werte pro Sensor für Verlaufsgrafiken (bei 1 s Takt = 10 Minuten).</summary>
    public const int HistoryLength = 600;
    public const int MinIntervalMs = 250, MaxIntervalMs = 10_000;

    private readonly object _lock = new();
    private readonly List<ISensorSource> _sources = new();
    private Dictionary<string, SensorReading> _snapshot = new();
    private readonly Dictionary<string, SensorHistory> _history = new();
    private readonly Dictionary<string, string> _lastErrors = new();
    private long _lastDemand = long.MinValue / 2;
    private int _intervalMs = 1000;
    private readonly AutoResetEvent _wake = new(false);
    private Thread? _thread;
    private volatile bool _stop;

    public event Action<LogLevel, string>? Log;

    /// <summary>Neue Werte liegen vor (Hub-Thread).</summary>
    public event Action? Updated;

    public int IntervalMs
    {
        get => _intervalMs;
        set { _intervalMs = Math.Clamp(value, MinIntervalMs, MaxIntervalMs); _wake.Set(); }
    }

    /// <summary>Zeitpunkt der letzten Abfrage (Stopwatch-Ticks), 0 = noch nie.</summary>
    public long LastPollTimestamp { get; private set; }

    public void AddSource(ISensorSource source)
    {
        lock (_lock) _sources.Add(source);
        _wake.Set();
    }

    public void RemoveSource(ISensorSource source)
    {
        lock (_lock) _sources.Remove(source);
        source.Dispose();
    }

    public IReadOnlyList<ISensorSource> Sources { get { lock (_lock) return _sources.ToList(); } }

    /// <summary>Hält die Abfrage am Laufen; beim ersten Aufruf nach einer Pause wird sofort abgefragt.</summary>
    public void Demand()
    {
        long now = Environment.TickCount64;
        long last = Interlocked.Exchange(ref _lastDemand, now);
        if (now - last >= DemandTimeoutMs) _wake.Set();
        EnsureThread();
    }

    public bool IsDemanded => Environment.TickCount64 - Volatile.Read(ref _lastDemand) < DemandTimeoutMs;

    /// <summary>Alle aktuellen Werte (fester Stand, kann gefahrlos gelesen werden).</summary>
    public IReadOnlyDictionary<string, SensorReading> Snapshot { get { lock (_lock) return _snapshot; } }

    public SensorReading? Get(string? id)
    {
        if (string.IsNullOrEmpty(id)) return null;
        lock (_lock) return _snapshot.TryGetValue(id, out var r) ? r : null;
    }

    /// <summary>Die letzten <paramref name="count"/> Werte des Sensors, ältester zuerst (NaN = keine Messung).</summary>
    public double[] GetHistory(string? id, int count)
    {
        if (string.IsNullOrEmpty(id) || count <= 0) return Array.Empty<double>();
        lock (_lock) return _history.TryGetValue(id, out var h) ? h.Last(count) : Array.Empty<double>();
    }

    /// <summary>Zur Prüfung ohne Thread (Tests): einmal alle Quellen abfragen.</summary>
    public void PollOnce()
    {
        List<ISensorSource> sources;
        lock (_lock) sources = _sources.ToList();
        var next = new Dictionary<string, SensorReading>();
        foreach (var source in sources)
        {
            IReadOnlyList<SensorReading> readings;
            try
            {
                readings = source.Poll();
                lock (_lock) _lastErrors.Remove(source.Name);
            }
            catch (Exception ex)
            {
                string text = ExceptionText.Describe(ex);
                bool repeat;
                lock (_lock) repeat = _lastErrors.TryGetValue(source.Name, out var prev) && prev == text;
                if (!repeat)
                {
                    lock (_lock) _lastErrors[source.Name] = text;
                    Log?.Invoke(LogLevel.Warning, $"Sensoren: {source.Name} fehlgeschlagen — {text}");
                }
                continue;
            }
            foreach (var r in readings)
                next.TryAdd(r.Id, r with { Source = source.Name }); // erste Quelle gewinnt bei gleicher ID
        }

        lock (_lock)
        {
            _snapshot = next;
            foreach (var (id, r) in next)
            {
                if (!_history.TryGetValue(id, out var h)) _history[id] = h = new SensorHistory(HistoryLength);
                h.Add(r.IsNumeric ? r.Value!.Value : double.NaN);
            }
            // Verschwundene Sensoren bekommen eine Lücke, damit Verläufe zeitlich stimmen.
            foreach (var (id, h) in _history)
                if (!next.ContainsKey(id)) h.Add(double.NaN);
        }
        LastPollTimestamp = Stopwatch.GetTimestamp();
        Updated?.Invoke();
    }

    private void EnsureThread()
    {
        if (_thread is not null) return;
        lock (_lock)
        {
            if (_thread is not null) return;
            _thread = new Thread(Run) { IsBackground = true, Name = "AetherRGB Sensoren", Priority = ThreadPriority.BelowNormal };
            _thread.Start();
        }
    }

    private void Run()
    {
        while (!_stop)
        {
            if (!IsDemanded)
            {
                _wake.WaitOne(1000);
                continue;
            }
            var start = Stopwatch.GetTimestamp();
            try { PollOnce(); }
            catch (Exception ex) { Log?.Invoke(LogLevel.Error, "Sensoren: Abfrage fehlgeschlagen — " + ExceptionText.Describe(ex)); }
            int spent = (int)Stopwatch.GetElapsedTime(start).TotalMilliseconds;
            _wake.WaitOne(Math.Max(20, _intervalMs - spent));
        }
    }

    public void Dispose()
    {
        _stop = true;
        _wake.Set();
        _thread?.Join(2000);
        lock (_lock)
        {
            foreach (var s in _sources) { try { s.Dispose(); } catch { } }
            _sources.Clear();
        }
    }
}

/// <summary>Ringpuffer fester Länge.</summary>
public sealed class SensorHistory
{
    private readonly double[] _values;
    private int _next, _count;

    public SensorHistory(int capacity) => _values = new double[Math.Max(1, capacity)];

    public int Count => _count;

    public void Add(double v)
    {
        _values[_next] = v;
        _next = (_next + 1) % _values.Length;
        if (_count < _values.Length) _count++;
    }

    public double[] Last(int count)
    {
        count = Math.Min(count, _count);
        var result = new double[count];
        int start = (_next - count + _values.Length) % _values.Length;
        for (int i = 0; i < count; i++) result[i] = _values[(start + i) % _values.Length];
        return result;
    }
}
