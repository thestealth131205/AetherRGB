using AetherRGB.Core.Devices;
using AetherRGB.Core.Diagnostics;

namespace AetherRGB.Providers.Redragon;

/// <summary>Direkter USB/HID-Treiber für Redragon-RGB-Mäuse, ohne OpenRGB als Zwischenschicht —
/// weil die M914 dort nicht bestätigt unterstützt ist.
///
/// STATUS: EXPERIMENTELL. Basiert auf einem allgemeinen, für dieses konkrete Modell nicht
/// verifizierten Report-Format (siehe <see cref="RedragonHidProtocol"/>). Erster Test sollte immer
/// <see cref="RunSafeColorTestAsync"/> sein, nicht ein voller Effekt.</summary>
public sealed class RedragonDeviceProvider : IDeviceProvider, IProviderLog
{
    public string ProviderId => "redragon";
    public bool IsConnected { get; private set; }

    /// <summary>LED-Anzahl der einzigen gemeldeten Zone — ohne verifizierte Einzel-LED-Ansteuerung
    /// wird konservativ eine Sammelzone gemeldet. Über die Konfiguration anpassbar.</summary>
    public int LedCountOverride { get; set; } = 1;

    public int VendorIdOverride { get; set; }
    public int ProductIdOverride { get; set; }

    public event Action<LogLevel, string>? Log;

    private readonly RedragonHidTransport _transport = new();

    /// <summary>Report-ID, die sich im sicheren Test (<see cref="RunSafeColorTestAsync"/>) als vom
    /// Gerät akzeptiert herausgestellt hat (SetFeature/Write hat nicht geworfen). Null, solange noch
    /// keine erfolgreich war — dann wird bei jedem Senden erneut die ganze Liste durchprobiert, was
    /// unnötig Zeit kostet, aber wenigstens nicht falsch rät.</summary>
    private byte? _workingReportId;

    public Task ConnectAsync(CancellationToken ct = default)
    {
        _transport.VendorIdOverride = VendorIdOverride;
        _transport.ProductIdOverride = ProductIdOverride;
        IsConnected = _transport.TryOpen(msg => Log?.Invoke(LogLevel.Info, msg));
        if (!IsConnected)
            Log?.Invoke(LogLevel.Warning, "Redragon: kein passendes HID-Gerät gefunden oder Öffnen fehlgeschlagen. " +
                        "Bekannte Geräte unter dieser Vendor-ID zur Diagnose: " +
                        string.Join(" | ", RedragonHidTransport.DumpDiscoveredDevices(
                            VendorIdOverride > 0 ? VendorIdOverride : RedragonHidTransport.RedragonVendorId)));
        return Task.CompletedTask;
    }

    public Task<IReadOnlyList<RgbDevice>> GetDevicesAsync(CancellationToken ct = default)
    {
        if (!IsConnected || _transport.Device is null)
            return Task.FromResult<IReadOnlyList<RgbDevice>>(Array.Empty<RgbDevice>());

        var device = new RgbDevice
        {
            Id = $"{ProviderId}:mouse",
            Name = "Redragon-Maus (eigenes HID-Protokoll, experimentell)",
            Vendor = DeviceVendor.Generic,
            Zones = new[]
            {
                new LedZone { Index = 0, Name = "Logo + Scrollrad (Sammelzone)", LedCount = LedCountOverride }
            },
            IsOnline = true
        };
        return Task.FromResult<IReadOnlyList<RgbDevice>>(new[] { device });
    }

    public Task SetZoneColorAsync(string deviceId, int zoneIndex, RgbColor color, CancellationToken ct = default)
    {
        if (!IsConnected) return Task.CompletedTask;
        void Diag(string msg) => Log?.Invoke(LogLevel.Debug, $"[redragon] {msg}");

        // Steht die funktionierende Report-ID schon fest (aus dem sicheren Test), nur noch die nehmen —
        // sonst bei jeder Farbänderung erneut alle Kandidaten durchprobieren und erst beim ersten
        // Erfolg aufhören, damit wenigstens irgendetwas ankommt, bis der Test gelaufen ist.
        foreach (var reportId in _workingReportId.HasValue ? new[] { _workingReportId.Value } : RedragonHidProtocol.CandidateReportIds)
        {
            var featureReport = RedragonHidProtocol.BuildSetColorReport(color.R, color.G, color.B, _transport.FeatureReportLength, reportId);
            if (_transport.WriteFeatureReport(featureReport, Diag)) { _workingReportId = reportId; return Task.CompletedTask; }

            // Fallback, falls dieses Modell Output- statt Feature-Reports erwartet — eigene Puffergröße,
            // weil Feature- und Output-Report-Länge auf demselben Gerät unterschiedlich sein können.
            var outputReport = RedragonHidProtocol.BuildSetColorReport(color.R, color.G, color.B, _transport.OutputReportLength, reportId);
            if (_transport.WriteOutputReport(outputReport, Diag)) { _workingReportId = reportId; return Task.CompletedTask; }
        }
        return Task.CompletedTask;
    }

    public Task SetZoneColorsAsync(string deviceId, int zoneIndex, IReadOnlyList<RgbColor> colors, CancellationToken ct = default)
    {
        if (!IsConnected || colors.Count == 0) return Task.CompletedTask;

        // Ohne verifizierte Pro-LED-Ansteuerung bekommt die Sammelzone den Mittelwert der Effektfarben —
        // genauso wie beim MSI-Direct-HID-Provider für Anschlüsse ohne Einzel-LED-Unterstützung.
        int r = 0, g = 0, b = 0;
        foreach (var c in colors) { r += c.R; g += c.G; b += c.B; }
        var mean = new RgbColor((byte)(r / colors.Count), (byte)(g / colors.Count), (byte)(b / colors.Count));
        return SetZoneColorAsync(deviceId, zoneIndex, mean, ct);
    }

    /// <summary>Sicherer erster Test für ein neues/unverifiziertes Modell: setzt die Zone auf eine
    /// auffällige Testfarbe (Magenta) und lässt den Aufrufer (UI) den Nutzer fragen, ob eine LED
    /// tatsächlich reagiert hat — bevor irgendein Effekt läuft.</summary>
    public async Task<bool> RunSafeColorTestAsync(CancellationToken ct = default)
    {
        if (!IsConnected) return false;
        void Diag(string msg) => Log?.Invoke(LogLevel.Debug, $"[redragon] {msg}");

        // Probiert nacheinander mehrere Report-IDs durch, weil eine SetFeature-Ablehnung ohne
        // Win32-Fehlercode meist bedeutet, dass die ID gar nicht zum HID-Report-Deskriptor dieses
        // Interfaces passt — nicht, dass das Interface grundsätzlich falsch ist.
        _workingReportId = null;
        bool sent = false;
        foreach (var reportId in RedragonHidProtocol.CandidateReportIds)
        {
            Log?.Invoke(LogLevel.Debug, $"[redragon] Teste Report-ID 0x{reportId:X2} …");

            var featureReport = RedragonHidProtocol.BuildSingleColorTestReport(_transport.FeatureReportLength, reportId);
            sent = _transport.WriteFeatureReport(featureReport, Diag);
            if (!sent)
            {
                var outputReport = RedragonHidProtocol.BuildSingleColorTestReport(_transport.OutputReportLength, reportId);
                sent = _transport.WriteOutputReport(outputReport, Diag);
            }
            if (sent)
            {
                _workingReportId = reportId;
                Log?.Invoke(LogLevel.Info, $"[redragon] Report-ID 0x{reportId:X2} wurde vom System angenommen (SetFeature/Write hat nicht geworfen).");
                break;
            }
        }

        Log?.Invoke(sent ? LogLevel.Info : LogLevel.Error, sent
            ? "Testreport (Magenta) gesendet — bitte prüfen, ob sich an der Maus etwas verändert hat. " +
              "Wichtig: Dass der Aufruf technisch durchging, heißt noch nicht, dass die Farbe auch stimmt — " +
              "nur dass diese Report-ID vom System akzeptiert wurde."
            : "Testreport konnte mit keiner der bekannten Report-IDs gesendet werden. Die genauen Win32-Fehlercodes " +
              "stehen in den Zeilen direkt darüber — die sind jetzt wichtig für die weitere Diagnose.");
        await Task.CompletedTask;
        return sent;
    }

    public ValueTask DisposeAsync()
    {
        _transport.Dispose();
        return ValueTask.CompletedTask;
    }
}
