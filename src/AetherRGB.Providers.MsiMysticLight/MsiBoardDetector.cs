using System.Diagnostics;
using System.Security.Principal;
using Microsoft.Win32;

namespace AetherRGB.Providers.MsiMysticLight;

public sealed record MsiBoardInfo(string Manufacturer, string Product);

/// <summary>Erkennt ein MSI-Mainboard und den Zustand der Mystic-Light-Umgebung — rein lesend,
/// ohne Hardwarezugriff (nur Registry/Prozessliste), damit die Abfrage beim Start gefahrlos ist.</summary>
public static class MsiBoardDetector
{
    /// <summary>Liest Hersteller/Modell des Mainboards aus den SMBIOS-Daten, die Windows unter
    /// HKLM\HARDWARE\DESCRIPTION\System\BIOS ablegt. Null, wenn es kein MSI-Board ist.</summary>
    public static MsiBoardInfo? Detect()
    {
        if (!OperatingSystem.IsWindows()) return null;
        try
        {
            using var key = Registry.LocalMachine.OpenSubKey(@"HARDWARE\DESCRIPTION\System\BIOS");
            var manufacturer = (key?.GetValue("BaseBoardManufacturer") as string ?? "").Trim();
            var product = (key?.GetValue("BaseBoardProduct") as string ?? "").Trim();

            bool isMsi = manufacturer.Contains("Micro-Star", StringComparison.OrdinalIgnoreCase) ||
                         manufacturer.StartsWith("MSI", StringComparison.OrdinalIgnoreCase);
            return isMsi ? new MsiBoardInfo(manufacturer, product) : null;
        }
        catch
        {
            return null;
        }
    }

    /// <summary>LEDKeeper2 ist der Hintergrunddienst des Mystic-Light-Moduls in MSI Center —
    /// ohne ihn kann das SDK nichts steuern.</summary>
    public static bool IsMysticLightServiceRunning()
    {
        try
        {
            var processes = Process.GetProcessesByName("LEDKeeper2");
            bool running = processes.Length > 0;
            foreach (var p in processes) p.Dispose();
            return running;
        }
        catch
        {
            return false;
        }
    }

    /// <summary>Das Mystic Light SDK verweigert MLAPI_Initialize ohne Administratorrechte.</summary>
    public static bool IsElevated()
    {
        if (!OperatingSystem.IsWindows()) return false;
        try
        {
            using var identity = WindowsIdentity.GetCurrent();
            return new WindowsPrincipal(identity).IsInRole(WindowsBuiltInRole.Administrator);
        }
        catch
        {
            return false;
        }
    }
}
