using System.Diagnostics;
using System.Reflection;
using System.Windows;
using System.Windows.Controls;
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

/// <summary>Zeile für die WLED-Instanzliste in der Einstellungen-Seite.</summary>
public sealed class WledRow
{
    public required string Name { get; init; }
    public required string Host { get; init; }
}

public partial class MainWindow : Window
{
    private AppConfig _config;
    private DeviceRegistry _registry = new();
    private readonly CanvasEffectEngine _engine;
    private CanvasLayout _layout = CanvasLayout.LoadOrCreateDefault();
    private LocalApiHost? _api;
    private MsiMysticLightDeviceProvider? _msiProvider;
    private readonly RepairService _repair = new();
    private readonly UpdateChecker _updateChecker = new();
    private readonly List<WledRow> _wledRows = new();

    public MainWindow()
    {
        InitializeComponent();

        _config = AppConfig.LoadOrCreateDefault();
        _engine = new CanvasEffectEngine(_registry);
        _engine.SetLayout(_layout);

        EffectCombo.ItemsSource = new[] { "Statisch Rot", "Statisch Blau", "Regenbogen-Welle (Canvas)", "Farbverlauf (Canvas)", "Radialer Puls (Canvas)" };
        EffectCombo.SelectedIndex = 2;

        LoadSettingsIntoUi();
        BuildProviders();
        Loaded += async (_, _) => await ConnectAndRefreshAsync();
    }

    // ===================== Seiten-Navigation (immer offene Sidebar) =====================

    private void OnNavClick(object sender, RoutedEventArgs e)
    {
        foreach (var btn in new[] { NavDevicesButton, NavEffectsButton, NavSettingsButton })
            btn.Tag = null;

        PageDevices.Visibility = Visibility.Collapsed;
        PageEffects.Visibility = Visibility.Collapsed;
        PageSettings.Visibility = Visibility.Collapsed;

        if (sender == NavDevicesButton) { PageDevices.Visibility = Visibility.Visible; NavDevicesButton.Tag = "Active"; }
        else if (sender == NavEffectsButton) { PageEffects.Visibility = Visibility.Visible; NavEffectsButton.Tag = "Active"; }
        else if (sender == NavSettingsButton) { PageSettings.Visibility = Visibility.Visible; NavSettingsButton.Tag = "Active"; }
    }

    // ===================== Provider-Aufbau =====================

    private void BuildProviders()
    {
        _registry = new DeviceRegistry();
        _registry.ProviderError += (providerId, ex) => Log($"[{providerId}] {ex.Message}");
        _msiProvider = null;

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

        _ = _api?.StopAsync();
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

        Log($"Provider aktiv: {string.Join(", ", _registry.Providers.Select(p => p.ProviderId))}" +
            (_registry.Providers.Count == 0 ? " (keiner — in den Einstellungen mindestens einen aktivieren)" : ""));
    }

    private async Task RebuildProvidersAndReconnectAsync()
    {
        await _registry.DisposeAsync();
        BuildProviders();
        await ConnectAndRefreshAsync();
    }

    private async Task ConnectAndRefreshAsync()
    {
        StatusText.Text = "Suche Geräte …";
        SidebarStatusText.Text = "Suche Geräte …";
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

        var onlineProviders = _registry.Providers.Count(p => p.IsConnected);
        StatusText.Text = $"{devices.Count} Gerät(e) gefunden — {onlineProviders}/{_registry.Providers.Count} Provider online";
        SidebarStatusText.Text = devices.Count > 0
            ? $"{devices.Count} Gerät(e) verbunden"
            : (_registry.Providers.Count == 0 ? "Kein Provider aktiv" : "Keine Geräte gefunden");

        _layout.SyncWithDevices(devices);
        _layout.Save();
        _engine.SetLayout(_layout);
    }

    private async void OnConnectClick(object sender, RoutedEventArgs e) => await ConnectAndRefreshAsync();

    // ===================== Einstellungen =====================

    private void LoadSettingsIntoUi()
    {
        OpenRgbEnabledCheck.IsChecked = _config.OpenRgb.Enabled;
        OpenRgbHostBox.Text = _config.OpenRgb.Host;
        OpenRgbPortBox.Text = _config.OpenRgb.Port.ToString();

        MsiEnabledCheck.IsChecked = _config.MsiMysticLight.Enabled;
        MsiLedCountBox.Text = _config.MsiMysticLight.LedCountOverride.ToString();

        HaEnabledCheck.IsChecked = _config.HomeAssistant.Enabled;
        HaUrlBox.Text = _config.HomeAssistant.BaseUrl;
        HaTokenBox.Password = _config.HomeAssistant.LongLivedToken;

        LocalApiEnabledCheck.IsChecked = _config.LocalApi.Enabled;
        LocalApiPortBox.Text = _config.LocalApi.Port.ToString();

        _wledRows.Clear();
        _wledRows.AddRange(_config.WledInstances.Select(w => new WledRow { Name = w.Name, Host = w.Host }));
        WledList.ItemsSource = null;
        WledList.ItemsSource = _wledRows;
    }

    private void OnWledAddClick(object sender, RoutedEventArgs e)
    {
        var name = WledNameBox.Text.Trim();
        var host = WledHostBox.Text.Trim();
        if (string.IsNullOrWhiteSpace(name) || string.IsNullOrWhiteSpace(host))
        {
            MessageBox.Show(this, "Bitte Name und Host/IP angeben.", "WLED", MessageBoxButton.OK, MessageBoxImage.Warning);
            return;
        }

        _wledRows.Add(new WledRow { Name = name, Host = host });
        WledList.ItemsSource = null;
        WledList.ItemsSource = _wledRows;
        WledNameBox.Text = "";
        WledHostBox.Text = "";
    }

    private void OnWledRemoveClick(object sender, RoutedEventArgs e)
    {
        if (WledList.SelectedItem is WledRow row)
        {
            _wledRows.Remove(row);
            WledList.ItemsSource = null;
            WledList.ItemsSource = _wledRows;
        }
    }

    private async void OnSettingsSaveClick(object sender, RoutedEventArgs e)
    {
        _config.OpenRgb.Enabled = OpenRgbEnabledCheck.IsChecked == true;
        _config.OpenRgb.Host = string.IsNullOrWhiteSpace(OpenRgbHostBox.Text) ? "127.0.0.1" : OpenRgbHostBox.Text.Trim();
        _config.OpenRgb.Port = int.TryParse(OpenRgbPortBox.Text, out var orgbPort) ? orgbPort : 6742;

        _config.MsiMysticLight.Enabled = MsiEnabledCheck.IsChecked == true;
        _config.MsiMysticLight.LedCountOverride = int.TryParse(MsiLedCountBox.Text, out var ledCount) && ledCount > 0 ? ledCount : 1;

        _config.HomeAssistant.Enabled = HaEnabledCheck.IsChecked == true;
        _config.HomeAssistant.BaseUrl = string.IsNullOrWhiteSpace(HaUrlBox.Text) ? _config.HomeAssistant.BaseUrl : HaUrlBox.Text.Trim();
        _config.HomeAssistant.LongLivedToken = HaTokenBox.Password;

        _config.LocalApi.Enabled = LocalApiEnabledCheck.IsChecked == true;
        _config.LocalApi.Port = int.TryParse(LocalApiPortBox.Text, out var apiPort) ? apiPort : 34533;

        _config.WledInstances = _wledRows.Select(r => new WledInstanceConfig { Name = r.Name, Host = r.Host }).ToList();

        _config.Save();
        Log("Einstellungen gespeichert. Provider werden neu aufgebaut …");
        await RebuildProvidersAndReconnectAsync();
    }

    // ===================== MSI-HID-Test =====================

    private async void OnMsiTestClick(object sender, RoutedEventArgs e)
    {
        var confirm = MessageBox.Show(this,
            "Dieser Test sendet einmalig einen Report an den MSI-USB-HID-Controller und setzt " +
            "eine Zone auf reines Magenta — das Protokoll ist für dieses Board nicht verifiziert, " +
            "wirkt sich aber nur auf die RGB-Beleuchtung aus, nicht auf andere Board-Funktionen.\n\n" +
            "Jetzt testen?",
            "AetherRGB — MSI-HID-Test", MessageBoxButton.YesNo, MessageBoxImage.Warning);
        if (confirm != MessageBoxResult.Yes) return;

        var provider = _msiProvider ?? new MsiMysticLightDeviceProvider
        {
            LedCountOverride = int.TryParse(MsiLedCountBox.Text, out var lc) && lc > 0 ? lc : 1
        };
        provider.Log += msg => Dispatcher.Invoke(() => Log("[MSI] " + msg));

        await provider.ConnectAsync();
        if (!provider.IsConnected)
        {
            MessageBox.Show(this, "Kein passendes MSI-USB-HID-Gerät gefunden. Details siehe Log unten.",
                "AetherRGB — MSI-HID-Test", MessageBoxButton.OK, MessageBoxImage.Error);
            return;
        }

        bool sent = await provider.RunSafeSingleZoneTestAsync();
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

    // ===================== Layout / Effekte =====================

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

    private ICanvasEffect _selectedEffect = new CanvasRainbowWaveEffect();

    private void OnEffectChanged(object sender, SelectionChangedEventArgs e)
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

    // ===================== Reparieren / Update =====================

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

    private void Log(string message) => LogText.Text = message + "\n" + LogText.Text;

    protected override void OnClosed(EventArgs e)
    {
        _engine.Dispose();
        _ = _api?.StopAsync();
        _ = _registry.DisposeAsync();
        base.OnClosed(e);
    }
}
