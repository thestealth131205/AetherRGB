using AetherRGB.Core.Devices;

namespace AetherRGB.App;

/// <summary>Flache Darstellung eines <see cref="RgbDevice"/> für die ListView-Bindung.</summary>
public sealed class DeviceRow
{
    public required string Id { get; init; }
    public required string Name { get; init; }
    public required string Vendor { get; init; }
    public required string ZoneSummary { get; init; }
    public required string StatusText { get; init; }
    public required IReadOnlyList<LedZone> Zones { get; init; }
}
