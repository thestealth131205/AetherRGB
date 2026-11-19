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
/// </summary>
public sealed class LocalApiHost
{
    private readonly DeviceRegistry _registry;
    private readonly int _port;
    private WebApplication? _app;

    public LocalApiHost(DeviceRegistry registry, int port)
    {
        _registry = registry;
        _port = port;
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
}
