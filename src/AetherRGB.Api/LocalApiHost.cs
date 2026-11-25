using AetherRGB.Core.Devices;
using AetherRGB.Core.Effects;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Logging;

namespace AetherRGB.Api;

/// <summary>
/// Offene, lokale REST-API für Drittsoftware (Spiele-Mods, eigene Skripte, Dashboards) und als
/// Gegenstück für eine Home-Assistant-Integration. Bindet absichtlich NUR an 127.0.0.1 — es gibt
/// keine Konfigurationsoption, die das nach außen öffnet, und keinerlei Cloud-Relay. Wer von
/// außerhalb des PCs zugreifen will, muss das über das eigene lokale Netz selbst einrichten
/// (z. B. Port-Weiterleitung im Router), das ist bewusst nicht Teil von AetherRGB.
///
/// Endpunkte:
///   GET  /api/devices                                 -> Liste aller erkannten Geräte/Zonen
///   PUT  /api/devices/{deviceId}/zones/{zoneIndex}/color   Body: {"hex":"#RRGGBB"}
///   GET  /api/health                                  -> {"status":"ok"}
///   GET  /api/sensors                                 -> alle Systemwerte (Windows, NVIDIA, AIDA64), wie im Sensor-Panel
///   GET  /api/sensors/{id}                            -> ein Wert, z. B. /api/sensors/win/cpu/load
///   GET  /api/profiles                                -> alle Profile, mit "active"
///   POST /api/profiles/{idOderName}/activate          -> Profil wechseln (Name ohne Groß-/Kleinschreibung)
///   POST /api/profiles/next, /api/profiles/previous   -> zum nächsten/vorherigen Profil
/// </summary>
public sealed class LocalApiHost
{
    private readonly DeviceRegistry _registry;
    private readonly int _port;
    private WebApplication? _app;

    /// <summary>Profilsteuerung der App — ohne (null) antworten die Profil-Endpunkte mit 503.</summary>
    public AetherRGB.Core.Layout.ILightingProfileControl? Profiles { get; init; }

    public LocalApiHost(DeviceRegistry registry, int port)
    {
        _registry = registry;
        _port = port;
    }

    private async Task<IResult> StepProfile(int direction)
    {
        if (Profiles is null) return Results.StatusCode(503);
        var name = await Profiles.StepAsync(direction);
        return name is null ? Results.Conflict(new { error = "Es gibt nur ein Profil." }) : Results.Json(new { active = name });
    }

    public async Task StartAsync()
    {
        var builder = WebApplication.CreateBuilder();
        builder.Logging.ClearProviders();
        builder.WebHost.ConfigureKestrel(options =>
        {
            options.Listen(System.Net.IPAddress.Loopback, _port);
        });

        var app = builder.Build();

        app.MapGet("/api/health", () => Results.Json(new { status = "ok" }));

        app.MapGet("/api/sensors", () =>
        {
            var hub = AetherRGB.Core.Sensors.SensorHub.Shared;
            hub.Demand();
            return Results.Json(hub.Snapshot.Values.OrderBy(r => r.Id).Select(SensorDto));
        });

        app.MapGet("/api/sensors/{**id}", (string id) =>
        {
            var hub = AetherRGB.Core.Sensors.SensorHub.Shared;
            hub.Demand();
            return hub.Get(id) is { } r ? Results.Json(SensorDto(r)) : Results.NotFound(new { error = $"Sensor '{id}' nicht gefunden (Liste: GET /api/sensors)." });
        });

        app.MapGet("/api/profiles", () => Profiles is null
            ? Results.StatusCode(503)
            : Results.Json(Profiles.ListProfiles().Select(p => new { id = p.Id, name = p.Name, active = p.Active, summary = p.Summary })));

        app.MapPost("/api/profiles/next", async () => await StepProfile(+1));
        app.MapPost("/api/profiles/previous", async () => await StepProfile(-1));

        app.MapPost("/api/profiles/{idOrName}/activate", async (string idOrName) =>
        {
            if (Profiles is null) return Results.StatusCode(503);
            var name = await Profiles.ActivateAsync(idOrName);
            return name is null
                ? Results.NotFound(new { error = $"Profil '{idOrName}' nicht gefunden (Liste: GET /api/profiles)." })
                : Results.Json(new { active = name });
        });

        app.MapGet("/api/devices", async () =>
        {
            var devices = await _registry.GetAllDevicesAsync();
            var dto = devices.Select(d => new
            {
                id = d.Id,
                name = d.Name,
                vendor = d.Vendor.ToString(),
                online = d.IsOnline,
                zones = d.Zones.Select(z => new { index = z.Index, name = z.Name, ledCount = z.LedCount })
            });
            return Results.Json(dto);
        });

        app.MapPut("/api/devices/{deviceId}/zones/{zoneIndex:int}/color", async (string deviceId, int zoneIndex, ColorRequest body) =>
        {
            if (string.IsNullOrWhiteSpace(body.Hex))
                return Results.BadRequest(new { error = "Feld 'hex' fehlt, z. B. {\"hex\":\"#FF0000\"}." });

            RgbColor color;
            try { color = RgbColor.FromHex(body.Hex); }
            catch { return Results.BadRequest(new { error = "Ungültiger Hex-Farbcode." }); }

            var provider = _registry.FindProviderFor(deviceId);
            if (provider is null)
                return Results.NotFound(new { error = $"Kein Provider für Gerät '{deviceId}' gefunden." });

            await provider.SetZoneColorAsync(deviceId, zoneIndex, color);
            return Results.Json(new { status = "ok" });
        });

        _app = app;
        await app.StartAsync();
    }

    public async Task StopAsync()
    {
        if (_app is not null) await _app.StopAsync();
    }

    public sealed record ColorRequest(string Hex);

    private static object SensorDto(AetherRGB.Core.Sensors.SensorReading r) => new
    {
        id = r.Id,
        name = r.Name,
        group = r.Group,
        source = r.Source,
        unit = r.Unit,
        value = r.IsNumeric ? r.Value : null,
        text = r.IsNumeric ? AetherRGB.Core.Sensors.SensorFormat.Format(r, culture: System.Globalization.CultureInfo.InvariantCulture) : r.Text,
    };
}
