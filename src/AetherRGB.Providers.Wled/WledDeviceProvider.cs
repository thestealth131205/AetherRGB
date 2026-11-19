using System.Net.Http.Json;
using System.Text.Json;
using System.Text.Json.Serialization;
using AetherRGB.Core.Devices;

namespace AetherRGB.Providers.Wled;

/// <summary>Steuert ein oder mehrere WLED-Geräte über deren offizielle lokale JSON-API
/// (`GET /json/info`, `POST /json/state`) — rein lokales HTTP im eigenen Netz, kein Cloud-Dienst
/// von WLED beteiligt. Siehe https://kno.wled.ge/interfaces/json-api/.</summary>
public sealed class WledDeviceProvider : IDeviceProvider
{
    public string ProviderId => "wled";
    public bool IsConnected { get; private set; }

    private readonly Dictionary<string, (string Host, int LedCount, string Name)> _instances = new();
    private readonly HttpClient _http;

    public WledDeviceProvider(IEnumerable<(string Name, string Host)> instances, HttpClient? http = null)
    {
        _http = http ?? new HttpClient { Timeout = TimeSpan.FromSeconds(3) };
        foreach (var (name, host) in instances)
            _instances[host] = (host, 0, name);
    }

    public async Task ConnectAsync(CancellationToken ct = default)
    {
        bool anyReachable = false;
        foreach (var host in _instances.Keys.ToList())
        {
            try
            {
                var info = await _http.GetFromJsonAsync<WledInfoResponse>($"http://{host}/json/info", ct);
                if (info is not null)
                {
                    var existing = _instances[host];
                    _instances[host] = (host, info.Leds.Count, existing.Name);
                    anyReachable = true;
                }
            }
            catch
            {
                // Einzelnes nicht erreichbares WLED-Gerät blockiert die anderen nicht.
            }
        }
        IsConnected = anyReachable;
    }

    public Task<IReadOnlyList<RgbDevice>> GetDevicesAsync(CancellationToken ct = default)
    {
        IReadOnlyList<RgbDevice> devices = _instances.Values.Where(i => i.LedCount > 0).Select(i => new RgbDevice
        {
            Id = $"{ProviderId}:{i.Host}",
            Name = i.Name,
            Vendor = DeviceVendor.Wled,
            Zones = new List<LedZone> { new() { Index = 0, Name = "Strip", LedCount = i.LedCount } },
            IsOnline = true
        }).ToList();
        return Task.FromResult(devices);
    }

    public async Task SetZoneColorAsync(string deviceId, int zoneIndex, RgbColor color, CancellationToken ct = default)
    {
        var host = ParseHost(deviceId);
        var body = new { on = true, seg = new[] { new { col = new[] { new[] { color.R, color.G, color.B } } } } };
        await _http.PostAsJsonAsync($"http://{host}/json/state", body, ct);
    }

    public async Task SetZoneColorsAsync(string deviceId, int zoneIndex, IReadOnlyList<RgbColor> colors, CancellationToken ct = default)
    {
        var host = ParseHost(deviceId);
        // WLED akzeptiert eine flache Liste von Hex-Farben in "i", angewendet ab LED 0 fortlaufend.
        var hex = colors.Select(c => c.ToHex().TrimStart('#')).ToList();
        var body = new { on = true, seg = new[] { new { i = (object)hex } } };
        await _http.PostAsJsonAsync($"http://{host}/json/state", body, ct);
    }

    private static string ParseHost(string deviceId)
    {
        var idx = deviceId.IndexOf(':');
        return idx >= 0 ? deviceId[(idx + 1)..] : deviceId;
    }

    public ValueTask DisposeAsync() => ValueTask.CompletedTask;

    private sealed class WledInfoResponse
    {
        [JsonPropertyName("leds")] public WledLedsInfo Leds { get; set; } = new();
        [JsonPropertyName("name")] public string Name { get; set; } = "";
    }

    private sealed class WledLedsInfo
    {
        [JsonPropertyName("count")] public int Count { get; set; }
    }
}
