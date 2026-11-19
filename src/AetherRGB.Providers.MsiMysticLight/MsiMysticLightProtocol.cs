namespace AetherRGB.Providers.MsiMysticLight;

/// <summary>Baut die rohen HID-Feature-Reports für MSI Mystic Light auf.
///
/// STATUS: EXPERIMENTELL / NICHT HERSTELLER-VERIFIZIERT. Es gibt keine offizielle, öffentliche
/// MSI-Spezifikation für dieses Protokoll. Die hier verwendete Report-Struktur (64-Byte-Report,
/// Report-ID als erstes Byte, Befehlsbyte an Index 1, RGB-Payload ab einem festen Offset pro Zone)
/// folgt dem Muster, das für HID-basierte ARGB-Controller auf AMD-400/500er- und Intel-Boards
/// allgemein üblich ist — die exakten Byte-Offsets für DEIN konkretes Board (MSI MPG B550 Gaming
/// Plus) sind nicht verifiziert und müssen am echten Gerät geprüft werden.
///
/// Empfohlener erster Test (siehe README/OS-Hinweise): zuerst <see cref="BuildSingleZoneTestReport"/>
/// mit einer auffälligen Testfarbe (z. B. reines Magenta) auf EINER Zone senden und beobachten,
/// ob überhaupt irgendeine LED reagiert — nicht sofort alle Zonen gleichzeitig ansteuern.</summary>
public static class MsiMysticLightProtocol
{
    public const int ReportLength = 64;

    // Befehlsbytes gemäß dem allgemein für MSI-USB-HID-RGB-Controller beschriebenen Schema.
    // UNVERIFIZIERT für dieses konkrete Board — Diagnose-Modus (DiagnosticsEnabled) loggt den
    // rohen Report vor dem Senden, damit sich bei Bedarf per USB-Capture (z. B. Wireshark+USBPcap
    // gegen die offizielle MSI-Center-App) der tatsächliche Befehlssatz gegenprüfen lässt.
    private const byte ReportId = 0x00;
    private const byte CmdSetColor = 0x50;
    private const byte CmdGetFirmwareInfo = 0x52;

    /// <summary>Baut einen Report, der die gesamte Zone (alle LEDs identisch) auf eine Farbe setzt —
    /// für einfache statische Farben/Sync-Effekte ohne Pro-LED-Ansteuerung.</summary>
    public static byte[] BuildSetZoneColorReport(int zoneIndex, byte r, byte g, byte b)
    {
        var report = new byte[ReportLength];
        report[0] = ReportId;
        report[1] = CmdSetColor;
        report[2] = (byte)Math.Clamp(zoneIndex, 0, 255);
        report[3] = r;
        report[4] = g;
        report[5] = b;
        return report;
    }

    /// <summary>Baut einen Report für individuelle Pro-LED-Farben innerhalb einer Zone (Regenbogen-
    /// Welle, Lauflicht usw.). Ein einzelner 64-Byte-Report reicht nur für wenige LEDs (hier
    /// konservativ auf die ersten <c>(ReportLength-4)/3</c> LEDs begrenzt) — für lange ARGB-Strips
    /// wären mehrere aufeinanderfolgende Reports mit einem Offset-Feld nötig, was hier bewusst noch
    /// nicht gebaut ist, bis das Grundformat an echter Hardware verifiziert ist.</summary>
    public static byte[] BuildSetZonePixelsReport(int zoneIndex, IReadOnlyList<(byte R, byte G, byte B)> pixels)
    {
        var report = new byte[ReportLength];
        report[0] = ReportId;
        report[1] = CmdSetColor;
        report[2] = (byte)Math.Clamp(zoneIndex, 0, 255);
        report[3] = (byte)Math.Clamp(pixels.Count, 0, 255);

        int offset = 4;
        int maxPixels = (ReportLength - offset) / 3;
        for (int i = 0; i < Math.Min(pixels.Count, maxPixels); i++)
        {
            report[offset + i * 3] = pixels[i].R;
            report[offset + i * 3 + 1] = pixels[i].G;
            report[offset + i * 3 + 2] = pixels[i].B;
        }
        return report;
    }

    public static byte[] BuildGetFirmwareInfoReport()
    {
        var report = new byte[ReportLength];
        report[0] = ReportId;
        report[1] = CmdGetFirmwareInfo;
        return report;
    }

    /// <summary>Auffällige, nicht mit "aus" verwechselbare Testfarbe für den ersten Verifikationsschritt
    /// an echter Hardware (reines Magenta kommt in keiner normalen Boot-LED-Konfiguration zufällig vor).</summary>
    public static byte[] BuildSingleZoneTestReport(int zoneIndex) =>
        BuildSetZoneColorReport(zoneIndex, 255, 0, 255);
}
