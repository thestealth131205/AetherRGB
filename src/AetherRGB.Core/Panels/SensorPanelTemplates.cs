using AetherRGB.Core.Devices;
using AetherRGB.Core.Sensors;

namespace AetherRGB.Core.Panels;

/// <summary>Mitgelieferte Panels für 1024 × 600 (5"-USB-Zweitdisplay). Sensoren mit Ausweich-Liste: zuerst AIDA64, dann
/// NVIDIA, dann Windows — so zeigt das Panel auch ohne AIDA64 Last und Speicher (Temperaturen gibt es ohne AIDA64 nur für
/// NVIDIA-Karten und NVMe-Laufwerke).</summary>
public static class SensorPanelTemplates
{
    public const string OverviewId = "uebersicht";
    public const string AnimatedId = "animation";

    public static readonly IReadOnlyList<Func<SensorPanelDocument>> All = new Func<SensorPanelDocument>[] { Overview, Animated };

    public static SensorPanelDocument? Get(string id) => All.Select(t => t()).FirstOrDefault(p => p.Id == id);

    private static readonly RgbColor Card = new(21, 22, 28), Border = new(42, 43, 53), Text = new(231, 232, 236), Muted = new(140, 143, 160);
    private static readonly RgbColor Violet = new(124, 92, 255), Cyan = new(34, 211, 238), Green = new(34, 197, 94), Pink = new(236, 72, 153), Orange = new(249, 115, 22);

    public const string CpuLoadIds = "aida/SCPUUTI|" + WinSensorIds.CpuLoad;
    public const string CpuTempIds = "aida/TCPUPKG|aida/TCPUDIO|aida/TCPU|aida/TCPUCCD1";
    public const string CpuClockIds = "aida/SCPUCLK|" + WinSensorIds.CpuClock;
    public static readonly string GpuLoadIds = $"aida/SGPU1UTI|{NvmlSensorIds.Load(0)}|{WinSensorIds.GpuLoad(0)}";
    public static readonly string GpuTempIds = $"aida/TGPU1DIO|aida/TGPU1|{NvmlSensorIds.Temp(0)}";
    public static readonly string GpuVramIds = $"{NvmlSensorIds.VramUsed(0)}|{WinSensorIds.GpuVramUsed(0)}";
    public const string MemLoadIds = "aida/SMEMUTI|" + WinSensorIds.MemLoad;

    private static PanelItem Box(double x, double y, double w, double h) => new()
    {
        Kind = PanelItemKind.Box, Name = "Hintergrund", X = x, Y = y, W = w, H = h, BackColor = Card, BorderColor = Border, BorderWidth = 1, CornerRadius = 12,
    };

    private static PanelItem Label(string text, double x, double y, double w, double size = 15) => new()
    {
        Kind = PanelItemKind.Text, Name = text, Text = text, X = x, Y = y, W = w, H = size * 1.6, FontSize = size, Bold = true, Color = Muted,
    };

    private static PanelItem Gauge(string name, string ids, double x, double y, RgbColor accent) => new()
    {
        Kind = PanelItemKind.Gauge, Name = name, Text = name, SensorId = ids, X = x, Y = y, W = 280, H = 186,
        AccentColor = accent, Thickness = 18, FontSize = 44, Bold = true, WarnAt = 80, CritAt = 95, Decimals = 0,
    };

    private static PanelItem Value(string label, string ids, double x, double y, double w, int decimals = -1, double? warn = null, double? crit = null) => new()
    {
        Kind = PanelItemKind.Value, Name = label, Text = label, SensorId = ids, X = x, Y = y, W = w, H = 44, FontSize = 20, Bold = true,
        Decimals = decimals, WarnAt = warn, CritAt = crit,
    };

    private static PanelItem Bar(string label, string ids, double x, double y, double w, RgbColor accent, double? warn = null, double? crit = null) => new()
    {
        Kind = PanelItemKind.Bar, Name = label, Text = label, SensorId = ids, X = x, Y = y, W = w, H = 38, FontSize = 15,
        AccentColor = accent, BackColor = new(42, 43, 53), CornerRadius = 4, WarnAt = warn, CritAt = crit, Decimals = 0,
    };

    private static PanelItem Graph(string label, string ids, double x, double y, double w, double h, RgbColor accent) => new()
    {
        Kind = PanelItemKind.Graph, Name = label, Text = label, SensorId = ids, X = x, Y = y, W = w, H = h, FontSize = 14,
        AccentColor = accent, BackColor = new(12, 13, 17), Thickness = 2, GraphSeconds = 120, CornerRadius = 8,
    };

    public static SensorPanelDocument Overview() => new()
    {
        Id = OverviewId,
        Name = "Systemübersicht (1024 × 600)",
        Width = 1024, Height = 600,
        Background = new(7, 8, 11),
        Items =
        {
            Box(16, 12, 992, 68),
            new PanelItem { Kind = PanelItemKind.Clock, Name = "Uhrzeit", Text = "HH:mm:ss", X = 36, Y = 18, W = 230, H = 56, FontSize = 40, Bold = true },
            new PanelItem { Kind = PanelItemKind.Clock, Name = "Datum", Text = "dddd, d. MMMM yyyy", X = 270, Y = 34, W = 420, H = 30, FontSize = 18, Color = Muted },
            new PanelItem { Kind = PanelItemKind.Value, Name = "Laufzeit", Text = "Laufzeit", SensorId = WinSensorIds.Uptime, X = 700, Y = 30, W = 290, H = 34, FontSize = 18, Align = PanelAlign.Right },

            Box(16, 94, 320, 256), Gauge("CPU", CpuLoadIds, 36, 104, Violet),
            Value("Temp.", CpuTempIds, 36, 296, 130, 0, 80, 90), Value("Takt", CpuClockIds, 176, 296, 140, 0),

            Box(352, 94, 320, 256), Gauge("GPU", GpuLoadIds, 372, 104, Cyan),
            Value("Temp.", GpuTempIds, 372, 296, 130, 0, 80, 88), Value("VRAM", GpuVramIds, 512, 296, 140, 1),

            Box(688, 94, 320, 256), Gauge("RAM", MemLoadIds, 708, 104, Green),
            Value("Belegt", WinSensorIds.MemUsed, 708, 296, 150, 1), Value("Gesamt", WinSensorIds.MemTotal, 858, 296, 140, 0),

            Box(16, 364, 496, 222),
            Label("LAUFWERKE", 36, 374, 300),
            Bar("NVMe", WinSensorIds.NvmeActive, 36, 404, 456, Violet, 80, 95),
            Bar("SATA-SSD", WinSensorIds.SsdActive, 36, 448, 456, Cyan, 80, 95),
            Bar("Festplatte (HDD)", WinSensorIds.HddActive, 36, 492, 456, Orange, 80, 95),
            Bar("C: belegt", WinSensorIds.VolumeLoad('C'), 36, 536, 456, Green, 85, 95),

            Graph("CPU-Last (2 min)", CpuLoadIds, 528, 364, 480, 106, Violet),
            Graph("Download", WinSensorIds.NetDown, 528, 480, 236, 106, Pink),
            new PanelItem
            {
                Kind = PanelItemKind.Animation, Name = "Animation", X = 772, Y = 480, W = 236, H = 106, CornerRadius = 8,
                EffectId = "rainbow-wave", SpeedPercent = 100, CellSize = 6,
            },
        },
    };

    public static SensorPanelDocument Animated() => new()
    {
        Id = AnimatedId,
        Name = "Animation mit Werten (1024 × 600)",
        Width = 1024, Height = 600,
        Background = RgbColor.Black,
        Items =
        {
            new PanelItem { Kind = PanelItemKind.Animation, Name = "Hintergrund-Animation", X = 0, Y = 0, W = 1024, H = 600, CornerRadius = 0, EffectId = "gradient-sweep", SpeedPercent = 60, CellSize = 8, Opacity = 0.55 },
            new PanelItem { Kind = PanelItemKind.Box, Name = "Abdunkeln", X = 40, Y = 40, W = 944, H = 520, BackColor = RgbColor.Black, BackOpacity = 0.55, CornerRadius = 24 },
            new PanelItem { Kind = PanelItemKind.Clock, Name = "Uhrzeit", Text = "HH:mm", X = 40, Y = 64, W = 944, H = 150, FontSize = 128, Bold = true, Align = PanelAlign.Center },
            new PanelItem { Kind = PanelItemKind.Clock, Name = "Datum", Text = "dddd, d. MMMM", X = 40, Y = 214, W = 944, H = 40, FontSize = 26, Color = Muted, Align = PanelAlign.Center },
            new PanelItem { Kind = PanelItemKind.Value, Name = "CPU", Text = "CPU", SensorId = CpuLoadIds, X = 80, Y = 300, W = 260, H = 70, FontSize = 40, Bold = true, Align = PanelAlign.Center, Decimals = 0 },
            new PanelItem { Kind = PanelItemKind.Value, Name = "GPU", Text = "GPU", SensorId = GpuLoadIds, X = 382, Y = 300, W = 260, H = 70, FontSize = 40, Bold = true, Align = PanelAlign.Center, Decimals = 0 },
            new PanelItem { Kind = PanelItemKind.Value, Name = "RAM", Text = "RAM", SensorId = MemLoadIds, X = 684, Y = 300, W = 260, H = 70, FontSize = 40, Bold = true, Align = PanelAlign.Center, Decimals = 0 },
            new PanelItem { Kind = PanelItemKind.Bar, Name = "CPU-Balken", SensorId = CpuLoadIds, X = 100, Y = 390, W = 220, H = 14, ShowLabel = false, ShowValue = false, AccentColor = Violet, BackColor = new(42, 43, 53), Segments = 20, CornerRadius = 2 },
            new PanelItem { Kind = PanelItemKind.Bar, Name = "GPU-Balken", SensorId = GpuLoadIds, X = 402, Y = 390, W = 220, H = 14, ShowLabel = false, ShowValue = false, AccentColor = Cyan, BackColor = new(42, 43, 53), Segments = 20, CornerRadius = 2 },
            new PanelItem { Kind = PanelItemKind.Bar, Name = "RAM-Balken", SensorId = MemLoadIds, X = 704, Y = 390, W = 220, H = 14, ShowLabel = false, ShowValue = false, AccentColor = Green, BackColor = new(42, 43, 53), Segments = 20, CornerRadius = 2 },
            new PanelItem { Kind = PanelItemKind.Value, Name = "CPU-Temp", Text = "CPU", SensorId = CpuTempIds, X = 80, Y = 450, W = 260, H = 50, FontSize = 26, Align = PanelAlign.Center, Decimals = 0, WarnAt = 80, CritAt = 90 },
            new PanelItem { Kind = PanelItemKind.Value, Name = "GPU-Temp", Text = "GPU", SensorId = GpuTempIds, X = 382, Y = 450, W = 260, H = 50, FontSize = 26, Align = PanelAlign.Center, Decimals = 0, WarnAt = 80, CritAt = 88 },
            new PanelItem { Kind = PanelItemKind.Value, Name = "NVMe-Temp", Text = "NVMe", SensorId = "aida/THDD1|" + WinSensorIds.NvmeTemp, X = 684, Y = 450, W = 260, H = 50, FontSize = 26, Align = PanelAlign.Center, Decimals = 0, WarnAt = 65, CritAt = 75 },
        },
    };
}
