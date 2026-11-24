using System.Runtime.InteropServices;
using Microsoft.Win32.SafeHandles;

namespace AetherRGB.App.Sensors;

public enum DiskKind { Unknown, Nvme, Ssd, Hdd, Usb }

/// <summary>Art und Temperatur eines Datenträgers über IOCTL_STORAGE_QUERY_PROPERTY auf <c>\\.\PhysicalDriveN</c>. Das Gerät
/// wird ohne Lese-/Schreibrecht geöffnet (geht auch ohne Administratorrechte) und es werden nur Eigenschaften abgefragt.
/// Temperaturen nur bei NVMe: bei Festplatten könnte die Abfrage einen schlafenden Datenträger aufwecken.</summary>
internal static class StorageInfo
{
    private const uint IOCTL_STORAGE_QUERY_PROPERTY = 0x002D1400;
    private const int StorageDeviceProperty = 0, StorageDeviceSeekPenaltyProperty = 7, StorageDeviceTemperatureProperty = 52;
    private const int BusUsb = 7, BusSata = 11, BusNvme = 17, BusSd = 12, BusMmc = 13;

    public sealed record DiskDescription(int Number, string Model, DiskKind Kind);

    public static DiskDescription Describe(int number)
    {
        using var handle = Open(number);
        if (handle is null) return new DiskDescription(number, $"Datenträger {number}", DiskKind.Unknown);

        string model = $"Datenträger {number}";
        int bus = -1;
        var dev = Query(handle, StorageDeviceProperty, 1024);
        if (dev is not null && dev.Length >= 32)
        {
            int vendorOff = BitConverter.ToInt32(dev, 12), productOff = BitConverter.ToInt32(dev, 16);
            bus = BitConverter.ToInt32(dev, 28);
            string vendor = AsciiAt(dev, vendorOff), product = AsciiAt(dev, productOff);
            var name = $"{vendor} {product}".Trim();
            if (name.Length > 0) model = name;
        }

        DiskKind kind;
        if (bus == BusNvme) kind = DiskKind.Nvme;
        else if (bus is BusUsb or BusSd or BusMmc) kind = DiskKind.Usb;
        else
        {
            var seek = Query(handle, StorageDeviceSeekPenaltyProperty, 16);
            kind = seek is { Length: >= 9 } ? (seek[8] != 0 ? DiskKind.Hdd : DiskKind.Ssd) : (bus == BusSata ? DiskKind.Unknown : DiskKind.Unknown);
        }
        return new DiskDescription(number, model, kind);
    }

    /// <summary>Temperatur in °C, null wenn das Laufwerk sie nicht meldet.</summary>
    public static double? Temperature(int number)
    {
        using var handle = Open(number);
        if (handle is null) return null;
        var data = Query(handle, StorageDeviceTemperatureProperty, 256);
        if (data is null || data.Length < 28) return null;
        int infoCount = BitConverter.ToUInt16(data, 12);
        if (infoCount < 1) return null;
        short t = BitConverter.ToInt16(data, 26);
        return t is > -50 and < 150 ? t : null;
    }

    public static string KindName(DiskKind kind) => kind switch
    {
        DiskKind.Nvme => "NVMe-SSD",
        DiskKind.Ssd => "SATA-SSD",
        DiskKind.Hdd => "Festplatte (HDD)",
        DiskKind.Usb => "USB-Laufwerk",
        _ => "Laufwerk",
    };

    private static SafeFileHandle? Open(int number)
    {
        var h = CreateFileW($@"\\.\PhysicalDrive{number}", 0, 3 /* read|write share */, IntPtr.Zero, 3 /* OPEN_EXISTING */, 0, IntPtr.Zero);
        if (h.IsInvalid) { h.Dispose(); return null; }
        return h;
    }

    private static byte[]? Query(SafeFileHandle handle, int propertyId, int outSize)
    {
        var query = new byte[12];
        BitConverter.GetBytes(propertyId).CopyTo(query, 0); // PropertyId, QueryType = PropertyStandardQuery (0)
        var output = new byte[outSize];
        return DeviceIoControl(handle, IOCTL_STORAGE_QUERY_PROPERTY, query, query.Length, output, output.Length, out int returned, IntPtr.Zero) && returned > 0
            ? output[..returned] : null;
    }

    private static string AsciiAt(byte[] buffer, int offset)
    {
        if (offset <= 0 || offset >= buffer.Length) return "";
        int end = offset;
        while (end < buffer.Length && buffer[end] != 0) end++;
        return System.Text.Encoding.ASCII.GetString(buffer, offset, end - offset).Trim();
    }

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern SafeFileHandle CreateFileW(string name, uint access, uint share, IntPtr security, uint creation, uint flags, IntPtr template);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool DeviceIoControl(SafeFileHandle device, uint code, byte[] inBuffer, int inSize, byte[] outBuffer, int outSize, out int returned, IntPtr overlapped);
}
