namespace AetherRGB.Providers.OpenRGB;

/// <summary>
/// Rohes TCP-Protokoll des OpenRGB-SDK-Servers (Standardport 6742). OpenRGB selbst spricht die
/// motherboard-eigenen SMBus/I2C-Register von ASUS Aura, ASRock Polychrome, Gigabyte RGB Fusion
/// und MSI Mystic Light bereits — AetherRGB implementiert diese Low-Level-Protokolle bewusst
/// NICHT selbst (siehe CLAUDE.md: kein Risiko eines Hardware-Bricks ohne echte Testboards),
/// sondern nutzt OpenRGB als bereits etablierte, geprüfte Bridge.
///
/// Paketaufbau (alle Zahlen Little-Endian):
///   4 Byte Magic "ORGB"
///   4 Byte device_id
///   4 Byte packet_type
///   4 Byte payload_length
///   payload_length Byte Payload
///
/// Diese Implementierung folgt der öffentlich dokumentierten OpenRGB-SDK-Protokollspezifikation
/// (wie sie u. a. von OpenRGB.NET/openrgb-python verwendet wird). Sie konnte in dieser Umgebung
/// NICHT gegen einen echten, laufenden OpenRGB-Server verifiziert werden (kein Windows-Rechner
/// mit Hardware verfügbar) — vor dem produktiven Einsatz unbedingt gegen eine echte
/// OpenRGB-Installation testen.
/// </summary>
internal static class OpenRgbProtocol
{
    public static readonly byte[] Magic = { (byte)'O', (byte)'R', (byte)'G', (byte)'B' };

    public enum PacketType : uint
    {
        RequestControllerCount = 0,
        RequestControllerData = 1,
        RequestProtocolVersion = 40,
        SetClientName = 50,
        DeviceListUpdated = 100,
        RgbControllerResizeZone = 1000,
        RgbControllerUpdateLeds = 1050,
        RgbControllerUpdateZoneLeds = 1051,
        RgbControllerUpdateSingleLed = 1052,
        RgbControllerSetCustomMode = 1100,
        RgbControllerUpdateMode = 1101,
    }

    public static byte[] BuildPacket(uint deviceId, PacketType type, byte[] payload)
    {
        var packet = new byte[16 + payload.Length];
        Buffer.BlockCopy(Magic, 0, packet, 0, 4);
        BitConverter.GetBytes(deviceId).CopyTo(packet, 4);
        BitConverter.GetBytes((uint)type).CopyTo(packet, 8);
        BitConverter.GetBytes((uint)payload.Length).CopyTo(packet, 12);
        Buffer.BlockCopy(payload, 0, packet, 16, payload.Length);
        return packet;
    }
}
