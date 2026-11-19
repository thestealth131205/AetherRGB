using HidSharp;

namespace AetherRGB.Providers.MsiMysticLight;

/// <summary>Rohe HID-Transportschicht für MSI-Mainboards (Mystic Light) — findet den internen
/// USB-HID-Controller und schreibt/liest Feature-Reports. Kennt selbst keine Farblogik, nur Bytes.
///
/// WICHTIG — Status dieser Implementierung: MSIs USB-HID-Protokoll für Mystic Light ist nicht
/// öffentlich offiziell dokumentiert. Diese Klasse filtert bewusst konservativ (nur USB-VID 0x1462
/// UND ein Produktname, der auf RGB/Mystic-Light-Funktion hindeutet), damit nicht versehentlich
/// irgendein fremdes MSI-USB-Gerät (z. B. eine Monitor-OSD-Schnittstelle) angesprochen wird.
/// Vor jedem echten Soforteinsatz sollte <see cref="DumpDiscoveredDevices"/> genutzt werden, um
/// zu verifizieren, dass genau der erwartete Controller gefunden wird.</summary>
public sealed class MsiHidTransport : IDisposable
{
    public const int MsiVendorId = 0x1462;

    private HidStream? _stream;
    public HidDevice? Device { get; private set; }

    public bool TryOpen(Action<string>? log = null)
    {
        var candidates = DeviceList.Local.GetHidDevices(vendorID: MsiVendorId).ToList();
        if (candidates.Count == 0)
        {
            log?.Invoke("Kein USB-HID-Gerät mit MSI-Vendor-ID (0x1462) gefunden.");
            return false;
        }

        // Konservative Vorauswahl: Produktname muss auf die RGB-Steuerung hindeuten. MSI benennt
        // den internen Mystic-Light-HID-Controller je nach Board-Generation unterschiedlich
        // (u. a. "MSI Mystic Light", "USB controller", teils ohne aussagekräftigen String) —
        // schlägt die Namensfilterung fehl, wird als Fallback das erste gefundene Gerät mit
        // passender Report-Länge (siehe unten) versucht.
        var byName = candidates.FirstOrDefault(d =>
            (d.GetProductName() ?? "").Contains("mystic", StringComparison.OrdinalIgnoreCase) ||
            (d.GetProductName() ?? "").Contains("rgb", StringComparison.OrdinalIgnoreCase));

        var ordered = byName is not null
            ? new[] { byName }.Concat(candidates.Where(d => d != byName))
            : candidates;

        foreach (var device in ordered)
        {
            try
            {
                if (!device.TryOpen(out var stream)) continue;
                Device = device;
                _stream = stream;
                log?.Invoke($"Verbunden: VID=0x{device.VendorID:X4} PID=0x{device.ProductID:X4} " +
                            $"Produkt='{device.GetProductName()}' MaxFeatureReportLength={device.GetMaxFeatureReportLength()}");
                return true;
            }
            catch (Exception ex)
            {
                log?.Invoke($"Öffnen von VID=0x{device.VendorID:X4} PID=0x{device.ProductID:X4} fehlgeschlagen: {ex.Message}");
            }
        }

        return false;
    }

    /// <summary>Listet alle unter der MSI-Vendor-ID sichtbaren HID-Geräte roh auf — zum Abgleich,
    /// welches davon tatsächlich der Mystic-Light-Controller ist (Diagnose auf echter Hardware).</summary>
    public static IReadOnlyList<string> DumpDiscoveredDevices()
    {
        return DeviceList.Local.GetHidDevices(vendorID: MsiVendorId)
            .Select(d => $"VID=0x{d.VendorID:X4} PID=0x{d.ProductID:X4} Product='{d.GetProductName()}' " +
                         $"Path={d.DevicePath} MaxFeature={d.GetMaxFeatureReportLength()} MaxOutput={d.GetMaxOutputReportLength()}")
            .ToList();
    }

    public bool WriteFeatureReport(byte[] report)
    {
        if (_stream is null) return false;
        try
        {
            _stream.SetFeature(report);
            return true;
        }
        catch
        {
            return false;
        }
    }

    public bool WriteOutputReport(byte[] report)
    {
        if (_stream is null) return false;
        try
        {
            _stream.Write(report);
            return true;
        }
        catch
        {
            return false;
        }
    }

    public byte[]? ReadFeatureReport(int length)
    {
        if (_stream is null) return null;
        try
        {
            var buffer = new byte[length];
            _stream.GetFeature(buffer);
            return buffer;
        }
        catch
        {
            return null;
        }
    }

    public void Dispose()
    {
        _stream?.Dispose();
        _stream = null;
    }
}
