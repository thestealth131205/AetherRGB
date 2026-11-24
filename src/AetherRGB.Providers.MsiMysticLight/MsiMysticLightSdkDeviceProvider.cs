using System.Collections.Concurrent;
using AetherRGB.Core.Devices;

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
public sealed class MsiMysticLightSdkDeviceProvider : IDeviceProvider
{
    public string ProviderId => "msisdk";
    public bool IsConnected { get; private set; }

    /// <summary>Optionaler Pfad zur SDK-DLL (Datei oder Ordner), leer = Standardorte.</summary>
    public string? DllPath { get; init; }

    public event Action<string>? Log;

    /// <summary>Erster existierender DLL-Pfad an den Suchorten, sonst null — für Statusanzeigen.</summary>
    public static string? FindSdkDll(string? configuredPath) => MysticLightSdkNative.FindDll(configuredPath);

    private sealed record SdkDevice(string Type, string[] AreaNames);

    private MysticLightSdkNative? _sdk;
    private readonly List<SdkDevice> _devices = new();
    private readonly ConcurrentDictionary<(string Type, int Index), RgbColor> _pending = new();
    private readonly Dictionary<(string Type, int Index), RgbColor> _lastSent = new();
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
            Log?.Invoke("Mystic Light SDK: " + loadError);
            return;
        }

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
            Log?.Invoke($"Mystic Light SDK: MLAPI_Initialize fehlgeschlagen — {_sdk.Describe(rc)}. Prüfen: {string.Join("; ", hints)}.");
            return;
        }

        string[] types, ledCounts;
        lock (_sdkLock) rc = _sdk.GetDeviceInfo(out types, out ledCounts);
        if (rc != MysticLightSdkNative.MLAPI_OK)
        {
            Log?.Invoke("Mystic Light SDK: MLAPI_GetDeviceInfo fehlgeschlagen — " + _sdk.Describe(rc));
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
                    if (_sdk.GetLedInfo(type, (uint)i, out var name, out var styles) == MysticLightSdkNative.MLAPI_OK)
                    {
                        if (!string.IsNullOrWhiteSpace(name)) names[i] = name;
                        // Nur im Modus "Steady" übernimmt das SDK gesetzte Farben 1:1 statt sie zu animieren.
                        if (styles.Contains("Steady", StringComparer.OrdinalIgnoreCase))
                            _sdk.SetLedStyle(type, (uint)i, "Steady");
                    }
                }
            }
            _devices.Add(new SdkDevice(type, names));
        }

        IsConnected = true;
        Log?.Invoke($"Mystic Light SDK verbunden ({_sdk.LoadedFrom}): " +
                    string.Join(", ", _devices.Select(x => $"{x.Type} ({x.AreaNames.Length} Bereich(e))")));

        _stopping = false;
        _worker = new Thread(SendLoop) { IsBackground = true, Name = "AetherRGB MSI SDK" };
        _worker.Start();
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
