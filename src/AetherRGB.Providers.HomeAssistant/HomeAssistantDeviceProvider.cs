using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using AetherRGB.Core.Devices;
using AetherRGB.Core.Diagnostics;

namespace AetherRGB.Providers.HomeAssistant;

/// <summary>
/// Bindet Home Assistant in zwei Richtungen an, ausschließlich über die REST-API der EIGENEN,
/// lokal erreichbaren HA-Instanz (kein Nabu-Casa-Cloud-Relais, kein Umweg über das Internet) mit
/// einem lokal in HA erzeugten "Long-Lived Access Token":
///   1. Als Ziel: HA-`light.*`-Entities lassen sich wie ein RGB-Gerät mit einem einzelnen Pixel
///      ansteuern (für Sync-Effekte, die auch smarte Lampen miteinbeziehen sollen).
///   2. Als Quelle: HA-Lichter/Szenen können umgekehrt ausgelesen werden, um ihre aktuelle Farbe
///      auf die PC-Beleuchtung zu spiegeln (<see cref="GetLightStateAsync"/>, von der
///      Effekt-Engine über <c>ExternalSourceEffect</c> genutzt).
/// Die lokale REST-API von AetherRGB selbst (AetherRGB.Api) ist der Gegenpart dafür — eine
/// HA-Integration (z. B. als `rest`-Light-Platform oder eigene HACS-Komponente) kann darüber
/// die PC-Beleuchtung genauso gut von HA aus steuern.
/// </summary>
public sealed class HomeAssistantDeviceProvider : IDeviceProvider, IProviderLog
{
    public string ProviderId => "homeassistant";
    public bool IsConnected { get; private set; }

    public event Action<LogLevel, string>? Log;

    private readonly string _baseUrl;
    private readonly HttpClient _http;
    private readonly List<(string EntityId, string Name)> _lights = new();

    public HomeAssistantDeviceProvider(string baseUrl, string longLivedToken, HttpClient? http = null)
    {
        _baseUrl = baseUrl.TrimEnd('/');
        _http = http ?? new HttpClient { Timeout = TimeSpan.FromSeconds(5) };
        _http.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", longLivedToken);
    }

    public async Task ConnectAsync(CancellationToken ct = default)
    {
        try
        {
            using var doc = await _http.GetFromJsonAsync<JsonDocument>($"{_baseUrl}/api/states", ct);
            _lights.Clear();
            if (doc is not null)
            {
                foreach (var entity in doc.RootElement.EnumerateArray())
                {
                    var entityId = entity.GetProperty("entity_id").GetString() ?? "";
                    if (!entityId.StartsWith("light.")) continue;
                    var friendlyName = entity.TryGetProperty("attributes", out var attrs) &&
                                        attrs.TryGetProperty("friendly_name", out var fn)
                        ? fn.GetString() ?? entityId
                        : entityId;
                    _lights.Add((entityId, friendlyName));
                }
            }
            IsConnected = true;
            Log?.Invoke(LogLevel.Info, $"Verbunden mit {_baseUrl}, {_lights.Count} Licht-Entities (light.*) gefunden.");
        }
        catch (Exception ex)
        {
            IsConnected = false;
            var hint = (ex as HttpRequestException)?.StatusCode switch
            {
                System.Net.HttpStatusCode.Unauthorized => " — Token ungültig oder abgelaufen (in HA ein neues Long-Lived Access Token erzeugen)",
                System.Net.HttpStatusCode.NotFound => " — URL zeigt nicht auf Home Assistant (Beispiel: http://homeassistant.local:8123)",
                _ => ""
            };
            Log?.Invoke(LogLevel.Warning, $"Keine Verbindung zu {_baseUrl} — {ExceptionText.Describe(ex)}{hint}");
        }
    }

    public Task<IReadOnlyList<RgbDevice>> GetDevicesAsync(CancellationToken ct = default)
    {
        IReadOnlyList<RgbDevice> devices = _lights.Select(l => new RgbDevice
        {
            Id = $"{ProviderId}:{l.EntityId}",
            Name = l.Name,
            Vendor = DeviceVendor.HomeAssistant,
            Zones = new List<LedZone> { new() { Index = 0, Name = "Licht", LedCount = 1 } },
            IsOnline = IsConnected
        }).ToList();
        return Task.FromResult(devices);
    }

    public async Task SetZoneColorAsync(string deviceId, int zoneIndex, RgbColor color, CancellationToken ct = default)
    {
        var entityId = ParseEntityId(deviceId);
        var body = new { entity_id = entityId, rgb_color = new[] { (int)color.R, (int)color.G, (int)color.B } };
        await _http.PostAsJsonAsync($"{_baseUrl}/api/services/light/turn_on", body, ct);
    }

    public Task SetZoneColorsAsync(string deviceId, int zoneIndex, IReadOnlyList<RgbColor> colors, CancellationToken ct = default) =>
        // Ein HA-Licht hat nur ein "Pixel" — Mittelwert, falls ein Mehr-Pixel-Effekt darauf gemappt wird.
        SetZoneColorAsync(deviceId, zoneIndex, Average(colors), ct);

    private static RgbColor Average(IReadOnlyList<RgbColor> colors)
    {
        if (colors.Count == 0) return RgbColor.Black;
        int r = 0, g = 0, b = 0;
        foreach (var c in colors) { r += c.R; g += c.G; b += c.B; }
        return new RgbColor((byte)(r / colors.Count), (byte)(g / colors.Count), (byte)(b / colors.Count));
    }

    /// <summary>Liest die aktuelle Farbe eines HA-Lichts (für <c>ExternalSourceEffect</c> als Quelle).</summary>
    public async Task<RgbColor?> GetLightStateAsync(string entityId, CancellationToken ct = default)
    {
        try
        {
            using var doc = await _http.GetFromJsonAsync<JsonDocument>($"{_baseUrl}/api/states/{entityId}", ct);
            if (doc is null) return null;
            if (!doc.RootElement.TryGetProperty("attributes", out var attrs)) return null;
            if (!attrs.TryGetProperty("rgb_color", out var rgb) || rgb.GetArrayLength() < 3) return null;
            var values = rgb.EnumerateArray().Select(e => (byte)e.GetInt32()).ToArray();
            return new RgbColor(values[0], values[1], values[2]);
        }
        catch
        {
            return null;
        }
    }

    private static string ParseEntityId(string deviceId)
    {
        var idx = deviceId.IndexOf(':');
        return idx >= 0 ? deviceId[(idx + 1)..] : deviceId;
    }

    public ValueTask DisposeAsync() => ValueTask.CompletedTask;
}
