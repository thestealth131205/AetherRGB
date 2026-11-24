using System.Globalization;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using AetherRGB.Core.Devices;
using AetherRGB.Core.Effects;
using AetherRGB.Core.Layout;
using AetherRGB.Providers.Wled;

namespace AetherRGB.App;

/// <summary>Eine platzierbare Zone in der Geräteliste rechts neben dem Effekt-Player.</summary>
public sealed class AvailableZone
{
    public required string DeviceId { get; init; }
    public required int ZoneIndex { get; init; }
    public required int LedCount { get; init; }
    public required string DisplayName { get; init; }
    public required string Vendor { get; init; }
    public string Details { get; set; } = "";
    public string Key => LedShapeStore.KeyFor(DeviceId, ZoneIndex);
}

/// <summary>Effekte-Seite im SignalRGB-Stil: mehrere Effekt-Fenster als Tabs (Plus-Knopf), pro Fenster eine
/// Fläche mit Live-Vorschau des Effekts, auf die Geräte aus der Liste rechts gezogen und dort verschoben,
/// skaliert und gedreht werden. Darunter die Eigenschaften des gewählten Geräts.</summary>
public partial class EffectsView : UserControl
{
    private const int PreviewW = 192, PreviewH = 96;

    private CanvasEffectEngine? _engine;
    private SceneCollection _scenes = new();
    private LedShapeStore _shapes = new();
    private EffectScene? _scene;
    private readonly List<DeviceVisual> _visuals = new();
    private DeviceVisual? _selected;
    private List<AvailableZone> _available = new();
    private readonly HashSet<string> _onlineKeys = new();

    private readonly WriteableBitmap _preview = new(PreviewW, PreviewH, 96, 96, PixelFormats.Bgra32, null);
    private readonly int[] _previewPixels = new int[PreviewW * PreviewH];
    private readonly DispatcherTimer _previewTimer = new() { Interval = TimeSpan.FromMilliseconds(33) };

    private bool _suppressUiEvents;
    private Point _dragStart;
    private AvailableZone? _dragCandidate;

    /// <summary>Meldung für die Log-Leiste des Hauptfensters.</summary>
    public event Action<string>? LogMessage;

    /// <summary>Vom Hauptfenster gesetzt: übernimmt WLED-Geräte in die Konfiguration und verbindet neu.</summary>
    public Func<IReadOnlyList<(string Name, string Host)>, Task>? AddWledDevicesAsync { get; set; }

    public EffectsView()
    {
        InitializeComponent();

        PreviewImage.Source = _preview;
        GridOverlay.Fill = CreateGridBrush();
        EffectCombo.ItemsSource = EffectCatalog.All;

        foreach (var box in new[] { FieldX, FieldY, FieldWidth, FieldHeight, FieldRotation, FieldDeviceBrightness, FieldLayoutBrightness })
        {
            box.LostFocus += (_, _) => ApplyFields();
            box.KeyDown += (_, e) => { if (e.Key == Key.Enter) ApplyFields(); };
        }
        PreviewKeyDown += OnPreviewKeyDown;

        _previewTimer.Tick += (_, _) => RenderPreview();
        IsVisibleChanged += (_, _) =>
        {
            if (IsVisible) _previewTimer.Start(); else _previewTimer.Stop();
        };
    }

    public void Initialize(CanvasEffectEngine engine, SceneCollection scenes, LedShapeStore shapes)
    {
        _engine = engine;
        _scenes = scenes;
        _shapes = shapes;
        BuildTabs();
        ShowScene(_scenes.Scenes.FirstOrDefault(s => s.Id == _scenes.ActiveSceneId) ?? _scenes.Scenes[0]);
    }

    /// <summary>Neue Geräteliste nach "Geräte neu suchen" — aktualisiert die Liste rechts, LED-Anzahlen und
    /// Namen bereits platzierter Geräte und dunkelt platzierte, aber nicht gefundene Geräte ab.</summary>
    public void SetDevices(IReadOnlyList<RgbDevice> devices)
    {
        _available = devices
            .SelectMany(d => d.Zones.Select(z => new AvailableZone
            {
                DeviceId = d.Id,
                ZoneIndex = z.Index,
                LedCount = z.LedCount,
                DisplayName = d.Zones.Count > 1 ? $"{d.Name} — {z.Name}" : d.Name,
                Vendor = d.Vendor.ToString()
            }))
            .ToList();

        _onlineKeys.Clear();
        foreach (var zone in _available) _onlineKeys.Add(zone.Key);

        foreach (var scene in _scenes.Scenes)
            foreach (var item in scene.Items)
                if (_available.FirstOrDefault(a => a.DeviceId == item.DeviceId && a.ZoneIndex == item.ZoneIndex) is { } zone)
                {
                    item.LedCount = zone.LedCount;
                    item.DisplayName = zone.DisplayName;
                }

        RefreshDeviceList();
        if (_scene is not null) ShowScene(_scene);
    }

    // ===================== Effekt-Fenster (Tabs) =====================

    private void BuildTabs()
    {
        SceneTabs.Children.Clear();
        foreach (var scene in _scenes.Scenes)
        {
            var content = new StackPanel { Orientation = Orientation.Horizontal };
            content.Children.Add(new TextBlock
            {
                Text = (scene.IsRunning ? "▶ " : "⏸ ") + scene.Name,
                VerticalAlignment = VerticalAlignment.Center
            });
            if (_scenes.Scenes.Count > 1)
            {
                var close = new TextBlock
                {
                    Text = "✕",
                    Margin = new Thickness(10, 0, 0, 0),
                    VerticalAlignment = VerticalAlignment.Center,
                    Foreground = (Brush)FindResource("BrushTextMuted"),
                    ToolTip = "Effekt-Fenster schließen"
                };
                close.MouseLeftButtonDown += (_, e) => { e.Handled = true; RemoveScene(scene); };
                content.Children.Add(close);
            }

            var tab = new Button
            {
                Style = (Style)FindResource("TabButton"),
                Content = content,
                Tag = scene == _scene ? "Active" : null
            };
            tab.Click += (_, _) => ShowScene(scene);
            SceneTabs.Children.Add(tab);
        }
    }

    private void OnAddSceneClick(object sender, RoutedEventArgs e)
    {
        // Plus-Menü: entweder der aktuelle Effekt läuft im neuen Fenster weiter, oder ein anderer wird gewählt.
        var menu = new ContextMenu { PlacementTarget = AddSceneButton, Placement = System.Windows.Controls.Primitives.PlacementMode.Bottom };
        var current = EffectCatalog.Get(_scene?.EffectId);
        var keep = new MenuItem { Header = $"Aktuellen Effekt weiterlaufen lassen ({current.Name})", FontWeight = FontWeights.SemiBold };
        keep.Click += (_, _) => AddScene(current.Id);
        menu.Items.Add(keep);
        menu.Items.Add(new Separator());
        menu.Items.Add(new MenuItem { Header = "Anderen Effekt wählen:", IsEnabled = false });
        foreach (var effect in EffectCatalog.All)
        {
            var entry = new MenuItem { Header = "    " + effect.Name };
            entry.Click += (_, _) => AddScene(effect.Id);
            menu.Items.Add(entry);
        }
        menu.IsOpen = true;
    }

    private void AddScene(string effectId)
    {
        int n = 1;
        while (_scenes.Scenes.Any(s => s.Name == $"Effekt {n}")) n++;
        var scene = new EffectScene
        {
            Name = $"Effekt {n}",
            EffectId = effectId,
            CanvasWidth = _scene?.CanvasWidth ?? 1600,
            CanvasHeight = _scene?.CanvasHeight ?? 800
        };
        _scenes.Scenes.Add(scene);
        ShowScene(scene);
        SaveScenes();
        LogMessage?.Invoke($"Effekt-Fenster '{scene.Name}' mit '{EffectCatalog.Get(effectId).Name}' angelegt.");
    }

    private void RemoveScene(EffectScene scene)
    {
        if (_scenes.Scenes.Count <= 1) return;
        if (scene.Items.Count > 0 && MessageBox.Show(Window.GetWindow(this),
                $"Effekt-Fenster '{scene.Name}' mit {scene.Items.Count} platzierten Gerät(en) schließen?",
                "AetherRGB", MessageBoxButton.YesNo, MessageBoxImage.Question) != MessageBoxResult.Yes)
            return;

        int index = _scenes.Scenes.IndexOf(scene);
        _scenes.Scenes.Remove(scene);
        if (scene == _scene) ShowScene(_scenes.Scenes[Math.Min(index, _scenes.Scenes.Count - 1)]);
        else BuildTabs();
        SaveScenes();
        RefreshDeviceList();
    }

    private void ShowScene(EffectScene scene)
    {
        _scene = scene;
        _scenes.ActiveSceneId = scene.Id;

        _suppressUiEvents = true;
        SceneNameBox.Text = scene.Name;
        EffectCombo.SelectedItem = EffectCatalog.Get(scene.EffectId);
        PlayPauseButton.Content = scene.IsRunning ? "⏸  Pausieren" : "▶  Abspielen";
        _suppressUiEvents = false;

        SurfaceRoot.Width = DeviceCanvas.Width = scene.CanvasWidth;
        SurfaceRoot.Height = DeviceCanvas.Height = scene.CanvasHeight;
        SurfaceRoot.Clip = new RectangleGeometry(new Rect(0, 0, scene.CanvasWidth, scene.CanvasHeight), 24, 24);

        DeviceCanvas.Children.Clear();
        _visuals.Clear();
        _selected = null;
        foreach (var item in scene.Items)
            AddVisual(item);

        EmptyHint.Visibility = scene.Items.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
        BuildTabs();
        UpdateFields();
        RenderPreview();
    }

    private void OnSceneNameChanged(object sender, TextChangedEventArgs e)
    {
        if (_suppressUiEvents || _scene is null) return;
        _scene.Name = string.IsNullOrWhiteSpace(SceneNameBox.Text) ? "Effekt" : SceneNameBox.Text.Trim();
        BuildTabs();
        SaveScenes();
        RefreshDeviceList();
    }

    private void OnEffectChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_suppressUiEvents || _scene is null || EffectCombo.SelectedItem is not EffectDescriptor effect) return;
        _scene.EffectId = effect.Id;
        SaveScenes();
        LogMessage?.Invoke($"'{_scene.Name}': Effekt '{effect.Name}'.");
    }

    private void OnPlayPauseClick(object sender, RoutedEventArgs e)
    {
        if (_scene is null) return;
        _scene.IsRunning = !_scene.IsRunning;
        PlayPauseButton.Content = _scene.IsRunning ? "⏸  Pausieren" : "▶  Abspielen";
        BuildTabs();
        SaveScenes();
        LogMessage?.Invoke($"'{_scene.Name}' {(_scene.IsRunning ? "läuft" : "pausiert")}.");
    }

    // ===================== Fläche / Geräte-Visuals =====================

    private DeviceVisual AddVisual(DeviceLayoutItem item)
    {
        var visual = new DeviceVisual(item, _shapes.Get(item.DeviceId, item.ZoneIndex, item.LedCount), DeviceCanvas);
        visual.SetOnline(_onlineKeys.Count == 0 || _onlineKeys.Contains(LedShapeStore.KeyFor(item.DeviceId, item.ZoneIndex)));
        visual.Selected += Select;
        visual.Changed += _ => UpdateFields();
        visual.DragCompleted += _ => SaveScenes();
        _visuals.Add(visual);
        DeviceCanvas.Children.Add(visual.Root);
        return visual;
    }

    private void Select(DeviceVisual? visual)
    {
        if (_selected is not null) _selected.IsSelected = false;
        _selected = visual;
        if (_selected is not null) _selected.IsSelected = true;
        Focus();
        UpdateFields();
    }

    private void OnCanvasBackgroundClick(object sender, MouseButtonEventArgs e)
    {
        if (e.OriginalSource == DeviceCanvas) Select(null);
    }

    private void OnCanvasDragOver(object sender, DragEventArgs e)
    {
        e.Effects = e.Data.GetDataPresent(typeof(AvailableZone)) ? DragDropEffects.Copy : DragDropEffects.None;
        e.Handled = true;
    }

    private void OnCanvasDrop(object sender, DragEventArgs e)
    {
        if (_scene is null || e.Data.GetData(typeof(AvailableZone)) is not AvailableZone zone) return;
        var pos = e.GetPosition(DeviceCanvas);

        var existing = _visuals.FirstOrDefault(v => v.Item.DeviceId == zone.DeviceId && v.Item.ZoneIndex == zone.ZoneIndex);
        if (existing is not null)
        {
            existing.Item.CenterX = pos.X;
            existing.Item.CenterY = pos.Y;
            existing.UpdateGeometry();
            Select(existing);
            SaveScenes();
            return;
        }

        // Eine LED kann nur eine Farbe zeigen — liegt die Zone schon in einem anderen Fenster, wird sie verschoben.
        foreach (var other in _scenes.Scenes.Where(s => s != _scene))
            if (other.Items.RemoveAll(i => i.DeviceId == zone.DeviceId && i.ZoneIndex == zone.ZoneIndex) > 0)
                LogMessage?.Invoke($"'{zone.DisplayName}' aus '{other.Name}' nach '{_scene.Name}' verschoben.");

        var shape = _shapes.Get(zone.DeviceId, zone.ZoneIndex, zone.LedCount);
        var (w, h) = DefaultSize(shape);
        var item = new DeviceLayoutItem
        {
            DeviceId = zone.DeviceId,
            ZoneIndex = zone.ZoneIndex,
            LedCount = zone.LedCount,
            DisplayName = zone.DisplayName,
            CenterX = pos.X,
            CenterY = pos.Y,
            Width = w,
            Height = h
        };
        _scene.Items.Add(item);
        EmptyHint.Visibility = Visibility.Collapsed;
        Select(AddVisual(item));
        SaveScenes();
        RefreshDeviceList();
    }

    private static (double W, double H) DefaultSize(LedShape shape) => shape.Kind switch
    {
        LedShapeKind.Line => (Math.Clamp(shape.LedCount * 30, 120, 1200), 50),
        LedShapeKind.Custom => shape.AspectRatio >= 1
            ? (Math.Min(600, 220 * shape.AspectRatio), 220)
            : (220, Math.Min(600, 220 / shape.AspectRatio)),
        _ => (220, 220)
    };

    // ===================== Eigenschaften des gewählten Geräts =====================

    private void UpdateFields()
    {
        _suppressUiEvents = true;
        var item = _selected?.Item;
        SelectedTitle.Text = item?.DisplayName ?? "Kein Gerät ausgewählt";
        foreach (var b in new[] { ShapeButton, CenterButton, ResetRotationButton, RemoveButton }) b.IsEnabled = item is not null;
        foreach (var box in new[] { FieldX, FieldY, FieldWidth, FieldHeight, FieldRotation, FieldDeviceBrightness }) box.IsEnabled = item is not null;

        FieldX.Text = item is null ? "-" : Fmt(item.CenterX);
        FieldY.Text = item is null ? "-" : Fmt(item.CenterY);
        FieldWidth.Text = item is null ? "-" : Fmt(item.Width);
        FieldHeight.Text = item is null ? "-" : Fmt(item.Height);
        FieldRotation.Text = item is null ? "-" : Fmt(item.RotationDegrees);
        FieldDeviceBrightness.Text = item is null ? "-" : item.Brightness.ToString();
        FieldLayoutBrightness.Text = _scene?.Brightness.ToString() ?? "100";
        _suppressUiEvents = false;
    }

    private static string Fmt(double v) => Math.Round(v).ToString(CultureInfo.InvariantCulture);

    private static double? Parse(string text) =>
        double.TryParse(text.Replace(',', '.'), NumberStyles.Float, CultureInfo.InvariantCulture, out var v) ? v : null;

    private void ApplyFields()
    {
        if (_suppressUiEvents || _scene is null) return;

        if (Parse(FieldLayoutBrightness.Text) is { } lb) _scene.Brightness = (int)Math.Clamp(lb, 0, 100);

        if (_selected?.Item is { } item)
        {
            if (Parse(FieldX.Text) is { } x) item.CenterX = Math.Clamp(x, 0, _scene.CanvasWidth);
            if (Parse(FieldY.Text) is { } y) item.CenterY = Math.Clamp(y, 0, _scene.CanvasHeight);
            if (Parse(FieldWidth.Text) is { } w) item.Width = Math.Max(20, w);
            if (Parse(FieldHeight.Text) is { } h) item.Height = Math.Max(20, h);
            if (Parse(FieldRotation.Text) is { } r) item.RotationDegrees = DeviceVisual.NormalizeAngle(r);
            if (Parse(FieldDeviceBrightness.Text) is { } db) item.Brightness = (int)Math.Clamp(db, 0, 100);
            _selected.UpdateGeometry();
        }

        UpdateFields();
        SaveScenes();
    }

    private void OnCenterClick(object sender, RoutedEventArgs e)
    {
        if (_selected is null || _scene is null) return;
        _selected.Item.CenterX = _scene.CanvasWidth / 2;
        _selected.Item.CenterY = _scene.CanvasHeight / 2;
        _selected.UpdateGeometry();
        UpdateFields();
        SaveScenes();
    }

    private void OnResetRotationClick(object sender, RoutedEventArgs e)
    {
        if (_selected is null) return;
        _selected.Item.RotationDegrees = 0;
        _selected.UpdateGeometry();
        UpdateFields();
        SaveScenes();
    }

    private void OnRemoveClick(object sender, RoutedEventArgs e) => RemoveSelected();

    private void RemoveSelected()
    {
        if (_selected is null || _scene is null) return;
        _scene.Items.Remove(_selected.Item);
        DeviceCanvas.Children.Remove(_selected.Root);
        _visuals.Remove(_selected);
        _selected = null;
        EmptyHint.Visibility = _scene.Items.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
        UpdateFields();
        SaveScenes();
        RefreshDeviceList();
    }

    private void OnPreviewKeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.Delete && Keyboard.FocusedElement is not TextBox && _selected is not null)
        {
            RemoveSelected();
            e.Handled = true;
        }
    }

    // ===================== LED-Anordnung =====================

    private void OnEditShapeClick(object sender, RoutedEventArgs e)
    {
        if (_selected?.Item is { } item) EditShape(item.DeviceId, item.ZoneIndex, item.LedCount, item.DisplayName);
    }

    private void OnDeviceListDoubleClick(object sender, MouseButtonEventArgs e)
    {
        if (DeviceListBox.SelectedItem is AvailableZone zone) EditShape(zone.DeviceId, zone.ZoneIndex, zone.LedCount, zone.DisplayName);
    }

    private void EditShape(string deviceId, int zoneIndex, int reportedCount, string name)
    {
        var current = _shapes.Get(deviceId, zoneIndex, reportedCount);
        var editor = new LedShapeEditorWindow(name, reportedCount, current) { Owner = Window.GetWindow(this) };
        if (editor.ShowDialog() != true || editor.Result is not { } shape) return;

        _shapes.Set(deviceId, zoneIndex, shape);
        try { _shapes.Save(); }
        catch (Exception ex) { LogMessage?.Invoke("LED-Anordnung konnte nicht gespeichert werden: " + ex.Message); }

        // Andere Grundform -> Rechteck in allen Fenstern auf eine passende Startgröße setzen, sonst wird
        // z. B. ein Kreis in einen flachen Streifen gequetscht.
        if (shape.Kind != current.Kind || shape.Kind == LedShapeKind.Custom)
        {
            var (w, h) = DefaultSize(shape);
            foreach (var item in _scenes.Scenes.SelectMany(s => s.Items).Where(i => i.DeviceId == deviceId && i.ZoneIndex == zoneIndex))
            {
                item.Width = w;
                item.Height = h;
            }
            SaveScenes();
        }

        foreach (var visual in _visuals.Where(v => v.Item.DeviceId == deviceId && v.Item.ZoneIndex == zoneIndex))
        {
            visual.SetShape(shape);
            visual.UpdateGeometry();
        }
        UpdateFields();
        RefreshDeviceList();
        LogMessage?.Invoke($"LED-Anordnung für '{name}' gespeichert: {ShapeName(shape)}.");
    }

    private static string ShapeName(LedShape shape) => shape.Kind switch
    {
        LedShapeKind.Circle => $"Kreis, {shape.LedCount} LEDs",
        LedShapeKind.Square => $"Quadrat, {shape.LedCount} LEDs",
        LedShapeKind.Custom => $"frei, {shape.LedCount} LEDs",
        _ => $"Schnur, {shape.LedCount} LEDs"
    };

    // ===================== Geräteliste + Drag & Drop =====================

    private void RefreshDeviceList()
    {
        var selectedKey = (DeviceListBox.SelectedItem as AvailableZone)?.Key;
        foreach (var zone in _available)
        {
            var shape = _shapes.Get(zone.DeviceId, zone.ZoneIndex, zone.LedCount);
            var placedIn = _scenes.Scenes.FirstOrDefault(s => s.Items.Any(i => i.DeviceId == zone.DeviceId && i.ZoneIndex == zone.ZoneIndex));
            zone.Details = $"{zone.Vendor} · {(_shapes.HasCustom(zone.DeviceId, zone.ZoneIndex) ? ShapeName(shape) : $"{zone.LedCount} LEDs")}"
                           + (placedIn is null ? "" : $" · in „{placedIn.Name}“");
        }
        DeviceListBox.ItemsSource = null;
        DeviceListBox.ItemsSource = _available;
        DeviceListBox.SelectedItem = _available.FirstOrDefault(z => z.Key == selectedKey);
    }

    private void OnDeviceListMouseDown(object sender, MouseButtonEventArgs e)
    {
        _dragStart = e.GetPosition(DeviceListBox);
        var container = ItemsControl.ContainerFromElement(DeviceListBox, (DependencyObject)e.OriginalSource) as ListBoxItem;
        _dragCandidate = container?.DataContext as AvailableZone;
    }

    private void OnDeviceListMouseMove(object sender, MouseEventArgs e)
    {
        if (_dragCandidate is null || e.LeftButton != MouseButtonState.Pressed) return;
        var diff = e.GetPosition(DeviceListBox) - _dragStart;
        if (Math.Abs(diff.X) < SystemParameters.MinimumHorizontalDragDistance &&
            Math.Abs(diff.Y) < SystemParameters.MinimumVerticalDragDistance) return;

        var zone = _dragCandidate;
        _dragCandidate = null;
        DragDrop.DoDragDrop(DeviceListBox, new DataObject(typeof(AvailableZone), zone), DragDropEffects.Copy);
    }

    // ===================== WLED =====================

    private async void OnWledAddClick(object sender, RoutedEventArgs e)
    {
        var host = WledHostBox.Text.Trim();
        if (string.IsNullOrWhiteSpace(host))
        {
            WledHintText.Text = "Bitte eine IP-Adresse oder einen Hostnamen eintragen.";
            return;
        }
        var name = string.IsNullOrWhiteSpace(WledNameBox.Text) ? host : WledNameBox.Text.Trim();
        if (AddWledDevicesAsync is null) return;

        WledHintText.Text = $"Verbinde mit {host} …";
        await AddWledDevicesAsync(new[] { (name, host) });
        WledNameBox.Text = WledHostBox.Text = "";
        WledHintText.Text = _available.Any(z => z.DeviceId == $"wled:{host}")
            ? $"'{name}' hinzugefügt — jetzt auf die Fläche ziehen."
            : $"'{name}' gespeichert, aber unter {host} antwortet (noch) kein WLED.";
    }

    private async void OnWledDiscoverClick(object sender, RoutedEventArgs e)
    {
        WledDiscoverButton.IsEnabled = false;
        WledHintText.Text = "Suche WLED-Geräte im lokalen Netz …";
        try
        {
            var found = await WledDiscovery.DiscoverAsync(TimeSpan.FromSeconds(3));
            var fresh = found.Where(f => !_available.Any(z => z.DeviceId == $"wled:{f.Host}")).ToList();
            if (fresh.Count > 0 && AddWledDevicesAsync is not null)
                await AddWledDevicesAsync(fresh.Select(f => (f.Name, f.Host)).ToList());

            WledHintText.Text = found.Count == 0
                ? "Keine WLED-Geräte gefunden. Ggf. per IP hinzufügen (mDNS wird manchmal von Router/Firewall blockiert)."
                : $"{found.Count} WLED-Gerät(e) gefunden, {fresh.Count} neu hinzugefügt.";
        }
        catch (Exception ex)
        {
            WledHintText.Text = "Suche fehlgeschlagen: " + ex.Message;
        }
        finally
        {
            WledDiscoverButton.IsEnabled = true;
        }
    }

    // ===================== Live-Vorschau =====================

    private void RenderPreview()
    {
        if (_scene is null || _engine is null) return;
        var effect = _scene.Effect;
        var elapsed = _engine.Elapsed;
        // Pausierte Fenster gedimmt anzeigen, damit sofort sichtbar ist, was gerade wirklich an die LEDs geht.
        double factor = Math.Clamp(_scene.Brightness, 0, 100) / 100.0 * (_scene.IsRunning ? 1.0 : 0.35);
        double sx = _scene.CanvasWidth / PreviewW, sy = _scene.CanvasHeight / PreviewH;

        for (int py = 0; py < PreviewH; py++)
            for (int px = 0; px < PreviewW; px++)
            {
                var c = effect.Sample((px + 0.5) * sx, (py + 0.5) * sy, _scene.CanvasWidth, _scene.CanvasHeight, elapsed);
                _previewPixels[py * PreviewW + px] = unchecked((int)0xFF000000)
                                                     | ((int)(c.R * factor) << 16) | ((int)(c.G * factor) << 8) | (int)(c.B * factor);
            }
        _preview.WritePixels(new Int32Rect(0, 0, PreviewW, PreviewH), _previewPixels, PreviewW * 4, 0);
    }

    /// <summary>Feines dunkles Raster über der Vorschau, wie im SignalRGB-Layout.</summary>
    private static Brush CreateGridBrush()
    {
        var pen = new Pen(new SolidColorBrush(Color.FromArgb(0x38, 0, 0, 0)), 1);
        var geometry = new GeometryGroup();
        geometry.Children.Add(new LineGeometry(new Point(0, 0), new Point(20, 0)));
        geometry.Children.Add(new LineGeometry(new Point(0, 0), new Point(0, 20)));
        var brush = new DrawingBrush(new GeometryDrawing(null, pen, geometry))
        {
            TileMode = TileMode.Tile,
            Viewport = new Rect(0, 0, 20, 20),
            ViewportUnits = BrushMappingMode.Absolute,
            Viewbox = new Rect(0, 0, 20, 20),
            ViewboxUnits = BrushMappingMode.Absolute
        };
        brush.Freeze();
        return brush;
    }

    private void SaveScenes()
    {
        try { _scenes.Save(); }
        catch (Exception ex) { LogMessage?.Invoke("Effekt-Fenster konnten nicht gespeichert werden: " + ex.Message); }
    }
}
