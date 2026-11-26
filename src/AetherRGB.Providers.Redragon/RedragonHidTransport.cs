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

    /// <summary>Listet alle unter der Vendor-ID sichtbaren HID-Geräte roh auf — zum Abgleich, welches
    /// Interface tatsächlich das RGB-/Makro-Interface ist (Diagnose auf echter Hardware).</summary>
    public static IReadOnlyList<string> DumpDiscoveredDevices(int vendorId = RedragonVendorId)
    {
        return DeviceList.Local.GetHidDevices(vendorID: vendorId)
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

    public void Dispose()
    {
        _stream?.Dispose();
        _stream = null;
    }
}
