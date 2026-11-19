using AetherRGB.Core.Devices;

namespace AetherRGB.Providers.OpenRGB;

/// <summary>Bindet Motherboard-ARGB (ASUS Aura, ASRock Polychrome, Gigabyte RGB Fusion,
/// MSI Mystic Light) über einen lokal laufenden OpenRGB-SDK-Server (Standard: 127.0.0.1:6742)
/// ein. Erfordert, dass OpenRGB selbst installiert ist und läuft — AetherRGB spricht nur die
/// SDK-Schnittstelle an, niemals direkt die Hardware.</summary>
public sealed class OpenRgbDeviceProvider : IDeviceProvider
{
    public string ProviderId => "openrgb";
    public bool IsConnected { get; private set; }

    private readonly string _host;
    private readonly int _port;
    private OpenRgbClient? _client;
    private readonly List<OpenRgbController> _controllers = new();

    public OpenRgbDeviceProvider(string host, int port)
    {
        _host = host;
        _port = port;
    }

    public async Task ConnectAsync(CancellationToken ct = default)
    {
        try
        {
            _client = new OpenRgbClient(_host, _port);
            await _client.ConnectAsync(ct);

            int count = await _client.GetControllerCountAsync(ct);
            _controllers.Clear();
            for (int i = 0; i < count; i++)
                _controllers.Add(await _client.GetControllerDataAsync(i, ct));

            IsConnected = true;
        }
        catch
        {
            IsConnected = false;
            // Bewusst kein Rethrow — kein laufendes OpenRGB ist ein normaler, erwarteter Zustand
            // (z. B. Nutzer hat nur WLED/Home Assistant, kein ARGB-Mainboard), kein Fehler der App.
        }
    }

    public Task<IReadOnlyList<RgbDevice>> GetDevicesAsync(CancellationToken ct = default)
    {
        IReadOnlyList<RgbDevice> devices = _controllers.Select(c => new RgbDevice
        {
            Id = $"{ProviderId}:{c.Index}",
            Name = c.Name,
            Vendor = GuessVendor(c.Name),
            Zones = c.Zones.Select((z, i) => new LedZone { Index = i, Name = z.Name, LedCount = (int)z.LedCount }).ToList(),
            IsOnline = IsConnected
        }).ToList();
        return Task.FromResult(devices);
    }

    private static DeviceVendor GuessVendor(string name)
    {
        var n = name.ToLowerInvariant();
        if (n.Contains("asus") || n.Contains("aura")) return DeviceVendor.AsusAura;
        if (n.Contains("asrock") || n.Contains("polychrome")) return DeviceVendor.AsRockPolychrome;
        if (n.Contains("gigabyte") || n.Contains("fusion")) return DeviceVendor.GigabyteRgbFusion;
        if (n.Contains("msi") || n.Contains("mystic")) return DeviceVendor.MsiMysticLight;
        return DeviceVendor.Generic;
    }

    public async Task SetZoneColorAsync(string deviceId, int zoneIndex, RgbColor color, CancellationToken ct = default)
    {
        int ledCount = ResolveLedCount(deviceId, zoneIndex);
        await SetZoneColorsAsync(deviceId, zoneIndex, Enumerable.Repeat(color, ledCount).ToList(), ct);
    }

    public async Task SetZoneColorsAsync(string deviceId, int zoneIndex, IReadOnlyList<RgbColor> colors, CancellationToken ct = default)
    {
        if (_client is null || !IsConnected) return;
        int controllerIndex = ParseControllerIndex(deviceId);
        var payload = colors.Select(c => (c.R, c.G, c.B)).ToList();
        await _client.UpdateZoneLedsAsync(controllerIndex, zoneIndex, payload, ct);
    }

    private int ResolveLedCount(string deviceId, int zoneIndex)
    {
        int idx = ParseControllerIndex(deviceId);
        var controller = _controllers.FirstOrDefault(c => c.Index == idx);
        return (int?)controller?.Zones.ElementAtOrDefault(zoneIndex)?.LedCount ?? 1;
    }

    private static int ParseControllerIndex(string deviceId)
    {
        var parts = deviceId.Split(':');
        return parts.Length == 2 && int.TryParse(parts[1], out var idx) ? idx : 0;
    }

    public async ValueTask DisposeAsync()
    {
        if (_client is not null) await _client.DisposeAsync();
    }
}
