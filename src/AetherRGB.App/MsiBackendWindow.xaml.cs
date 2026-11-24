using System.Windows;
using AetherRGB.Core.Config;
using AetherRGB.Providers.MsiMysticLight;

namespace AetherRGB.App;

/// <summary>Fragt bei erkanntem MSI-Mainboard, ob das offizielle Mystic Light SDK oder die eigene
/// USB/HID-Variante genutzt werden soll. Fasst selbst keine Hardware an — zeigt nur den Zustand
/// (Dienst, DLL, Adminrechte) an, damit die Wahl informiert getroffen wird.</summary>
public partial class MsiBackendWindow : Window
{
    /// <summary>Gewählter Weg nach "Übernehmen", sonst null ("Später entscheiden").</summary>
    public MsiBackend? Result { get; private set; }
    public string DllPath => DllPathBox.Text.Trim();

    public MsiBackendWindow(MsiBoardInfo? board, MsiBackend current, string dllPath)
    {
        InitializeComponent();

        BoardText.Text = board is null
            ? "Board-Modell konnte nicht ausgelesen werden."
            : $"{board.Manufacturer} — {board.Product}";
        DllPathBox.Text = dllPath;

        // Vorauswahl: bisherige Wahl, sonst das SDK, wenn MSI Center schon läuft.
        var preselect = current != MsiBackend.Ask
            ? current
            : (MsiBoardDetector.IsMysticLightServiceRunning() ? MsiBackend.MysticLightSdk : MsiBackend.DirectHid);
        (preselect switch
        {
            MsiBackend.MysticLightSdk => ChoiceSdk,
            MsiBackend.DirectHid => ChoiceHid,
            _ => ChoiceOff
        }).IsChecked = true;

        UpdateSdkStatus();
    }

    /// <summary>Zustand der SDK-Voraussetzungen als mehrzeiliger Text (auch für die Einstellungen-Seite).</summary>
    public static string DescribeSdkStatus(string dllPath)
    {
        var dll = MsiMysticLightSdkDeviceProvider.FindSdkDll(dllPath);
        return string.Join("\n",
            (MsiBoardDetector.IsMysticLightServiceRunning() ? "✓" : "✗") + " MSI Center / Mystic-Light-Dienst (LEDKeeper2) läuft",
            (dll is not null ? "✓ SDK-DLL gefunden: " + dll : "✗ SDK-DLL nicht gefunden (MysticLight_SDK_x64.dll von MSI laden und hier angeben)"),
            (MsiBoardDetector.IsElevated() ? "✓" : "✗") + " AetherRGB läuft mit Administratorrechten",
            "• In Mystic Light muss die Steuerung durch Drittanbieter-Software erlaubt sein");
    }

    private void UpdateSdkStatus()
    {
        if (SdkStatusText is null) return;
        SdkStatusText.Text = DescribeSdkStatus(DllPathBox.Text.Trim());
    }

    private void OnChoiceChanged(object sender, RoutedEventArgs e)
    {
        if (SdkPanel is not null) SdkPanel.IsEnabled = ChoiceSdk.IsChecked == true;
    }

    private void OnDllPathChanged(object sender, System.Windows.Controls.TextChangedEventArgs e) => UpdateSdkStatus();

    private void OnBrowseClick(object sender, RoutedEventArgs e)
    {
        if (BrowseForDll(this) is { } path) DllPathBox.Text = path;
    }

    public static string? BrowseForDll(Window owner)
    {
        var dialog = new Microsoft.Win32.OpenFileDialog
        {
            Title = "MysticLight_SDK DLL auswählen",
            Filter = "Mystic Light SDK|MysticLight_SDK*.dll|DLL-Dateien|*.dll"
        };
        return dialog.ShowDialog(owner) == true ? dialog.FileName : null;
    }

    private void OnApplyClick(object sender, RoutedEventArgs e)
    {
        Result = ChoiceSdk.IsChecked == true ? MsiBackend.MysticLightSdk
            : ChoiceHid.IsChecked == true ? MsiBackend.DirectHid
            : MsiBackend.Off;
        DialogResult = true;
    }

    private void OnLaterClick(object sender, RoutedEventArgs e) => DialogResult = false;
}
