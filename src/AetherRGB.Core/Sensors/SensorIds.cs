namespace AetherRGB.Core.Sensors;

/// <summary>Feste Sensor-IDs der eingebauten Windows-Quelle (ohne AIDA64). Panels speichern diese IDs.</summary>
public static class WinSensorIds
{
    public const string Prefix = "win/";
    public const string CpuLoad = "win/cpu/load";
    public const string CpuClock = "win/cpu/clock";
    public const string CpuName = "win/cpu/name";
    public static string CpuCoreLoad(int core) => $"win/cpu/core/{core}/load";
    public const string Processes = "win/sys/processes";
    public const string Threads = "win/sys/threads";
    public const string Uptime = "win/sys/uptime";
    public const string UptimeHours = "win/sys/uptime/h";

    public const string MemLoad = "win/mem/load";
    public const string MemUsed = "win/mem/used";
    public const string MemFree = "win/mem/free";
    public const string MemTotal = "win/mem/total";
    public const string CommitLoad = "win/mem/commit/load";

    public static string GpuLoad(int i) => $"win/gpu/{i}/load";
    public static string GpuEngine(int i, string engine) => $"win/gpu/{i}/{engine}";
    public static string GpuVramUsed(int i) => $"win/gpu/{i}/vram/used";
    public static string GpuVramLoad(int i) => $"win/gpu/{i}/vram/load";
    public static string GpuName(int i) => $"win/gpu/{i}/name";

    public static string DiskActive(int n) => $"win/disk/{n}/active";
    public static string DiskRead(int n) => $"win/disk/{n}/read";
    public static string DiskWrite(int n) => $"win/disk/{n}/write";
    public static string DiskTemp(int n) => $"win/disk/{n}/temp";
    public static string DiskName(int n) => $"win/disk/{n}/name";

    /// <summary>Höchste Aktivität aller Laufwerke dieser Art (praktisch für Panels, die Datenträgernummer egal).</summary>
    public const string NvmeActive = "win/nvme/active";
    public const string SsdActive = "win/ssd/active";
    public const string HddActive = "win/hdd/active";
    public const string NvmeTemp = "win/nvme/temp";

    public static string VolumeLoad(char letter) => $"win/vol/{char.ToUpperInvariant(letter)}/load";
    public static string VolumeFree(char letter) => $"win/vol/{char.ToUpperInvariant(letter)}/free";

    public const string NetDown = "win/net/down";
    public const string NetUp = "win/net/up";
}

/// <summary>IDs der NVIDIA-Quelle (NVML aus dem Grafiktreiber).</summary>
public static class NvmlSensorIds
{
    public static string Temp(int i) => $"nvml/{i}/temp";
    public static string Load(int i) => $"nvml/{i}/load";
    public static string MemLoad(int i) => $"nvml/{i}/memload";
    public static string VramUsed(int i) => $"nvml/{i}/vram/used";
    public static string VramTotal(int i) => $"nvml/{i}/vram/total";
    public static string Power(int i) => $"nvml/{i}/power";
    public static string Fan(int i) => $"nvml/{i}/fan";
    public static string CoreClock(int i) => $"nvml/{i}/clock/core";
    public static string MemClock(int i) => $"nvml/{i}/clock/mem";
    public static string Name(int i) => $"nvml/{i}/name";
}
