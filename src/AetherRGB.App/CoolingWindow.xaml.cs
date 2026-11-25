using System.Diagnostics;
using System.Globalization;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Shapes;
using System.Windows.Threading;
using AetherRGB.Core.Config;
using AetherRGB.Core.Cooling;
using AetherRGB.Core.Diagnostics;
using AetherRGB.Core.Sensors;

namespace AetherRGB.App;

/// <summary>Verwaltung erkannter Lüfter: Modus (fest/Kurve/Smart), Temperaturquelle, Kurve, Smart-Kalibrierung.</summary>
public sealed partial class CoolingWindow : Window
{
    private sealed class FanRow
    {
        public required FanInfo Info { get; set; }
        public required FanProfileConfig Profile { get; set; }
        public string DisplayLabel => $"{Info.Name}" +
            (Info.Rpm is { } rpm ? $" — {rpm:0} RPM" : Profile.HasRpmSignal ? " — kein Signal" : " — ohne Tacho") +
            $" ({Profile.ManualPercentOrCurrent(Info.Percent)} %)";
    }

    private readonly CoolingService _service;
    private readonly CoolingConfig _config;
    private readonly List<FanRow> _rows = new();
    private readonly DispatcherTimer _timer;
    private FanRow? _selected;
    private bool _suppressEvents;
    private CancellationTokenSource? _calibrationCts;

    public event Action<string, LogLevel>? LogMessage;
    public event Action? SaveConfig;

    public CoolingWindow(CoolingService service, CoolingConfig config)
    {
        InitializeComponent();
        _service = service;
        _config = config;
        EnabledCheck.IsChecked = config.Enabled;
        _timer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(2) };
        _timer.Tick += (_, _) => RefreshLive();
        Loaded += (_, _) => { RefreshFans(); _timer.Start(); };
        Closed += (_, _) => _timer.Stop();
    }

    private void UpdateStatusAndWarning()
    {
        StatusText.Text = _service.Status;
        ElevationWarning.Visibility = _service.IsAvailable ? Visibility.Collapsed : Visibility.Visible;
    }

    private void RefreshFans()
    {
        SensorHub.Shared.Demand();
        UpdateStatusAndWarning();
        var detected = _service.Detect();
        string? keepSelected = _selected?.Info.Id;
        _rows.Clear();
        foreach (var fan in detected)
        {
            var profile = _config.Fans.FirstOrDefault(f => f.FanId == fan.Id);
            if (profile is null)
            {
                profile = new FanProfileConfig { FanId = fan.Id, DisplayName = fan.Name };
                _config.Fans.Add(profile);
            }
            else profile.DisplayName = fan.Name;
            _rows.Add(new FanRow { Info = fan, Profile = profile });
        }
        FanList.ItemsSource = null;
        FanList.ItemsSource = _rows;
        if (keepSelected is not null)
            FanList.SelectedItem = _rows.FirstOrDefault(r => r.Info.Id == keepSelected);
        else if (_rows.Count > 0) FanList.SelectedIndex = 0;

        if (_rows.Count == 0 && _service.IsAvailable)
            LogMessage?.Invoke("Kühlung: keine steuerbaren Lüfter gefunden (NZXT/Corsair-USB-Controller werden nicht unterstützt, nur Mainboard- und AIO-PWM-Kanäle).", LogLevel.Info);
    }

    private void RefreshLive()
    {
        SensorHub.Shared.Demand();
        UpdateStatusAndWarning();
        var detected = _service.Detect().ToDictionary(f => f.Id);
        foreach (var row in _rows)
            if (detected.TryGetValue(row.Info.Id, out var fresh)) row.Info = fresh;
        var selectedId = _selected?.Info.Id;
        FanList.Items.Refresh();
        if (selectedId is not null) FanTitleText.Text = _rows.FirstOrDefault(r => r.Info.Id == selectedId)?.Info.Name ?? FanTitleText.Text;
    }

    // ===================== Auswahl & Details =====================

    private void OnSelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        _selected = FanList.SelectedItem as FanRow;
        DetailPanel.Visibility = _selected is null ? Visibility.Collapsed : Visibility.Visible;
        if (_selected is null) return;
        LoadDetails(_selected);
    }

    private void LoadDetails(FanRow row)
    {
        _suppressEvents = true;
        try
        {
            FanTitleText.Text = row.Info.Name;
            FanInfoText.Text = row.Info.Rpm is { } rpm
                ? $"Aktuell {rpm:0} RPM bei {row.Info.Percent ?? 0} % Ansteuerung."
                : row.Profile.HasRpmSignal ? "Noch kein Drehzahlsignal empfangen." : "Liefert kein Drehzahlsignal (kalibriert).";

            ModeCombo.SelectedItem = ModeCombo.Items.Cast<ComboBoxItem>().FirstOrDefault(i => (string)i.Tag == row.Profile.Mode.ToString())
                ?? ModeCombo.Items[1];
            ManualPanel.Visibility = row.Profile.Mode == CoolingFanMode.Manual ? Visibility.Visible : Visibility.Collapsed;
            CurvePanel.Visibility = row.Profile.Mode == CoolingFanMode.Manual ? Visibility.Collapsed : Visibility.Visible;

            ManualSlider.Value = row.Profile.ManualPercent;
            ManualPercentText.Text = $"{row.Profile.ManualPercent} %";

            LoadTempSensorCombo(row.Profile.TempSensorId);
            RebuildCurveEditor(row.Profile);

            CalibrationInfoText.Text = row.Profile.Calibrated
                ? $"Kalibriert am {row.Profile.LastCalibrationUtc?.ToLocalTime():g}, Minimum {row.Profile.MeasuredMinPercent?.ToString() + " %" ?? "kein Signal"}."
                : "Noch nicht kalibriert — die Mindest-Ansteuerung ist unbekannt, deshalb startet die Kurve vorsichtig bei 50 %.";
        }
        finally { _suppressEvents = false; }
    }

    private void LoadTempSensorCombo(string selectedId)
    {
        TempSensorCombo.Items.Clear();
        var auto = new ComboBoxItem { Content = "Automatisch (erste gefundene CPU-Temperatur)", Tag = "" };
        TempSensorCombo.Items.Add(auto);
        object selected = auto;
        var temps = SensorHub.Shared.Snapshot.Values
            .Where(v => v.IsNumeric && v.Group == SensorGroups.Temperature)
            .OrderBy(v => v.Name);
        foreach (var t in temps)
        {
            var item = new ComboBoxItem { Content = $"{t.Name} ({t.Value:0.#} °C, {t.Source})", Tag = t.Id };
            TempSensorCombo.Items.Add(item);
            if (t.Id == selectedId) selected = item;
        }
        TempSensorCombo.SelectedItem = selected;
    }

    // ===================== Modus / manuell =====================

    private void OnModeChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_suppressEvents || _selected is null) return;
        if (ModeCombo.SelectedItem is not ComboBoxItem item || item.Tag is not string tag) return;
        _selected.Profile.Mode = Enum.Parse<CoolingFanMode>(tag);
        ManualPanel.Visibility = _selected.Profile.Mode == CoolingFanMode.Manual ? Visibility.Visible : Visibility.Collapsed;
        CurvePanel.Visibility = _selected.Profile.Mode == CoolingFanMode.Manual ? Visibility.Collapsed : Visibility.Visible;
        Persist();
    }

    private void OnManualPercentChanged(object sender, RoutedPropertyChangedEventArgs<double> e)
    {
        if (_suppressEvents || _selected is null) return;
        int pct = (int)Math.Round(ManualSlider.Value);
        ManualPercentText.Text = $"{pct} %";
        _selected.Profile.ManualPercent = pct;
        _service.SetPercent(_selected.Info.Id, pct);
        Persist();
    }

    private void OnTempSensorChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_suppressEvents || _selected is null) return;
        _selected.Profile.TempSensorId = (TempSensorCombo.SelectedItem as ComboBoxItem)?.Tag as string ?? "";
        Persist();
    }

    // ===================== Kurve =====================

    private void RebuildCurveEditor(FanProfileConfig profile)
    {
        var stack = new StackPanel();
        foreach (var point in profile.Curve.OrderBy(p => p.TempC).ToList())
        {
            var row = new Grid { Margin = new Thickness(0, 0, 0, 4) };
            row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            row.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            row.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });

            var tempBox = new TextBox { Text = point.TempC.ToString("0.#", CultureInfo.InvariantCulture), Margin = new Thickness(0, 0, 4, 0) };
            tempBox.LostFocus += (_, _) =>
            {
                if (double.TryParse(tempBox.Text, NumberStyles.Float, CultureInfo.InvariantCulture, out var t))
                { point.TempC = t; RebuildCurveEditor(profile); Persist(); }
            };
            Grid.SetColumn(tempBox, 0);

            var arrow = new TextBlock { Text = "°C →", VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(0, 0, 4, 0), Style = (Style)FindResource("MutedText") };
            Grid.SetColumn(arrow, 1);

            var pctBox = new TextBox { Text = point.Percent.ToString(CultureInfo.InvariantCulture), Margin = new Thickness(0, 0, 4, 0) };
            pctBox.LostFocus += (_, _) =>
            {
                if (int.TryParse(pctBox.Text, out var p)) { point.Percent = Math.Clamp(p, 0, 100); RebuildCurveEditor(profile); Persist(); }
            };
            Grid.SetColumn(pctBox, 2);

            var remove = new Button { Content = "✕", Padding = new Thickness(8, 2, 8, 2) };
            remove.Click += (_, _) =>
            {
                if (profile.Curve.Count <= 2) return; // mindestens zwei Stützpunkte
                profile.Curve.Remove(point);
                RebuildCurveEditor(profile);
                Persist();
            };
            Grid.SetColumn(remove, 3);

            row.Children.Add(tempBox);
            row.Children.Add(arrow);
            row.Children.Add(pctBox);
            row.Children.Add(remove);
            stack.Children.Add(row);
        }
        CurvePointsList.Content = stack;
        DrawCurvePreview(profile);
    }

    private void OnAddPointClick(object sender, RoutedEventArgs e)
    {
        if (_selected is null) return;
        double midTemp = _selected.Profile.Curve.Count > 0 ? _selected.Profile.Curve.Average(p => p.TempC) + 5 : 50;
        _selected.Profile.Curve.Add(new FanCurvePointConfig { TempC = Math.Round(midTemp), Percent = FanCurve.Evaluate(_selected.Profile.Curve, midTemp) });
        RebuildCurveEditor(_selected.Profile);
        Persist();
    }

    private void DrawCurvePreview(FanProfileConfig profile)
    {
        CurvePreview.Children.Clear();
        double w = CurvePreview.ActualWidth > 0 ? CurvePreview.ActualWidth : 600;
        double h = 120;
        var points = profile.Curve.OrderBy(p => p.TempC).ToList();
        if (points.Count == 0) return;
        double minT = Math.Min(0, points.Min(p => p.TempC));
        double maxT = Math.Max(100, points.Max(p => p.TempC));
        var line = new Polyline { Stroke = (Brush)FindResource("BrushAccent"), StrokeThickness = 2 };
        foreach (var p in points)
        {
            double x = (p.TempC - minT) / Math.Max(1, maxT - minT) * w;
            double y = h - p.Percent / 100.0 * h;
            line.Points.Add(new Point(x, y));
        }
        CurvePreview.Children.Add(line);
        CurvePreview.SizeChanged -= OnPreviewSizeChanged;
        CurvePreview.SizeChanged += OnPreviewSizeChanged;
    }

    private void OnPreviewSizeChanged(object sender, SizeChangedEventArgs e)
    {
        if (_selected is not null) DrawCurvePreview(_selected.Profile);
    }

    // ===================== Kalibrierung =====================

    private async void OnCalibrateClick(object sender, RoutedEventArgs e)
    {
        if (!_service.IsAvailable)
        {
            MessageBox.Show(this, "Kühlung ist nicht verbunden — ohne Administratorrechte findet AetherRGB keine Lüfter.",
                "Kühlung", MessageBoxButton.OK, MessageBoxImage.Warning);
            return;
        }
        var answer = MessageBox.Show(this,
            "Die Smart-Kalibrierung fährt alle erkannten Lüfter kurz einzeln hoch (um die niedrigste Ansteuerung mit Drehzahlsignal zu finden) und stellt sie danach für etwa eine Minute auf dieses Minimum, um die Temperatur zu beobachten. Das dauert ein bis zwei Minuten und läuft hörbar ab. Jetzt starten?",
            "Smart-Kalibrierung", MessageBoxButton.YesNo, MessageBoxImage.Question);
        if (answer != MessageBoxResult.Yes) return;

        CalibrateButton.IsEnabled = false;
        RefreshButton.IsEnabled = false;
        CalibrationBar.Visibility = Visibility.Visible;
        CalibrationText.Visibility = Visibility.Visible;
        _calibrationCts = new CancellationTokenSource();
        var progress = new Progress<CalibrationProgress>(p =>
        {
            CalibrationBar.Value = p.FractionDone * 100;
            CalibrationText.Text = p.Text;
        });
        try
        {
            var efficient = await _service.CalibrateAsync(progress, _calibrationCts.Token);
            var quiet = FanCalibrator.ToQuiet(efficient, 80);

            foreach (var profile in efficient)
            {
                var idx = _config.Fans.FindIndex(f => f.FanId == profile.FanId);
                var current = idx >= 0 ? _config.Fans[idx] : null;
                // Standard nach der Kalibrierung: Effizient, wenn der Modus noch nicht bewusst gewählt war (Curve = Default).
                if (current is null || current.Mode == CoolingFanMode.Curve)
                {
                    if (idx >= 0) _config.Fans[idx] = profile; else _config.Fans.Add(profile);
                }
                else
                {
                    current.MeasuredMinPercent = profile.MeasuredMinPercent;
                    current.HasRpmSignal = profile.HasRpmSignal;
                    current.Calibrated = true;
                    current.LastCalibrationUtc = profile.LastCalibrationUtc;
                    if (current.Mode == CoolingFanMode.SmartEfficient) current.Curve = profile.Curve;
                    else if (current.Mode == CoolingFanMode.SmartQuiet)
                        current.Curve = quiet.First(q => q.FanId == profile.FanId).Curve;
                }
            }
            Persist();
            RefreshFans();
            LogMessage?.Invoke("Kühlung: Smart-Kalibrierung abgeschlossen.", LogLevel.Info);
        }
        catch (OperationCanceledException)
        {
            LogMessage?.Invoke("Kühlung: Kalibrierung abgebrochen.", LogLevel.Warning);
        }
        catch (Exception ex)
        {
            LogMessage?.Invoke("Kühlung: Kalibrierung fehlgeschlagen — " + ExceptionText.Describe(ex), LogLevel.Error);
            MessageBox.Show(this, "Kalibrierung fehlgeschlagen:\n" + ExceptionText.Describe(ex), "Kühlung", MessageBoxButton.OK, MessageBoxImage.Error);
        }
        finally
        {
            CalibrateButton.IsEnabled = true;
            RefreshButton.IsEnabled = true;
            CalibrationBar.Visibility = Visibility.Collapsed;
            CalibrationText.Visibility = Visibility.Collapsed;
            _calibrationCts = null;
        }
    }

    private void OnRefreshClick(object sender, RoutedEventArgs e) => RefreshFans();

    private void OnEnabledChanged(object sender, RoutedEventArgs e)
    {
        _config.Enabled = EnabledCheck.IsChecked == true;
        Persist();
        _service.Apply();
    }

    private void OnRestartElevatedClick(object sender, RoutedEventArgs e)
    {
        if (Environment.ProcessPath is not { } exe) return;
        try { Process.Start(new ProcessStartInfo(exe) { UseShellExecute = true, Verb = "runas" }); }
        catch (System.ComponentModel.Win32Exception) { return; } // UAC abgelehnt
        Application.Current.Shutdown();
    }

    private void Persist() => SaveConfig?.Invoke();
}

internal static class FanProfileExtensions
{
    public static int ManualPercentOrCurrent(this FanProfileConfig profile, int? current) =>
        current ?? profile.ManualPercent;
}
