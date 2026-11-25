using AetherRGB.App.Cooling;
using AetherRGB.Core.Config;
using AetherRGB.Core.Cooling;
using AetherRGB.Core.Diagnostics;
using AetherRGB.Core.Sensors;

namespace AetherRGB.App;

/// <summary>Verbindet die Lüftererkennung (LibreHardwareMonitor), die Temperaturregelung
/// (<see cref="CoolingController"/>) und das Kühlung-Fenster. Spiegelt den Aufbau von
/// <c>SensorPanelService</c>.</summary>
public sealed class CoolingService : IDisposable
{
    private readonly Func<CoolingConfig> _config;
    private readonly SensorHub _hub = SensorHub.Shared;
    private LibreHardwareMonitorFanSource? _source;
    private CoolingController? _controller;
    private CoolingWindow? _window;

    public event Action<string, LogLevel>? LogMessage;

    public CoolingService(Func<CoolingConfig> config)
    {
        _config = config;
        try
        {
            _source = new LibreHardwareMonitorFanSource();
            _hub.AddSource(_source);
            _controller = new CoolingController(_source, _hub, _config);
            _controller.Log += (level, msg) => Log(msg, level);
            _controller.Start();
            Log(_source.IsAvailable
                ? "Kühlung: mit LibreHardwareMonitor verbunden."
                : "Kühlung: " + _source.Status, _source.IsAvailable ? LogLevel.Debug : LogLevel.Warning);
        }
        catch (Exception ex)
        {
            Log("Kühlung konnte nicht gestartet werden: " + ExceptionText.Describe(ex), LogLevel.Error);
        }
    }

    public bool IsAvailable => _source?.IsAvailable ?? false;
    public string Status => _source?.Status ?? "nicht verfügbar";

    /// <summary>Für den Lüfter-Dialog: Lüfter neu einlesen.</summary>
    public IReadOnlyList<FanInfo> Detect() => _source?.Detect() ?? Array.Empty<FanInfo>();

    public void SetPercent(string fanId, int percent) => _source?.SetPercent(fanId, percent);

    public async Task<List<FanProfileConfig>> CalibrateAsync(IProgress<CalibrationProgress>? progress, CancellationToken ct)
    {
        if (_source is null) throw new InvalidOperationException("Kühlung ist nicht verbunden.");
        var calibrator = new FanCalibrator(_source, ReadCpuTemp, ReadBoardTemp);
        return await calibrator.RunAsync(_config().Fans, progress, ct);
    }

    private double ReadCpuTemp()
    {
        var r = _hub.Snapshot.Values.FirstOrDefault(v =>
            v.IsNumeric && v.Group == SensorGroups.Temperature && v.Name.Contains("CPU", StringComparison.OrdinalIgnoreCase));
        return r?.Value ?? throw new InvalidOperationException("Keine CPU-Temperatur gefunden.");
    }

    private double ReadBoardTemp()
    {
        var r = _hub.Snapshot.Values.FirstOrDefault(v =>
            v.IsNumeric && v.Group == SensorGroups.Temperature &&
            (v.Name.Contains("Mainboard", StringComparison.OrdinalIgnoreCase) ||
             v.Name.Contains("Motherboard", StringComparison.OrdinalIgnoreCase) ||
             v.Name.Contains("System", StringComparison.OrdinalIgnoreCase)));
        return r?.Value ?? ReadCpuTemp(); // kein eigener Gehäusesensor -> CPU-Temperatur als Näherung
    }

    /// <summary>Nach dem Speichern der Einstellungen: Regler sofort neu takten lassen.</summary>
    public void Apply() => _controller?.Nudge();

    public void OpenWindow(System.Windows.Window? owner)
    {
        if (_window is not null)
        {
            if (_window.WindowState == System.Windows.WindowState.Minimized) _window.WindowState = System.Windows.WindowState.Normal;
            _window.Activate();
            return;
        }
        _window = new CoolingWindow(this, _config()) { Owner = owner };
        _window.LogMessage += Log;
        _window.SaveConfig += () => SaveConfig?.Invoke();
        _window.Closed += (_, _) => _window = null;
        _window.Show();
    }

    public Action? SaveConfig { get; set; }

    private void Log(string message, LogLevel level = LogLevel.Info) => LogMessage?.Invoke(message, level);

    public void Dispose()
    {
        _window?.Close();
        _controller?.Dispose();
        if (_source is not null) _hub.RemoveSource(_source);
    }
}
