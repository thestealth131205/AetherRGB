using AetherRGB.App;
using AetherRGB.Core.Cooling;
using AetherRGB.Core.Diagnostics;
using AetherRGB.Core.Sensors;
using LibreHardwareMonitor.Hardware;

namespace AetherRGB.App.Cooling;

/// <summary>Erkennt und steuert Lüfter über die Super-I/O-Chips des Mainboards (und AIO-/Lüfter-Controller)
/// mit <c>LibreHardwareMonitorLib</c> — dieselbe, geprüfte Bibliothek, die auch FanControl und
/// OpenHardwareMonitor benutzen. Braucht Adminrechte (lädt den WinRing0-Treiber). Liefert außerdem
/// CPU-/Mainboard-Temperaturen als <see cref="ISensorSource"/> für den <c>SensorHub</c> — damit funktioniert
/// die Temperaturkurve auch ohne AIDA64.</summary>
public sealed class LibreHardwareMonitorFanSource : IFanControlSource, ISensorSource
{
    private const string IdPrefix = "lhm/";

    private readonly object _lock = new();
    private readonly Computer _computer = new()
    {
        IsMotherboardEnabled = true,
        IsControllerEnabled = true,
        IsCpuEnabled = true,
    };
    private readonly UpdateVisitor _visitor = new();
    private readonly HashSet<string> _touchedFanIds = new();
    private bool _opened;
    private string _status = "noch nicht verbunden";

    public string Name => "LibreHardwareMonitor";
    public string Status { get { lock (_lock) return _status; } }
    public bool IsAvailable { get { lock (_lock) return _opened; } }

    public LibreHardwareMonitorFanSource()
    {
        lock (_lock)
        {
            try
            {
                _computer.Open();
                _opened = true;
                _status = "verbunden";
            }
            catch (Exception ex)
            {
                _status = "nicht verbunden — " + ExceptionText.Describe(ex) +
                           (ChromaBridgeInstaller.IsElevated() ? "" : " (keine Administratorrechte)");
            }
        }
    }

    // ===================== IFanControlSource =====================

    public IReadOnlyList<FanInfo> Detect()
    {
        lock (_lock)
        {
            if (!_opened) return Array.Empty<FanInfo>();
            try { _computer.Accept(_visitor); }
            catch (Exception ex)
            {
                _status = "Abfrage fehlgeschlagen — " + ExceptionText.Describe(ex);
                return Array.Empty<FanInfo>();
            }

            var result = new List<FanInfo>();
            foreach (var hw in AllHardware(_computer.Hardware))
            {
                foreach (var sensor in hw.Sensors)
                {
                    if (sensor.SensorType != SensorType.Control || sensor.Control is null) continue;
                    var rpmSensor = hw.Sensors.FirstOrDefault(s => s.SensorType == SensorType.Fan && s.Index == sensor.Index);
                    string id = IdPrefix + sensor.Identifier;
                    string name = rpmSensor?.Name ?? sensor.Name ?? $"Lüfter {sensor.Index + 1}";
                    if (!string.Equals(hw.Name, name, StringComparison.OrdinalIgnoreCase)) name = $"{hw.Name} — {name}";
                    result.Add(new FanInfo(id, name, true, rpmSensor?.Value, (int?)sensor.Value));
                }
            }
            _status = $"verbunden, {result.Count} Lüfter";
            return result;
        }
    }

    public void SetPercent(string fanId, int percent)
    {
        lock (_lock)
        {
            if (FindControl(fanId) is not { } control) return;
            float clamped = Math.Clamp(percent, control.MinSoftwareValue, control.MaxSoftwareValue);
            control.SetSoftware(clamped);
            _touchedFanIds.Add(fanId);
        }
    }

    public void ReleaseControl(string fanId)
    {
        lock (_lock)
        {
            FindControl(fanId)?.SetDefault();
            _touchedFanIds.Remove(fanId);
        }
    }

    private IControl? FindControl(string fanId)
    {
        if (!_opened) return null;
        foreach (var hw in AllHardware(_computer.Hardware))
            foreach (var sensor in hw.Sensors)
                if (sensor.SensorType == SensorType.Control && sensor.Control is { } c && IdPrefix + sensor.Identifier == fanId)
                    return c;
        return null;
    }

    // ===================== ISensorSource (CPU-/Mainboard-Temperaturen für den SensorHub) =====================

    public IReadOnlyList<SensorReading> Poll()
    {
        lock (_lock)
        {
            if (!_opened) return Array.Empty<SensorReading>();
            _computer.Accept(_visitor);
            var result = new List<SensorReading>();
            foreach (var hw in AllHardware(_computer.Hardware))
            {
                foreach (var sensor in hw.Sensors)
                {
                    if (sensor.SensorType != SensorType.Temperature || sensor.Value is not { } v || !float.IsFinite(v)) continue;
                    string id = IdPrefix + "temp/" + sensor.Identifier;
                    string name = hw.HardwareType == HardwareType.Cpu ? $"CPU — {sensor.Name}" : $"{hw.Name} — {sensor.Name}";
                    result.Add(new SensorReading(id, name, SensorGroups.Temperature, "°C", v));
                }
            }
            return result;
        }
    }

    private static IEnumerable<IHardware> AllHardware(IEnumerable<IHardware> top)
    {
        foreach (var hw in top)
        {
            yield return hw;
            foreach (var sub in AllHardware(hw.SubHardware)) yield return sub;
        }
    }

    public void Dispose()
    {
        lock (_lock)
        {
            if (!_opened) return;
            foreach (var fanId in _touchedFanIds)
                try { FindControl(fanId)?.SetDefault(); } catch { /* beim Beenden egal */ }
            try { _computer.Close(); } catch { /* beim Beenden egal */ }
            _opened = false;
        }
    }

    private sealed class UpdateVisitor : IVisitor
    {
        public void VisitComputer(IComputer computer) => computer.Traverse(this);
        public void VisitHardware(IHardware hardware) { hardware.Update(); hardware.Traverse(this); }
        public void VisitSensor(ISensor sensor) { }
        public void VisitParameter(IParameter parameter) { }
    }
}
