using System.Diagnostics;
using System.Reflection;
using System.Windows;
using AetherRGB.Api;
using AetherRGB.Core.Config;
using AetherRGB.Core.Devices;
using AetherRGB.Core.Effects;
using AetherRGB.Core.Update;
using AetherRGB.Providers.HomeAssistant;
using AetherRGB.Providers.OpenRGB;
using AetherRGB.Providers.Wled;

namespace AetherRGB.App;

public partial class MainWindow : Window
{
    private readonly AppConfig _config;
    private readonly DeviceRegistry _registry = new();
    private readonly EffectEngine _engine;
    private LocalApiHost? _api;
    private readonly RepairService _repair = new();
    private readonly UpdateChecker _updateChecker = new();

    public MainWindow()
    {
        InitializeComponent();

        _config = AppConfig.LoadOrCreateDefault();
        _engine = new EffectEngine(_registry);
        _registry.ProviderError += (providerId, ex) => Log($"[{providerId}] {ex.Message}");

        EffectCombo.ItemsSource = new[] { "Statisch Rot", "Statisch Blau", "Regenbogen-Welle" };
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
    }

    private async void OnConnectClick(object sender, RoutedEventArgs e) => await ConnectAndRefreshAsync();

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

    private IEffect _selectedEffect = new RainbowWaveEffect();

    private void OnEffectChanged(object sender, System.Windows.Controls.SelectionChangedEventArgs e)
    {
        _selectedEffect = EffectCombo.SelectedIndex switch
        {
            0 => new StaticColorEffect(RgbColor.FromHex("#FF0000")),
            1 => new StaticColorEffect(RgbColor.FromHex("#0000FF")),
            _ => new RainbowWaveEffect()
        };
    }

    private void OnEffectStartClick(object sender, RoutedEventArgs e)
    {
        var targets = (DeviceList.ItemsSource as IEnumerable<DeviceRow> ?? Enumerable.Empty<DeviceRow>())
            .SelectMany(d => d.Zones.Select(z => (d.Id, z.Index, z.LedCount)));
        _engine.SetTargets(targets);
        _engine.SetEffect(_selectedEffect);
        _engine.Start();
        Log($"Effekt '{_selectedEffect.Name}' gestartet.");
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
