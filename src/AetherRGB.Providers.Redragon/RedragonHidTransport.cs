using System.ComponentModel;
using System.Runtime.InteropServices;
using HidSharp;

namespace AetherRGB.Providers.Redragon;

/// <summary>Rohe HID-Transportschicht für Redragon-Mäuse (z. B. M914) — findet den USB-HID-Controller
/// und schreibt Feature-Reports. Kennt selbst keine Farblogik, nur Bytes.
///
/// WICHTIG — Status: Redragon veröffentlicht sein USB-HID-RGB-Protokoll nicht offiziell. Diese Klasse
/// filtert bewusst konservativ auf die Redragon/Dragonrise-Vendor-ID 0x258A (ggf. per Einstellung
/// überschreibbar), damit nicht versehentlich ein fremdes Gerät angesprochen wird. Eine Maus hat meist
/// mehrere HID-Interfaces (Standard-Maus-Nutzung plus ein herstellerspezifisches Interface für RGB/
/// Makros) — <see cref="DumpDiscoveredDevices"/> vor dem ersten echten Einsatz nutzen, um zu sehen,
/// welches Interface tatsächlich existiert, und <see cref="MsiMysticLight.MsiHidTransport"/>-artig den
/// sicheren Einzel-Test fahren, bevor ein Effekt läuft.</summary>
public sealed class RedragonHidTransport : IDisposable
{
    public const int RedragonVendorId = 0x258A;

    private HidStream? _stream;
    public HidDevice? Device { get; private set; }

    /// <summary>Vom geöffneten Gerät gemeldete Feature-Report-Länge (inkl. Report-ID-Byte). Windows
    /// verlangt bei HidD_SetFeature exakt diese Länge, unabhängig von der verwendeten Report-ID
    /// innerhalb desselben Top-Level-Collection — ein kürzerer Puffer schlägt sonst ohne brauchbare
    /// Fehlermeldung fehl. 0, solange kein Gerät offen ist.</summary>
    public int FeatureReportLength { get; private set; }

    /// <summary>Entsprechend für WriteFile/Output-Reports. 0, solange kein Gerät offen ist.</summary>
    public int OutputReportLength { get; private set; }

    /// <summary>0 = Standard-Vendor-ID verwenden.</summary>
    public int VendorIdOverride { get; set; }

    /// <summary>0 = jedes Gerät unter der Vendor-ID durchprobieren statt einer festen Produkt-ID.</summary>
    public int ProductIdOverride { get; set; }

    public bool TryOpen(Action<string>? log = null)
    {
        int vendorId = VendorIdOverride > 0 ? VendorIdOverride : RedragonVendorId;
        var candidates = DeviceList.Local.GetHidDevices(vendorID: vendorId).ToList();
        if (ProductIdOverride > 0)
            candidates = candidates.Where(d => d.ProductID == ProductIdOverride).ToList();

        if (candidates.Count == 0)
        {
            log?.Invoke($"Kein USB-HID-Gerät mit Vendor-ID 0x{vendorId:X4} gefunden" +
                        (ProductIdOverride > 0 ? $" (Produkt-ID-Filter 0x{ProductIdOverride:X4})." : "."));
            return false;
        }

        // Eine Maus mit mehreren HID-Interfaces meldet jedes als eigenes HidDevice. Das Interface mit
        // dem größten Feature-Report gewinnt zuerst den Versuch — RGB-/Makro-Interfaces brauchen meist
        // einen größeren Report als das reine Bewegungs-/Klick-Interface (8 Byte).
        var ordered = candidates.OrderByDescending(d => d.GetMaxFeatureReportLength());

        foreach (var device in ordered)
        {
            try
            {
                if (!device.TryOpen(out var stream)) continue;
                Device = device;
                _stream = stream;
                FeatureReportLength = device.GetMaxFeatureReportLength();
                OutputReportLength = device.GetMaxOutputReportLength();
                log?.Invoke($"Verbunden: VID=0x{device.VendorID:X4} PID=0x{device.ProductID:X4} " +
                            $"Produkt='{device.GetProductName()}' MaxFeatureReportLength={FeatureReportLength} " +
                            $"MaxOutputReportLength={OutputReportLength}");
                return true;
            }
            catch (Exception ex)
            {
                log?.Invoke($"Öffnen von VID=0x{device.VendorID:X4} PID=0x{device.ProductID:X4} fehlgeschlagen: {ex.Message}");
            }
        }

        return false;
    }

    /// <summary>Listet alle unter der Vendor-ID sichtbaren HID-Geräte roh auf — zum Abgleich, welches
    /// Interface tatsächlich das RGB-/Makro-Interface ist (Diagnose auf echter Hardware).</summary>
    public static IReadOnlyList<string> DumpDiscoveredDevices(int vendorId = RedragonVendorId)
    {
        return DeviceList.Local.GetHidDevices(vendorID: vendorId)
            .Select(d => $"VID=0x{d.VendorID:X4} PID=0x{d.ProductID:X4} Product='{d.GetProductName()}' " +
                         $"Path={d.DevicePath} MaxFeature={d.GetMaxFeatureReportLength()} MaxOutput={d.GetMaxOutputReportLength()}")
            .ToList();
    }

    public bool WriteFeatureReport(byte[] report, Action<string>? log = null)
    {
        if (_stream is null) return false;
        try
        {
            _stream.SetFeature(report);
            return true;
        }
        catch (Exception ex)
        {
            int win32Error = ExtractWin32Error(ex);
            string decoded = DecodeWin32Error(win32Error);
            log?.Invoke($"SetFeature(Report-ID 0x{(report.Length > 0 ? report[0] : 0):X2}, {report.Length} Byte, Gerät erwartet {FeatureReportLength}) " +
                        $"fehlgeschlagen: {ex.Message} [Win32-Fehler {win32Error}: {decoded}]");
            return false;
        }
    }

    public bool WriteOutputReport(byte[] report, Action<string>? log = null)
    {
        if (_stream is null) return false;
        try
        {
            _stream.Write(report);
            return true;
        }
        catch (Exception ex)
        {
            int win32Error = ExtractWin32Error(ex);
            string decoded = DecodeWin32Error(win32Error);
            log?.Invoke($"Write(Report-ID 0x{(report.Length > 0 ? report[0] : 0):X2}, {report.Length} Byte, Gerät erwartet {OutputReportLength}) " +
                        $"fehlgeschlagen: {ex.Message} [Win32-Fehler {win32Error}: {decoded}]");
            return false;
        }
    }

    /// <summary>Ermittelt den echten Win32-Fehlercode zu einer fehlgeschlagenen HidSharp-Operation.
    ///
    /// <see cref="Marshal.GetLastWin32Error"/> ist thread-lokal und wird vom nächsten P/Invoke-Aufruf
    /// überschrieben. HidSharp liest den Code intern direkt nach dem Systemaufruf korrekt aus, aber bis
    /// die Exception bei uns im catch-Block ankommt, sind bereits etliche weitere P/Invokes gelaufen
    /// (Exception-Konstruktion, Stacktrace-Aufbau, GC) — <c>GetLastWin32Error()</c> liefert an dieser
    /// Stelle praktisch immer 0, auch wenn der Treiber einen echten Fehler gemeldet hat. Deshalb zuerst
    /// in <see cref="Exception.HResult"/> nachsehen: .NET bettet einen Win32-Fehlercode dort nach dem
    /// Standardschema <c>HRESULT_FROM_WIN32</c> ein (High-Word <c>0x8007</c>, Low-Word = Fehlercode),
    /// und dieser Wert wird schon bei der Exception-Erzeugung festgeschrieben, bevor weitere P/Invokes
    /// ihn überschreiben können.</summary>
    private static int ExtractWin32Error(Exception ex)
    {
        const int Facility_Win32 = unchecked((int)0x80070000);
        if ((ex.HResult & unchecked((int)0xFFFF0000)) == Facility_Win32)
            return ex.HResult & 0xFFFF;

        int fallback = Marshal.GetLastWin32Error();
        return fallback;
    }

    /// <summary>Übersetzt einen Win32-Fehlercode in einen lesbaren Text. 0 heißt, dass auch über
    /// <see cref="Exception.HResult"/> kein Win32-Fehlercode zu finden war — die Ablehnung kam dann
    /// wirklich aus einer reinen .NET/HidSharp-Prüfung vor dem Systemaufruf (z. B. Pufferlänge), nicht
    /// vom Gerät oder Treiber.</summary>
    private static string DecodeWin32Error(int code)
    {
        if (code == 0) return "kein Win32-Fehlercode gefunden (weder aktuell noch im HResult der Exception — Ablehnung kam vermutlich aus einer reinen Prüfung vor dem Systemaufruf, nicht vom Gerät/Treiber)";
        try { return new Win32Exception(code).Message; }
        catch { return "unbekannt"; }
    }

    public void Dispose()
    {
        _stream?.Dispose();
        _stream = null;
    }
}
