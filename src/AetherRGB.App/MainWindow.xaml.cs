using System.Diagnostics;
using System.Reflection;
using System.Windows;
using AetherRGB.Api;
using AetherRGB.Core.Config;
using AetherRGB.Core.Devices;
using AetherRGB.Core.Effects;
using AetherRGB.Core.Layout;
using AetherRGB.Core.Update;
using AetherRGB.Providers.HomeAssistant;
using AetherRGB.Providers.MsiMysticLight;
using AetherRGB.Providers.OpenRGB;
using AetherRGB.Providers.Wled;

namespace AetherRGB.App;

public partial class MainWindow : Window
{
    private readonly AppConfig _config;
    private readonly DeviceRegistry _registry = new();
    private readonly CanvasEffectEngine _engine;
    private CanvasLayout _layout = CanvasLayout.LoadOrCreateDefault();
    private LocalApiHost? _api;
    private MsiMysticLightDeviceProvider? _msiProvider;
    private readonly RepairService _repair = new();
    private readonly UpdateChecker _updateChecker = new();

    public MainWindow()
    {
        InitializeComponent();

        _config = AppConfig.LoadOrCreateDefault();
        _engine = new CanvasEffectEngine(_registry);
        _engine.SetLayout(_layout);
        _registry.ProviderError += (providerId, ex) => Log($"[{providerId}] {ex.Message}");

        EffectCombo.ItemsSource = new[] { "Statisch Rot", "Statisch Blau", "Regenbogen-Welle (Canvas)", "Farbverlauf (Canvas)", "Radialer Puls (Canvas)" };
        EffectCombo.SelectedIndex = 2;

        BuildProviders();
        Loaded += async (_, _) => await ConnectAndRefreshAsync();
    }

    private void BuildProviders()
    {
        if (_config.OpenRgb.Enabled)
            _registry.Register(new OpenRgbDeviceProvider(_config.OpenRgb.Host, _config.OpenRgb.Port));

        if (_config.WledInstances.Count > 0)
            _registry.Register(new WledDeviceProvider(_config.WledInstances.Select(w => (w.Name, w.Host))));

        if (_config.HomeAssistant.Enabled && !string.IsNullOrWhiteSpace(_config.HomeAssistant.LongLivedToken))
            _registry.Register(new HomeAssistantDeviceProvider(_config.HomeAssistant.BaseUrl, _config.HomeAssistant.LongLivedToken));

        if (_config.MsiMysticLight.Enabled)
        {
            _msiProvider = new MsiMysticLightDeviceProvider { LedCountOverride = _config.MsiMysticLight.LedCountOverride };
            _msiProvider.Log += msg => Dispatcher.Invoke(() => Log("[MSI] " + msg));
            _registry.Register(_msiProvider);
        }

        if (_config.LocalApi.Enabled)
        {
            _api = new LocalApiHost(_registry, _config.LocalApi.Port);
            _ = _api.StartAsync().ContinueWith(t =>
            {
                if (t.Exception is not null)
                    Dispatcher.Invoke(() => Log($"Lokale API konnte Port {_config.LocalApi.Port} nicht binden: {t.Exception.InnerException?.Message}"));
                else
                    Dispatcher.Invoke(() => Log($"Lokale API läuft auf http://127.0.0.1:{_config.LocalApi.Port}/api — keine Verbindung nach außen."));
            });
        }
    }

    private async Task ConnectAndRefreshAsync()
    {
        StatusText.Text = "Suche Geräte …";
        await _registry.ConnectAllAsync();
        var devices = await _registry.GetAllDevicesAsync();

        DeviceList.ItemsSource = devices.Select(d => new DeviceRow
        {
            Id = d.Id,
            Name = d.Name,
            Vendor = d.Vendor.ToString(),
            ZoneSummary = string.Join(", ", d.Zones.Select(z => $"{z.Name}: {z.LedCount}")),
            StatusText = d.IsOnline ? "Verbunden" : "Offline",
            Zones = d.Zones
        }).ToList();

        StatusText.Text = $"{devices.Count} Gerät(e) gefunden — {_registry.Providers.Count(p => p.IsConnected)}/{_registry.Providers.Count} Provider online";

        // Neu gefundene Zonen automatisch als unplatzierte Rechtecke ergänzen, entfernte Geräte
        // aus dem Layout streichen — wer schon platziert hat, verliert dadurch nichts.
        _layout.SyncWithDevices(devices);
        _layout.Save();
        _engine.SetLayout(_layout);
    }

    private async void OnConnectClick(object sender, RoutedEventArgs e) => await ConnectAndRefreshAsync();

    private async void OnMsiTestClick(object sender, RoutedEventArgs e)
    {
        var confirm = MessageBox.Show(this,
            "Dieser Test sendet einmalig einen Report an den MSI-USB-HID-Controller und setzt " +
            "eine Zone auf reines Magenta — das Protokoll ist für dieses Board nicht verifiziert, " +
            "wirkt sich aber nur auf die RGB-Beleuchtung aus, nicht auf andere Board-Funktionen.\n\n" +
            "Jetzt testen?",
            "AetherRGB — MSI-HID-Test", MessageBoxButton.YesNo, MessageBoxImage.Warning);
        if (confirm != MessageBoxResult.Yes) return;

        if (_msiProvider is null)
        {
            _msiProvider = new MsiMysticLightDeviceProvider { LedCountOverride = _config.MsiMysticLight.LedCountOverride };
            _msiProvider.Log += msg => Dispatcher.Invoke(() => Log("[MSI] " + msg));
            _registry.Register(_msiProvider);
        }

        await _msiProvider.ConnectAsync();
        if (!_msiProvider.IsConnected)
        {
            MessageBox.Show(this, "Kein passendes MSI-USB-HID-Gerät gefunden. Details siehe Log unten.",
                "AetherRGB — MSI-HID-Test", MessageBoxButton.OK, MessageBoxImage.Error);
            return;
        }

        bool sent = await _msiProvider.RunSafeSingleZoneTestAsync();
        var result = MessageBox.Show(this,
            sent
                ? "Report wurde gesendet. Hat sich an einer LED (onboard oder am ARGB-Header) etwas verändert?"
                : "Senden ist fehlgeschlagen — siehe Log unten.",
            "AetherRGB — MSI-HID-Test: Ergebnis", MessageBoxButton.YesNo, MessageBoxImage.Question);

        if (sent)
        {
            Log(result == MessageBoxResult.Yes
                ? "MSI-HID-Test: Nutzer bestätigt sichtbare Reaktion — Grundprotokoll scheint zu passen."
                : "MSI-HID-Test: keine sichtbare Reaktion — Byte-Layout in MsiMysticLightProtocol muss " +
                  "anhand einer USB-Capture der offiziellen MSI-Center-App nachjustiert werden.");
        }
    }

    private void OnLayoutClick(object sender, RoutedEventArgs e)
    {
        var editor = new LayoutEditorWindow(_layout, savedLayout =>
        {
            _layout = savedLayout;
            _engine.SetLayout(_layout);
            Log("Layout gespeichert.");
        })
        { Owner = this };
        editor.ShowDialog();
    }

    private void OnRepairClick(object sender, RoutedEventArgs e)
    {
        var result = _repair.Repair();
        Log("Reparatur abgeschlossen:");
        foreach (var step in result.StepsPerformed) Log("  • " + step);
        foreach (var warning in result.Warnings) Log("  ⚠ " + warning);
        MessageBox.Show(this, "Reparatur abgeschlossen. Details siehe Log unten.\nEin Neustart der App wird empfohlen.",
            "AetherRGB — Reparieren", MessageBoxButton.OK, MessageBoxImage.Information);
    }

    private async void OnCheckUpdateClick(object sender, RoutedEventArgs e)
    {
        var currentVersion = Assembly.GetExecutingAssembly().GetName().Version?.ToString(3) ?? "0.0.0";
        try
        {
            var release = await _updateChecker.CheckForUpdateAsync(currentVersion);
            if (release is null)
            {
                MessageBox.Show(this, "Du nutzt bereits die neueste Version.", "AetherRGB — Update", MessageBoxButton.OK, MessageBoxImage.Information);
                return;
            }

            var answer = MessageBox.Show(this,
                $"Version {release.Version} ist verfügbar (aktuell: {currentVersion}).\nSeite jetzt im Browser öffnen?",
                "AetherRGB — Update verfügbar", MessageBoxButton.YesNo, MessageBoxImage.Question);
            if (answer == MessageBoxResult.Yes)
                Process.Start(new ProcessStartInfo(release.DownloadUrl) { UseShellExecute = true });
        }
        catch (Exception ex)
        {
            Log("Update-Prüfung fehlgeschlagen: " + ex.Message);
        }
    }

    private ICanvasEffect _selectedEffect = new CanvasRainbowWaveEffect();

    private void OnEffectChanged(object sender, System.Windows.Controls.SelectionChangedEventArgs e)
    {
        _selectedEffect = EffectCombo.SelectedIndex switch
        {
            0 => new CanvasStaticColorEffect(RgbColor.FromHex("#FF0000")),
            1 => new CanvasStaticColorEffect(RgbColor.FromHex("#0000FF")),
            2 => new CanvasRainbowWaveEffect(),
            3 => new CanvasGradientSweepEffect(RgbColor.FromHex("#3B82F6"), RgbColor.FromHex("#EC4899")),
            _ => new CanvasRadialPulseEffect(RgbColor.FromHex("#10B981"), RgbColor.FromHex("#0B0B0B"))
        };
    }

    private void OnEffectStartClick(object sender, RoutedEventArgs e)
    {
        _engine.SetLayout(_layout);
        _engine.SetEffect(_selectedEffect);
        _engine.Start();
        Log($"Effekt '{_selectedEffect.Name}' gestartet (Layout: {_layout.Items.Count} platzierte Zone(n)).");
    }

    private void OnEffectStopClick(object sender, RoutedEventArgs e)
    {
        _engine.Stop();
        Log("Effekt gestoppt.");
    }

    private void Log(string message) => LogText.Text = message + "\n" + LogText.Text;

    protected override void OnClosed(EventArgs e)
    {
        _engine.Dispose();
        _ = _api?.StopAsync();
        _ = _registry.DisposeAsync();
        base.OnClosed(e);
    }
}
