using System.Net;
using System.Net.Http.Json;
using System.Net.Sockets;
using System.Text.Json;
using System.Text.Json.Serialization;
using AetherRGB.Core.Devices;
using AetherRGB.Core.Diagnostics;

namespace AetherRGB.Providers.Wled;

/// <summary>Steuert ein oder mehrere WLED-Geräte, rein lokal im eigenen Netz, kein Cloud-Dienst von WLED
/// beteiligt. Geräteinfos kommen über die JSON-API (`GET /json/info`); die Effektbilder gehen standardmäßig
/// über WLEDs UDP-Echtzeit-Schnittstelle (DRGB bis 490 LEDs, darüber DNRGB in Blöcken, Port aus
/// `info.udpport`, sonst 21324) — ein Datagramm pro Bild statt eines HTTP-POST, damit sind 30 Bilder/s
/// ohne Stau möglich. Schicken wir nichts mehr (Fenster pausiert, App beendet), kehrt WLED nach
/// <see cref="RealtimeTimeoutSeconds"/> zu seinem eigenen Effekt zurück.
/// Siehe https://kno.wled.ge/interfaces/udp-realtime/ und https://kno.wled.ge/interfaces/json-api/.</summary>
public sealed class WledDeviceProvider : IDeviceProvider, IProviderLog
{
    public string ProviderId => "wled";
    public bool IsConnected { get; private set; }

    public event Action<LogLevel, string>? Log;

    private readonly Dictionary<string, (string Host, int LedCount, string Name)> _instances = new();
    private readonly HttpClient _http;

    /// <summary>true = Effektbilder per UDP-Echtzeit (DRGB/DNRGB), false = wie bis 0.3.6 per JSON-API
    /// (`seg.i`), z. B. falls „Receive UDP realtime“ in WLED abgeschaltet bleiben soll.</summary>
    public bool UseUdpRealtime { get; init; } = true;

    /// <summary>Byte 2 jedes Echtzeit-Pakets: so viele Sekunden nach dem letzten Paket kehrt WLED zum
    /// eigenen Effekt zurück.</summary>
    public const byte RealtimeTimeoutSeconds = 2;

    private const int DefaultUdpPort = 21324;
    private const int DrgbMaxLeds = 490;   // 2 + 490·3 = 1472 Bytes, passt in ein Ethernet-MTU-Datagramm
    private const int DnrgbMaxLeds = 489;  // 4 + 489·3 = 1471 Bytes

    private readonly UdpClient _udp = new(AddressFamily.InterNetwork);
    private readonly Dictionary<string, IPEndPoint> _endpoints = new();
    private readonly HashSet<string> _liveStarted = new();
    private readonly HashSet<string> _jsonBusy = new();

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
                    string transport = "JSON-API";
                    if (UseUdpRealtime)
                    {
                        int port = info.UdpPort is > 0 and < 65536 ? info.UdpPort : DefaultUdpPort;
                        var address = await ResolveIPv4Async(host, ct);
                        if (address is not null)
                        {
                            lock (_endpoints) _endpoints[host] = new IPEndPoint(address, port);
                            transport = $"UDP-Echtzeit an {address}:{port} ({(info.Leds.Count <= DrgbMaxLeds ? "DRGB" : "DNRGB")})";
                        }
                        else
                        {
                            Log?.Invoke(LogLevel.Warning, $"{existing.Name} ({host}): keine IPv4-Adresse gefunden — sende stattdessen über die JSON-API");
                        }
                    }
                    Log?.Invoke(info.Leds.Count > 0 ? LogLevel.Info : LogLevel.Warning,
                        $"{existing.Name} ({host}): erreichbar, WLED {info.Version} „{info.Name}“, {info.Leds.Count} LEDs, {transport}" +
                        (info.Leds.Count > 0 ? "" : " — ohne LEDs wird das Gerät nicht angezeigt (LED-Anzahl in WLED einstellen)"));
                }
            }
            catch (Exception ex)
            {
                // Einzelnes nicht erreichbares WLED-Gerät blockiert die anderen nicht.
                Log?.Invoke(LogLevel.Warning, $"{_instances[host].Name} ({host}): nicht erreichbar — {ExceptionText.Describe(ex)}");
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
        // Einzelfarbe (z. B. über die lokale REST-API) bleibt bei der JSON-API: Sie soll stehen bleiben und
        // nicht nach dem Echtzeit-Timeout wieder verschwinden.
        var host = ParseHost(deviceId);
        var body = new { on = true, seg = new[] { new { col = new[] { new[] { color.R, color.G, color.B } } } } };
        using var _ = await _http.PostAsJsonAsync($"http://{host}/json/state", body, ct);
    }

    public Task SetZoneColorsAsync(string deviceId, int zoneIndex, IReadOnlyList<RgbColor> colors, CancellationToken ct = default)
    {
        var host = ParseHost(deviceId);
        IPEndPoint? endpoint;
        lock (_endpoints) _endpoints.TryGetValue(host, out endpoint);
        if (endpoint is null) return SendJsonFrameAsync(host, colors, ct);

        bool first;
        lock (_liveStarted) first = _liveStarted.Add(host);
        if (first)
        {
            // Einmal einschalten und eine evtl. gesetzte "Live-Daten ignorieren"-Sperre (lor) lösen —
            // sonst zeigt ein ausgeschaltetes WLED die Echtzeitdaten nicht an.
            _ = _http.PostAsJsonAsync($"http://{host}/json/state", new { on = true, lor = 0 }, ct)
                .ContinueWith(t => { if (t.IsCompletedSuccessfully) t.Result.Dispose(); }, TaskScheduler.Default);
            Log?.Invoke(LogLevel.Info, $"{host}: UDP-Echtzeit gestartet ({colors.Count} LEDs pro Bild).");
        }

        // Synchron werfen würde den ganzen Engine-Takt abbrechen (auch für alle anderen Geräte) — Fehler
        // deshalb als fehlgeschlagenen Task melden.
        try
        {
            foreach (var packet in BuildRealtimePackets(colors, RealtimeTimeoutSeconds))
                _udp.Send(packet, packet.Length, endpoint);
            return Task.CompletedTask;
        }
        catch (Exception ex)
        {
            return Task.FromException(ex);
        }
    }

    /// <summary>WLED-UDP-Echtzeitpakete: bis <see cref="DrgbMaxLeds"/> LEDs ein DRGB-Paket
    /// <c>[2, timeout, r,g,b, …]</c>, darüber DNRGB-Pakete <c>[4, timeout, start_hi, start_lo, r,g,b, …]</c>
    /// mit je höchstens <see cref="DnrgbMaxLeds"/> LEDs.</summary>
    public static List<byte[]> BuildRealtimePackets(IReadOnlyList<RgbColor> colors, byte timeoutSeconds)
    {
        var packets = new List<byte[]>();
        if (colors.Count <= DrgbMaxLeds)
        {
            var p = new byte[2 + colors.Count * 3];
            p[0] = 2; p[1] = timeoutSeconds;
            for (int i = 0; i < colors.Count; i++)
            {
                p[2 + i * 3] = colors[i].R; p[3 + i * 3] = colors[i].G; p[4 + i * 3] = colors[i].B;
            }
            packets.Add(p);
            return packets;
        }
        for (int start = 0; start < colors.Count; start += DnrgbMaxLeds)
        {
            int n = Math.Min(DnrgbMaxLeds, colors.Count - start);
            var p = new byte[4 + n * 3];
            p[0] = 4; p[1] = timeoutSeconds; p[2] = (byte)(start >> 8); p[3] = (byte)start;
            for (int i = 0; i < n; i++)
            {
                var c = colors[start + i];
                p[4 + i * 3] = c.R; p[5 + i * 3] = c.G; p[6 + i * 3] = c.B;
            }
            packets.Add(p);
        }
        return packets;
    }

    private async Task SendJsonFrameAsync(string host, IReadOnlyList<RgbColor> colors, CancellationToken ct)
    {
        // Ein HTTP-POST dauert länger als ein Engine-Takt — läuft noch einer, dieses Bild auslassen, statt
        // Anfragen aufzustauen.
        lock (_jsonBusy) if (!_jsonBusy.Add(host)) return;
        try
        {
            // WLED akzeptiert eine flache Liste von Hex-Farben in "i", angewendet ab LED 0 fortlaufend.
            var hex = colors.Select(c => c.ToHex().TrimStart('#')).ToList();
            var body = new { on = true, seg = new[] { new { i = (object)hex } } };
            using var _ = await _http.PostAsJsonAsync($"http://{host}/json/state", body, ct);
        }
        finally
        {
            lock (_jsonBusy) _jsonBusy.Remove(host);
        }
    }

    private static async Task<IPAddress?> ResolveIPv4Async(string host, CancellationToken ct)
    {
        // "192.168.1.50:8080" — der HTTP-Port gilt nicht für UDP, nur die Adresse übernehmen.
        if (host.Count(ch => ch == ':') == 1) host = host[..host.IndexOf(':')];
        if (IPAddress.TryParse(host, out var ip)) return ip.AddressFamily == AddressFamily.InterNetwork ? ip : null;
        try
        {
            var addresses = await Dns.GetHostAddressesAsync(host, AddressFamily.InterNetwork, ct);
            return addresses.FirstOrDefault();
        }
        catch (SocketException)
        {
            return null;
        }
    }

    private static string ParseHost(string deviceId)
    {
        var idx = deviceId.IndexOf(':');
        return idx >= 0 ? deviceId[(idx + 1)..] : deviceId;
    }

    public ValueTask DisposeAsync()
    {
        _udp.Dispose();
        return ValueTask.CompletedTask;
    }

    private sealed class WledInfoResponse
    {
        [JsonPropertyName("leds")] public WledLedsInfo Leds { get; set; } = new();
        [JsonPropertyName("name")] public string Name { get; set; } = "";
        [JsonPropertyName("ver")] public string Version { get; set; } = "";
        [JsonPropertyName("udpport")] public int UdpPort { get; set; }
    }

    private sealed class WledLedsInfo
    {
        [JsonPropertyName("count")] public int Count { get; set; }
    }
}
