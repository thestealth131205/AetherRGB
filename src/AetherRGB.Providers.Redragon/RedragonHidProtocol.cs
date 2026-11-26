namespace AetherRGB.Providers.Redragon;

/// <summary>Baut die rohen HID-Feature-Reports für Redragon-RGB-Mäuse (Ziel: M914) auf.
///
/// STATUS: EXPERIMENTELL / NICHT HERSTELLER-VERIFIZIERT. Redragon veröffentlicht keine offizielle
/// Protokoll-Spezifikation. Das hier verwendete Report-Layout (Report-ID 0x08, Befehlsbyte an Index 1,
/// Zonen-/Moduswahl an Index 2, RGB-Payload ab einem festen Offset) folgt dem Muster, das für mehrere
/// ältere Redragon-Modelle öffentlich reverse-engineert wurde (siehe z. B. das Community-Projekt
/// dokutan/mouse_m908 auf GitHub, das genau dieses Grundschema für die M908 beschreibt). Ob die M914
/// exakt dieselben Byte-Offsets verwendet, ist NICHT geprüft — unterschiedliche Redragon-Modelle nutzen
/// teils abweichende Report-Längen oder Befehlsbytes, auch wenn sie vom selben Chip-Anbieter stammen.
///
/// Empfohlener erster Test: zuerst <see cref="BuildSingleColorTestReport"/> mit einer auffälligen
/// Testfarbe (Magenta) senden und beobachten, ob überhaupt irgendeine LED reagiert — nicht sofort
/// einen vollen Effekt schicken. Reagiert nichts, mit <see cref="RedragonHidTransport.DumpDiscoveredDevices"/>
/// prüfen, ob das richtige HID-Interface getroffen wurde, und bei Bedarf per USB-Capture (Wireshark +
/// USBPcap) gegen die offizielle Redragon-Software den tatsächlichen Befehlssatz abgleichen.</summary>
public static class RedragonHidProtocol
{
    /// <summary>Fallback-Länge, falls die tatsächliche Report-Länge des Geräts nicht bekannt ist
    /// (z. B. in Unit-Tests ohne echtes HID-Gerät). Auf echter Hardware MUSS die an
    /// <see cref="RedragonHidTransport.FeatureReportLength"/>/<see cref="RedragonHidTransport.OutputReportLength"/>
    /// gemeldete Länge verwendet werden — Windows verlangt bei HidD_SetFeature/WriteFile einen Puffer,
    /// der exakt der vom Gerät gemeldeten Report-Länge entspricht, sonst schlägt der Aufruf ganz ohne
    /// aussagekräftigen Fehlercode fehl (genau das war die Ursache des ersten gescheiterten Tests: ein
    /// fest auf 65 Byte verdrahteter Puffer gegen ein Gerät, das 1032 Byte erwartet).</summary>
    public const int ReportLength = 65;

    private const byte DefaultReportId = 0x08;
    private const byte CmdSetStaticColor = 0x07;

    /// <summary>Report-IDs, die nacheinander durchprobiert werden, wenn die erste (0x08) von
    /// HidD_SetFeature ohne Geräte-Rückmeldung abgelehnt wird (Windows prüft die Report-ID gegen den
    /// HID-Report-Deskriptor, bevor der Aufruf das Gerät überhaupt erreicht — eine falsche ID scheitert
    /// deshalb sofort und ohne nützlichen Fehlercode). 0x00 steht für "keine nummerierten Reports"
    /// (viele einfachere HID-Collections nutzen das), die übrigen sind Rateversuche basierend auf dem
    /// Byte-Bereich, den andere Redragon-Modelle laut Community-Reverse-Engineering verwenden.</summary>
    public static readonly byte[] CandidateReportIds = { 0x08, 0x00, 0x04, 0x07, 0x0A };

    /// <summary>Setzt die gesamte Sammelzone (Logo + Scrollrad-Ring als eine Zone, siehe
    /// <see cref="AetherRGB.Core.Config.RedragonConfig.LedCountOverride"/>) auf eine Farbe — für
    /// einfache statische Farben/Sync-Effekte ohne Pro-LED-Ansteuerung.</summary>
    /// <param name="reportLength">Vom Gerät gemeldete Report-Länge (Feature oder Output). Der Rest des
    /// Puffers bleibt Null-gepolstert, Windows verlangt trotzdem die exakte Gesamtlänge.</param>
    /// <param name="reportId">Byte an Index 0 des Reports. Bei Report-ID 0 (unnummerierte Reports)
    /// erwartet HidSharp trotzdem ein führendes 0x00-Byte im Puffer, das zählt mit zur Gesamtlänge.</param>
    public static byte[] BuildSetColorReport(byte r, byte g, byte b, int reportLength = ReportLength, byte reportId = DefaultReportId)
    {
        var report = new byte[Math.Max(reportLength, 6)];
        report[0] = reportId;
        report[1] = CmdSetStaticColor;
        report[2] = 0x01; // Modus: statische Farbe (unverifiziert, analog zu vergleichbaren Redragon-Modellen)
        report[3] = r;
        report[4] = g;
        report[5] = b;
        return report;
    }

    /// <summary>Auffällige, nicht mit "aus" verwechselbare Testfarbe für den ersten Verifikationsschritt
    /// an echter Hardware (reines Magenta kommt in keiner normalen Boot-Beleuchtung zufällig vor).</summary>
    public static byte[] BuildSingleColorTestReport(int reportLength = ReportLength, byte reportId = DefaultReportId) =>
        BuildSetColorReport(255, 0, 255, reportLength, reportId);
}
