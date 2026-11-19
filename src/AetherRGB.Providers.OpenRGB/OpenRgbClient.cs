using System.Net.Sockets;

namespace AetherRGB.Providers.OpenRGB;

internal sealed class OpenRgbZone
{
    public required string Name { get; init; }
    public required uint LedCount { get; init; }
}

internal sealed class OpenRgbController
{
    public required int Index { get; init; }
    public required string Name { get; init; }
    public required List<OpenRgbZone> Zones { get; init; }
    public required int TotalLedCount { get; init; }
}

/// <summary>Minimaler TCP-Client für das OpenRGB-SDK-Protokoll. Siehe <see cref="OpenRgbProtocol"/>
/// für den Hinweis zum fehlenden Live-Test gegen echte Hardware.</summary>
internal sealed class OpenRgbClient : IAsyncDisposable
{
    private readonly string _host;
    private readonly int _port;
    private TcpClient? _tcp;
    private NetworkStream? _stream;

    public OpenRgbClient(string host, int port)
    {
        _host = host;
        _port = port;
    }

    public bool IsConnected => _tcp?.Connected ?? false;

    public async Task ConnectAsync(CancellationToken ct)
    {
        _tcp = new TcpClient();
        await _tcp.ConnectAsync(_host, _port, ct);
        _stream = _tcp.GetStream();
        await SendClientNameAsync("AetherRGB", ct);
    }

    private async Task SendClientNameAsync(string name, CancellationToken ct)
    {
        var payload = System.Text.Encoding.ASCII.GetBytes(name + "\0");
        await SendAsync(0, OpenRgbProtocol.PacketType.SetClientName, payload, ct);
    }

    private async Task SendAsync(uint deviceId, OpenRgbProtocol.PacketType type, byte[] payload, CancellationToken ct)
    {
        if (_stream is null) throw new InvalidOperationException("OpenRGB-Client ist nicht verbunden.");
        var packet = OpenRgbProtocol.BuildPacket(deviceId, type, payload);
        await _stream.WriteAsync(packet, ct);
    }

    private async Task<(uint DeviceId, OpenRgbProtocol.PacketType Type, byte[] Payload)> ReceiveAsync(CancellationToken ct)
    {
        if (_stream is null) throw new InvalidOperationException("OpenRGB-Client ist nicht verbunden.");
        var header = await ReadExactAsync(16, ct);
        uint deviceId = BitConverter.ToUInt32(header, 4);
        var type = (OpenRgbProtocol.PacketType)BitConverter.ToUInt32(header, 8);
        uint length = BitConverter.ToUInt32(header, 12);
        var payload = length > 0 ? await ReadExactAsync((int)length, ct) : Array.Empty<byte>();
        return (deviceId, type, payload);
    }

    private async Task<byte[]> ReadExactAsync(int count, CancellationToken ct)
    {
        var buffer = new byte[count];
        int read = 0;
        while (read < count)
        {
            int n = await _stream!.ReadAsync(buffer.AsMemory(read, count - read), ct);
            if (n == 0) throw new IOException("OpenRGB-Server hat die Verbindung geschlossen.");
            read += n;
        }
        return buffer;
    }

    public async Task<int> GetControllerCountAsync(CancellationToken ct)
    {
        await SendAsync(0, OpenRgbProtocol.PacketType.RequestControllerCount, Array.Empty<byte>(), ct);
        var (_, _, payload) = await ReceiveAsync(ct);
        return (int)BitConverter.ToUInt32(payload, 0);
    }

    public async Task<OpenRgbController> GetControllerDataAsync(int index, CancellationToken ct)
    {
        await SendAsync((uint)index, OpenRgbProtocol.PacketType.RequestControllerData, Array.Empty<byte>(), ct);
        var (_, _, payload) = await ReceiveAsync(ct);
        return ParseControllerData(index, payload);
    }

    private static OpenRgbController ParseControllerData(int index, byte[] data)
    {
        int pos = 4; // data_size (uint32) überspringen
        pos += 4;    // device_type (uint32) überspringen
        string name = ReadString(data, ref pos);
        _ = ReadString(data, ref pos); // vendor
        _ = ReadString(data, ref pos); // description
        _ = ReadString(data, ref pos); // version
        _ = ReadString(data, ref pos); // serial
        _ = ReadString(data, ref pos); // location

        ushort numModes = BitConverter.ToUInt16(data, pos); pos += 2;
        pos += 4; // active_mode (int32)
        for (int m = 0; m < numModes; m++)
            pos = SkipMode(data, pos);

        ushort numZones = BitConverter.ToUInt16(data, pos); pos += 2;
        var zones = new List<OpenRgbZone>();
        for (int z = 0; z < numZones; z++)
        {
            string zoneName = ReadString(data, ref pos);
            pos += 4; // zone_type
            pos += 4; // leds_min
            pos += 4; // leds_max
            uint ledsCount = BitConverter.ToUInt32(data, pos); pos += 4;
            ushort matrixLen = BitConverter.ToUInt16(data, pos); pos += 2;
            pos += matrixLen;
            zones.Add(new OpenRgbZone { Name = zoneName, LedCount = ledsCount });
        }

        // Restliche Felder (LED-Namen, Farbwerte) werden für die reine Zonen-Farbsteuerung nicht
        // benötigt und deshalb hier nicht weiter geparst.
        int totalLeds = zones.Sum(z => (int)z.LedCount);
        return new OpenRgbController { Index = index, Name = name, Zones = zones, TotalLedCount = totalLeds };
    }

    private static int SkipMode(byte[] data, int pos)
    {
        _ = ReadString(data, ref pos); // mode name
        pos += 4;  // value
        pos += 4;  // flags
        pos += 4;  // speed_min
        pos += 4;  // speed_max
        pos += 4;  // colors_min
        pos += 4;  // colors_max
        pos += 4;  // speed
        pos += 4;  // direction
        pos += 4;  // color_mode
        ushort numColors = BitConverter.ToUInt16(data, pos); pos += 2;
        pos += numColors * 4;
        return pos;
    }

    private static string ReadString(byte[] data, ref int pos)
    {
        ushort len = BitConverter.ToUInt16(data, pos);
        pos += 2;
        var str = System.Text.Encoding.ASCII.GetString(data, pos, Math.Max(0, len - 1)); // ohne Null-Terminator
        pos += len;
        return str;
    }

    /// <summary>Setzt alle LEDs einer Zone (RGBCONTROLLER_UPDATEZONELEDS). Farbformat pro LED: 4 Byte,
    /// Reihenfolge R,G,B,0 gemäß OpenRGB-"ORGBColor"-Packing.</summary>
    public async Task UpdateZoneLedsAsync(int controllerIndex, int zoneIndex, IReadOnlyList<(byte R, byte G, byte B)> colors, CancellationToken ct)
    {
        using var ms = new MemoryStream();
        using var w = new BinaryWriter(ms);
        // data_size wird am Ende vorangestellt (OpenRGB erwartet die Gesamtgröße inkl. sich selbst zuerst)
        using var inner = new MemoryStream();
        using var iw = new BinaryWriter(inner);
        iw.Write((uint)zoneIndex);
        iw.Write((ushort)colors.Count);
        foreach (var c in colors)
        {
            iw.Write(c.R);
            iw.Write(c.G);
            iw.Write(c.B);
            iw.Write((byte)0);
        }
        var innerBytes = inner.ToArray();
        w.Write((uint)(innerBytes.Length + 4));
        w.Write(innerBytes);

        await SendAsync((uint)controllerIndex, OpenRgbProtocol.PacketType.RgbControllerUpdateZoneLeds, ms.ToArray(), ct);
    }

    public async ValueTask DisposeAsync()
    {
        _stream?.Dispose();
        _tcp?.Dispose();
        await Task.CompletedTask;
    }
}
