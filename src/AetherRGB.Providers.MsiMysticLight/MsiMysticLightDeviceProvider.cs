using AetherRGB.Core.Devices;
using AetherRGB.Core.Diagnostics;

namespace AetherRGB.Providers.MsiMysticLight;

/// <summary>Direkter USB/HID-Treiber für MSI-Mainboard-RGB (Mystic Light), ohne OpenRGB-Server
/// als Zwischenschicht — speziell gebaut und zuerst zu testen am MSI MPG B550 Gaming Plus.
///
/// STATUS: EXPERIMENTELL. Anders als der OpenRGB-Provider (community-verifiziertes Protokoll für
/// viele Boards) basiert dieser Provider auf einem allgemeinen, nicht für dieses konkrete Board
/// verifizierten Report-Format (siehe <see cref="MsiMysticLightProtocol"/>). Erster Test sollte
/// immer <see cref="RunSafeSingleZoneTestAsync"/> sein, nicht ein voller Effekt über alle Zonen.</summary>
public sealed class MsiMysticLightDeviceProvider : IDeviceProvider, IProviderLog
{
    public string ProviderId => "msimysticlight";
    public bool IsConnected { get; private set; }

    /// <summary>LED-Anzahl der einzigen gemeldeten Zone — am B550 Gaming Plus gibt es onboard-LEDs
    /// plus einen JRGB1-Header; ohne verifizierte Pro-Header-Trennung wird hier konservativ eine
    /// einzige Sammelzone gemeldet. Über <see cref="LedCountOverride"/> aus der Konfiguration anpassbar,
    /// falls die tatsächliche LED-Anzahl am Header abweicht.</summary>
    public int LedCountOverride { get; set; } = 1;

    public event Action<LogLevel, string>? Log;

    private readonly MsiHidTransport _transport = new();

    public Task ConnectAsync(CancellationToken ct = default)
    {
        IsConnected = _transport.TryOpen(msg => Log?.Invoke(LogLevel.Info, msg));
        if (!IsConnected)
            Log?.Invoke(LogLevel.Warning, "MSI Mystic Light: kein passendes HID-Gerät gefunden oder Öffnen fehlgeschlagen. " +
                        "Bekannte MSI-USB-HID-Geräte zur Diagnose: " +
                        string.Join(" | ", MsiHidTransport.DumpDiscoveredDevices()));
        return Task.CompletedTask;
    }

    public Task<IReadOnlyList<RgbDevice>> GetDevicesAsync(CancellationToken ct = default)
    {
        if (!IsConnected || _transport.Device is null)
            return Task.FromResult<IReadOnlyList<RgbDevice>>(Array.Empty<RgbDevice>());

        var device = new RgbDevice
        {
            Id = $"{ProviderId}:mainboard",
            Name = "MSI Mainboard (Mystic Light, direkt per USB/HID)",
            Vendor = DeviceVendor.MsiMysticLight,
            Zones = new[]
            {
                new LedZone { Index = 0, Name = "Onboard + ARGB-Header (Sammelzone)", LedCount = LedCountOverride }
            },
            IsOnline = true
        };
        return Task.FromResult<IReadOnlyList<RgbDevice>>(new[] { device });
    }

    public Task SetZoneColorAsync(string deviceId, int zoneIndex, RgbColor color, CancellationToken ct = default)
    {
        if (!IsConnected) return Task.CompletedTask;
        var report = MsiMysticLightProtocol.BuildSetZoneColorReport(zoneIndex, color.R, color.G, color.B);
        if (!_transport.WriteFeatureReport(report))
            _transport.WriteOutputReport(report); // Fallback, falls dieses Board Output- statt Feature-Reports erwartet.
        return Task.CompletedTask;
    }

    public Task SetZoneColorsAsync(string deviceId, int zoneIndex, IReadOnlyList<RgbColor> colors, CancellationToken ct = default)
    {
        if (!IsConnected) return Task.CompletedTask;

        if (colors.Count == 1)
            return SetZoneColorAsync(deviceId, zoneIndex, colors[0], ct);

        var pixels = colors.Select(c => (c.R, c.G, c.B)).ToList();
        var report = MsiMysticLightProtocol.BuildSetZonePixelsReport(zoneIndex, pixels);
        if (!_transport.WriteFeatureReport(report))
            _transport.WriteOutputReport(report);
        return Task.CompletedTask;
    }

    /// <summary>Sicherer erster Test für neue/unverifizierte Boards: setzt genau eine Zone auf eine
    /// auffällige Testfarbe (Magenta) und lässt den Aufrufer (UI) den Nutzer fragen, ob eine LED
    /// tatsächlich reagiert hat — bevor irgendein Effekt über alle Zonen läuft.</summary>
    public async Task<bool> RunSafeSingleZoneTestAsync(CancellationToken ct = default)
    {
        if (!IsConnected) return false;
        var report = MsiMysticLightProtocol.BuildSingleZoneTestReport(zoneIndex: 0);
        bool sent = _transport.WriteFeatureReport(report) || _transport.WriteOutputReport(report);
        Log?.Invoke(sent ? LogLevel.Info : LogLevel.Error, sent
            ? "Testreport (Magenta, Zone 0) gesendet — bitte prüfen, ob eine LED reagiert hat."
            : "Testreport konnte nicht gesendet werden (Schreibfehler auf dem HID-Stream).");
        await Task.CompletedTask;
        return sent;
    }

    public ValueTask DisposeAsync()
    {
        _transport.Dispose();
        return ValueTask.CompletedTask;
    }
}
