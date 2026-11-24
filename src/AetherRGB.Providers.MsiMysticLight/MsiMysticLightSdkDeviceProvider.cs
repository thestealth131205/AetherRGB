using System.Collections.Concurrent;
using System.Diagnostics;
using AetherRGB.Core.Devices;
using AetherRGB.Core.Diagnostics;
using AetherRGB.Core.Timing;

namespace AetherRGB.Providers.MsiMysticLight;

/// <summary>Steuert MSI-RGB über das offizielle Mystic Light SDK: AetherRGB meldet sich als
/// Drittanbieter-Client beim laufenden MSI-Center-Dienst (LEDKeeper2) an, statt selbst auf den
/// USB/SMBus zu schreiben — dadurch kein Konflikt mit MSI Center.
///
/// Voraussetzungen: MSI Center mit Mystic-Light-Modul installiert und laufend, Steuerung durch
/// Drittanbieter-Software in Mystic Light erlaubt, AetherRGB mit Administratorrechten gestartet,
/// MysticLight_SDK(_x64).dll vorhanden (siehe <see cref="MysticLightSdkNative"/>).
///
/// Das SDK adressiert LED-Bereiche (am Board die Anschlüsse, z. B. "PCH", "JRGB1", "JRAINBOW1"). Bereiche,
/// die "Direct Lighting Control" können und laut MLAPI_GetLedName mehrere LEDs haben (PCH = 6 Onboard-LEDs,
/// JRAINBOW = ARGB-Header für WS2812-Strips), werden als Zone mit einzelnen LEDs gemeldet und per
/// MLAPI_SetLedColors LED für LED gefärbt. Alle anderen (z. B. JRGB1 = 12-V-RGB, eine Farbe) bekommen
/// per MLAPI_SetLedColor im statischen Stil die Mischfarbe. Die SDK-Aufrufe gehen über IPC und sind
/// langsam; ein eigener Sende-Thread schickt pro Bereich nur die jeweils neuesten Farben und nur bei
/// Änderung, damit die Effekt-Engine und die übrigen Geräte nie auf MSI warten.
///
/// Jeder SetLedColor-Aufruf wird beim Dienst zu einem USB-Schreibvorgang am Board-Controller. Mit
/// Effekt-Bildrate (~30 Runden/s × alle Bereiche) stürzt der Controller ab und meldet sich am USB neu
/// an (Flackern, Windows-Ton für USB trennen/verbinden, MSI Center setzt danach seinen Stil zurück).
/// Daher sind die Senderunden auf <see cref="MaxUpdatesPerSecond"/> gedrosselt (Standard 10 — am B550 mit 3 Anschlüssen ohnehin die Grenze und dort stabil). Meldet der Dienst
/// mehrere Fehler oder langsame Aufrufe kurz hintereinander, setzt er den Stil zurück oder hängt ein Aufruf,
/// regelt der Sende-Thread das Tempo selbst herunter und tastet sich nach einer stabilen Phase wieder an den
/// eingestellten Wert heran. Einzelne Ausreißer (gelegentliches -101, kurzer Hänger bei Systemlast) bremsen nicht.
///
/// Ein Aufruf dauert am B550 sehr gleichmäßig ~31 ms. Der Sende-Thread läuft deshalb auf festen
/// Rundenterminen ab Rundenbeginn (eine Runde mit 3 Anschlüssen dauert allein schon ~93 ms) und holt sich
/// die Farben über <see cref="IFrameSourceProvider"/> erst unmittelbar vor jedem Aufruf — so sind die
/// Zeitschritte auf den LEDs gleich groß. Bei Strips, deren LED-Anordnung weniger LEDs belegt als der
/// Header kann, werden nach einmal Schwarzsetzen nur noch die belegten LEDs gesendet.</summary>
public sealed class MsiMysticLightSdkDeviceProvider : IDeviceProvider, IProviderLog, IFrameSourceProvider
{
    public string ProviderId => "msisdk";
    public bool IsConnected { get; private set; }

    /// <summary>Optionaler Pfad zur SDK-DLL (Datei oder Ordner), leer = Standardorte.</summary>
    public string? DllPath { get; init; }

    /// <summary>Höchstens so viele Senderunden (alle geänderten Bereiche) pro Sekunde.</summary>
    public int MaxUpdatesPerSecond { get; init; } = 10;

    /// <summary>Bereiche mit mehreren LEDs einzeln ansteuern (Direct Lighting Control).</summary>
    public bool PerLed { get; init; } = true;

    /// <summary>Bei Einzel-LED-Bereichen nur die laut LED-Anordnung belegten LEDs senden (die übrigen
    /// werden einmal schwarz gesetzt). Lehnt das Board eine Teilliste ab, wird der Bereich wieder komplett gesendet.</summary>
    public bool SendOnlyUsedLeds { get; init; } = true;

    /// <summary>JRAINBOW-Header treiben bis zu 100 LEDs; MLAPI_GetLedName liefert teils mehr Namen,
    /// als ein Header kann — mehr als das wird nie gemeldet/gesendet.</summary>
    public const int MaxLedsPerArea = 100;

    public const int MaxAllowedUpdatesPerSecond = 60;

    public event Action<LogLevel, string>? Log;

    /// <summary>Erster existierender DLL-Pfad an den Suchorten, sonst null — für Statusanzeigen.</summary>
    public static string? FindSdkDll(string? configuredPath) => MysticLightSdkNative.FindDll(configuredPath);

    /// <summary>Ein LED-Bereich des SDK. <see cref="PerLed"/> wird im Sende-Thread auf false gesetzt, wenn
    /// das Board MLAPI_SetLedColors ablehnt — dann bekommt der Bereich wieder eine Mischfarbe.</summary>
    private sealed class SdkArea
    {
        public required string Type { get; init; }
        public required int Index { get; init; }
        public required string Name { get; init; }
        public string[] Styles { get; set; } = Array.Empty<string>();
        public string? StaticStyle { get; set; }
        /// <summary>LED-Namen aus MLAPI_GetLedName ohne "n:"-Präfix bzw. wie geliefert (inkl. Präfix).</summary>
        public string[] LedNames { get; set; } = Array.Empty<string>();
        public string[] RawLedNames { get; set; } = Array.Empty<string>();
        public int? NamePrefix { get; set; }
        public volatile bool PerLed;
        /// <summary>Welche Adressierung beim ersten Senden funktioniert hat (siehe <see cref="ProbePerLed"/>).</summary>
        public uint SendIndex { get; set; }
        public string[]? SendNames { get; set; }
        public int ConsecutiveFailures { get; set; }
        public int ReportedLedCount => PerLed ? LedNames.Length : 1;

        /// <summary>Ab dieser LED sind laut letztem Senden alle LEDs schwarz (LedNames.Length = unbekannt).
        /// Nur dann dürfen Teillisten gesendet werden, ohne dass alte Farben hinten stehen bleiben.</summary>
        public int BlackFrom { get; set; } = int.MaxValue;
        public bool SubsetRejected { get; set; }
        /// <summary>Wie oft in Folge die Teilliste scheiterte, die komplette Liste direkt danach aber klappte.
        /// Erst ab <see cref="SubsetRejectAfter"/> gilt die Teilliste als abgelehnt — ein einzelnes -101 kommt
        /// auch bei kompletten Listen gelegentlich vor und ist kein Beweis.</summary>
        public int SubsetFailures { get; set; }
        /// <summary>Teilliste der <see cref="SendNames"/> für die zuletzt benutzte Anzahl belegter LEDs.</summary>
        public string[]? SubsetNames { get; set; }

        /// <summary>Gleitender Mittelwert der Aufrufdauer — die Farben werden für die Mitte des Aufrufs berechnet.</summary>
        public double AverageCallMs { get; set; }
        public int StatCalls, StatSkipped;
        public double StatTotalMs, StatMaxMs;
    }

    private sealed record SdkDevice(string Type, List<SdkArea> Areas);

    private const string DirectStyle = "Direct Lighting Control";

    private MysticLightSdkNative? _sdk;
    private readonly List<SdkDevice> _devices = new();
    private readonly Dictionary<(string Type, int Index), SdkArea> _areas = new();
    private readonly ConcurrentDictionary<(string Type, int Index), RgbColor[]> _pending = new();
    private readonly Dictionary<(string Type, int Index), RgbColor[]> _lastSent = new();
    private readonly HashSet<(string Type, int Index)> _failedAreas = new();
    private readonly ConcurrentDictionary<(string Type, int Index), ZoneFrameSource> _sources = new();
    private readonly AutoResetEvent _wake = new(false);
    private readonly ManualResetEvent _stopEvent = new(false);
    private Thread? _worker;
    private volatile bool _stopping;
    private readonly object _sdkLock = new();
    private int _connecting;

    public Task ConnectAsync(CancellationToken ct = default)
    {
        // MLAPI_Initialize/GetDeviceInfo können lange blockieren, während der Dienst antwortet.
        return Task.Run(() =>
        {
            // Nie zwei Anmeldungen gleichzeitig: ein zweiter Initialize/Release-Zyklus würde die
            // laufende Sitzung beim Dienst abmelden.
            if (Interlocked.Exchange(ref _connecting, 1) == 1)
            {
                Log?.Invoke(LogLevel.Debug, "Mystic Light SDK: Verbindungsversuch läuft bereits, überspringe.");
                return;
            }
            try { Connect(); }
            finally { Volatile.Write(ref _connecting, 0); }
        }, ct);
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
                              (MsiBoardDetector.IsElevated() ? "" : "Wahrscheinlichste Ursache: AetherRGB läuft ohne Administratorrechte — als Administrator starten. ") +
                              "Sonst prüfen: MSI Center einmal öffnen und Mystic Light anzeigen lassen; Dienst LEDKeeper2 neu starten; " +
                              "in Mystic Light die Steuerung durch Drittanbieter-Software erlauben; andere RGB-Software (SignalRGB, OpenRGB) beenden."
                            : ""));
            lock (_sdkLock) _sdk.Release();
            return;
        }

        _devices.Clear();
        _areas.Clear();
        for (int d = 0; d < types.Length; d++)
        {
            var type = types[d];
            int count = d < ledCounts.Length && int.TryParse(ledCounts[d], out var c) ? c : 0;
            var areas = new List<SdkArea>();
            for (int i = 0; i < count; i++)
            {
                int infoRc;
                string? name;
                string[]? styles;
                lock (_sdkLock) infoRc = _sdk.GetLedInfo(type, (uint)i, out name, out styles);
                if (infoRc != MysticLightSdkNative.MLAPI_OK)
                {
                    Log?.Invoke(LogLevel.Warning, $"Mystic Light SDK: MLAPI_GetLedInfo({type}, {i}) fehlgeschlagen — {_sdk.Describe(infoRc)}");
                    name = null;
                    styles = null;
                }
                var area = new SdkArea
                {
                    Type = type, Index = i,
                    Name = string.IsNullOrWhiteSpace(name) ? $"LED-Bereich {i + 1}" : name,
                    Styles = styles ?? Array.Empty<string>()
                };
                area.StaticStyle = PickStaticStyle(area.Styles);
                areas.Add(area);
            }

            // Namen der Einzel-LEDs ("0:PCH_1", "1:JRAINBOW2_1", …) den Bereichen zuordnen. Das Präfix
            // passt am B550 nicht zum Bereichsindex (JRAINBOW2 = Bereich 3, Präfix 1), daher über den Namen.
            string[] ledNames;
            lock (_sdkLock)
            {
                int nameRc = _sdk.GetLedName(type, out var names);
                ledNames = nameRc == MysticLightSdkNative.MLAPI_OK ? names ?? Array.Empty<string>() : Array.Empty<string>();
                Log?.Invoke(LogLevel.Debug, nameRc == MysticLightSdkNative.MLAPI_OK
                    ? $"Mystic Light SDK: {type} MLAPI_GetLedName: {ledNames.Length} Name(n)"
                    : $"Mystic Light SDK: {type} MLAPI_GetLedName fehlgeschlagen — {_sdk.Describe(nameRc)}");
            }
            AssignLedNames(areas, ledNames);

            foreach (var area in areas)
            {
                bool canDirect = PerLed && _sdk.SetLedColors is not null && area.LedNames.Length > 1
                                 && area.Styles.Any(x => string.Equals(x, DirectStyle, StringComparison.OrdinalIgnoreCase));
                area.PerLed = canDirect;
                var style = canDirect ? DirectStyle : area.StaticStyle;
                string current, result;
                lock (_sdkLock)
                {
                    _sdk.GetLedStyle(type, (uint)area.Index, out current);
                    // SetLedColor gilt nur in Stilen mit wählbarer Farbe — in "Rainbow" & Co. meldet das SDK
                    // MLAPI_NOT_SUPPORTED (-103); SetLedColors nur in "Direct Lighting Control".
                    if (style is null) result = " — kein statischer Stil bekannt, Farben werden evtl. abgelehnt";
                    else
                    {
                        int styleRc = _sdk.SetLedStyle(type, (uint)area.Index, style);
                        result = styleRc == MysticLightSdkNative.MLAPI_OK
                            ? $" — Stil „{style}“ gesetzt"
                            : $" — Stil „{style}“ setzen fehlgeschlagen: {_sdk.Describe(styleRc)}";
                        if (styleRc != MysticLightSdkNative.MLAPI_OK && canDirect)
                        {
                            area.PerLed = false;
                            result += FallBackToStatic(area);
                        }
                    }
                }
                var leds = area.LedNames.Length == 0 ? "keine LED-Namen"
                    : $"{area.LedNames.Length} LED-Namen ({area.LedNames[0]} … {area.LedNames[^1]})";
                Log?.Invoke(result.Contains("fehlgeschlagen") || style is null ? LogLevel.Warning : LogLevel.Debug,
                    $"Mystic Light SDK: {type} #{area.Index} „{area.Name}“, {leds}, " +
                    $"{(area.PerLed ? "einzelne LEDs" : "eine Farbe")}, bisheriger Stil: „{current}“, Stile: {string.Join("/", area.Styles)}{result}");
                _areas[(type, area.Index)] = area;
            }
            _devices.Add(new SdkDevice(type, areas));
        }

        IsConnected = true;
        Log?.Invoke(LogLevel.Info, $"Mystic Light SDK verbunden ({_sdk.LoadedFrom}): " +
                    string.Join(", ", _devices.Select(x => $"{x.Type} (" +
                        string.Join(", ", x.Areas.Select(a => $"{a.Name}: {a.ReportedLedCount} LED(s)")) + ")")));

        _stopping = false;
        _worker = new Thread(SendLoop) { IsBackground = true, Name = "AetherRGB MSI SDK", Priority = ThreadPriority.AboveNormal };
        _worker.Start();
    }

    private const int MLAPI_TIMEOUT = -2;
    private const int MLAPI_NOT_SUPPORTED = -103;

    /// <summary>Stile, in denen das SDK eine per SetLedColor gesetzte Farbe unverändert zeigt.
    /// Ältere Boards nennen ihn "Steady", neuere (z. B. B550) "NoAnimation".</summary>
    private static readonly string[] StaticStyles = { "Steady", "NoAnimation", "Static" };

    private static string? PickStaticStyle(string[] styles) =>
        StaticStyles.Select(s => styles.FirstOrDefault(x => string.Equals(x, s, StringComparison.OrdinalIgnoreCase)))
                    .FirstOrDefault(x => x is not null);
    private static readonly TimeSpan DeviceInfoTimeout = TimeSpan.FromSeconds(30);

    private static void AssignLedNames(List<SdkArea> areas, string[] rawNames)
    {
        foreach (var area in areas)
        {
            var stripped = new List<string>();
            var raw = new List<string>();
            int? prefix = null;
            foreach (var entry in rawNames)
            {
                if (string.IsNullOrWhiteSpace(entry)) continue;
                int colon = entry.IndexOf(':');
                int? p = colon > 0 && int.TryParse(entry[..colon], out var n) ? n : null;
                var name = p is null ? entry : entry[(colon + 1)..];
                // "JRAINBOW1" passt zu "JRAINBOW1_7", aber nicht zu "JRAINBOW10_7".
                if (!string.Equals(name, area.Name, StringComparison.OrdinalIgnoreCase)
                    && !name.StartsWith(area.Name + "_", StringComparison.OrdinalIgnoreCase)) continue;
                if (stripped.Count >= MaxLedsPerArea) break;
                stripped.Add(name);
                raw.Add(entry);
                prefix ??= p;
            }
            area.LedNames = stripped.ToArray();
            area.RawLedNames = raw.ToArray();
            area.NamePrefix = prefix;
        }
    }

    /// <summary>Bereich auf eine Farbe zurückstellen (statischer Stil). Muss unter <see cref="_sdkLock"/> laufen.</summary>
    private string FallBackToStatic(SdkArea area)
    {
        if (area.StaticStyle is null) return " — kein statischer Stil als Ersatz";
        int rc = _sdk!.SetLedStyle(area.Type, (uint)area.Index, area.StaticStyle);
        return rc == MysticLightSdkNative.MLAPI_OK
            ? $" — stattdessen „{area.StaticStyle}“ mit einer Farbe"
            : $" — auch „{area.StaticStyle}“ fehlgeschlagen: {_sdk.Describe(rc)}";
    }

    private int CallSetLedColors(SdkArea area, uint index, string[] names, RgbColor[] colors)
    {
        int n = names.Length;
        var r = new uint[n];
        var g = new uint[n];
        var b = new uint[n];
        for (int i = 0; i < n && i < colors.Length; i++)
        {
            r[i] = colors[i].R;
            g[i] = colors[i].G;
            b[i] = colors[i].B;
        }
        var namesCopy = (string[])names.Clone(); // ref-SAFEARRAY kann vom Marshaller ersetzt werden
        return _sdk!.SetLedColors!(area.Type, index, ref namesCopy, r, g, b);
    }

    /// <summary>Beim ersten Senden herausfinden, wie das Board die LEDs adressiert haben will: Die Doku
    /// sagt nur "LED name of specific area" und "area index" — ob mit dem "n:"-Präfix aus GetLedName und ob
    /// mit dem Bereichsindex oder dem Präfix, ist offen. Die erste Variante mit MLAPI_OK bleibt.
    /// Muss unter <see cref="_sdkLock"/> laufen.</summary>
    private int ProbePerLed(SdkArea area, RgbColor[] colors)
    {
        var variants = new List<(uint Index, string[] Names, string Label)>
        {
            ((uint)area.Index, area.LedNames, "Bereichsindex, Namen ohne Präfix"),
            ((uint)area.Index, area.RawLedNames, "Bereichsindex, Namen mit Präfix")
        };
        if (area.NamePrefix is int p && p != area.Index)
        {
            variants.Add(((uint)p, area.LedNames, $"Präfix {p} als Index, Namen ohne Präfix"));
            variants.Add(((uint)p, area.RawLedNames, $"Präfix {p} als Index, Namen mit Präfix"));
        }

        int rc = MLAPI_NOT_SUPPORTED;
        var tried = new List<string>();
        for (int v = 0; v < variants.Count; v++)
        {
            var (index, names, label) = variants[v];
            // Ohne Präfix in den Namen sind beide Namensvarianten gleich — nur einmal probieren.
            if (variants.Take(v).Any(x => x.Index == index && x.Names.SequenceEqual(names))) continue;
            rc = CallSetLedColors(area, index, names, colors);
            if (rc == MLAPI_NOT_SUPPORTED && _sdk!.SetLedStyle(area.Type, (uint)area.Index, DirectStyle) == MysticLightSdkNative.MLAPI_OK)
                rc = CallSetLedColors(area, index, names, colors);
            if (rc == MysticLightSdkNative.MLAPI_OK)
            {
                area.SendIndex = index;
                area.SendNames = names;
                Log?.Invoke(LogLevel.Info, $"Mystic Light SDK: {area.Type} „{area.Name}“: einzelne LEDs aktiv ({area.LedNames.Length} LEDs, {label}).");
                return rc;
            }
            tried.Add($"{label}: {_sdk!.Describe(rc)}");
        }

        area.PerLed = false;
        Log?.Invoke(LogLevel.Warning, $"Mystic Light SDK: {area.Type} „{area.Name}“: MLAPI_SetLedColors abgelehnt ({string.Join("; ", tried)})" +
                                      FallBackToStatic(area) + ".");
        return rc;
    }

    /// <summary>MLAPI_Initialize meldet schon OK, während LEDKeeper2 die Geräte noch einliest —
    /// bis dahin liefert MLAPI_GetDeviceInfo MLAPI_TIMEOUT (laut SDK-Doku "MLAPI_Initialize
    /// timeout"). Daher mehrfach versuchen; nach der Hälfte der Wartezeit einmal neu anmelden,
    /// falls die erste Anmeldung beim Dienst hängengeblieben ist.</summary>
    private bool TryGetDeviceInfo(out string[] types, out string[] ledCounts, out int rc)
    {
        var started = DateTime.UtcNow;
        bool reinitialized = false, logged = false;
        int attempt = 0;
        while (true)
        {
            var callStarted = DateTime.UtcNow;
            lock (_sdkLock) rc = _sdk!.GetDeviceInfo(out types, out ledCounts);
            attempt++;
            if (rc == MysticLightSdkNative.MLAPI_OK) return true;

            var elapsed = DateTime.UtcNow - started;
            // Die DLL blockiert selbst oft viele Sekunden, bevor sie Timeout meldet — Dauer mitloggen.
            Log?.Invoke(LogLevel.Debug, $"Mystic Light SDK: MLAPI_GetDeviceInfo Versuch {attempt}: {_sdk.Describe(rc)} " +
                                        $"nach {(DateTime.UtcNow - callStarted).TotalSeconds:0.0} s");
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
            Zones = d.Areas.Select(a => new LedZone { Index = a.Index, Name = a.Name, LedCount = a.ReportedLedCount }).ToList(),
            IsOnline = true
        }).ToList();
        return Task.FromResult<IReadOnlyList<RgbDevice>>(list);
    }

    public Task SetZoneColorAsync(string deviceId, int zoneIndex, RgbColor color, CancellationToken ct = default) =>
        SetZoneColorsAsync(deviceId, zoneIndex, new[] { color }, ct);

    public Task SetZoneColorsAsync(string deviceId, int zoneIndex, IReadOnlyList<RgbColor> colors, CancellationToken ct = default)
    {
        if (!IsConnected || colors.Count == 0) return Task.CompletedTask;
        var type = deviceId[(ProviderId.Length + 1)..];
        if (!_areas.TryGetValue((type, zoneIndex), out var area)) return Task.CompletedTask;

        RgbColor[] frame;
        if (area.PerLed)
        {
            // Eine einzelne Farbe (z. B. API "ganze Zone") auf alle LEDs verteilen.
            frame = new RgbColor[area.LedNames.Length];
            for (int i = 0; i < frame.Length; i++)
                frame[i] = colors.Count == 1 ? colors[0] : i < colors.Count ? colors[i] : RgbColor.Black;
        }
        else
        {
            // Ein Bereich mit nur einer Farbe — bei mehreren Pixeln den Mittelwert nehmen.
            frame = new[] { Average(colors) };
        }
        _pending[(type, zoneIndex)] = frame;
        _wake.Set();
        return Task.CompletedTask;
    }

    public void SetZoneFrameSource(string deviceId, int zoneIndex, ZoneFrameSource? source)
    {
        if (!deviceId.StartsWith(ProviderId + ":", StringComparison.OrdinalIgnoreCase)) return;
        var key = (deviceId[(ProviderId.Length + 1)..], zoneIndex);
        if (!_areas.ContainsKey(key)) return;
        if (source is null) _sources.TryRemove(key, out _);
        else
        {
            _sources[key] = source;
            _wake.Set();
        }
    }

    /// <summary>Farbänderungen bis zu dieser Größe je Kanal werden bei Bereichen mit nur einer Farbe nicht
    /// geschickt — sie sind am Board kaum sichtbar und kosten trotzdem einen USB-Schreibvorgang. Einzel-LED-
    /// Bereiche werden schon bei jeder Änderung gesendet, sonst bleiben LEDs bei langsamen Übergängen
    /// stehen und springen dann.</summary>
    private const int ColorTolerance = 3;

    /// <summary>Dauert ein Aufruf länger, hängt der Dienst vermutlich am neu verbundenen Controller.</summary>
    private static readonly TimeSpan SlowCall = TimeSpan.FromMilliseconds(500);
    /// <summary>So lange hängt ein Aufruf nur, wenn der Controller wirklich neu verbindet — dann Pause.</summary>
    private static readonly TimeSpan HungCall = TimeSpan.FromSeconds(2);
    private static readonly TimeSpan ErrorPause = TimeSpan.FromSeconds(2);
    /// <summary>Einzelne Ausreißer (ein -101, ein langsamer Aufruf bei kurzer Systemlast, z. B. Spielstart)
    /// bremsen nicht — erst so viele Probleme innerhalb von <see cref="ProblemWindow"/>.</summary>
    private const int ProblemsBeforeSlowdown = 3;
    private static readonly TimeSpan ProblemWindow = TimeSpan.FromSeconds(10);
    private const int SubsetRejectAfter = 3;
    private static readonly TimeSpan StatsInterval = TimeSpan.FromSeconds(30);

    /// <summary>Unter dieses Tempo regelt die automatische Drosselung nie herunter.</summary>
    private const double MinAdaptiveRate = 2;
    /// <summary>So lange ohne Probleme, bevor das Tempo wieder um einen Schritt steigt.</summary>
    private static readonly TimeSpan RateRecoveryAfter = TimeSpan.FromSeconds(15);

    private void SendLoop()
    {
        double maxRate = Math.Clamp(MaxUpdatesPerSecond, 1, MaxAllowedUpdatesPerSecond);
        double rate = maxRate;
        var clock = Stopwatch.StartNew();
        using var waiter = new PreciseWaiter();
        Log?.Invoke(LogLevel.Debug, $"Mystic Light SDK: Sende-Thread läuft, höchstens {rate:0.#} Runde(n)/s, " +
                                    $"Takt {(waiter.IsHighResolution ? "hochauflösend" : "timeBeginPeriod(1)")}, " +
                                    $"{(SendOnlyUsedLeds ? "nur belegte LEDs senden" : "immer alle LEDs senden")}.");
        var stableSince = clock.Elapsed;
        var nextRound = clock.Elapsed;
        var statsStarted = clock.Elapsed;
        TimeSpan? lastRoundStart = null;
        int rounds = 0, calls = 0, failures = 0, restyles = 0, slowCalls = 0;
        double totalMs = 0, maxMs = 0, sumGap = 0, sumGapSq = 0;
        int gaps = 0;
        var recentProblems = new Queue<TimeSpan>();

        while (!_stopping)
        {
            // Nichts zu tun (kein Effekt läuft auf MSI, keine API-Farben) → schlafen, bis sich das ändert.
            if (_sources.IsEmpty && _pending.IsEmpty)
            {
                _wake.WaitOne(500);
                nextRound = clock.Elapsed;
                lastRoundStart = null;
                continue;
            }

            // Feste Rundentermine ab Rundenbeginn: Eine Runde mit 3 Anschlüssen dauert selbst schon ~93 ms.
            // Früher wurde das Intervall erst ab dem Ende der Runde gezählt (66 + 93 ms → nur ~6 Runden/s).
            waiter.WaitUntil(clock, nextRound, _stopEvent);
            if (_stopping) break;

            var roundStart = clock.Elapsed;
            if (lastRoundStart is { } prev)
            {
                double gap = (roundStart - prev).TotalMilliseconds;
                sumGap += gap; sumGapSq += gap * gap; gaps++;
            }

            bool sentAny = false, pause = false;
            int problemsThisRound = 0;
            // Feste Reihenfolge (wie vom SDK gemeldet), damit jeder Anschluss gleichmäßig drankommt.
            var keys = _areas.Keys.Where(k => _sources.ContainsKey(k) || _pending.ContainsKey(k)).ToList();
            foreach (var key in keys)
            {
                if (_stopping) break;
                var area = _areas[key];
                bool perLed = area.PerLed;

                if (!TryBuildFrame(key, area, perLed, out var frame, out int used)) continue;
                if (_lastSent.TryGetValue(key, out var last) && (perLed ? Same(last, frame) : Close(last, frame))
                    && (!perLed || area.BlackFrom <= used))
                {
                    area.StatSkipped++;
                    continue;
                }

                int rc;
                bool restyled = false, subset = false, subsetRejectedNow = false;
                var callStarted = clock.Elapsed;
                lock (_sdkLock)
                {
                    if (_sdk is null || !IsConnected) return;
                    if (perLed)
                    {
                        if (area.SendNames is null)
                            rc = ProbePerLed(area, Pad(frame, area.LedNames.Length));
                        else
                        {
                            subset = SendOnlyUsedLeds && !area.SubsetRejected && used < area.LedNames.Length && area.BlackFrom <= used;
                            rc = SendPerLed(area, frame, used, subset);
                            // MSI Center oder ein neu verbundener Controller kann den Stil zurückgestellt haben.
                            if (rc == MLAPI_NOT_SUPPORTED && _sdk.SetLedStyle(key.Type, (uint)key.Index, DirectStyle) == MysticLightSdkNative.MLAPI_OK)
                            {
                                restyled = true;
                                rc = SendPerLed(area, frame, used, subset);
                            }
                            if (subset && rc != MysticLightSdkNative.MLAPI_OK)
                            {
                                // Teilliste gescheitert → diesmal komplett senden. Nur wenn das wiederholt klappt,
                                // während die Teilliste scheitert, nimmt das Board wirklich keine Teilliste an.
                                int subsetRc = rc;
                                subset = false;
                                subsetRejectedNow = true;
                                rc = SendPerLed(area, frame, used, subset);
                                if (rc == MysticLightSdkNative.MLAPI_OK && ++area.SubsetFailures >= SubsetRejectAfter)
                                {
                                    area.SubsetRejected = true;
                                    Log?.Invoke(LogLevel.Warning, $"Mystic Light SDK: {area.Type} „{area.Name}“: Teilliste mit {used} von {area.LedNames.Length} LEDs " +
                                                                  $"{SubsetRejectAfter}× in Folge abgelehnt ({_sdk.Describe(subsetRc)}) — sende ab jetzt immer alle LEDs.");
                                }
                            }
                            else if (subset) area.SubsetFailures = 0;
                        }
                    }
                    else
                    {
                        var color = frame[0];
                        rc = _sdk.SetLedColor(key.Type, (uint)key.Index, color.R, color.G, color.B);
                        if (rc == MLAPI_NOT_SUPPORTED && area.StaticStyle is not null
                            && _sdk.SetLedStyle(key.Type, (uint)key.Index, area.StaticStyle) == MysticLightSdkNative.MLAPI_OK)
                        {
                            restyled = true;
                            rc = _sdk.SetLedColor(key.Type, (uint)key.Index, color.R, color.G, color.B);
                        }
                    }

                    // Klappt Einzel-LED-Steuerung dauerhaft nicht mehr, lieber eine Farbe als gar keine.
                    if (perLed && area.PerLed && rc != MysticLightSdkNative.MLAPI_OK && ++area.ConsecutiveFailures >= 5)
                    {
                        area.PerLed = false;
                        Log?.Invoke(LogLevel.Warning, $"Mystic Light SDK: {area.Type} „{area.Name}“: MLAPI_SetLedColors 5× in Folge fehlgeschlagen " +
                                                      $"({_sdk.Describe(rc)}){FallBackToStatic(area)}.");
                    }
                    else if (rc == MysticLightSdkNative.MLAPI_OK) area.ConsecutiveFailures = 0;
                }
                double ms = (clock.Elapsed - callStarted).TotalMilliseconds;
                sentAny = true;
                calls++;
                totalMs += ms;
                maxMs = Math.Max(maxMs, ms);
                area.StatCalls++;
                area.StatTotalMs += ms;
                area.StatMaxMs = Math.Max(area.StatMaxMs, ms);
                area.AverageCallMs = area.AverageCallMs <= 0 ? ms : area.AverageCallMs * 0.9 + ms * 0.1;
                // Kam der Stil-Neusatz nur von der abgelehnten Teilliste, ist der Controller nicht instabil.
                // Neu gesetzter Stil = Controller war weg → Pause. Hängende Aufrufe ebenso.
                if (restyled && !subsetRejectedNow) { restyles++; problemsThisRound++; pause = true; }
                if (ms > SlowCall.TotalMilliseconds)
                {
                    slowCalls++; problemsThisRound++;
                    if (ms > HungCall.TotalMilliseconds) pause = true;
                }

                if (rc == MysticLightSdkNative.MLAPI_OK)
                {
                    _lastSent[key] = frame;
                    // Komplett gesendet: hinter den belegten LEDs ist alles schwarz. Teilliste: dahinter
                    // bleibt es schwarz, davor steht jetzt das neue Bild.
                    // Nach neu gesetztem Stil (Controller evtl. neu gestartet) ist unbekannt, was hinten auf
                    // dem Strip steht — nächste Runde dann komplett senden.
                    if (perLed) area.BlackFrom = restyled && subset ? int.MaxValue : used;
                }
                else
                {
                    // Ein schneller Fehler ist meist ein Ausreißer des Dienstes — nächste Runde einfach neu
                    // senden, ohne Pause (die 2 s Stillstand wären sichtbarer als der eine verlorene Frame).
                    failures++;
                    problemsThisRound++;
                    _lastSent.Remove(key);
                    area.BlackFrom = int.MaxValue;
                    if (_failedAreas.Add(key))
                        Log?.Invoke(LogLevel.Warning, $"Mystic Light SDK: {(perLed ? "MLAPI_SetLedColors" : "MLAPI_SetLedColor")}({key.Type}, {key.Index}) fehlgeschlagen — " +
                                                      $"{_sdk!.Describe(rc)} (weitere Fehler dieses Bereichs werden nicht erneut gemeldet)");
                }
            }
            if (sentAny) { rounds++; lastRoundStart = roundStart; }

            // Automatische Drosselung: gehäufte Probleme oder Controller-Neustart → Tempo senken;
            // lange genug stabil → wieder anheben. Einzelne Ausreißer bremsen nicht.
            var now = clock.Elapsed;
            for (int i = 0; i < problemsThisRound; i++) recentProblems.Enqueue(now);
            while (recentProblems.Count > 0 && now - recentProblems.Peek() > ProblemWindow) recentProblems.Dequeue();
            if (problemsThisRound > 0) stableSince = now;
            if (pause || recentProblems.Count >= ProblemsBeforeSlowdown)
            {
                recentProblems.Clear();
                if (rate > MinAdaptiveRate)
                {
                    rate = Math.Max(MinAdaptiveRate, Math.Floor(rate * 0.6));
                    Log?.Invoke(LogLevel.Warning, $"Mystic Light SDK: MSI-Dienst/Controller kommt nicht hinterher — Tempo automatisch auf {rate:0} Runde(n)/s gesenkt.");
                }
            }
            else if (sentAny && rate < maxRate && clock.Elapsed - stableSince >= RateRecoveryAfter)
            {
                stableSince = clock.Elapsed;
                rate = Math.Min(maxRate, rate + 2);
                Log?.Invoke(LogLevel.Info, $"Mystic Light SDK: {RateRecoveryAfter.TotalSeconds:0} s stabil — Tempo wieder auf {rate:0} Runde(n)/s.");
            }

            if (clock.Elapsed - statsStarted >= StatsInterval)
            {
                if (calls > 0)
                {
                    var secs = (clock.Elapsed - statsStarted).TotalSeconds;
                    var problems = failures + restyles + slowCalls > 0;
                    double meanGap = gaps > 0 ? sumGap / gaps : 0;
                    double sdGap = gaps > 0 ? Math.Sqrt(Math.Max(0, sumGapSq / gaps - meanGap * meanGap)) : 0;
                    var perArea = string.Join("; ", _areas.Values.Where(a => a.StatCalls + a.StatSkipped > 0).Select(a =>
                        $"{a.Name}: {a.StatCalls} Aufrufe, Ø {(a.StatCalls > 0 ? a.StatTotalMs / a.StatCalls : 0):0} ms, max. {a.StatMaxMs:0} ms" +
                        (a.PerLed && a.BlackFrom < a.LedNames.Length && !a.SubsetRejected && SendOnlyUsedLeds ? $", {a.BlackFrom}/{a.LedNames.Length} LEDs" : "") +
                        (a.StatSkipped > 0 ? $", {a.StatSkipped}× unverändert übersprungen" : "")));
                    Log?.Invoke(problems ? LogLevel.Warning : LogLevel.Debug,
                        $"Mystic Light SDK: letzte {secs:0} s: {rounds / secs:0.0} Runden/s (Abstand Ø {meanGap:0} ms, Streuung {sdGap:0} ms), {calls} Farb-Aufrufe, " +
                        $"Ø {totalMs / calls:0} ms, max. {maxMs:0} ms, aktuelles Limit {rate:0} Runden/s, {failures} Fehler, {restyles}× Stil neu gesetzt, {slowCalls} langsame Aufrufe" +
                        $" — pro Anschluss: {perArea}" +
                        (problems ? " — Stil zurückgesetzt oder langsame Aufrufe deuten auf einen neu verbundenen Controller hin; " +
                                    "ggf. „Max. Farb-Updates pro Sekunde“ in den Einstellungen senken." : ""));
                }
                statsStarted = clock.Elapsed;
                rounds = calls = failures = restyles = slowCalls = gaps = 0;
                totalMs = maxMs = sumGap = sumGapSq = 0;
                foreach (var a in _areas.Values) { a.StatCalls = a.StatSkipped = 0; a.StatTotalMs = a.StatMaxMs = 0; }
            }

            // Nächster Termin ab Beginn dieser Runde. Hat die Runde länger gedauert als das Intervall,
            // geht es ohne zusätzliche Pause sofort weiter.
            nextRound = roundStart + TimeSpan.FromSeconds(1.0 / rate);
            if (nextRound < clock.Elapsed) nextRound = clock.Elapsed;

            // Nach Fehlern oder hängenden Aufrufen dem Controller Zeit zum Neuverbinden lassen,
            // statt ihn sofort wieder mit Aufrufen zu fluten.
            if (pause && !_stopping)
            {
                _stopEvent.WaitOne(ErrorPause);
                nextRound = clock.Elapsed;
                lastRoundStart = null;
            }
        }
    }

    /// <summary>Farben für einen Bereich: von einer laufenden Effekt-Zone (zum Zeitpunkt der Mitte des
    /// voraussichtlichen Aufrufs) oder sonst die zuletzt per API gesetzten. <paramref name="used"/> = Anzahl
    /// belegter LEDs; bei Einzel-LED-Bereichen hat <paramref name="frame"/> genau so viele Einträge.</summary>
    private bool TryBuildFrame((string Type, int Index) key, SdkArea area, bool perLed, out RgbColor[] frame, out int used)
    {
        frame = Array.Empty<RgbColor>();
        used = 0;
        RgbColor[]? colors = null;
        if (_sources.TryGetValue(key, out var source))
        {
            _pending.TryRemove(key, out _); // Ein laufender Effekt hat Vorrang vor alten API-Farben.
            try { colors = source(TimeSpan.FromMilliseconds(area.AverageCallMs / 2)); }
            catch (Exception ex)
            {
                // Die UI kann Fenster/Formen gerade ändern — diese Runde für den Bereich auslassen.
                Log?.Invoke(LogLevel.Debug, $"Mystic Light SDK: Farben für {area.Name} nicht berechenbar: {ExceptionText.Describe(ex)}");
                return false;
            }
        }
        else if (!_pending.TryRemove(key, out colors)) return false;

        if (colors.Length == 0) return false;
        if (!perLed)
        {
            frame = new[] { colors.Length == 1 ? colors[0] : Average(colors) };
            used = 1;
            return true;
        }
        // Eine einzelne Farbe (z. B. API "ganze Zone") gilt für alle LEDs.
        if (colors.Length == 1 && area.LedNames.Length > 1) colors = Pad(colors, area.LedNames.Length, fill: colors[0]);
        used = Math.Min(colors.Length, area.LedNames.Length);
        frame = colors.Length == used ? colors : colors[..used];
        return true;
    }

    /// <summary>Sendet die belegten LEDs: als Teilliste (nur <paramref name="used"/> Namen) oder komplett mit
    /// schwarzem Rest. Muss unter <see cref="_sdkLock"/> laufen.</summary>
    private int SendPerLed(SdkArea area, RgbColor[] frame, int used, bool subset)
    {
        if (!subset) return CallSetLedColors(area, area.SendIndex, area.SendNames!, Pad(frame, area.LedNames.Length));
        if (area.SubsetNames is null || area.SubsetNames.Length != used)
            area.SubsetNames = area.SendNames![..used];
        return CallSetLedColors(area, area.SendIndex, area.SubsetNames, frame);
    }

    private static RgbColor[] Pad(RgbColor[] colors, int length, RgbColor fill = default)
    {
        if (colors.Length >= length) return colors;
        var padded = new RgbColor[length];
        Array.Copy(colors, padded, colors.Length);
        for (int i = colors.Length; i < length; i++) padded[i] = fill;
        return padded;
    }

    private static bool Close(RgbColor a, RgbColor b) =>
        Math.Abs(a.R - b.R) <= ColorTolerance && Math.Abs(a.G - b.G) <= ColorTolerance && Math.Abs(a.B - b.B) <= ColorTolerance;

    private static bool Close(RgbColor[] a, RgbColor[] b)
    {
        if (a.Length != b.Length) return false;
        for (int i = 0; i < a.Length; i++)
            if (!Close(a[i], b[i])) return false;
        return true;
    }

    private static bool Same(RgbColor[] a, RgbColor[] b) => a.AsSpan().SequenceEqual(b);

    private static RgbColor Average(IReadOnlyList<RgbColor> colors)
    {
        int r = 0, g = 0, b = 0;
        foreach (var c in colors) { r += c.R; g += c.G; b += c.B; }
        int n = Math.Max(1, colors.Count);
        return new RgbColor((byte)(r / n), (byte)(g / n), (byte)(b / n));
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
        _stopEvent.Set();
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
        _stopEvent.Dispose();
        return ValueTask.CompletedTask;
    }
}
