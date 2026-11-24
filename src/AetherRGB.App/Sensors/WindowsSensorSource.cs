using System.Globalization;
using System.IO;
using System.Runtime.InteropServices;
using System.Text.RegularExpressions;
using AetherRGB.Core.Sensors;
using Microsoft.Win32;
using Vortice.DXGI;

namespace AetherRGB.App.Sensors;

/// <summary>Eingebaute Messwerte ohne Zusatzprogramm: CPU-Last und -Takt, Arbeitsspeicher, GPU-Last je Engine und
/// VRAM (Windows-GPU-Zähler wie im Task-Manager), Aktivität und Datenrate je Laufwerk mit Art (NVMe/SSD/HDD), NVMe-
/// Temperatur, Belegung der Partitionen, Netzwerk. Alles über Leistungsindikatoren und Abfragen ohne Treiber —
/// CPU-/Mainboard-Temperaturen und Lüfter gibt es nur mit AIDA64.</summary>
public sealed class WindowsSensorSource : ISensorSource
{
    private const string CpuUtility = @"\Processor Information(_Total)\% Processor Utility";
    private const string CpuTime = @"\Processor(_Total)\% Processor Time";
    private const string CoreUtility = @"\Processor Information(*)\% Processor Utility";
    private const string CpuFrequency = @"\Processor Information(_Total)\Processor Frequency";
    private const string CpuPerformance = @"\Processor Information(_Total)\% Processor Performance";
    private const string Processes = @"\System\Processes";
    private const string Threads = @"\System\Threads";
    private const string Commit = @"\Memory\% Committed Bytes In Use";
    private const string GpuEngine = @"\GPU Engine(*)\Utilization Percentage";
    private const string GpuMemory = @"\GPU Adapter Memory(*)\Dedicated Usage";
    private const string DiskIdle = @"\PhysicalDisk(*)\% Idle Time";
    private const string DiskRead = @"\PhysicalDisk(*)\Disk Read Bytes/sec";
    private const string DiskWrite = @"\PhysicalDisk(*)\Disk Write Bytes/sec";
    private const string NetDown = @"\Network Interface(*)\Bytes Received/sec";
    private const string NetUp = @"\Network Interface(*)\Bytes Sent/sec";

    private static readonly Regex EngineRx = new(@"luid_(0x[0-9A-Fa-f]+_0x[0-9A-Fa-f]+)_phys_(\d+)_eng_(\d+)_engtype_(.+)$", RegexOptions.Compiled);
    private static readonly Regex AdapterRx = new(@"luid_(0x[0-9A-Fa-f]+_0x[0-9A-Fa-f]+)_phys_(\d+)", RegexOptions.Compiled);

    private PdhQuery? _pdh;
    /// <summary>GPU-Engines in eigener Abfrage: <see cref="_gpuNext"/> wird vorbereitet (erste Messung) und erst im nächsten
    /// Takt übernommen, wenn sie zwei Messungen hat — so gibt es beim Neuanlegen keine Lücke.</summary>
    private PdhQuery? _gpuQuery, _gpuNext;
    private bool _cpuUtility;
    private long _lastGpuRebuild, _lastDiskScan, _lastSlow;
    private readonly Dictionary<int, StorageInfo.DiskDescription> _disks = new();
    private readonly Dictionary<int, double?> _diskTemps = new();
    private List<GpuAdapter> _gpus = new();
    private List<SensorReading> _slow = new();
    private readonly string _cpuName = ReadCpuName();
    private string _status = "noch nicht abgefragt";

    public string Name => "Windows";
    public string Status => _status;
    public bool IsAvailable { get; private set; }

    private sealed record GpuAdapter(int Index, string Luid, string Name, ulong DedicatedMemory);

    public IReadOnlyList<SensorReading> Poll()
    {
        long now = Environment.TickCount64;
        if (_pdh is null) Build();
        var pdh = _pdh!;

        // GPU-Engines kommen und gehen mit den Prozessen — die Abfrage regelmäßig neu anlegen, damit neue Instanzen sicher dabei sind.
        if (_gpuNext is not null)
        {
            _gpuNext.Collect();
            _gpuQuery?.Dispose();
            _gpuQuery = _gpuNext;
            _gpuNext = null;
        }
        else _gpuQuery?.Collect();
        if (now - _lastGpuRebuild > 30_000)
        {
            _lastGpuRebuild = now;
            _gpus = EnumerateGpus();
            var next = new PdhQuery();
            if (next.Add(GpuEngine))
            {
                next.Collect();
                if (_gpuQuery is null) { _gpuQuery = next; } // allererster Start: ab dem nächsten Takt gültig
                else _gpuNext = next;
            }
            else next.Dispose();
        }
        pdh.Collect();

        var list = new List<SensorReading>(160);
        ReadCpu(pdh, list);
        ReadMemory(pdh, list);
        ReadGpu(pdh, list);
        ReadDisks(pdh, list, now);
        ReadNetwork(pdh, list);

        var up = TimeSpan.FromMilliseconds(Environment.TickCount64);
        list.Add(new SensorReading(WinSensorIds.Uptime, "Laufzeit seit Systemstart", SensorGroups.System, "", null,
            up.TotalDays >= 1 ? $"{(int)up.TotalDays} T {up:hh\\:mm}" : up.ToString(@"hh\:mm\:ss")));
        list.Add(new SensorReading(WinSensorIds.UptimeHours, "Laufzeit (Stunden)", SensorGroups.System, "h", up.TotalHours));

        // Partitionen nur alle 10 s (Dateisystem-Abfrage)
        if (now - _lastSlow > 10_000 || _slow.Count == 0)
        {
            _lastSlow = now;
            _slow = ReadVolumes();
        }
        list.AddRange(_slow);

        IsAvailable = true;
        _status = $"{list.Count} Werte (CPU, RAM, {_gpus.Count} GPU, {_disks.Count} Laufwerk(e), Netzwerk)";
        return list;
    }

    private void Build()
    {
        var pdh = new PdhQuery();
        _cpuUtility = pdh.Add(CpuUtility);
        if (!_cpuUtility) pdh.Add(CpuTime);
        foreach (var p in new[] { CoreUtility, CpuFrequency, CpuPerformance, Processes, Threads, Commit, GpuMemory, DiskIdle, DiskRead, DiskWrite, NetDown, NetUp })
            pdh.Add(p);
        pdh.Collect(); // Raten brauchen zwei Messungen
        _pdh = pdh;
    }

    private void ReadCpu(PdhQuery pdh, List<SensorReading> list)
    {
        double load = pdh.Value(_cpuUtility ? CpuUtility : CpuTime);
        list.Add(new SensorReading(WinSensorIds.CpuLoad, "CPU-Last", SensorGroups.Cpu, "%", Clamp100(load)));
        list.Add(new SensorReading(WinSensorIds.CpuName, "CPU-Modell", SensorGroups.Cpu, "", null, _cpuName));

        double freq = pdh.Value(CpuFrequency), perf = pdh.Value(CpuPerformance);
        if (double.IsFinite(freq) && double.IsFinite(perf) && freq > 0)
            list.Add(new SensorReading(WinSensorIds.CpuClock, "CPU-Takt (aktuell)", SensorGroups.Cpu, "MHz", freq * perf / 100.0));

        // Instanzen "0,3" = Gruppe 0, Kern 3; "_Total"/"0,_Total" auslassen.
        var cores = pdh.Array(CoreUtility)
            .Select(i => (Parts: i.Instance.Split(','), i.Value))
            .Where(c => c.Parts.Length == 2 && int.TryParse(c.Parts[0], out _) && int.TryParse(c.Parts[1], out _))
            .OrderBy(c => int.Parse(c.Parts[0])).ThenBy(c => int.Parse(c.Parts[1]))
            .ToList();
        for (int i = 0; i < cores.Count; i++)
            list.Add(new SensorReading(WinSensorIds.CpuCoreLoad(i), $"Logischer Kern {i + 1}", SensorGroups.Cpu, "%", Clamp100(cores[i].Value)));

        AddIfFinite(list, WinSensorIds.Processes, "Prozesse", SensorGroups.System, "", pdh.Value(Processes));
        AddIfFinite(list, WinSensorIds.Threads, "Threads", SensorGroups.System, "", pdh.Value(Threads));
    }

    private static void ReadMemory(PdhQuery pdh, List<SensorReading> list)
    {
        var status = new MEMORYSTATUSEX { dwLength = (uint)Marshal.SizeOf<MEMORYSTATUSEX>() };
        if (GlobalMemoryStatusEx(ref status))
        {
            double total = status.ullTotalPhys, free = status.ullAvailPhys;
            list.Add(new SensorReading(WinSensorIds.MemLoad, "Arbeitsspeicher belegt", SensorGroups.Memory, "%", total > 0 ? (total - free) / total * 100 : double.NaN));
            list.Add(new SensorReading(WinSensorIds.MemUsed, "Arbeitsspeicher belegt", SensorGroups.Memory, "B", total - free));
            list.Add(new SensorReading(WinSensorIds.MemFree, "Arbeitsspeicher frei", SensorGroups.Memory, "B", free));
            list.Add(new SensorReading(WinSensorIds.MemTotal, "Arbeitsspeicher gesamt", SensorGroups.Memory, "B", total));
        }
        AddIfFinite(list, WinSensorIds.CommitLoad, "Zugesicherter Speicher (Commit)", SensorGroups.Memory, "%", Clamp100(pdh.Value(Commit)));
    }

    private void ReadGpu(PdhQuery pdh, List<SensorReading> list)
    {
        // Wie der Task-Manager: Auslastung je Engine = Summe über alle Prozesse, GPU-Last = die stärkste Engine.
        var engines = new Dictionary<(string Luid, string Engine), (string Type, double Value)>();
        foreach (var (instance, value) in _gpuQuery?.Array(GpuEngine) ?? new())
        {
            var m = EngineRx.Match(instance);
            if (!m.Success) continue;
            var key = (m.Groups[1].Value.ToLowerInvariant(), m.Groups[2].Value + "_" + m.Groups[3].Value);
            string type = EngineType(m.Groups[4].Value);
            engines[key] = engines.TryGetValue(key, out var prev) ? (type, prev.Value + value) : (type, value);
        }
        var memory = pdh.Array(GpuMemory)
            .Select(i => (Match: AdapterRx.Match(i.Instance), i.Value))
            .Where(i => i.Match.Success)
            .GroupBy(i => i.Match.Groups[1].Value.ToLowerInvariant())
            .ToDictionary(g => g.Key, g => g.Sum(x => x.Value));

        var adapters = _gpus.Count > 0 ? _gpus
            : engines.Keys.Select(k => k.Luid).Distinct().Select((l, i) => new GpuAdapter(i, l, $"GPU {i + 1}", 0)).ToList();
        foreach (var gpu in adapters)
        {
            var own = engines.Where(e => e.Key.Luid == gpu.Luid).Select(e => e.Value).ToList();
            string prefix = adapters.Count > 1 ? $"GPU {gpu.Index + 1} " : "GPU ";
            list.Add(new SensorReading(WinSensorIds.GpuName(gpu.Index), prefix + "Modell", SensorGroups.Gpu, "", null, gpu.Name));
            if (own.Count > 0)
            {
                list.Add(new SensorReading(WinSensorIds.GpuLoad(gpu.Index), prefix + "Last", SensorGroups.Gpu, "%", Clamp100(own.Max(e => e.Value))));
                foreach (var g in own.GroupBy(e => e.Type).OrderBy(g => g.Key))
                    list.Add(new SensorReading(WinSensorIds.GpuEngine(gpu.Index, g.Key), prefix + EngineLabel(g.Key), SensorGroups.Gpu, "%", Clamp100(g.Max(e => e.Value))));
            }
            if (memory.TryGetValue(gpu.Luid, out var used))
            {
                list.Add(new SensorReading(WinSensorIds.GpuVramUsed(gpu.Index), prefix + "Grafikspeicher belegt", SensorGroups.Gpu, "B", used));
                if (gpu.DedicatedMemory > 0)
                    list.Add(new SensorReading(WinSensorIds.GpuVramLoad(gpu.Index), prefix + "Grafikspeicher belegt (%)", SensorGroups.Gpu, "%", Clamp100(used / gpu.DedicatedMemory * 100)));
            }
        }
    }

    private static string EngineType(string raw)
    {
        var r = raw.ToLowerInvariant();
        if (r.StartsWith("3d")) return "3d";
        if (r.StartsWith("videodecode")) return "decode";
        if (r.StartsWith("videoencode")) return "encode";
        if (r.StartsWith("copy")) return "copy";
        if (r.StartsWith("compute")) return "compute";
        return new string(r.Where(char.IsAsciiLetterOrDigit).Take(20).ToArray());
    }

    private static string EngineLabel(string type) => type switch
    {
        "3d" => "3D",
        "decode" => "Video-Dekodierung",
        "encode" => "Video-Kodierung",
        "copy" => "Kopieren",
        "compute" => "Compute",
        _ => type,
    };

    private void ReadDisks(PdhQuery pdh, List<SensorReading> list, long now)
    {
        var idle = ByDisk(pdh.Array(DiskIdle));
        var read = ByDisk(pdh.Array(DiskRead));
        var write = ByDisk(pdh.Array(DiskWrite));

        // Datenträgerart und Temperatur nicht jede Sekunde abfragen.
        bool rescan = now - _lastDiskScan > 10_000 || idle.Keys.Any(n => !_disks.ContainsKey(n));
        if (rescan)
        {
            _lastDiskScan = now;
            foreach (var n in idle.Keys)
            {
                if (!_disks.ContainsKey(n)) _disks[n] = StorageInfo.Describe(n);
                if (_disks[n].Kind == DiskKind.Nvme) _diskTemps[n] = StorageInfo.Temperature(n);
            }
            foreach (var gone in _disks.Keys.Where(n => !idle.ContainsKey(n)).ToList()) { _disks.Remove(gone); _diskTemps.Remove(gone); }
        }

        var byKind = new Dictionary<DiskKind, double>();
        var nvmeTemps = new List<double>();
        foreach (var (n, (letters, idleValue)) in idle.OrderBy(d => d.Key))
        {
            var info = _disks.TryGetValue(n, out var d) ? d : new StorageInfo.DiskDescription(n, $"Datenträger {n}", DiskKind.Unknown);
            string label = $"Laufwerk {n}{(letters.Length > 0 ? " (" + letters + ")" : "")}";
            double active = Clamp100(100 - idleValue);
            list.Add(new SensorReading(WinSensorIds.DiskName(n), label + " — Modell", SensorGroups.Storage, "", null, $"{info.Model} · {StorageInfo.KindName(info.Kind)}"));
            list.Add(new SensorReading(WinSensorIds.DiskActive(n), $"{label} — Aktivität ({StorageInfo.KindName(info.Kind)})", SensorGroups.Storage, "%", active));
            if (read.TryGetValue(n, out var r)) list.Add(new SensorReading(WinSensorIds.DiskRead(n), label + " — Lesen", SensorGroups.Storage, "B/s", r.Value));
            if (write.TryGetValue(n, out var w)) list.Add(new SensorReading(WinSensorIds.DiskWrite(n), label + " — Schreiben", SensorGroups.Storage, "B/s", w.Value));
            if (_diskTemps.TryGetValue(n, out var t) && t is { } temp)
            {
                list.Add(new SensorReading(WinSensorIds.DiskTemp(n), label + " — Temperatur", SensorGroups.Temperature, "°C", temp));
                nvmeTemps.Add(temp);
            }
            if (info.Kind is DiskKind.Nvme or DiskKind.Ssd or DiskKind.Hdd)
                byKind[info.Kind] = byKind.TryGetValue(info.Kind, out var prev) ? Math.Max(prev, active) : active;
        }
        if (byKind.TryGetValue(DiskKind.Nvme, out var nv)) list.Add(new SensorReading(WinSensorIds.NvmeActive, "NVMe-Aktivität (alle, höchster Wert)", SensorGroups.Storage, "%", nv));
        if (byKind.TryGetValue(DiskKind.Ssd, out var ssd)) list.Add(new SensorReading(WinSensorIds.SsdActive, "SATA-SSD-Aktivität (alle, höchster Wert)", SensorGroups.Storage, "%", ssd));
        if (byKind.TryGetValue(DiskKind.Hdd, out var hdd)) list.Add(new SensorReading(WinSensorIds.HddActive, "Festplatten-Aktivität (HDD, höchster Wert)", SensorGroups.Storage, "%", hdd));
        if (nvmeTemps.Count > 0) list.Add(new SensorReading(WinSensorIds.NvmeTemp, "NVMe-Temperatur (höchste)", SensorGroups.Temperature, "°C", nvmeTemps.Max()));
    }

    /// <summary>"0 C: D:" → Datenträger 0 mit Laufwerksbuchstaben, "_Total" auslassen.</summary>
    private static Dictionary<int, (string Letters, double Value)> ByDisk(List<(string Instance, double Value)> items)
    {
        var result = new Dictionary<int, (string, double)>();
        foreach (var (instance, value) in items)
        {
            var parts = instance.Split(' ', 2);
            if (!int.TryParse(parts[0], NumberStyles.Integer, CultureInfo.InvariantCulture, out int n)) continue;
            result[n] = (parts.Length > 1 ? parts[1].Trim() : "", value);
        }
        return result;
    }

    private static void ReadNetwork(PdhQuery pdh, List<SensorReading> list)
    {
        static bool Physical(string name)
        {
            var n = name.ToLowerInvariant();
            return !(n.Contains("loopback") || n.Contains("isatap") || n.Contains("teredo") || n.Contains("pseudo") || n.Contains("vethernet") || n.Contains("hyper-v") || n.Contains("wan miniport"));
        }
        var down = pdh.Array(NetDown).Where(i => Physical(i.Instance)).ToList();
        var up = pdh.Array(NetUp).Where(i => Physical(i.Instance)).ToList();
        if (down.Count > 0) list.Add(new SensorReading(WinSensorIds.NetDown, "Netzwerk Empfang (gesamt)", SensorGroups.Network, "B/s", down.Sum(i => i.Value)));
        if (up.Count > 0) list.Add(new SensorReading(WinSensorIds.NetUp, "Netzwerk Senden (gesamt)", SensorGroups.Network, "B/s", up.Sum(i => i.Value)));
        int i = 0;
        foreach (var d in down.OrderBy(x => x.Instance))
        {
            string id = "win/net/" + i++;
            list.Add(new SensorReading(id + "/down", d.Instance + " — Empfang", SensorGroups.Network, "B/s", d.Value));
            var u = up.FirstOrDefault(x => x.Instance == d.Instance);
            if (u.Instance is not null) list.Add(new SensorReading(id + "/up", d.Instance + " — Senden", SensorGroups.Network, "B/s", u.Value));
        }
    }

    private static List<SensorReading> ReadVolumes()
    {
        var list = new List<SensorReading>();
        foreach (var drive in DriveInfo.GetDrives())
        {
            try
            {
                if (drive.DriveType != DriveType.Fixed || !drive.IsReady || drive.TotalSize <= 0) continue;
                char letter = drive.Name[0];
                double total = drive.TotalSize, free = drive.TotalFreeSpace;
                string label = string.IsNullOrWhiteSpace(drive.VolumeLabel) ? $"{letter}:" : $"{letter}: ({drive.VolumeLabel})";
                list.Add(new SensorReading(WinSensorIds.VolumeLoad(letter), label + " belegt", SensorGroups.Storage, "%", (total - free) / total * 100));
                list.Add(new SensorReading(WinSensorIds.VolumeFree(letter), label + " frei", SensorGroups.Storage, "B", free));
                list.Add(new SensorReading($"win/vol/{char.ToUpperInvariant(letter)}/total", label + " Größe", SensorGroups.Storage, "B", total));
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { }
        }
        return list;
    }

    private static List<GpuAdapter> EnumerateGpus()
    {
        var result = new List<GpuAdapter>();
        try
        {
            using var factory = DXGI.CreateDXGIFactory1<IDXGIFactory1>();
            for (uint a = 0; factory.EnumAdapters1(a, out var adapter).Success; a++)
            {
                using (adapter)
                {
                    var d = adapter.Description1;
                    if ((d.Flags & AdapterFlags.Software) != 0) continue;
                    string luid = $"0x{(uint)d.Luid.HighPart:x8}_0x{d.Luid.LowPart:x8}";
                    if (result.Any(g => g.Luid == luid)) continue;
                    result.Add(new GpuAdapter(result.Count, luid, d.Description.Trim(), (ulong)d.DedicatedVideoMemory));
                }
            }
        }
        catch { /* ohne DXGI: Namen aus den Zählern */ }
        return result;
    }

    private static string ReadCpuName()
    {
        try
        {
            using var key = Registry.LocalMachine.OpenSubKey(@"HARDWARE\DESCRIPTION\System\CentralProcessor\0");
            return (key?.GetValue("ProcessorNameString") as string)?.Trim() ?? "CPU";
        }
        catch { return "CPU"; }
    }

    private static double Clamp100(double v) => double.IsFinite(v) ? Math.Clamp(v, 0, 100) : double.NaN;

    private static void AddIfFinite(List<SensorReading> list, string id, string name, string group, string unit, double value)
    {
        if (double.IsFinite(value)) list.Add(new SensorReading(id, name, group, unit, value));
    }

    public void Dispose()
    {
        _pdh?.Dispose();
        _gpuQuery?.Dispose();
        _gpuNext?.Dispose();
        _pdh = _gpuQuery = _gpuNext = null;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct MEMORYSTATUSEX
    {
        public uint dwLength, dwMemoryLoad;
        public ulong ullTotalPhys, ullAvailPhys, ullTotalPageFile, ullAvailPageFile, ullTotalVirtual, ullAvailVirtual, ullAvailExtendedVirtual;
    }

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool GlobalMemoryStatusEx(ref MEMORYSTATUSEX buffer);
}
