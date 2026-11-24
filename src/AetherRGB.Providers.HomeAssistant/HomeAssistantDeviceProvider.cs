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

    // ---- Senden: höchstens ein Aufruf pro Licht und Intervall -------------------------------------
    // HA-Lichter (Zigbee, Hue, WLAN-Lampen) vertragen keine 30 Befehle pro Sekunde: Die Befehle stauen
    // sich in HA bzw. im Funknetz, Lampen hängen hinterher oder reagieren gar nicht mehr. Die Engine
    // liefert ihre Bilder deshalb nur in einen Sammler; ein eigener Sende-Takt schickt pro Licht
    // höchstens alle MinUpdateInterval den Mittelwert aller Farben seit dem letzten Senden.

    /// <summary>Kleinster Abstand zwischen zwei Befehlen an dasselbe Licht. Unter 3 s wird nicht zugelassen.</summary>
    public static readonly TimeSpan MinAllowedInterval = TimeSpan.FromSeconds(3);

    private TimeSpan _interval = MinAllowedInterval;
    public TimeSpan MinUpdateInterval
    {
        get => _interval;
        init => _interval = value < MinAllowedInterval ? MinAllowedInterval : value;
    }

    /// <summary>HA-Parameter <c>transition</c> = Intervall mitsenden: Lampen, die das können, blenden weich
    /// zur nächsten Farbe über, statt alle paar Sekunden zu springen. HA lässt ihn bei Lampen ohne
    /// Übergangsfunktion selbst weg.</summary>
    public bool UseTransition { get; init; } = true;

    private sealed class LightState
    {
        public long SumR, SumG, SumB;
        public int Count;
        public DateTime LastSentUtc = DateTime.MinValue;
        public (byte R, byte G, byte B)? LastSent;
        public bool Sending;
    }

    private readonly Dictionary<string, LightState> _states = new();
    private readonly CancellationTokenSource _cts = new();
    private Task? _sendLoop;

    public Task SetZoneColorAsync(string deviceId, int zoneIndex, RgbColor color, CancellationToken ct = default)
    {
        var entityId = ParseEntityId(deviceId);
        lock (_states)
        {
            if (!_states.TryGetValue(entityId, out var st)) _states[entityId] = st = new LightState();
            st.SumR += color.R; st.SumG += color.G; st.SumB += color.B; st.Count++;
            if (_sendLoop is null && !_cts.IsCancellationRequested) _sendLoop = Task.Run(() => SendLoopAsync(_cts.Token));
        }
        return Task.CompletedTask;
    }

    public Task SetZoneColorsAsync(string deviceId, int zoneIndex, IReadOnlyList<RgbColor> colors, CancellationToken ct = default) =>
        // Ein HA-Licht hat nur ein "Pixel" — Mittelwert, falls ein Mehr-Pixel-Effekt darauf gemappt wird.
        SetZoneColorAsync(deviceId, zoneIndex, Average(colors), ct);

    private async Task SendLoopAsync(CancellationToken ct)
    {
        Log?.Invoke(LogLevel.Info, $"Sende-Takt: höchstens ein Befehl pro Licht alle {_interval.TotalSeconds:0.#} s " +
                                   $"(Mittelwert der Effektfarben dazwischen{(UseTransition ? ", mit weichem Übergang" : "")}).");
        using var timer = new PeriodicTimer(TimeSpan.FromMilliseconds(250));
        try
        {
            while (await timer.WaitForNextTickAsync(ct))
            {
                var now = DateTime.UtcNow;
                var due = new List<(string EntityId, LightState State, byte R, byte G, byte B)>();
                lock (_states)
                {
                    foreach (var (entityId, st) in _states)
                    {
                        if (st.Count == 0 || st.Sending || now - st.LastSentUtc < _interval) continue;
                        var avg = ((byte)(st.SumR / st.Count), (byte)(st.SumG / st.Count), (byte)(st.SumB / st.Count));
                        st.SumR = st.SumG = st.SumB = 0; st.Count = 0;
                        if (st.LastSent is { } last && Math.Abs(last.R - avg.Item1) <= 2 &&
                            Math.Abs(last.G - avg.Item2) <= 2 && Math.Abs(last.B - avg.Item3) <= 2)
                            continue; // unverändert — nichts senden, Intervall läuft weiter ab letztem Befehl
                        st.Sending = true;
                        st.LastSentUtc = now;
                        due.Add((entityId, st, avg.Item1, avg.Item2, avg.Item3));
                    }
                }
                foreach (var d in due)
                    _ = SendAsync(d.EntityId, d.State, d.R, d.G, d.B, ct);
            }
        }
        catch (OperationCanceledException) { }
    }

    private async Task SendAsync(string entityId, LightState st, byte r, byte g, byte b, CancellationToken ct)
    {
        try
        {
            int max = Math.Max(r, Math.Max(g, b));
            object body;
            string service;
            double? transition = UseTransition ? Math.Round(_interval.TotalSeconds, 1) : null;
            if (max < 4)
            {
                // rgb_color [0,0,0] verstehen viele Lampen nicht — Schwarz heißt hier ausschalten.
                service = "turn_off";
                body = transition is { } t ? new { entity_id = entityId, transition = t } : new { entity_id = entityId };
            }
            else
            {
                // Farbe auf volle Sättigung normieren und die Helligkeit getrennt schicken — viele Lampen
                // nehmen rgb_color nur als Farbton und behielten sonst ihre alte Helligkeit.
                var rgb = new[] { r * 255 / max, g * 255 / max, b * 255 / max };
                service = "turn_on";
                body = transition is { } t
                    ? new { entity_id = entityId, rgb_color = rgb, brightness = max, transition = t }
                    : new { entity_id = entityId, rgb_color = rgb, brightness = max };
            }
            using var response = await _http.PostAsJsonAsync($"{_baseUrl}/api/services/light/{service}", body, ct);
            if (!response.IsSuccessStatusCode)
                Log?.Invoke(LogLevel.Warning, $"{entityId}: light.{service} abgelehnt — HTTP {(int)response.StatusCode} {response.ReasonPhrase}");
            lock (_states) st.LastSent = (r, g, b);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested) { }
        catch (Exception ex)
        {
            Log?.Invoke(LogLevel.Warning, $"{entityId}: Senden fehlgeschlagen — {ExceptionText.Describe(ex)}");
        }
        finally
        {
            lock (_states) st.Sending = false;
        }
    }

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

    public async ValueTask DisposeAsync()
    {
        _cts.Cancel();
        if (_sendLoop is not null)
        {
            try { await _sendLoop; } catch { /* beim Beenden egal */ }
        }
        // _cts bewusst nicht entsorgen: Laufende SendAsync-Aufrufe können das Token noch abfragen.
    }
}
