using System.IO;
using System.Runtime.InteropServices;
using System.Text;
using AetherRGB.Core.Sensors;

namespace AetherRGB.App.Sensors;

/// <summary>NVIDIA-Grafikkarten über NVML (<c>nvml.dll</c>, kommt mit jedem NVIDIA-Treiber, wird nicht mitgeliefert):
/// Temperatur, Last, Grafikspeicher, Leistungsaufnahme, Lüfter, Takte. Ohne NVIDIA-Karte meldet die Quelle nur
/// „nicht vorhanden“.</summary>
public sealed unsafe class NvmlSensorSource : ISensorSource
{
    private IntPtr _lib;
    private bool _initialized, _failed;
    private readonly List<(IntPtr Handle, string Name)> _devices = new();
    private string _status = "noch nicht abgefragt";

    private delegate* unmanaged[Cdecl]<int> _init, _shutdown;
    private delegate* unmanaged[Cdecl]<uint*, int> _count;
    private delegate* unmanaged[Cdecl]<uint, IntPtr*, int> _handle;
    private delegate* unmanaged[Cdecl]<IntPtr, byte*, uint, int> _name;
    private delegate* unmanaged[Cdecl]<IntPtr, int, uint*, int> _temp;
    private delegate* unmanaged[Cdecl]<IntPtr, Utilization*, int> _util;
    private delegate* unmanaged[Cdecl]<IntPtr, Memory*, int> _mem;
    private delegate* unmanaged[Cdecl]<IntPtr, uint*, int> _power, _fan;
    private delegate* unmanaged[Cdecl]<IntPtr, int, uint*, int> _clock;

    [StructLayout(LayoutKind.Sequential)] private struct Utilization { public uint Gpu, Memory; }
    [StructLayout(LayoutKind.Sequential)] private struct Memory { public ulong Total, Free, Used; }

    public string Name => "NVIDIA";
    public string Status => _status;
    public bool IsAvailable => _initialized && _devices.Count > 0;

    public IReadOnlyList<SensorReading> Poll()
    {
        if (!_initialized && !_failed) Initialize();
        if (!_initialized) return Array.Empty<SensorReading>();
        var list = new List<SensorReading>();
        for (int i = 0; i < _devices.Count; i++)
        {
            var (h, name) = _devices[i];
            string p = _devices.Count > 1 ? $"NVIDIA {i + 1} " : "NVIDIA ";
            list.Add(new SensorReading(NvmlSensorIds.Name(i), p + "Modell", SensorGroups.Gpu, "", null, name));
            uint v;
            if (_temp(h, 0, &v) == 0) list.Add(new SensorReading(NvmlSensorIds.Temp(i), p + "GPU-Temperatur", SensorGroups.Temperature, "°C", v));
            Utilization u;
            if (_util(h, &u) == 0)
            {
                list.Add(new SensorReading(NvmlSensorIds.Load(i), p + "GPU-Last", SensorGroups.Gpu, "%", u.Gpu));
                list.Add(new SensorReading(NvmlSensorIds.MemLoad(i), p + "Speicher-Controller-Last", SensorGroups.Gpu, "%", u.Memory));
            }
            Memory m;
            if (_mem(h, &m) == 0)
            {
                list.Add(new SensorReading(NvmlSensorIds.VramUsed(i), p + "Grafikspeicher belegt", SensorGroups.Gpu, "B", m.Used));
                list.Add(new SensorReading(NvmlSensorIds.VramTotal(i), p + "Grafikspeicher gesamt", SensorGroups.Gpu, "B", m.Total));
                if (m.Total > 0) list.Add(new SensorReading($"nvml/{i}/vram/load", p + "Grafikspeicher belegt (%)", SensorGroups.Gpu, "%", m.Used * 100.0 / m.Total));
            }
            if (_power(h, &v) == 0) list.Add(new SensorReading(NvmlSensorIds.Power(i), p + "Leistungsaufnahme", SensorGroups.Power, "W", v / 1000.0));
            if (_fan(h, &v) == 0) list.Add(new SensorReading(NvmlSensorIds.Fan(i), p + "Lüfter", SensorGroups.Fan, "%", v));
            if (_clock(h, 0, &v) == 0) list.Add(new SensorReading(NvmlSensorIds.CoreClock(i), p + "GPU-Takt", SensorGroups.Gpu, "MHz", v));
            if (_clock(h, 2, &v) == 0) list.Add(new SensorReading(NvmlSensorIds.MemClock(i), p + "Speichertakt", SensorGroups.Gpu, "MHz", v));
        }
        _status = $"verbunden, {_devices.Count} Karte(n)";
        return list;
    }

    private void Initialize()
    {
        _failed = true;
        var candidates = new[]
        {
            Path.Combine(Environment.SystemDirectory, "nvml.dll"),
            Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles), "NVIDIA Corporation", "NVSMI", "nvml.dll"),
        };
        foreach (var path in candidates)
            if (File.Exists(path) && NativeLibrary.TryLoad(path, out _lib)) break;
        if (_lib == IntPtr.Zero) { _status = "nicht vorhanden (keine NVIDIA-Karte/-Treiber)"; return; }
        try
        {
            _init = (delegate* unmanaged[Cdecl]<int>)Export("nvmlInit_v2");
            _shutdown = (delegate* unmanaged[Cdecl]<int>)Export("nvmlShutdown");
            _count = (delegate* unmanaged[Cdecl]<uint*, int>)Export("nvmlDeviceGetCount_v2");
            _handle = (delegate* unmanaged[Cdecl]<uint, IntPtr*, int>)Export("nvmlDeviceGetHandleByIndex_v2");
            _name = (delegate* unmanaged[Cdecl]<IntPtr, byte*, uint, int>)Export("nvmlDeviceGetName");
            _temp = (delegate* unmanaged[Cdecl]<IntPtr, int, uint*, int>)Export("nvmlDeviceGetTemperature");
            _util = (delegate* unmanaged[Cdecl]<IntPtr, Utilization*, int>)Export("nvmlDeviceGetUtilizationRates");
            _mem = (delegate* unmanaged[Cdecl]<IntPtr, Memory*, int>)Export("nvmlDeviceGetMemoryInfo");
            _power = (delegate* unmanaged[Cdecl]<IntPtr, uint*, int>)Export("nvmlDeviceGetPowerUsage");
            _fan = (delegate* unmanaged[Cdecl]<IntPtr, uint*, int>)Export("nvmlDeviceGetFanSpeed");
            _clock = (delegate* unmanaged[Cdecl]<IntPtr, int, uint*, int>)Export("nvmlDeviceGetClockInfo");
        }
        catch (EntryPointNotFoundException ex) { _status = "nvml.dll zu alt: " + ex.Message; return; }

        int rc = _init();
        if (rc != 0) { _status = $"nvmlInit fehlgeschlagen (Code {rc})"; return; }
        uint count;
        if (_count(&count) != 0) count = 0;
        var buf = new byte[96];
        for (uint i = 0; i < count; i++)
        {
            IntPtr h;
            if (_handle(i, &h) != 0) continue;
            string name = $"NVIDIA {i + 1}";
            fixed (byte* b = buf)
            {
                if (_name(h, b, (uint)buf.Length) == 0)
                {
                    int end = Array.IndexOf(buf, (byte)0);
                    name = Encoding.ASCII.GetString(buf, 0, end < 0 ? buf.Length : end).Trim();
                }
            }
            _devices.Add((h, name));
        }
        _initialized = true;
        _failed = false;
        _status = _devices.Count > 0 ? $"verbunden, {_devices.Count} Karte(n)" : "keine NVIDIA-Karte gefunden";
    }

    private IntPtr Export(string name) => NativeLibrary.GetExport(_lib, name);

    public void Dispose()
    {
        if (_initialized) { try { _shutdown(); } catch { } }
        _initialized = false;
        if (_lib != IntPtr.Zero) NativeLibrary.Free(_lib);
        _lib = IntPtr.Zero;
    }
}
