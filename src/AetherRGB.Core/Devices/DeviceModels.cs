namespace AetherRGB.Core.Devices;

/// <summary>Herstellerklasse, primär zur Anzeige/Gruppierung in der UI und im lokalen API-Response.</summary>
public enum DeviceVendor
{
    Unknown,
    AsusAura,
    AsRockPolychrome,
    GigabyteRgbFusion,
    MsiMysticLight,
    Wled,
    HomeAssistant,
    Generic
}

/// <summary>Eine einzelne adressierbare LED innerhalb einer Zone (z. B. ein ARGB-Pixel im Strip).</summary>
public sealed class LedZone
{
    public required int Index { get; init; }
    public required string Name { get; init; }
    public required int LedCount { get; init; }
}

/// <summary>Ein physisches/logisches Gerät, wie es ein <see cref="IDeviceProvider"/> meldet.</summary>
public sealed class RgbDevice
{
    public required string Id { get; init; }
    public required string Name { get; init; }
    public required DeviceVendor Vendor { get; init; }
    public required IReadOnlyList<LedZone> Zones { get; init; }
    public bool IsOnline { get; set; } = true;
}
