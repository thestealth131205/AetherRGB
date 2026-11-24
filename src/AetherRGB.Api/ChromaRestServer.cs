using System.Collections.Concurrent;
using System.Text.Json;
using AetherRGB.Core.Chroma;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Logging;

namespace AetherRGB.Api;

/// <summary>Nachbau der Razer-Chroma-REST-API (<c>http://localhost:54235/razer/chromasdk</c>), wie sie Razer
/// Synapse bereitstellt. Spiele, die Chroma über REST statt über die DLL ansprechen, landen so bei AetherRGB —
/// ohne Code im Spielprozess. Bindet nur an die Loopback-Adressen. Läuft Razer Synapse, ist der Port belegt
/// und <see cref="StartAsync"/> schlägt fehl (dann bekommt eben Synapse die Effekte).
///
///   GET    /razer/chromasdk                  → Version
///   POST   /razer/chromasdk                  → Sitzung anlegen, {"sessionid", "uri"}
///   PUT    {uri}/heartbeat                   → {"tick"}
///   PUT    {uri}/{gerät}   {"effect","param"} → sofort anzeigen
///   POST   {uri}/{gerät}   {"effect","param"} oder {"effects":[…]} → speichern, {"id"} / {"results"}
///   PUT    {uri}/effect    {"id"} oder {"ids":[…]} → gespeicherten Effekt anzeigen
///   DELETE {uri}/effect    {"id"} oder {"ids":[…]} → löschen
///   DELETE {uri}                             → Sitzung beenden
/// </summary>
public sealed class ChromaRestServer
{
    public const int DefaultPort = 54235;
    public const string SourceName = "Chroma REST";

    /// <summary>Synapse beendet Sitzungen ohne Heartbeat nach 15 s.</summary>
    private static readonly TimeSpan SessionTimeout = TimeSpan.FromSeconds(15);
    private const int MaxEffectsPerSession = 2000;
    private const long MaxBodyBytes = 256 * 1024;

    private const int ResultSuccess = 0, ResultInvalidParameter = 87, ResultNotFound = 1168, ResultNotSupported = 50;

    private readonly ChromaHub _hub;
    private readonly int _port;
    private WebApplication? _app;
    private Timer? _pruneTimer;
    private int _nextSession = new Random().Next(10000, 60000);

    private sealed class RestSession
    {
        public readonly ConcurrentDictionary<Guid, (ChromaDevice Device, ChromaFrame Frame)> Effects = new();
        public long Tick;
    }

    private readonly ConcurrentDictionary<int, RestSession> _sessions = new();

    public ChromaRestServer(ChromaHub hub, int port = DefaultPort)
    {
        _hub = hub;
        _port = port;
    }

    public int Port => _port;

    public async Task StartAsync()
    {
        var builder = WebApplication.CreateBuilder();
        builder.Logging.ClearProviders();
        builder.WebHost.ConfigureKestrel(options =>
        {
            // Spiele sprechen "localhost" an — das kann je nach System 127.0.0.1 oder ::1 sein.
            options.ListenLocalhost(_port);
            options.Limits.MaxRequestBodySize = MaxBodyBytes;
        });

        var app = builder.Build();

        app.MapGet("/razer/chromasdk", () => Results.Json(new { core = "3.36.0", device = "3.36.0", version = "3.36.0" }));

        app.MapPost("/razer/chromasdk", async (HttpRequest request) =>
        {
            var body = await ReadJsonAsync(request);
            string title = body is { ValueKind: JsonValueKind.Object } b && b.TryGetProperty("title", out var t) && t.ValueKind == JsonValueKind.String
                ? t.GetString() ?? "" : "";
            int id = Interlocked.Increment(ref _nextSession);
            if (!_hub.Open(Key(id), title.Length > 0 ? title : "REST-Client " + id, SourceName))
                return Results.Json(new { result = ResultNotSupported, error = "Zu viele Chroma-Sitzungen." });
            _sessions[id] = new RestSession();
            return Results.Json(new { sessionid = id, uri = $"http://localhost:{_port}/sessions/{id}/chromasdk" });
        });

        app.MapPut("/sessions/{id:int}/chromasdk/heartbeat", (int id) =>
        {
            if (!_sessions.TryGetValue(id, out var s)) return Results.NotFound();
            _hub.Touch(Key(id));
            return Results.Json(new { tick = Interlocked.Increment(ref s.Tick) });
        });

        app.MapDelete("/sessions/{id:int}/chromasdk", (int id) =>
        {
            if (_sessions.TryRemove(id, out _)) _hub.Close(Key(id));
            return Results.Json(new { result = ResultSuccess });
        });

        app.MapPut("/sessions/{id:int}/chromasdk/effect", async (int id, HttpRequest request) =>
        {
            if (!_sessions.TryGetValue(id, out var s)) return Results.NotFound();
            _hub.Touch(Key(id));
            return EachId(await ReadJsonAsync(request), effectId =>
            {
                if (!s.Effects.TryGetValue(effectId, out var e)) return ResultNotFound;
                _hub.SetFrame(Key(id), e.Device, e.Frame);
                return ResultSuccess;
            });
        });

        app.MapDelete("/sessions/{id:int}/chromasdk/effect", async (int id, HttpRequest request) =>
        {
            if (!_sessions.TryGetValue(id, out var s)) return Results.NotFound();
            _hub.Touch(Key(id));
            return EachId(await ReadJsonAsync(request), effectId => s.Effects.TryRemove(effectId, out _) ? ResultSuccess : ResultNotFound);
        });

        app.MapPut("/sessions/{id:int}/chromasdk/{device}", async (int id, string device, HttpRequest request) =>
        {
            if (!_sessions.TryGetValue(id, out _)) return Results.NotFound();
            if (!ChromaDecoder.TryParseDevice(device, out var dev)) return Results.Json(new { result = ResultNotSupported });
            _hub.Touch(Key(id));
            var body = await ReadJsonAsync(request);
            if (body is not { } json || ChromaDecoder.DecodeRest(dev, json) is not { } frame)
                return Results.Json(new { result = ResultInvalidParameter });
            _hub.SetFrame(Key(id), dev, frame);
            return Results.Json(new { result = ResultSuccess });
        });

        app.MapPost("/sessions/{id:int}/chromasdk/{device}", async (int id, string device, HttpRequest request) =>
        {
            if (!_sessions.TryGetValue(id, out var s)) return Results.NotFound();
            if (!ChromaDecoder.TryParseDevice(device, out var dev)) return Results.Json(new { result = ResultNotSupported });
            _hub.Touch(Key(id));
            var body = await ReadJsonAsync(request);
            if (body is not { ValueKind: JsonValueKind.Object } json) return Results.Json(new { result = ResultInvalidParameter });

            if (json.TryGetProperty("effects", out var effects) && effects.ValueKind == JsonValueKind.Array)
            {
                var results = new List<object>();
                foreach (var effect in effects.EnumerateArray()) results.Add(Store(s, dev, effect));
                return Results.Json(new { results });
            }
            return Results.Json(Store(s, dev, json));
        });

        _app = app;
        await app.StartAsync();
        _pruneTimer = new Timer(_ => Prune(), null, TimeSpan.FromSeconds(5), TimeSpan.FromSeconds(5));
    }

    public async Task StopAsync()
    {
        _pruneTimer?.Dispose();
        _pruneTimer = null;
        if (_app is not null)
        {
            await _app.StopAsync();
            await _app.DisposeAsync();
            _app = null;
        }
        _sessions.Clear();
        _hub.CloseAll(SourceName);
    }

    private static string Key(int id) => "rest:" + id;

    private object Store(RestSession s, ChromaDevice device, JsonElement effect)
    {
        if (ChromaDecoder.DecodeRest(device, effect) is not { } frame) return new { result = ResultInvalidParameter };
        if (s.Effects.Count >= MaxEffectsPerSession) return new { result = ResultNotSupported };
        var id = Guid.NewGuid();
        s.Effects[id] = (device, frame);
        return new { id = id.ToString(), result = ResultSuccess };
    }

    /// <summary><c>{"id":"…"}</c> → <c>{"result"}</c>, <c>{"ids":[…]}</c> → <c>{"results":[{"id","result"}]}</c>.</summary>
    private static IResult EachId(JsonElement? body, Func<Guid, int> action)
    {
        if (body is not { ValueKind: JsonValueKind.Object } json) return Results.Json(new { result = ResultInvalidParameter });
        if (json.TryGetProperty("ids", out var ids) && ids.ValueKind == JsonValueKind.Array)
        {
            var results = ids.EnumerateArray().Select(el =>
            {
                var text = el.ValueKind == JsonValueKind.String ? el.GetString() : null;
                return (object)new { id = text, result = Guid.TryParse(text, out var g) ? action(g) : ResultInvalidParameter };
            }).ToList();
            return Results.Json(new { results });
        }
        if (json.TryGetProperty("id", out var idEl) && idEl.ValueKind == JsonValueKind.String && Guid.TryParse(idEl.GetString(), out var guid))
            return Results.Json(new { result = action(guid) });
        return Results.Json(new { result = ResultInvalidParameter });
    }

    private static async Task<JsonElement?> ReadJsonAsync(HttpRequest request)
    {
        try
        {
            using var doc = await JsonDocument.ParseAsync(request.Body, new JsonDocumentOptions { MaxDepth = 8 });
            return doc.RootElement.Clone();
        }
        catch (Exception e) when (e is JsonException or BadHttpRequestException or IOException)
        {
            return null;
        }
    }

    private void Prune()
    {
        _hub.Prune(_ => true, source => source == SourceName ? SessionTimeout : null);
        foreach (var id in _sessions.Keys)
            if (!_hub.IsOpen(Key(id))) _sessions.TryRemove(id, out _);
    }
}
