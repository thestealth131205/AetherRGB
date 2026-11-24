using System.Diagnostics;
using System.Reflection;
using System.Windows;
using System.Windows.Controls;
using Drawing = System.Drawing;
using Forms = System.Windows.Forms;
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
    private readonly SceneCollection _scenes = SceneCollection.LoadOrCreateDefault();
    private readonly LedShapeStore _shapes = LedShapeStore.LoadOrCreateDefault();
    private LocalApiHost? _api;
    private MsiMysticLightDeviceProvider? _msiProvider;
    private readonly MsiBoardInfo? _msiBoard = MsiBoardDetector.Detect();
    private readonly RepairService _repair = new();
    private readonly UpdateChecker _updateChecker = new();
    private readonly List<WledRow> _wledRows = new();
    private Forms.NotifyIcon? _trayIcon;
    private bool _isExiting;

    public MainWindow()
    {
        InitializeComponent();

        _config = AppConfig.LoadOrCreateDefault();
        _engine = new CanvasEffectEngine(_registry);
        _engine.SetScenes(_scenes);
        _engine.SetShapes(_shapes);

        EffectsPanel.LogMessage += Log;
        EffectsPanel.AddWledDevicesAsync = AddWledDevicesAsync;
        EffectsPanel.Initialize(_engine, _scenes, _shapes);

        LoadSettingsIntoUi();
        InitializeTrayIcon();
        Closing += OnWindowClosing;

        try
        {
            BuildProviders();
        }
        catch (Exception ex)
        {
            // Ein fehlerhafter Provider (z. B. nicht erreichbares OpenRGB/HA, falsch konfigurierter
            // Port) darf die ganze App nicht mit in den Abgrund reißen — lieber sichtbar loggen und
            // mit den übrigen, funktionierenden Providern weiterlaufen.
            Log("Fehler beim Aufbau der Geräte-Provider: " + ex.Message);
        }

        Loaded += async (_, _) =>
        {
            try
            {
                bool msiChanged = AskMsiBackendIfNeeded();
                if (_isExiting) return; // Neustart als Administrator läuft bereits.
                if (msiChanged) await RebuildProvidersAndReconnectAsync();
                else await ConnectAndRefreshAsync();
            }
            catch (Exception ex) { Log("Fehler beim Verbinden/Suchen von Geräten: " + ex.Message); }

            // Die Engine läuft dauerhaft; was tatsächlich an die LEDs geht, steuert jedes
            // Effekt-Fenster selbst (Abspielen/Pausieren).
            _engine.Start();
        };
    }

    // ===================== Taskleiste / System-Tray =====================

    private void InitializeTrayIcon()
    {
        try
        {
            var menu = new Forms.ContextMenuStrip();
            menu.Items.Add("Öffnen", null, (_, _) => RestoreFromTray());
            menu.Items.Add(new Forms.ToolStripSeparator());
            menu.Items.Add("Beenden", null, (_, _) => ExitFromTray());

            _trayIcon = new Forms.NotifyIcon
            {
                Icon = Drawing.SystemIcons.Application,
                Text = "AetherRGB",
                Visible = false,
                ContextMenuStrip = menu
            };
            _trayIcon.DoubleClick += (_, _) => RestoreFromTray();
        }
        catch (Exception ex)
        {
            Log("Tray-Symbol konnte nicht erstellt werden: " + ex.Message);
        }
    }

    private void RestoreFromTray()
    {
        Show();
        WindowState = WindowState.Normal;
        Activate();
        if (_trayIcon is not null) _trayIcon.Visible = false;
    }

    private void ExitFromTray()
    {
        _isExiting = true;
        if (_trayIcon is not null)
        {
            _trayIcon.Visible = false;
            _trayIcon.Dispose();
        }
        Close();
    }

    private void OnWindowClosing(object? sender, System.ComponentModel.CancelEventArgs e)
    {
        if (_isExiting) return;

        // Schließen minimiert nur noch in die Taskleiste bei der Uhr, statt die App zu beenden —
        // "Beenden" im Tray-Menü ist der einzige echte Exit-Weg.
        e.Cancel = true;
        Hide();
        if (_trayIcon is not null) _trayIcon.Visible = true;
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
        // Ohne das würde die Engine nach "Einstellungen speichern" weiter an die alte, bereits
        // entsorgte Registry senden.
        _engine.SetRegistry(_registry);
        _registry.ProviderError += (providerId, ex) => Log($"[{providerId}] {ex.Message}");
        _msiProvider = null;

        if (_config.OpenRgb.Enabled)
            _registry.Register(new OpenRgbDeviceProvider(_config.OpenRgb.Host, _config.OpenRgb.Port));

        if (_config.WledInstances.Count > 0)
            _registry.Register(new WledDeviceProvider(_config.WledInstances.Select(w => (w.Name, w.Host))));

        if (_config.HomeAssistant.Enabled && !string.IsNullOrWhiteSpace(_config.HomeAssistant.LongLivedToken))
            _registry.Register(new HomeAssistantDeviceProvider(_config.HomeAssistant.BaseUrl, _config.HomeAssistant.LongLivedToken));

        switch (_config.MsiMysticLight.Backend)
        {
            case MsiBackend.MysticLightSdk:
                var sdkProvider = new MsiMysticLightSdkDeviceProvider
                {
                    DllPath = string.IsNullOrWhiteSpace(_config.MsiMysticLight.SdkDllPath) ? null : _config.MsiMysticLight.SdkDllPath
                };
                sdkProvider.Log += msg => Dispatcher.Invoke(() => Log("[MSI SDK] " + msg));
                _registry.Register(sdkProvider);
                break;
            case MsiBackend.DirectHid:
                _msiProvider = new MsiMysticLightDeviceProvider { LedCountOverride = _config.MsiMysticLight.LedCountOverride };
                _msiProvider.Log += msg => Dispatcher.Invoke(() => Log("[MSI] " + msg));
                _registry.Register(_msiProvider);
                break;
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

        EffectsPanel.SetDevices(devices);
    }

    private async void OnConnectClick(object sender, RoutedEventArgs e) => await ConnectAndRefreshAsync();

    // ===================== Einstellungen =====================

    private void LoadSettingsIntoUi()
    {
        OpenRgbEnabledCheck.IsChecked = _config.OpenRgb.Enabled;
        OpenRgbHostBox.Text = _config.OpenRgb.Host;
        OpenRgbPortBox.Text = _config.OpenRgb.Port.ToString();

        MsiBoardText.Text = _msiBoard is null
            ? "Kein MSI-Mainboard erkannt. Die Auswahl gilt trotzdem, z. B. für MSI-Grafikkarten über das SDK."
            : $"Erkannt: {_msiBoard.Manufacturer} — {_msiBoard.Product}";
        (_config.MsiMysticLight.Backend switch
        {
            MsiBackend.MysticLightSdk => MsiBackendSdk,
            MsiBackend.DirectHid => MsiBackendHid,
            _ => MsiBackendOff
        }).IsChecked = true;
        MsiSdkPathBox.Text = _config.MsiMysticLight.SdkDllPath;
        MsiLedCountBox.Text = _config.MsiMysticLight.LedCountOverride.ToString();
        UpdateMsiPanels();

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

        _config.MsiMysticLight.Backend = SelectedMsiBackend();
        _config.MsiMysticLight.SdkDllPath = MsiSdkPathBox.Text.Trim();
        _config.MsiMysticLight.LedCountOverride = int.TryParse(MsiLedCountBox.Text, out var ledCount) && ledCount > 0 ? ledCount : 1;

        _config.HomeAssistant.Enabled = HaEnabledCheck.IsChecked == true;
        _config.HomeAssistant.BaseUrl = string.IsNullOrWhiteSpace(HaUrlBox.Text) ? _config.HomeAssistant.BaseUrl : HaUrlBox.Text.Trim();
        _config.HomeAssistant.LongLivedToken = HaTokenBox.Password;

        _config.LocalApi.Enabled = LocalApiEnabledCheck.IsChecked == true;
        _config.LocalApi.Port = int.TryParse(LocalApiPortBox.Text, out var apiPort) ? apiPort : 34533;

        _config.WledInstances = _wledRows.Select(r => new WledInstanceConfig { Name = r.Name, Host = r.Host }).ToList();

        _config.Save();
        if (_config.MsiMysticLight.Backend == MsiBackend.MysticLightSdk && OfferElevatedRestart()) return;
        Log("Einstellungen gespeichert. Provider werden neu aufgebaut …");
        await RebuildProvidersAndReconnectAsync();
    }

    // ===================== MSI: Auswahl SDK / eigene Variante =====================

    /// <summary>Fragt beim ersten Start mit erkanntem MSI-Board, welcher Steuerweg genutzt werden soll.
    /// True, wenn sich die Konfiguration geändert hat und die Provider neu aufgebaut werden müssen.</summary>
    private bool AskMsiBackendIfNeeded()
    {
        if (_msiBoard is null || _config.MsiMysticLight.Backend != MsiBackend.Ask) return false;

        var dialog = new MsiBackendWindow(_msiBoard, _config.MsiMysticLight.Backend, _config.MsiMysticLight.SdkDllPath) { Owner = this };
        if (dialog.ShowDialog() != true || dialog.Result is not { } choice)
        {
            Log("MSI-Mainboard erkannt — Steuerweg noch nicht gewählt (Einstellungen → MSI Mystic Light).");
            return false;
        }

        _config.MsiMysticLight.Backend = choice;
        _config.MsiMysticLight.SdkDllPath = dialog.DllPath;
        _config.Save();
        LoadSettingsIntoUi();
        Log("MSI-Steuerweg gewählt: " + choice switch
        {
            MsiBackend.MysticLightSdk => "offizielles Mystic Light SDK",
            MsiBackend.DirectHid => "eigene Variante (USB/HID)",
            _ => "nicht direkt steuern"
        });
        if (choice == MsiBackend.MysticLightSdk && OfferElevatedRestart()) return false;
        return true;
    }

    /// <summary>Das Mystic Light SDK funktioniert nur mit Administratorrechten. Bietet einen Neustart
    /// mit UAC-Abfrage an; true, wenn die App dafür gerade beendet wird.</summary>
    private bool OfferElevatedRestart()
    {
        if (MsiBoardDetector.IsElevated()) return false;
        var answer = MessageBox.Show(this,
            "Das Mystic Light SDK funktioniert nur, wenn AetherRGB mit Administratorrechten läuft.\n\n" +
            "Jetzt als Administrator neu starten?",
            "AetherRGB — Mystic Light SDK", MessageBoxButton.YesNo, MessageBoxImage.Question);
        if (answer != MessageBoxResult.Yes || Environment.ProcessPath is not { } exe) return false;

        try
        {
            Process.Start(new ProcessStartInfo(exe) { UseShellExecute = true, Verb = "runas" });
        }
        catch (System.ComponentModel.Win32Exception)
        {
            // UAC-Abfrage abgelehnt — einfach ohne Adminrechte weiterlaufen.
            Log("Neustart als Administrator abgebrochen — das Mystic Light SDK wird ohne Adminrechte nicht starten.");
            return false;
        }
        ExitFromTray();
        return true;
    }

    private MsiBackend SelectedMsiBackend() =>
        MsiBackendSdk.IsChecked == true ? MsiBackend.MysticLightSdk
        : MsiBackendHid.IsChecked == true ? MsiBackend.DirectHid
        : MsiBackend.Off;

    private void UpdateMsiPanels()
    {
        // Wird schon während InitializeComponent über Checked ausgelöst, bevor alle Elemente existieren.
        if (MsiSdkPanel is null || MsiHidPanel is null || MsiSdkStatusText is null) return;
        var backend = SelectedMsiBackend();
        MsiSdkPanel.Visibility = backend == MsiBackend.MysticLightSdk ? Visibility.Visible : Visibility.Collapsed;
        MsiHidPanel.Visibility = backend == MsiBackend.DirectHid ? Visibility.Visible : Visibility.Collapsed;
        if (backend == MsiBackend.MysticLightSdk)
            MsiSdkStatusText.Text = MsiBackendWindow.DescribeSdkStatus(MsiSdkPathBox.Text.Trim());
    }

    private void OnMsiBackendChanged(object sender, RoutedEventArgs e) => UpdateMsiPanels();

    private void OnMsiSdkPathChanged(object sender, TextChangedEventArgs e) => UpdateMsiPanels();

    private void OnMsiSdkBrowseClick(object sender, RoutedEventArgs e)
    {
        if (MsiBackendWindow.BrowseForDll(this) is { } path) MsiSdkPathBox.Text = path;
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

    // ===================== WLED aus der Effekte-Seite =====================

    /// <summary>Übernimmt WLED-Geräte (manuell per IP oder per Netzwerksuche gefunden) in die Konfiguration
    /// und baut die Provider neu auf, damit sie sofort in der Geräteliste zum Platzieren erscheinen.</summary>
    private async Task AddWledDevicesAsync(IReadOnlyList<(string Name, string Host)> devices)
    {
        int added = 0;
        foreach (var (name, host) in devices)
        {
            if (_config.WledInstances.Any(w => string.Equals(w.Host, host, StringComparison.OrdinalIgnoreCase))) continue;
            _config.WledInstances.Add(new WledInstanceConfig { Name = name, Host = host });
            _wledRows.Add(new WledRow { Name = name, Host = host });
            added++;
        }
        if (added == 0) return;

        _config.Save();
        WledList.ItemsSource = null;
        WledList.ItemsSource = _wledRows;
        Log($"{added} WLED-Gerät(e) hinzugefügt. Provider werden neu aufgebaut …");
        await RebuildProvidersAndReconnectAsync();
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
