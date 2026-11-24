using System.Collections.Concurrent;
using AetherRGB.Core.Devices;
using AetherRGB.Core.Diagnostics;

namespace AetherRGB.Providers.MsiMysticLight;

/// <summary>Steuert MSI-RGB über das offizielle Mystic Light SDK: AetherRGB meldet sich als
/// Drittanbieter-Client beim laufenden MSI-Center-Dienst (LEDKeeper2) an, statt selbst auf den
/// USB/SMBus zu schreiben — dadurch kein Konflikt mit MSI Center.
///
/// Voraussetzungen: MSI Center mit Mystic-Light-Modul installiert und laufend, Steuerung durch
/// Drittanbieter-Software in Mystic Light erlaubt, AetherRGB mit Administratorrechten gestartet,
/// MysticLight_SDK(_x64).dll vorhanden (siehe <see cref="MysticLightSdkNative"/>).
///
/// Das SDK adressiert LED-Bereiche (z. B. "JRAINBOW1", "Onboard LED"), nicht einzelne Pixel — jeder
/// Bereich wird daher als Zone mit einer LED gemeldet. Die SDK-Aufrufe gehen über IPC und sind
/// langsam; ein eigener Sende-Thread schickt pro Bereich nur die jeweils neueste Farbe und nur bei
/// Änderung, damit die Effekt-Engine und die übrigen Geräte nie auf MSI warten.</summary>
public sealed class MsiMysticLightSdkDeviceProvider : IDeviceProvider, IProviderLog
{
    public string ProviderId => "msisdk";
    public bool IsConnected { get; private set; }

    /// <summary>Optionaler Pfad zur SDK-DLL (Datei oder Ordner), leer = Standardorte.</summary>
    public string? DllPath { get; init; }

    public event Action<LogLevel, string>? Log;

    /// <summary>Erster existierender DLL-Pfad an den Suchorten, sonst null — für Statusanzeigen.</summary>
    public static string? FindSdkDll(string? configuredPath) => MysticLightSdkNative.FindDll(configuredPath);

    private sealed record SdkDevice(string Type, string[] AreaNames);

    private MysticLightSdkNative? _sdk;
    private readonly List<SdkDevice> _devices = new();
    private readonly ConcurrentDictionary<(string Type, int Index), RgbColor> _pending = new();
    private readonly Dictionary<(string Type, int Index), RgbColor> _lastSent = new();
    private readonly HashSet<(string Type, int Index)> _failedAreas = new();
    private readonly AutoResetEvent _wake = new(false);
    private Thread? _worker;
    private volatile bool _stopping;
    private readonly object _sdkLock = new();

    public Task ConnectAsync(CancellationToken ct = default)
    {
        // MLAPI_Initialize kann einige Sekunden blockieren, während der Dienst antwortet.
        return Task.Run(Connect, ct);
    }

    private void Connect()
    {
        if (IsConnected) return;

        _sdk = MysticLightSdkNative.TryLoad(DllPath, out var loadError);
        if (_sdk is null)
        {
            Log?.Invoke(LogLevel.Error, "Mystic Light SDK: " + loadError);
            return;
        }

        Log?.Invoke(LogLevel.Debug, $"Mystic Light SDK: DLL geladen ({_sdk.LoadedFrom}), rufe MLAPI_Initialize auf …");
        int rc;
        lock (_sdkLock) rc = _sdk.Initialize();
        if (rc != MysticLightSdkNative.MLAPI_OK)
        {
            var hints = new List<string>();
            if (!MsiBoardDetector.IsMysticLightServiceRunning())
                hints.Add("MSI-Center-Dienst LEDKeeper2 läuft nicht (MSI Center mit Mystic Light installieren/starten)");
            if (!MsiBoardDetector.IsElevated())
                hints.Add("AetherRGB läuft ohne Administratorrechte (das SDK verlangt sie)");
            hints.Add("in Mystic Light die Steuerung durch Drittanbieter-Software erlauben");
            Log?.Invoke(LogLevel.Error, $"Mystic Light SDK: MLAPI_Initialize fehlgeschlagen — {_sdk.Describe(rc)}. Prüfen: {string.Join("; ", hints)}.");
            return;
        }

        Log?.Invoke(LogLevel.Debug, "Mystic Light SDK: MLAPI_Initialize OK, frage Geräte ab (MLAPI_GetDeviceInfo) …");
        if (!TryGetDeviceInfo(out var types, out var ledCounts, out rc))
        {
            Log?.Invoke(LogLevel.Error, $"Mystic Light SDK: MLAPI_GetDeviceInfo fehlgeschlagen — {_sdk.Describe(rc)}. " +
                        (rc == MLAPI_TIMEOUT
                            ? $"Der MSI-Dienst hat in {DeviceInfoTimeout.TotalSeconds:0} s keine Geräteliste geliefert. " +
                              "Prüfen: MSI Center einmal öffnen und Mystic Light anzeigen lassen; Dienst LEDKeeper2 neu starten; " +
                              "in Mystic Light die Steuerung durch Drittanbieter-Software erlauben; andere RGB-Software (SignalRGB, OpenRGB) beenden."
                            : ""));
            lock (_sdkLock) _sdk.Release();
            return;
        }

        _devices.Clear();
        for (int d = 0; d < types.Length; d++)
        {
            var type = types[d];
            int count = d < ledCounts.Length && int.TryParse(ledCounts[d], out var c) ? c : 0;
            var names = new string[count];
            for (int i = 0; i < count; i++)
            {
                names[i] = $"LED-Bereich {i + 1}";
                lock (_sdkLock)
                {
                    int infoRc = _sdk.GetLedInfo(type, (uint)i, out var name, out var styles);
                    if (infoRc == MysticLightSdkNative.MLAPI_OK)
                    {
                        styles ??= Array.Empty<string>();
                        if (!string.IsNullOrWhiteSpace(name)) names[i] = name;
                        // Nur im Modus "Steady" übernimmt das SDK gesetzte Farben 1:1 statt sie zu animieren.
                        bool steady = styles.Contains("Steady", StringComparer.OrdinalIgnoreCase);
                        int styleRc = steady ? _sdk.SetLedStyle(type, (uint)i, "Steady") : MysticLightSdkNative.MLAPI_OK;
                        Log?.Invoke(steady && styleRc != MysticLightSdkNative.MLAPI_OK ? LogLevel.Warning : LogLevel.Debug,
                            $"Mystic Light SDK: {type} #{i} „{names[i]}“, Stile: {string.Join("/", styles)}" +
                            (!steady ? " — kein „Steady“-Stil, Farben werden evtl. animiert"
                                : styleRc != MysticLightSdkNative.MLAPI_OK ? $" — Stil „Steady“ setzen fehlgeschlagen: {_sdk.Describe(styleRc)}" : ""));
                    }
                    else
                    {
                        Log?.Invoke(LogLevel.Warning, $"Mystic Light SDK: MLAPI_GetLedInfo({type}, {i}) fehlgeschlagen — {_sdk.Describe(infoRc)}");
                    }
                }
            }
            _devices.Add(new SdkDevice(type, names));
        }

        IsConnected = true;
        Log?.Invoke(LogLevel.Info, $"Mystic Light SDK verbunden ({_sdk.LoadedFrom}): " +
                    string.Join(", ", _devices.Select(x => $"{x.Type} ({x.AreaNames.Length} Bereich(e))")));

        _stopping = false;
        _worker = new Thread(SendLoop) { IsBackground = true, Name = "AetherRGB MSI SDK" };
        _worker.Start();
    }

    private const int MLAPI_TIMEOUT = -2;
    private static readonly TimeSpan DeviceInfoTimeout = TimeSpan.FromSeconds(30);

    /// <summary>MLAPI_Initialize meldet schon OK, während LEDKeeper2 die Geräte noch einliest —
    /// bis dahin liefert MLAPI_GetDeviceInfo MLAPI_TIMEOUT (laut SDK-Doku "MLAPI_Initialize
    /// timeout"). Daher mehrfach versuchen; nach der Hälfte der Wartezeit einmal neu anmelden,
    /// falls die erste Anmeldung beim Dienst hängengeblieben ist.</summary>
    private bool TryGetDeviceInfo(out string[] types, out string[] ledCounts, out int rc)
    {
        var started = DateTime.UtcNow;
        bool reinitialized = false, logged = false;
        while (true)
        {
            lock (_sdkLock) rc = _sdk!.GetDeviceInfo(out types, out ledCounts);
            if (rc == MysticLightSdkNative.MLAPI_OK) return true;

            var elapsed = DateTime.UtcNow - started;
            if (rc != MLAPI_TIMEOUT || elapsed >= DeviceInfoTimeout || _stopping) return false;

            if (!logged)
            {
                Log?.Invoke(LogLevel.Info, "Mystic Light SDK: MSI-Dienst liest die Geräte noch ein, warte …");
                logged = true;
            }
            if (!reinitialized && elapsed >= DeviceInfoTimeout / 2)
            {
                reinitialized = true;
                lock (_sdkLock)
                {
                    _sdk.Release();
                    rc = _sdk.Initialize();
                }
                if (rc != MysticLightSdkNative.MLAPI_OK) return false;
            }
            Thread.Sleep(1000);
        }
    }

    public Task<IReadOnlyList<RgbDevice>> GetDevicesAsync(CancellationToken ct = default)
    {
        if (!IsConnected)
            return Task.FromResult<IReadOnlyList<RgbDevice>>(Array.Empty<RgbDevice>());

        var list = _devices.Select(d => new RgbDevice
        {
            Id = $"{ProviderId}:{d.Type}",
            Name = $"{FriendlyTypeName(d.Type)} (Mystic Light SDK)",
            Vendor = DeviceVendor.MsiMysticLight,
            Zones = d.AreaNames.Select((n, i) => new LedZone { Index = i, Name = n, LedCount = 1 }).ToList(),
            IsOnline = true
        }).ToList();
        return Task.FromResult<IReadOnlyList<RgbDevice>>(list);
    }

    public Task SetZoneColorAsync(string deviceId, int zoneIndex, RgbColor color, CancellationToken ct = default)
    {
        if (!IsConnected) return Task.CompletedTask;
        var type = deviceId[(ProviderId.Length + 1)..];
        _pending[(type, zoneIndex)] = color;
        _wake.Set();
        return Task.CompletedTask;
    }

    public Task SetZoneColorsAsync(string deviceId, int zoneIndex, IReadOnlyList<RgbColor> colors, CancellationToken ct = default)
    {
        if (colors.Count == 0) return Task.CompletedTask;
        // Ein SDK-Bereich hat nur eine Farbe — bei mehreren Pixeln den Mittelwert nehmen.
        if (colors.Count == 1) return SetZoneColorAsync(deviceId, zoneIndex, colors[0], ct);
        int r = 0, g = 0, b = 0;
        foreach (var c in colors) { r += c.R; g += c.G; b += c.B; }
        int n = colors.Count;
        return SetZoneColorAsync(deviceId, zoneIndex, new RgbColor((byte)(r / n), (byte)(g / n), (byte)(b / n)), ct);
    }

    private void SendLoop()
    {
        while (!_stopping)
        {
            _wake.WaitOne(500);
            foreach (var key in _pending.Keys)
            {
                if (_stopping) break;
                if (!_pending.TryRemove(key, out var color)) continue;
                if (_lastSent.TryGetValue(key, out var last) && last == color) continue;

                int rc;
                lock (_sdkLock)
                {
                    if (_sdk is null || !IsConnected) return;
                    rc = _sdk.SetLedColor(key.Type, (uint)key.Index, color.R, color.G, color.B);
                }
                if (rc == MysticLightSdkNative.MLAPI_OK) _lastSent[key] = color;
                else if (_failedAreas.Add(key))
                    Log?.Invoke(LogLevel.Warning, $"Mystic Light SDK: MLAPI_SetLedColor({key.Type}, {key.Index}) fehlgeschlagen — " +
                                                  $"{_sdk.Describe(rc)} (weitere Fehler dieses Bereichs werden nicht erneut gemeldet)");
            }
        }
    }

    private static string FriendlyTypeName(string type) => type.ToUpperInvariant() switch
    {
        "MSI_MB" => "MSI Mainboard",
        "MSI_VGA" => "MSI Grafikkarte",
        "MSI_COOLER" => "MSI Kühler",
        "MSI_MOUSE" => "MSI Maus",
        "MSI_KEYBOARD" => "MSI Tastatur",
        "MSI_HEADSET" => "MSI Headset",
        "MSI_MOUSEPAD" => "MSI Mauspad",
        "MSI_CASE" or "MSI_CHASSIS" => "MSI Gehäuse",
        "MSI_MEMORY" or "MSI_DRAM" => "MSI Arbeitsspeicher",
        _ => type
    };

    public ValueTask DisposeAsync()
    {
        _stopping = true;
        _wake.Set();
        _worker?.Join(2000);
        lock (_sdkLock)
        {
            if (IsConnected && _sdk is not null)
            {
                try { _sdk.Release(); } catch { /* Dienst evtl. schon weg */ }
            }
            IsConnected = false;
        }
        _wake.Dispose();
        return ValueTask.CompletedTask;
    }
}
