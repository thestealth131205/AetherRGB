using AetherRGB.Core.Config;
using AetherRGB.Core.Diagnostics;
using AetherRGB.Core.Sensors;

namespace AetherRGB.Core.Cooling;

/// <summary>Regelt alle erkannten Lüfter anhand ihrer Kurve/Temperatur nach — eigener Thread, läuft nur,
/// solange <see cref="CoolingConfig.Enabled"/> an ist. Ändert die Ansteuerung sanft (höchstens 4 %/Takt) und
/// nicht öfter als nötig, damit der Controller nicht mit Befehlen überschüttet wird.</summary>
public sealed class CoolingController : IDisposable
{
    private const int MaxStepPercent = 4;
    private const int MinResendDiff = 2;

    private readonly IFanControlSource _source;
    private readonly SensorHub _hub;
    private readonly Func<CoolingConfig> _config;
    private readonly Dictionary<string, double> _currentPercent = new();
    private readonly AutoResetEvent _wake = new(false);
    private Thread? _thread;
    private volatile bool _stop;

    public event Action<LogLevel, string>? Log;
    /// <summary>Nach jedem Takt (für die Oberfläche) — aktuelle Lüfter mit Drehzahl/Ansteuerung.</summary>
    public event Action? Updated;

    public IReadOnlyList<FanInfo> LastFans { get; private set; } = Array.Empty<FanInfo>();

    public CoolingController(IFanControlSource source, SensorHub hub, Func<CoolingConfig> config)
    {
        _source = source;
        _hub = hub;
        _config = config;
    }

    public void Start()
    {
        if (_thread is not null) return;
        _hub.Demand();
        _thread = new Thread(Run) { IsBackground = true, Name = "AetherRGB Kühlung", Priority = ThreadPriority.BelowNormal };
        _thread.Start();
    }

    public void Nudge() => _wake.Set();

    private void Run()
    {
        while (!_stop)
        {
            var cfg = _config();
            if (!cfg.Enabled)
            {
                _wake.WaitOne(1000);
                continue;
            }
            _hub.Demand();
            try { Tick(cfg); }
            catch (Exception ex) { Log?.Invoke(LogLevel.Warning, "Kühlung: Takt fehlgeschlagen — " + ExceptionText.Describe(ex)); }
            _wake.WaitOne(Math.Clamp(cfg.IntervalMs, 250, 10_000));
        }
    }

    private void Tick(CoolingConfig cfg)
    {
        IReadOnlyList<FanInfo> fans;
        try { fans = _source.Detect(); }
        catch (Exception ex)
        {
            Log?.Invoke(LogLevel.Warning, "Kühlung: Lüfter konnten nicht abgefragt werden — " + ExceptionText.Describe(ex));
            return;
        }
        LastFans = fans;

        foreach (var fan in fans)
        {
            if (!fan.Controllable) continue;
            var profile = cfg.Fans.FirstOrDefault(f => f.FanId == fan.Id);
            if (profile is null) continue; // noch nie im Fenster gesehen -> unverändert lassen

            int target = profile.Mode == CoolingFanMode.Manual
                ? profile.ManualPercent
                : FanCurve.Evaluate(profile.Curve, ReadTemp(profile));

            if (!_currentPercent.TryGetValue(fan.Id, out var current))
                current = fan.Percent ?? target;

            double diff = target - current;
            double step = Math.Clamp(diff, -MaxStepPercent, MaxStepPercent);
            double next = current + step;

            if (Math.Abs(next - current) >= MinResendDiff || Math.Abs(next - target) < 0.5)
            {
                try { _source.SetPercent(fan.Id, (int)Math.Round(next)); }
                catch (Exception ex) { Log?.Invoke(LogLevel.Warning, $"Kühlung: {fan.Name} konnte nicht gesetzt werden — " + ExceptionText.Describe(ex)); }
            }
            _currentPercent[fan.Id] = next;
        }
        Updated?.Invoke();
    }

    /// <summary>Temperatur für die Kurve: die eingestellte Sensor-ID, sonst die erste gefundene CPU-Temperatur,
    /// sonst 50 °C (sicherer Mittelwert statt 0 — sonst würde die Kurve dauerhaft auf Minimum bleiben).</summary>
    private double ReadTemp(FanProfileConfig profile)
    {
        if (!string.IsNullOrEmpty(profile.TempSensorId) && _hub.Get(profile.TempSensorId) is { IsNumeric: true } r)
            return r.Value!.Value;
        var cpu = _hub.Snapshot.Values.FirstOrDefault(v =>
            v.IsNumeric && v.Group == SensorGroups.Temperature &&
            (v.Id.Contains("cpu", StringComparison.OrdinalIgnoreCase) || v.Name.Contains("CPU", StringComparison.OrdinalIgnoreCase)));
        if (cpu is not null) return cpu.Value!.Value;
        var any = _hub.Snapshot.Values.FirstOrDefault(v => v.IsNumeric && v.Group == SensorGroups.Temperature);
        return any?.Value ?? 50;
    }

    public void Dispose()
    {
        _stop = true;
        _wake.Set();
        _thread?.Join(2000);
    }
}
