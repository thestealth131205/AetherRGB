using AetherRGB.Core.Diagnostics;

namespace AetherRGB.App;

/// <summary>Eine Zeile der Log-Leiste.</summary>
public sealed class LogEntry
{
    public required DateTime Time { get; init; }
    public required LogLevel Level { get; init; }
    public required string Message { get; init; }

    public string TimeText => Time.ToString("HH:mm:ss.fff");

    public string LevelText => Level switch
    {
        LogLevel.Debug => "DETAIL",
        LogLevel.Warning => "WARN  ",
        LogLevel.Error => "FEHLER",
        _ => "INFO  "
    };

    public override string ToString() => $"{Time:yyyy-MM-dd HH:mm:ss.fff} {LevelText} {Message}";
}
