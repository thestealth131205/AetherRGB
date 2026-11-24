using System.Windows;
using AetherRGB.App.Sensors;
using AetherRGB.Core.Config;
using AetherRGB.Core.Diagnostics;
using AetherRGB.Core.Panels;
using AetherRGB.Core.Sensors;
using Microsoft.Win32;
using Forms = System.Windows.Forms;

namespace AetherRGB.App.Panels;

/// <summary>Verbindet Sensor-Quellen, Panel-Dateien, das Zweitdisplay-Fenster und den Editor.</summary>
public sealed class SensorPanelService : IDisposable
{
    private readonly Func<SensorPanelConfig> _config;
    private readonly SensorHub _hub = SensorHub.Shared;
    private SensorPanelWindow? _window;
    private SensorPanelEditorWindow? _editor;
    private ISensorSource? _windows, _nvml, _aida;
    /// <summary>Vom Editor: dieses (ungespeicherte) Panel statt der Datei auf dem Display zeigen.</summary>
    private SensorPanelDocument? _livePreview;
    private bool _hiddenByUser;

    public SensorPanelStore Store { get; } = new();
    public SensorPanelRenderer DisplayRenderer { get; }

    public event Action<string, LogLevel>? LogMessage;
    /// <summary>Anzeige-Zustand hat sich geändert (für die Einstellungen).</summary>
    public event Action? StatusChanged;

    public SensorPanelService(Func<SensorPanelConfig> config)
    {
        _config = config;
        DisplayRenderer = new SensorPanelRenderer(_hub, Store.ResolveAsset);
        _hub.Log += (level, msg) => Log(msg, level);
        Store.Log += (level, msg) => Log(msg, level);
        try
        {
            Store.LoadAll();
            Log($"Sensor-Panel: {Store.Panels.Count} Panel(s) in {Store.Directory}.", LogLevel.Debug);
        }
        catch (Exception ex) { Log("Sensor-Panels konnten nicht geladen werden: " + ExceptionText.Describe(ex), LogLevel.Warning); }
        Store.Changed += () => Application.Current?.Dispatcher.BeginInvoke(RefreshDocument);
        SystemEvents.DisplaySettingsChanged += OnDisplaySettingsChanged;
        ApplySources();
    }

    /// <summary>Quellen nach Einstellung an-/abschalten, Display-Fenster öffnen/schließen/verschieben.</summary>
    public void Apply()
    {
        _hiddenByUser = false;
        ApplySources();
        ApplyWindow();
    }

    private void ApplySources()
    {
        var cfg = _config();
        _hub.IntervalMs = cfg.IntervalMs;
        if (_windows is null) { _windows = new WindowsSensorSource(); _hub.AddSource(_windows); }
        Toggle(ref _aida, cfg.UseAida64, () => new Aida64SensorSource());
        Toggle(ref _nvml, cfg.UseNvidia, () => new NvmlSensorSource());
    }

    private void Toggle(ref ISensorSource? source, bool enabled, Func<ISensorSource> create)
    {
        if (enabled && source is null)
        {
            source = create();
            _hub.AddSource(source);
        }
        else if (!enabled && source is not null)
        {
            _hub.RemoveSource(source);
            source = null;
        }
    }

    /// <summary>Zustand der Quellen für die Einstellungen und den Editor.</summary>
    public string SourceStatus()
    {
        _hub.Demand();
        var parts = _hub.Sources.Select(s => $"{s.Name}: {s.Status}");
        return string.Join("\n", parts);
    }

    public string DisplayStatus()
    {
        var cfg = _config();
        if (!cfg.DisplayEnabled) return "Anzeige auf dem Zweitdisplay ist aus.";
        if (_hiddenByUser) return "Panel ausgeblendet (Rechtsklick → „Panel ausblenden“). „Speichern“ in den Einstellungen zeigt es wieder.";
        if (_window is null) return "Kein passender Monitor gefunden — ist das Display angeschlossen und unter Windows als „Erweitern“ eingerichtet?";
        var s = Forms.Screen.AllScreens.FirstOrDefault(x => x.DeviceName == _window.DeviceName);
        return $"Läuft auf {_window.DeviceName}" + (s is null ? "" : $" ({s.Bounds.Width} × {s.Bounds.Height})") + $" — Panel „{_window.Document?.Name}“.";
    }

    /// <summary>Monitore für die Auswahl: Gerätename, Beschreibung.</summary>
    public static IReadOnlyList<(string DeviceName, string Label)> Monitors() =>
        Forms.Screen.AllScreens.OrderBy(s => s.Bounds.X).ThenBy(s => s.Bounds.Y)
            .Select((s, i) => (s.DeviceName, $"Monitor {i + 1}: {s.Bounds.Width} × {s.Bounds.Height}{(s.Primary ? ", Hauptmonitor" : "")} ({s.DeviceName.TrimStart('\\', '.')})"))
            .ToList();

    private Forms.Screen? PickScreen(SensorPanelDocument? doc)
    {
        var cfg = _config();
        var screens = Forms.Screen.AllScreens;
        if (!string.IsNullOrEmpty(cfg.Monitor))
            return screens.FirstOrDefault(s => string.Equals(s.DeviceName, cfg.Monitor, StringComparison.OrdinalIgnoreCase));
        // Automatisch nie den Hauptmonitor zudecken.
        var candidates = screens.Where(s => !s.Primary).ToList();
        if (candidates.Count == 0) return null;
        int w = doc?.Width ?? 1024, h = doc?.Height ?? 600;
        return candidates.FirstOrDefault(s => s.Bounds.Width == w && s.Bounds.Height == h)
            ?? candidates.FirstOrDefault(s => s.Bounds.Width == h && s.Bounds.Height == w)
            ?? candidates.OrderBy(s => s.Bounds.Width * s.Bounds.Height).First();
    }

    private SensorPanelDocument? CurrentDocument() =>
        _livePreview ?? Store.Get(_config().PanelId) ?? Store.Panels.FirstOrDefault()?.Clone();

    private void ApplyWindow()
    {
        var cfg = _config();
        var doc = CurrentDocument();
        var screen = cfg.DisplayEnabled && !_hiddenByUser ? PickScreen(doc) : null;
        if (screen is null || doc is null)
        {
            if (_window is not null)
            {
                _window.Close();
                _window = null;
                Log("Sensor-Panel: Anzeige auf dem Zweitdisplay beendet.");
            }
            else if (cfg.DisplayEnabled && !_hiddenByUser)
                Log("Sensor-Panel: kein Monitor für das Panel gefunden (Zweitdisplay angeschlossen und als erweiterter Bildschirm eingerichtet?).", LogLevel.Warning);
            StatusChanged?.Invoke();
            return;
        }
        if (_window is null)
        {
            _window = new SensorPanelWindow(DisplayRenderer, screen, cfg.Fps, cfg.Topmost);
            _window.OpenEditorRequested += () => OpenEditor(null);
            _window.HideRequested += () =>
            {
                _hiddenByUser = true;
                ApplyWindow();
            };
            _window.Document = doc;
            _window.Show();
            Log($"Sensor-Panel „{doc.Name}“ läuft auf {screen.DeviceName} ({screen.Bounds.Width} × {screen.Bounds.Height}, {cfg.Fps} Bilder/s).");
        }
        else
        {
            _window.Topmost = cfg.Topmost;
            _window.SetFps(cfg.Fps);
            if (_window.DeviceName != screen.DeviceName) _window.MoveTo(screen);
            _window.Document = doc;
        }
        StatusChanged?.Invoke();
    }

    private void RefreshDocument()
    {
        if (_window is not null && _livePreview is null) _window.Document = CurrentDocument();
        DisplayRenderer.ForgetImages();
    }

    private void OnDisplaySettingsChanged(object? sender, EventArgs e) =>
        Application.Current?.Dispatcher.BeginInvoke(() =>
        {
            Log("Sensor-Panel: Monitore haben sich geändert — Panel wird neu platziert.", LogLevel.Debug);
            ApplyWindow();
        });

    /// <summary>Editor zeigt sein ungespeichertes Panel live auf dem Display (null = wieder die gespeicherte Datei).</summary>
    public void SetLivePreview(SensorPanelDocument? doc)
    {
        _livePreview = doc;
        if (_window is not null) _window.Document = CurrentDocument();
        else if (doc is not null && _config().DisplayEnabled) ApplyWindow();
    }

    public bool IsDisplayRunning => _window is not null;

    public void OpenEditor(Window? owner)
    {
        if (_editor is not null)
        {
            if (_editor.WindowState == WindowState.Minimized) _editor.WindowState = WindowState.Normal;
            _editor.Activate();
            return;
        }
        _editor = new SensorPanelEditorWindow(this, _config()) { Owner = owner ?? Application.Current?.MainWindow };
        _editor.LogMessage += Log;
        _editor.Closed += (_, _) =>
        {
            _editor = null;
            SetLivePreview(null);
        };
        _editor.Show();
    }

    /// <summary>Panel für das Display wählen (aus dem Editor).</summary>
    public void UsePanel(string id, bool enableDisplay)
    {
        var cfg = _config();
        cfg.PanelId = id;
        if (enableDisplay) cfg.DisplayEnabled = true;
        SaveConfig?.Invoke();
        Apply();
    }

    /// <summary>Speichert die App-Konfiguration (vom Hauptfenster gesetzt).</summary>
    public Action? SaveConfig { get; set; }

    public SensorPanelConfig Config => _config();

    private void Log(string message, LogLevel level = LogLevel.Info) => LogMessage?.Invoke(message, level);

    public void Dispose()
    {
        SystemEvents.DisplaySettingsChanged -= OnDisplaySettingsChanged;
        _editor?.Close();
        _window?.Close();
        _window = null;
        _hub.Dispose();
    }
}
