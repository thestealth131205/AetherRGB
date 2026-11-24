using System.Collections.ObjectModel;
using System.Diagnostics;
using System.IO;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Text;
using System.Windows;
using System.Windows.Controls;
using Drawing = System.Drawing;
using Forms = System.Windows.Forms;
using AetherRGB.Api;
using AetherRGB.Core.Config;
using AetherRGB.Core.Devices;
using AetherRGB.Core.Diagnostics;
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
    private readonly ObservableCollection<LogEntry> _log = new();
    private const int MaxLogEntries = 3000;
    private int _logWarnings, _logErrors;

    private static string AppVersion => Assembly.GetExecutingAssembly().GetName().Version?.ToString(3) ?? "0.0.0";
    private static bool IsInstalled => File.Exists(Path.Combine(AppContext.BaseDirectory, "unins000.exe"));

    public MainWindow()
    {
        InitializeComponent();
        LogList.ItemsSource = _log;

        CleanupOldPortableExe();
        _config = AppConfig.LoadOrCreateDefault();
        LogStartupInfo();
        _engine = new CanvasEffectEngine(_registry);
        _engine.SetScenes(_scenes);
        _engine.SetShapes(_shapes);
        _engine.SendError += (deviceId, error) => Log($"Senden an {deviceId} fehlgeschlagen: {error}", LogLevel.Warning);

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
            Log("Fehler beim Aufbau der Geräte-Provider: " + ExceptionText.Describe(ex), LogLevel.Error);
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
            catch (Exception ex) { Log("Fehler beim Verbinden/Suchen von Geräten: " + ExceptionText.Describe(ex), LogLevel.Error); }

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
            Log("Tray-Symbol konnte nicht erstellt werden: " + ExceptionText.Describe(ex), LogLevel.Warning);
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
        _registry.ProviderError += (providerId, ex) => Log($"[{providerId}] {ExceptionText.Describe(ex)}", LogLevel.Error);
        _registry.ProviderConnectFinished += (provider, elapsed) => Log(
            $"[{provider.ProviderId}] {(provider.IsConnected ? "verbunden" : "nicht verbunden")} ({elapsed.TotalMilliseconds:0} ms)",
            provider.IsConnected ? LogLevel.Info : LogLevel.Warning);
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
                _registry.Register(sdkProvider);
                break;
            case MsiBackend.DirectHid:
                _msiProvider = new MsiMysticLightDeviceProvider { LedCountOverride = _config.MsiMysticLight.LedCountOverride };
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
                    Log($"Lokale API konnte Port {_config.LocalApi.Port} nicht binden: {ExceptionText.Describe(t.Exception)}", LogLevel.Error);
                else
                    Log($"Lokale API läuft auf http://127.0.0.1:{_config.LocalApi.Port}/api — keine Verbindung nach außen.");
            });
        }

        // Alle Provider-Details (Verbindungsversuche, Fehlergründe) landen in der Log-Leiste.
        foreach (var provider in _registry.Providers.OfType<IProviderLog>())
        {
            var id = ((IDeviceProvider)provider).ProviderId;
            provider.Log += (level, msg) => Log($"[{id}] {msg}", level);
        }

        Log($"Provider aktiv: {string.Join(", ", _registry.Providers.Select(p => p.ProviderId))}" +
            (_registry.Providers.Count == 0 ? " (keiner — in den Einstellungen mindestens einen aktivieren)" : ""),
            _registry.Providers.Count == 0 ? LogLevel.Warning : LogLevel.Info);
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
        Log($"Suche Geräte bei {_registry.Providers.Count} Provider(n) …", LogLevel.Debug);
        await _registry.ConnectAllAsync();
        var devices = await _registry.GetAllDevicesAsync();
        LogDevices(devices);

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
            Log("Neustart als Administrator abgebrochen — das Mystic Light SDK wird ohne Adminrechte nicht starten.", LogLevel.Warning);
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
        if (provider != _msiProvider) provider.Log += (level, msg) => Log("[msi] " + msg, level);

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
        foreach (var warning in result.Warnings) Log("  ⚠ " + warning, LogLevel.Warning);
        MessageBox.Show(this, "Reparatur abgeschlossen. Details siehe Log unten.\nEin Neustart der App wird empfohlen.",
            "AetherRGB — Reparieren", MessageBoxButton.OK, MessageBoxImage.Information);
    }

    private async void OnCheckUpdateClick(object sender, RoutedEventArgs e)
    {
        var currentVersion = AppVersion;
        var originalContent = UpdateButton.Content;
        UpdateButton.IsEnabled = false;
        UpdateButton.Content = "Suche …";
        try
        {
            var release = await _updateChecker.CheckForUpdateAsync(currentVersion);
            if (release is null)
            {
                MessageBox.Show(this, $"Du nutzt bereits die neueste Version ({currentVersion}).", "AetherRGB — Update", MessageBoxButton.OK, MessageBoxImage.Information);
                return;
            }

            // Installiert = Inno Setup hat seinen Uninstaller neben die EXE gelegt. Sonst läuft die
            // portable EXE, und die wird durch die neue portable EXE ersetzt.
            bool installed = IsInstalled;
            var asset = installed ? release.Setup : release.Portable;
            if (asset is null)
            {
                var open = MessageBox.Show(this,
                    $"Version {release.Version} ist verfügbar, enthält aber keine passende Datei ({(installed ? UpdateChecker.SetupAssetName : UpdateChecker.PortableAssetName)}).\nRelease-Seite im Browser öffnen?",
                    "AetherRGB — Update verfügbar", MessageBoxButton.YesNo, MessageBoxImage.Warning);
                if (open == MessageBoxResult.Yes) OpenUrl(release.ReleaseNotesUrl);
                return;
            }

            var answer = MessageBox.Show(this,
                $"Version {release.Version} ist verfügbar (aktuell: {currentVersion}).\n\n" +
                (installed
                    ? "Installer jetzt herunterladen und starten? AetherRGB wird dafür beendet."
                    : "Neue portable Version jetzt herunterladen? Die EXE wird ersetzt und AetherRGB neu gestartet."),
                "AetherRGB — Update verfügbar", MessageBoxButton.YesNo, MessageBoxImage.Question);
            if (answer != MessageBoxResult.Yes) return;

            var progress = new Progress<double>(p => UpdateButton.Content = $"Lade … {p:P0}");
            Log($"Lade {asset.Name} ({asset.Size / 1024.0 / 1024.0:F1} MB) von GitHub …");
            if (asset.Sha256 is null) Log("  ⚠ Release enthält keine SHA-256-Prüfsumme — nur die Dateigröße wird geprüft.", LogLevel.Warning);

            if (installed) await InstallSetupUpdateAsync(asset, release.Version, progress);
            else await ReplacePortableExeAsync(asset, release.Version, progress);
        }
        catch (Exception ex)
        {
            Log("Update fehlgeschlagen: " + ExceptionText.Describe(ex), LogLevel.Error);
            MessageBox.Show(this, "Update fehlgeschlagen:\n\n" + ex.Message, "AetherRGB — Update", MessageBoxButton.OK, MessageBoxImage.Error);
        }
        finally
        {
            UpdateButton.Content = originalContent;
            UpdateButton.IsEnabled = true;
        }
    }

    private async Task InstallSetupUpdateAsync(ReleaseAsset asset, string version, IProgress<double> progress)
    {
        var dir = Path.Combine(Path.GetTempPath(), "AetherRGB-Update");
        Directory.CreateDirectory(dir);
        var setupPath = Path.Combine(dir, $"AetherRGB-Setup-x64-{version}.exe");
        await _updateChecker.DownloadAsync(asset, setupPath, progress);
        Log("Download fertig, starte Installer: " + setupPath);

        try
        {
            // Normaler Assistent statt /SILENT: Inno Setup übernimmt den bisherigen Installationsordner,
            // fragt nach Adminrechten und bietet am Ende "AetherRGB jetzt starten" an.
            Process.Start(new ProcessStartInfo(setupPath) { UseShellExecute = true });
        }
        catch (System.ComponentModel.Win32Exception)
        {
            Log("Installer-Start abgebrochen. Er liegt unter: " + setupPath, LogLevel.Warning);
            return;
        }
        ExitFromTray();
    }

    private async Task ReplacePortableExeAsync(ReleaseAsset asset, string version, IProgress<double> progress)
    {
        var exePath = Environment.ProcessPath ?? throw new InvalidOperationException("Pfad der laufenden EXE unbekannt.");
        var newPath = exePath + ".new";
        try
        {
            await _updateChecker.DownloadAsync(asset, newPath, progress);
        }
        catch (UnauthorizedAccessException)
        {
            // Ordner der portablen EXE ist schreibgeschützt — dann wenigstens in Downloads ablegen.
            var downloads = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), "Downloads");
            var fallback = Path.Combine(downloads, $"AetherRGB-Portable-x64-{version}.exe");
            await _updateChecker.DownloadAsync(asset, fallback, progress);
            Log("Kein Schreibzugriff auf den EXE-Ordner — neue Version liegt unter: " + fallback, LogLevel.Warning);
            Process.Start(new ProcessStartInfo("explorer.exe", $"/select,\"{fallback}\"") { UseShellExecute = true });
            return;
        }

        // Windows erlaubt, eine laufende EXE umzubenennen (nicht zu löschen). Die alte Datei räumt
        // der nächste Start in CleanupOldPortableExe() weg.
        var oldPath = exePath + ".old";
        File.Delete(oldPath);
        File.Move(exePath, oldPath);
        try
        {
            File.Move(newPath, exePath);
        }
        catch
        {
            File.Move(oldPath, exePath);
            throw;
        }

        Log($"Version {version} installiert, starte neu …");
        Process.Start(new ProcessStartInfo(exePath) { UseShellExecute = true });
        ExitFromTray();
    }

    /// <summary>Entfernt die nach einem portablen Update übrig gebliebene <c>.old</c>-EXE.</summary>
    private static void CleanupOldPortableExe()
    {
        if (Environment.ProcessPath is not { } exePath) return;
        // Der alte Prozess beendet sich evtl. gerade erst — kurz warten und notfalls beim nächsten Start.
        _ = Task.Run(async () =>
        {
            for (int i = 0; i < 10 && File.Exists(exePath + ".old"); i++)
            {
                try { File.Delete(exePath + ".old"); }
                catch { await Task.Delay(1000); }
            }
        });
    }

    private static void OpenUrl(string url) => Process.Start(new ProcessStartInfo(url) { UseShellExecute = true });

    // ===================== Log-Leiste =====================

    /// <summary>Hängt eine Zeile an die Log-Leiste an. Darf von jedem Thread aufgerufen werden.</summary>
    private void Log(string message, LogLevel level = LogLevel.Info)
    {
        var entry = new LogEntry { Time = DateTime.Now, Level = level, Message = message };
        if (!Dispatcher.CheckAccess())
        {
            Dispatcher.BeginInvoke(() => AddLogEntry(entry));
            return;
        }
        AddLogEntry(entry);
    }

    private void AddLogEntry(LogEntry entry)
    {
        // Nur mitscrollen, wenn man unten steht — sonst springt die Ansicht beim Zurücklesen weg.
        bool atEnd = LogScroll.VerticalOffset >= LogScroll.ScrollableHeight - 2;
        _log.Add(entry);
        Count(entry, +1);
        while (_log.Count > MaxLogEntries)
        {
            Count(_log[0], -1);
            _log.RemoveAt(0);
        }
        if (atEnd) LogScroll.ScrollToEnd();

        LogCountText.Text = $"{_log.Count} Zeilen" + (_logErrors > 0 ? $" · {_logErrors} Fehler" : "") + (_logWarnings > 0 ? $" · {_logWarnings} Warn." : "");

        void Count(LogEntry e, int delta)
        {
            if (e.Level == LogLevel.Error) _logErrors += delta;
            else if (e.Level == LogLevel.Warning) _logWarnings += delta;
        }
    }

    /// <summary>Umgebung und Konfiguration beim Start — damit ein gespeichertes Log ohne Rückfragen
    /// zeigt, womit die App lief. Das HA-Token wird bewusst nie geloggt.</summary>
    private void LogStartupInfo()
    {
        Log($"AetherRGB {AppVersion} gestartet ({(IsInstalled ? "installiert" : "portabel")}): {Environment.ProcessPath}");
        Log($"System: {RuntimeInformation.OSDescription} ({RuntimeInformation.OSArchitecture}), {RuntimeInformation.FrameworkDescription}, " +
            $"Prozess {(Environment.Is64BitProcess ? "64" : "32")}-Bit, " +
            $"{(MsiBoardDetector.IsElevated() ? "mit" : "ohne")} Administratorrechte, Sprache {System.Globalization.CultureInfo.CurrentUICulture.Name}", LogLevel.Debug);
        Log("Datenordner: " + Path.GetDirectoryName(AppConfig.DefaultPath), LogLevel.Debug);
        Log(_msiBoard is null
            ? "Mainboard: kein MSI-Board erkannt."
            : $"Mainboard: {_msiBoard.Manufacturer} {_msiBoard.Product} — MSI-Steuerweg: {_config.MsiMysticLight.Backend}", LogLevel.Debug);
        Log("Konfiguration: " + string.Join("; ", new[]
        {
            _config.OpenRgb.Enabled ? $"OpenRGB {_config.OpenRgb.Host}:{_config.OpenRgb.Port}" : "OpenRGB aus",
            _config.WledInstances.Count > 0 ? $"WLED {string.Join(", ", _config.WledInstances.Select(w => $"{w.Name} ({w.Host})"))}" : "kein WLED",
            _config.HomeAssistant.Enabled
                ? $"Home Assistant {_config.HomeAssistant.BaseUrl} (Token {(string.IsNullOrWhiteSpace(_config.HomeAssistant.LongLivedToken) ? "fehlt" : "gesetzt")})"
                : "Home Assistant aus",
            $"MSI {_config.MsiMysticLight.Backend}" + (_config.MsiMysticLight.Backend == MsiBackend.MysticLightSdk
                ? $" (DLL: {MsiMysticLightSdkDeviceProvider.FindSdkDll(_config.MsiMysticLight.SdkDllPath) ?? "nicht gefunden"})" : ""),
            _config.LocalApi.Enabled ? $"lokale API Port {_config.LocalApi.Port}" : "lokale API aus"
        }), LogLevel.Debug);
        Log($"Effekt-Fenster: {_scenes.Scenes.Count}, platzierte Zonen: {_scenes.Scenes.Sum(sc => sc.Items.Count)}, " +
            $"eigene LED-Anordnungen: {_shapes.Shapes.Count}", LogLevel.Debug);
    }

    private void LogDevices(IReadOnlyList<RgbDevice> devices)
    {
        Log($"{devices.Count} Gerät(e) gefunden.", devices.Count > 0 ? LogLevel.Info : LogLevel.Warning);
        foreach (var d in devices)
            Log($"  • {d.Name} [{d.Id}, {d.Vendor}{(d.IsOnline ? "" : ", offline")}] — " +
                string.Join(", ", d.Zones.Select(z => $"Zone {z.Index} „{z.Name}“: {z.LedCount} LEDs")), LogLevel.Debug);
    }

    private void OnLogSaveClick(object sender, RoutedEventArgs e)
    {
        var dialog = new Microsoft.Win32.SaveFileDialog
        {
            Title = "Log speichern",
            FileName = $"AetherRGB-Log-{DateTime.Now:yyyyMMdd-HHmmss}.txt",
            DefaultExt = ".txt",
            Filter = "Textdatei (*.txt)|*.txt|Alle Dateien (*.*)|*.*",
            InitialDirectory = Environment.GetFolderPath(Environment.SpecialFolder.DesktopDirectory)
        };
        if (dialog.ShowDialog(this) != true) return;

        try
        {
            var text = new StringBuilder()
                .AppendLine($"AetherRGB {AppVersion} — Log gespeichert am {DateTime.Now:yyyy-MM-dd HH:mm:ss}")
                .AppendLine(new string('-', 80));
            foreach (var entry in _log) text.AppendLine(entry.ToString());
            File.WriteAllText(dialog.FileName, text.ToString(), new UTF8Encoding(encoderShouldEmitUTF8Identifier: true));
            Log("Log gespeichert: " + dialog.FileName);
        }
        catch (Exception ex)
        {
            Log("Log konnte nicht gespeichert werden: " + ExceptionText.Describe(ex), LogLevel.Error);
            MessageBox.Show(this, "Log konnte nicht gespeichert werden:\n\n" + ex.Message, "AetherRGB — Log", MessageBoxButton.OK, MessageBoxImage.Error);
        }
    }

    protected override void OnClosed(EventArgs e)
    {
        _engine.Dispose();
        _ = _api?.StopAsync();
        _ = _registry.DisposeAsync();
        base.OnClosed(e);
    }
}
