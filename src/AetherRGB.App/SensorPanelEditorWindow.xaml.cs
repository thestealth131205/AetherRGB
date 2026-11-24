using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Globalization;
using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Data;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Shapes;
using System.Windows.Threading;
using AetherRGB.Core.Config;
using AetherRGB.Core.Devices;
using AetherRGB.Core.Diagnostics;
using AetherRGB.Core.Effects;
using AetherRGB.Core.Panels;
using AetherRGB.Core.Sensors;
using Microsoft.Win32;
using Forms = System.Windows.Forms;

namespace AetherRGB.App.Panels;

/// <summary>Editor für Sensor-Panels (AIDA64-SensorPanel-Stil): Elemente auf die Fläche setzen, verschieben, in der Größe
/// ziehen, Sensoren aus der Liste per Drag &amp; Drop zuordnen, Eigenschaften rechts. Die Fläche zeigt live die echten Werte
/// und läuft auf Wunsch gleichzeitig auf dem Zweitdisplay.</summary>
public partial class SensorPanelEditorWindow : Window
{
    private readonly SensorPanelService _service;
    private readonly SensorPanelConfig _config;
    private readonly SensorHub _hub = SensorHub.Shared;
    private readonly SensorPanelSurface _surface;
    private readonly DispatcherTimer _frameTimer = new(DispatcherPriority.Render) { Interval = TimeSpan.FromMilliseconds(40) };
    private readonly DispatcherTimer _sensorTimer = new() { Interval = TimeSpan.FromSeconds(1) };

    public event Action<string, LogLevel>? LogMessage;

    private SensorPanelDocument _doc = new();
    private PanelItem? _selected;
    private bool _dirty, _suppress;

    private readonly List<string> _undo = new(), _redo = new();
    private const int MaxUndo = 100;
    private PanelItem? _clipboard;

    private enum Drag { None, Move, Resize }
    private Drag _drag;
    private Point _dragStart;
    private Rect _dragOrigin;
    private bool _dragCheckpointed;
    private Point _sensorDragStart;

    private readonly ObservableCollection<SensorRow> _sensorRows = new();
    private readonly ICollectionView _sensorView;
    private readonly HashSet<string> _knownSensorIds = new();
    private readonly Dictionary<string, int> _missing = new();

    private static readonly (string Label, int W, int H)[] SizePresets =
    {
        ("1024 × 600 (5\" USB-Display)", 1024, 600),
        ("600 × 1024 (5\", hochkant)", 600, 1024),
        ("800 × 480 (5\"/7\")", 800, 480),
        ("480 × 320 (3,5\")", 480, 320),
        ("320 × 480 (3,5\", hochkant)", 320, 480),
        ("1280 × 480 (8,8\" Streifen)", 1280, 480),
        ("1920 × 480 (Streifen)", 1920, 480),
        ("1920 × 1080 (Full HD)", 1920, 1080),
    };

    private static readonly int[] GridSizes = { 2, 4, 8, 10, 16, 20 };

    public SensorPanelEditorWindow(SensorPanelService service, SensorPanelConfig config)
    {
        _service = service;
        _config = config;
        InitializeComponent();

        _surface = new SensorPanelSurface(new SensorPanelRenderer(_hub, service.Store.ResolveAsset));
        SurfaceHost.Content = _surface;

        SizeCombo.Items.Add("Eigene Größe");
        foreach (var p in SizePresets) SizeCombo.Items.Add(p.Label);
        foreach (var g in GridSizes) GridCombo.Items.Add(g + " px");
        GridCombo.SelectedIndex = 1;

        _sensorView = CollectionViewSource.GetDefaultView(_sensorRows);
        _sensorView.GroupDescriptions.Add(new PropertyGroupDescription(nameof(SensorRow.Group)));
        _sensorView.SortDescriptions.Add(new SortDescription(nameof(SensorRow.GroupRank), ListSortDirection.Ascending));
        _sensorView.SortDescriptions.Add(new SortDescription(nameof(SensorRow.Group), ListSortDirection.Ascending));
        _sensorView.SortDescriptions.Add(new SortDescription(nameof(SensorRow.SortKey), ListSortDirection.Ascending));
        _sensorView.Filter = FilterSensor;
        SensorList.ItemsSource = _sensorView;

        _frameTimer.Tick += (_, _) => { _hub.Demand(); _surface.InvalidateVisual(); };
        _sensorTimer.Tick += (_, _) => RefreshSensors();
        _frameTimer.Start();
        _sensorTimer.Start();
        _hub.Demand();

        LiveToggle.IsChecked = false;
        _service.Store.Changed += OnStoreChanged;
        Closed += (_, _) =>
        {
            _frameTimer.Stop();
            _sensorTimer.Stop();
            _service.Store.Changed -= OnStoreChanged;
        };
        Closing += OnWindowClosing;

        ReloadPanelCombo(config.PanelId);
        RefreshSensors();
    }

    private void OnStoreChanged() => Dispatcher.BeginInvoke(() =>
    {
        // Liste aktualisieren, das geöffnete Panel bleibt (auch ungespeichert) erhalten.
        _suppress = true;
        PanelCombo.ItemsSource = _service.Store.Panels;
        PanelCombo.SelectedItem = _service.Store.Panels.FirstOrDefault(p => p.Id == _doc.Id);
        _suppress = false;
    });

    // ===================== Panels laden/speichern =====================

    private void ReloadPanelCombo(string? selectId)
    {
        _suppress = true;
        var panels = _service.Store.Panels;
        PanelCombo.ItemsSource = panels;
        var pick = panels.FirstOrDefault(p => p.Id == selectId) ?? panels.FirstOrDefault();
        PanelCombo.SelectedItem = pick;
        _suppress = false;
        LoadDocument(pick?.Clone() ?? SensorPanelTemplates.Overview());
    }

    private void LoadDocument(SensorPanelDocument doc)
    {
        _doc = doc;
        _dirty = false;
        _undo.Clear();
        _redo.Clear();
        _selected = null;
        _surface.Document = _doc;
        ResetTemplateButton.IsEnabled = SensorPanelTemplates.Get(_doc.Id) is not null;
        if (LiveToggle.IsChecked == true) _service.SetLivePreview(_doc);
        RefreshDocumentFields();
        RefreshItemList();
        BuildProperties();
        RedrawOverlay();
        UpdateTitle();
    }

    private void RefreshDocumentFields()
    {
        _suppress = true;
        WidthBox.Text = _doc.Width.ToString(CultureInfo.InvariantCulture);
        HeightBox.Text = _doc.Height.ToString(CultureInfo.InvariantCulture);
        int preset = Array.FindIndex(SizePresets, p => p.W == _doc.Width && p.H == _doc.Height);
        SizeCombo.SelectedIndex = preset + 1;
        BackgroundColorHost.Content = ColorSwatch(() => _doc.Background, c => { Checkpoint(); _doc.Background = c; Changed(); });
        _suppress = false;
        CanvasRoot.Width = _doc.Width;
        CanvasRoot.Height = _doc.Height;
        Overlay.Width = _doc.Width;
        Overlay.Height = _doc.Height;
    }

    private void UpdateTitle() => Title = $"AetherRGB — Sensor-Panel-Editor: {_doc.Name}{(_dirty ? " •" : "")}";

    private bool ConfirmDiscard()
    {
        if (!_dirty) return true;
        var answer = MessageBox.Show(this, $"Änderungen an „{_doc.Name}“ speichern?", "Sensor-Panel", MessageBoxButton.YesNoCancel, MessageBoxImage.Question);
        if (answer == MessageBoxResult.Cancel) return false;
        if (answer == MessageBoxResult.Yes) Save();
        return true;
    }

    private void OnPanelComboChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_suppress || PanelCombo.SelectedItem is not SensorPanelDocument picked || picked.Id == _doc.Id) return;
        if (!ConfirmDiscard())
        {
            _suppress = true;
            PanelCombo.SelectedItem = _service.Store.Panels.FirstOrDefault(p => p.Id == _doc.Id);
            _suppress = false;
            return;
        }
        LoadDocument(picked.Clone());
    }

    private void OnNewClick(object sender, RoutedEventArgs e)
    {
        if (!ConfirmDiscard()) return;
        var doc = new SensorPanelDocument { Name = "Neues Panel", Width = _doc.Width, Height = _doc.Height };
        _service.Store.Save(doc);
        ReloadPanelCombo(doc.Id);
    }

    private void OnCopyClick(object sender, RoutedEventArgs e)
    {
        var copy = _doc.Clone();
        copy.Id = Guid.NewGuid().ToString("N")[..12];
        copy.Name = _doc.Name + " (Kopie)";
        if (!ConfirmDiscard()) return;
        _service.Store.Save(copy);
        ReloadPanelCombo(copy.Id);
    }

    private void OnDeleteClick(object sender, RoutedEventArgs e)
    {
        if (MessageBox.Show(this, $"Panel „{_doc.Name}“ löschen?", "Sensor-Panel", MessageBoxButton.YesNo, MessageBoxImage.Warning) != MessageBoxResult.Yes) return;
        _service.Store.Delete(_doc.Id);
        _dirty = false;
        if (_service.Store.Panels.Count == 0) _service.Store.Save(SensorPanelTemplates.Overview());
        ReloadPanelCombo(null);
    }

    private void OnResetTemplateClick(object sender, RoutedEventArgs e)
    {
        var template = SensorPanelTemplates.Get(_doc.Id);
        if (template is null) return;
        if (MessageBox.Show(this, "Alle Änderungen an diesem Panel verwerfen und die mitgelieferte Vorlage wiederherstellen?", "Sensor-Panel",
                MessageBoxButton.YesNo, MessageBoxImage.Question) != MessageBoxResult.Yes) return;
        Checkpoint();
        template.Name = _doc.Name;
        _doc.Items = template.Items;
        _doc.Width = template.Width;
        _doc.Height = template.Height;
        _doc.Background = template.Background;
        _selected = null;
        RefreshDocumentFields();
        StructureChanged();
    }

    private void OnSaveClick(object sender, RoutedEventArgs e) => Save();

    private void Save()
    {
        try
        {
            _service.Store.Save(_doc);
            _dirty = false;
            UpdateTitle();
            Log($"Sensor-Panel „{_doc.Name}“ gespeichert.");
        }
        catch (Exception ex)
        {
            Log("Sensor-Panel konnte nicht gespeichert werden: " + ExceptionText.Describe(ex), LogLevel.Error);
            MessageBox.Show(this, "Speichern fehlgeschlagen:\n\n" + ex.Message, "Sensor-Panel", MessageBoxButton.OK, MessageBoxImage.Error);
        }
    }

    private void OnImportClick(object sender, RoutedEventArgs e)
    {
        var dialog = new OpenFileDialog { Filter = "AetherRGB-Sensor-Panel (*.json)|*.json|Alle Dateien|*.*", Title = "Sensor-Panel importieren" };
        if (dialog.ShowDialog(this) != true) return;
        try
        {
            if (new FileInfo(dialog.FileName).Length > SensorPanelStore.MaxFileBytes) throw new InvalidDataException("Datei größer als 2 MB.");
            var doc = SensorPanelSerializer.Deserialize(File.ReadAllText(dialog.FileName));
            if (_service.Store.Panels.Any(p => p.Id == doc.Id)) doc.Id = Guid.NewGuid().ToString("N")[..12];
            if (!ConfirmDiscard()) return;
            _service.Store.Save(doc);
            ReloadPanelCombo(doc.Id);
            Log($"Sensor-Panel „{doc.Name}“ importiert.");
        }
        catch (Exception ex)
        {
            MessageBox.Show(this, "Die Datei ist kein gültiges Sensor-Panel:\n\n" + ex.Message, "Sensor-Panel", MessageBoxButton.OK, MessageBoxImage.Warning);
        }
    }

    private void OnExportClick(object sender, RoutedEventArgs e)
    {
        var dialog = new SaveFileDialog { Filter = "AetherRGB-Sensor-Panel (*.json)|*.json", FileName = SafeFileName(_doc.Name) + ".json", Title = "Sensor-Panel exportieren" };
        if (dialog.ShowDialog(this) != true) return;
        try
        {
            File.WriteAllText(dialog.FileName, SensorPanelSerializer.Serialize(_doc));
            Log("Sensor-Panel exportiert: " + dialog.FileName + " (Bilder liegen getrennt in " + _service.Store.AssetDirectory + ")");
        }
        catch (Exception ex) { MessageBox.Show(this, "Export fehlgeschlagen:\n\n" + ex.Message, "Sensor-Panel", MessageBoxButton.OK, MessageBoxImage.Error); }
    }

    private static string SafeFileName(string name) =>
        string.Concat(name.Select(c => System.IO.Path.GetInvalidFileNameChars().Contains(c) ? '_' : c)).Trim() is { Length: > 0 } s ? s : "panel";

    private void OnLiveChanged(object sender, RoutedEventArgs e)
    {
        if (LiveToggle.IsChecked == true)
        {
            _service.SetLivePreview(_doc);
            if (!_service.IsDisplayRunning)
                MessageBox.Show(this, _service.Config.DisplayEnabled
                        ? "Das Zweitdisplay wurde nicht gefunden. Ist es angeschlossen und in den Windows-Anzeigeeinstellungen auf „Erweitern“ gestellt?"
                        : "Die Anzeige auf dem Zweitdisplay ist ausgeschaltet. Mit „📟 Auf dem Display verwenden“ oder in den Einstellungen unter „Sensor-Panel“ einschalten.",
                    "Sensor-Panel", MessageBoxButton.OK, MessageBoxImage.Information);
        }
        else _service.SetLivePreview(null);
    }

    private void OnUseOnDisplayClick(object sender, RoutedEventArgs e)
    {
        Save();
        if (_dirty) return;
        _service.UsePanel(_doc.Id, enableDisplay: true);
        if (LiveToggle.IsChecked == true) _service.SetLivePreview(_doc);
        if (!_service.IsDisplayRunning)
            MessageBox.Show(this, "Kein Zweitdisplay gefunden. Ist es angeschlossen und in den Windows-Anzeigeeinstellungen auf „Erweitern“ gestellt? Den Monitor kannst du in den Einstellungen unter „Sensor-Panel“ auch fest wählen.",
                "Sensor-Panel", MessageBoxButton.OK, MessageBoxImage.Information);
    }

    // ===================== Größe, Hintergrund =====================

    private void OnSizePresetChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_suppress || SizeCombo.SelectedIndex <= 0) return;
        var p = SizePresets[SizeCombo.SelectedIndex - 1];
        SetSize(p.W, p.H);
    }

    private void OnSizeBoxKey(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.Enter) OnSizeBoxCommit(sender, e);
    }

    private void OnSizeBoxCommit(object sender, RoutedEventArgs e)
    {
        if (_suppress) return;
        if (int.TryParse(WidthBox.Text, out int w) && int.TryParse(HeightBox.Text, out int h)) SetSize(w, h);
        else RefreshDocumentFields();
    }

    private void SetSize(int w, int h)
    {
        w = Math.Clamp(w, SensorPanelSerializer.MinSize, SensorPanelSerializer.MaxSize);
        h = Math.Clamp(h, SensorPanelSerializer.MinSize, SensorPanelSerializer.MaxSize);
        if (w == _doc.Width && h == _doc.Height) { RefreshDocumentFields(); return; }
        bool scale = _doc.Items.Count > 0 && MessageBox.Show(this,
            $"Elemente mit auf {w} × {h} skalieren?\n\nJa = alles wird mitskaliert, Nein = Elemente behalten Lage und Größe.",
            "Panel-Größe", MessageBoxButton.YesNo, MessageBoxImage.Question) == MessageBoxResult.Yes;
        Checkpoint();
        if (scale)
        {
            double sx = (double)w / _doc.Width, sy = (double)h / _doc.Height, sf = Math.Min(sx, sy);
            foreach (var i in _doc.Items)
            {
                i.X *= sx; i.Y *= sy; i.W *= sx; i.H *= sy;
                i.FontSize = Math.Max(4, i.FontSize * sf);
                i.Thickness = Math.Max(1, i.Thickness * sf);
            }
        }
        _doc.Width = w;
        _doc.Height = h;
        RefreshDocumentFields();
        _surface.Document = _doc;
        StructureChanged();
    }

    private void OnBackgroundImageClick(object sender, RoutedEventArgs e)
    {
        var name = PickImage();
        if (name is null) return;
        Checkpoint();
        _doc.BackgroundImage = name;
        Changed();
    }

    private void OnBackgroundImageClearClick(object sender, RoutedEventArgs e)
    {
        if (_doc.BackgroundImage.Length == 0) return;
        Checkpoint();
        _doc.BackgroundImage = "";
        Changed();
    }

    private string? PickImage()
    {
        var dialog = new OpenFileDialog { Filter = "Bilder (*.png;*.jpg;*.jpeg;*.bmp;*.gif)|*.png;*.jpg;*.jpeg;*.bmp;*.gif", Title = "Bild wählen" };
        if (dialog.ShowDialog(this) != true) return null;
        try
        {
            var name = _service.Store.ImportAsset(dialog.FileName);
            _surface.Renderer.ForgetImages();
            _service.DisplayRenderer.ForgetImages();
            return name;
        }
        catch (Exception ex)
        {
            MessageBox.Show(this, "Bild konnte nicht übernommen werden:\n\n" + ex.Message, "Sensor-Panel", MessageBoxButton.OK, MessageBoxImage.Warning);
            return null;
        }
    }

    // ===================== Änderungen, Rückgängig =====================

    /// <summary>Vor jeder Änderung: Stand für Strg+Z merken.</summary>
    private void Checkpoint()
    {
        _undo.Add(SensorPanelSerializer.Serialize(_doc));
        if (_undo.Count > MaxUndo) _undo.RemoveAt(0);
        _redo.Clear();
    }

    private void Changed()
    {
        _dirty = true;
        UpdateTitle();
        _surface.InvalidateVisual();
        RedrawOverlay();
    }

    private void StructureChanged()
    {
        Changed();
        RefreshItemList();
        BuildProperties();
    }

    private void Undo() => Restore(_undo, _redo);
    private void Redo() => Restore(_redo, _undo);

    private void Restore(List<string> from, List<string> to)
    {
        if (from.Count == 0) return;
        to.Add(SensorPanelSerializer.Serialize(_doc));
        var json = from[^1];
        from.RemoveAt(from.Count - 1);
        string? selectedId = _selected?.Id;
        var restored = SensorPanelSerializer.Deserialize(json);
        // Gleiche Instanz behalten (Live-Vorschau zeigt sie), nur den Inhalt ersetzen.
        _doc.Name = restored.Name;
        _doc.Width = restored.Width;
        _doc.Height = restored.Height;
        _doc.Background = restored.Background;
        _doc.BackgroundImage = restored.BackgroundImage;
        _doc.Items = restored.Items;
        _selected = _doc.Items.FirstOrDefault(i => i.Id == selectedId);
        RefreshDocumentFields();
        _surface.Document = _doc;
        StructureChanged();
    }

    // ===================== Elemente =====================

    private void OnAddClick(object sender, RoutedEventArgs e)
    {
        if (sender is not Button { Tag: string tag } || !Enum.TryParse<PanelItemKind>(tag, out var kind)) return;
        var item = CreateItem(kind, SelectedSensorRow()?.Id);
        if (kind == PanelItemKind.Image)
        {
            var name = PickImage();
            if (name is null) return;
            item.ImagePath = name;
        }
        item.X = Math.Round((_doc.Width - item.W) / 2);
        item.Y = Math.Round((_doc.Height - item.H) / 2);
        AddItem(item);
    }

    private void AddItem(PanelItem item)
    {
        Checkpoint();
        _doc.Items.Add(item);
        _selected = item;
        StructureChanged();
    }

    private PanelItem CreateItem(PanelItemKind kind, string? sensorId)
    {
        var reading = _hub.Get(sensorId);
        var item = new PanelItem { Kind = kind, Name = PanelItem.KindName(kind) };
        switch (kind)
        {
            case PanelItemKind.Text:
                item.Text = "Text"; item.W = 200; item.H = 40; break;
            case PanelItemKind.Value:
                item.W = 260; item.H = 56; item.FontSize = 26; item.Bold = true; break;
            case PanelItemKind.Bar:
                item.W = 360; item.H = 40; item.FontSize = 16; item.BackColor = new(42, 43, 53); item.CornerRadius = 4; item.Decimals = 0; break;
            case PanelItemKind.Gauge:
                item.W = 240; item.H = 170; item.FontSize = 40; item.Bold = true; item.Thickness = 16; item.Decimals = 0; break;
            case PanelItemKind.Graph:
                item.W = 420; item.H = 120; item.FontSize = 14; item.Thickness = 2; item.BackColor = new(12, 13, 17); item.CornerRadius = 8; break;
            case PanelItemKind.Clock:
                item.Text = "HH:mm:ss"; item.W = 260; item.H = 60; item.FontSize = 42; item.Bold = true; break;
            case PanelItemKind.Animation:
                item.W = 320; item.H = 180; item.CornerRadius = 10; break;
            case PanelItemKind.Image:
                item.W = 240; item.H = 160; item.CornerRadius = 0; break;
            case PanelItemKind.Box:
                item.W = 320; item.H = 200; item.BackColor = new(21, 22, 28); item.BorderColor = new(42, 43, 53); item.BorderWidth = 1; item.CornerRadius = 12; break;
        }
        if (UsesSensor(kind))
        {
            item.SensorId = sensorId ?? SensorPanelTemplates.CpuLoadIds;
            item.Name = reading?.Name ?? (sensorId is null ? "CPU-Last" : item.Name);
            if (reading?.Unit == "%") { item.WarnAt = 80; item.CritAt = 95; }
        }
        return item;
    }

    private static bool UsesSensor(PanelItemKind k) => k is PanelItemKind.Value or PanelItemKind.Bar or PanelItemKind.Gauge or PanelItemKind.Graph;
    private static bool HasText(PanelItemKind k) => k is PanelItemKind.Text or PanelItemKind.Value or PanelItemKind.Bar or PanelItemKind.Gauge or PanelItemKind.Graph or PanelItemKind.Clock;

    private void Select(PanelItem? item)
    {
        _selected = item;
        _suppress = true;
        ItemList.SelectedItem = item;
        _suppress = false;
        if (item is not null) ItemList.ScrollIntoView(item);
        BuildProperties();
        RedrawOverlay();
    }

    private void RefreshItemList()
    {
        _suppress = true;
        ItemList.ItemsSource = null;
        ItemList.ItemsSource = _doc.Items.AsEnumerable().Reverse().ToList(); // oben in der Liste = vorn
        ItemList.SelectedItem = _selected;
        _suppress = false;
    }

    private void OnItemSelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_suppress) return;
        Select(ItemList.SelectedItem as PanelItem);
    }

    private void MoveSelected(int delta)
    {
        if (_selected is null) return;
        int i = _doc.Items.IndexOf(_selected), j = Math.Clamp(i + delta, 0, _doc.Items.Count - 1);
        if (i == j) return;
        Checkpoint();
        _doc.Items.RemoveAt(i);
        _doc.Items.Insert(j, _selected);
        StructureChanged();
    }

    private void OnItemUpClick(object sender, RoutedEventArgs e) => MoveSelected(+1);
    private void OnItemDownClick(object sender, RoutedEventArgs e) => MoveSelected(-1);
    private void OnItemDuplicateClick(object sender, RoutedEventArgs e) => DuplicateSelected();
    private void OnItemDeleteClick(object sender, RoutedEventArgs e) => DeleteSelected();

    private void DuplicateSelected()
    {
        if (_selected is null) return;
        var copy = _selected.Clone();
        copy.Id = Guid.NewGuid().ToString("N")[..8];
        copy.X += 16;
        copy.Y += 16;
        AddItem(copy);
    }

    private void DeleteSelected()
    {
        if (_selected is null) return;
        Checkpoint();
        int i = _doc.Items.IndexOf(_selected);
        _doc.Items.Remove(_selected);
        _selected = _doc.Items.Count == 0 ? null : _doc.Items[Math.Clamp(i - 1, 0, _doc.Items.Count - 1)];
        StructureChanged();
    }

    // ===================== Fläche: Maus =====================

    private double GridSize => GridSizes[Math.Max(0, GridCombo.SelectedIndex)];
    private bool SnapActive => SnapCheck.IsChecked == true && (Keyboard.Modifiers & ModifierKeys.Alt) == 0;
    private double Snap(double v) => SnapActive ? PanelLogic.Snap(v, GridSize) : Math.Round(v);

    private void OnGridChanged(object sender, SelectionChangedEventArgs e) => RedrawOverlay();
    private void OnOutlineChanged(object sender, RoutedEventArgs e) => RedrawOverlay();

    private const double HandleSize = 14;

    private bool OnHandle(PanelItem item, Point p) =>
        p.X >= item.X + item.W - HandleSize && p.X <= item.X + item.W + 4 && p.Y >= item.Y + item.H - HandleSize && p.Y <= item.Y + item.H + 4;

    private void OnOverlayMouseDown(object sender, MouseButtonEventArgs e)
    {
        Overlay.Focus();
        var p = e.GetPosition(Overlay);
        if (_selected is not null && OnHandle(_selected, p)) _drag = Drag.Resize;
        else
        {
            var hit = PanelLogic.HitTest(_doc, p.X, p.Y);
            // Klick auf das schon gewählte Element hat Vorrang vor Elementen darüber (Box über Box).
            if (_selected is not null && hit != _selected && Contains(_selected, p) && (Keyboard.Modifiers & ModifierKeys.Control) == 0) hit = _selected;
            if (hit != _selected) Select(hit);
            _drag = hit is null ? Drag.None : Drag.Move;
        }
        if (_drag != Drag.None && _selected is not null)
        {
            _dragStart = p;
            _dragOrigin = new Rect(_selected.X, _selected.Y, _selected.W, _selected.H);
            _dragCheckpointed = false;
            Overlay.CaptureMouse();
        }
        e.Handled = true;
    }

    private static bool Contains(PanelItem i, Point p) => p.X >= i.X && p.X <= i.X + i.W && p.Y >= i.Y && p.Y <= i.Y + i.H;

    private void OnOverlayMouseMove(object sender, MouseEventArgs e)
    {
        var p = e.GetPosition(Overlay);
        CursorText.Text = $"x {p.X:0}  y {p.Y:0}" + (_selected is null ? "" : $"   ·   {_selected.Name}: {_selected.X:0}, {_selected.Y:0}  {_selected.W:0} × {_selected.H:0}");
        Overlay.Cursor = _selected is not null && OnHandle(_selected, p) ? Cursors.SizeNWSE : PanelLogic.HitTest(_doc, p.X, p.Y) is not null ? Cursors.SizeAll : Cursors.Arrow;
        if (_drag == Drag.None || _selected is null || e.LeftButton != MouseButtonState.Pressed) return;
        var d = p - _dragStart;
        if (!_dragCheckpointed)
        {
            if (Math.Abs(d.X) < 2 && Math.Abs(d.Y) < 2) return;
            Checkpoint();
            _dragCheckpointed = true;
        }
        if (_drag == Drag.Move)
        {
            _selected.X = Snap(_dragOrigin.X + d.X);
            _selected.Y = Snap(_dragOrigin.Y + d.Y);
        }
        else
        {
            double w = Snap(_dragOrigin.X + _dragOrigin.Width + d.X) - _selected.X;
            double h = Snap(_dragOrigin.Y + _dragOrigin.Height + d.Y) - _selected.Y;
            if ((Keyboard.Modifiers & ModifierKeys.Shift) != 0 && _dragOrigin.Height > 0)
            {
                double ratio = _dragOrigin.Width / _dragOrigin.Height;
                h = w / ratio;
            }
            _selected.W = Math.Max(4, w);
            _selected.H = Math.Max(4, h);
        }
        Changed();
    }

    private void OnOverlayMouseUp(object sender, MouseButtonEventArgs e)
    {
        if (_drag != Drag.None)
        {
            Overlay.ReleaseMouseCapture();
            if (_dragCheckpointed) BuildProperties();
        }
        _drag = Drag.None;
    }

    private void RedrawOverlay()
    {
        Overlay.Children.Clear();
        if (OutlineCheck.IsChecked == true)
        {
            foreach (var item in _doc.Items.Where(i => i.Visible && i != _selected))
                AddRect(item.X, item.Y, item.W, item.H, Color.FromArgb(70, 255, 255, 255), 1, new DoubleCollection { 3, 3 });
        }
        if (_selected is null) return;
        var s = _selected;
        var accent = ((SolidColorBrush)FindResource("BrushAccent")).Color;
        AddRect(s.X - 1, s.Y - 1, s.W + 2, s.H + 2, accent, 2, new DoubleCollection { 4, 2 });
        var handle = new Rectangle { Width = HandleSize, Height = HandleSize, Fill = new SolidColorBrush(accent), Stroke = Brushes.White, StrokeThickness = 1.5, IsHitTestVisible = false };
        Canvas.SetLeft(handle, s.X + s.W - HandleSize + 2);
        Canvas.SetTop(handle, s.Y + s.H - HandleSize + 2);
        Overlay.Children.Add(handle);
    }

    private void AddRect(double x, double y, double w, double h, Color color, double thickness, DoubleCollection dash)
    {
        var r = new Rectangle { Width = Math.Max(1, w), Height = Math.Max(1, h), Stroke = new SolidColorBrush(color), StrokeThickness = thickness, StrokeDashArray = dash, IsHitTestVisible = false };
        Canvas.SetLeft(r, x);
        Canvas.SetTop(r, y);
        Overlay.Children.Add(r);
    }

    // ===================== Sensoren: Liste, Drag & Drop =====================

    public sealed class SensorRow : INotifyPropertyChanged
    {
        public required string Id { get; init; }
        public required string Name { get; init; }
        public required string Group { get; init; }
        public required string Source { get; init; }
        public int GroupRank => SensorGroups.Rank(Group);
        /// <summary>Natürliche Sortierung (Kern 2 vor Kern 10).</summary>
        public string SortKey => System.Text.RegularExpressions.Regex.Replace(Name, @"\d+", m => m.Value.PadLeft(6, '0'));
        private string _value = "";
        public string ValueText { get => _value; set { if (_value != value) { _value = value; PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(ValueText))); } } }
        public event PropertyChangedEventHandler? PropertyChanged;
    }

    private void RefreshSensors()
    {
        _hub.Demand();
        var snap = _hub.Snapshot;
        var ids = snap.Keys.ToHashSet();
        // Neue Sensoren sofort aufnehmen, verschwundene erst nach 10 s entfernen — sonst springt die Liste bei kurzen Lücken.
        foreach (var r in snap.Values)
        {
            _missing.Remove(r.Id);
            if (_knownSensorIds.Add(r.Id)) _sensorRows.Add(new SensorRow { Id = r.Id, Name = r.Name, Group = r.Group, Source = r.Source });
        }
        foreach (var id in _knownSensorIds.Where(id => !ids.Contains(id)).ToList())
        {
            _missing[id] = _missing.TryGetValue(id, out var n) ? n + 1 : 1;
            if (_missing[id] < 10) continue;
            _missing.Remove(id);
            _knownSensorIds.Remove(id);
            if (_sensorRows.FirstOrDefault(row => row.Id == id) is { } gone) _sensorRows.Remove(gone);
        }
        foreach (var row in _sensorRows)
            row.ValueText = snap.TryGetValue(row.Id, out var r) ? SensorFormat.Format(r) : "–";
        SourceStatusText.Text = _service.SourceStatus() +
            (_hub.Sources.Any(s => s.Name == "AIDA64" && !s.IsAvailable)
                ? "\n\nTemperaturen, Lüfter und Spannungen kommen von AIDA64: dort unter Datei → Einstellungen → Hardwareüberwachung → Externe Programme „Gemeinsamen Speicher aktivieren“ einschalten und die gewünschten Werte anhaken."
                : "");
    }

    private bool FilterSensor(object o)
    {
        var q = SensorSearchBox.Text.Trim();
        if (q.Length == 0 || o is not SensorRow r) return true;
        return r.Name.Contains(q, StringComparison.CurrentCultureIgnoreCase) || r.Id.Contains(q, StringComparison.OrdinalIgnoreCase) ||
               r.Group.Contains(q, StringComparison.CurrentCultureIgnoreCase) || r.Source.Contains(q, StringComparison.CurrentCultureIgnoreCase);
    }

    private void OnSensorSearchChanged(object sender, TextChangedEventArgs e) => _sensorView.Refresh();

    private SensorRow? SelectedSensorRow() => SensorList.SelectedItem as SensorRow;

    private void OnSensorListMouseDown(object sender, MouseButtonEventArgs e) => _sensorDragStart = e.GetPosition(SensorList);

    private void OnSensorListMouseMove(object sender, MouseEventArgs e)
    {
        if (e.LeftButton != MouseButtonState.Pressed) return;
        var d = e.GetPosition(SensorList) - _sensorDragStart;
        if (Math.Abs(d.X) < SystemParameters.MinimumHorizontalDragDistance && Math.Abs(d.Y) < SystemParameters.MinimumVerticalDragDistance) return;
        var row = (e.OriginalSource as DependencyObject) is { } src ? ItemsControl.ContainerFromElement(SensorList, src) as ListBoxItem : null;
        if (row?.DataContext is not SensorRow sensor) return;
        DragDrop.DoDragDrop(SensorList, new DataObject(typeof(SensorRow), sensor), DragDropEffects.Copy);
    }

    private void OnOverlayDragOver(object sender, DragEventArgs e)
    {
        e.Effects = e.Data.GetDataPresent(typeof(SensorRow)) ? DragDropEffects.Copy : DragDropEffects.None;
        e.Handled = true;
    }

    private void OnOverlayDrop(object sender, DragEventArgs e)
    {
        if (e.Data.GetData(typeof(SensorRow)) is not SensorRow sensor) return;
        var p = e.GetPosition(Overlay);
        var target = PanelLogic.HitTest(_doc, p.X, p.Y);
        if (target is not null && UsesSensor(target.Kind))
        {
            AssignSensor(target, sensor.Id, append: (e.KeyStates & DragDropKeyStates.ControlKey) != 0);
            Select(target);
            return;
        }
        var item = CreateItem(PanelItemKind.Value, sensor.Id);
        item.X = Snap(p.X - item.W / 2);
        item.Y = Snap(p.Y - item.H / 2);
        AddItem(item);
    }

    private void OnSensorListDoubleClick(object sender, MouseButtonEventArgs e)
    {
        if (SelectedSensorRow() is not { } sensor) return;
        if (_selected is not null && UsesSensor(_selected.Kind))
        {
            AssignSensor(_selected, sensor.Id, append: (Keyboard.Modifiers & ModifierKeys.Control) != 0);
            BuildProperties();
            return;
        }
        var item = CreateItem(PanelItemKind.Value, sensor.Id);
        item.X = Math.Round((_doc.Width - item.W) / 2);
        item.Y = Math.Round((_doc.Height - item.H) / 2);
        AddItem(item);
    }

    /// <summary>Sensor setzen; mit Strg als Ausweich-Sensor hinten anhängen.</summary>
    private void AssignSensor(PanelItem item, string id, bool append)
    {
        Checkpoint();
        if (append && item.SensorId.Length > 0 && !item.SensorId.Split('|').Contains(id)) item.SensorId += "|" + id;
        else item.SensorId = id;
        if (!append && _hub.Get(id) is { } r && (string.IsNullOrWhiteSpace(item.Name) || item.Name == PanelItem.KindName(item.Kind))) item.Name = r.Name;
        StructureChanged();
    }

    // ===================== Tastatur =====================

    private void OnPreviewKeyDown(object sender, KeyEventArgs e)
    {
        bool ctrl = (Keyboard.Modifiers & ModifierKeys.Control) != 0;
        if (ctrl && e.Key == Key.S) { Save(); e.Handled = true; return; }
        if (Keyboard.FocusedElement is TextBox) return; // Tippen in Feldern nicht abfangen
        if (ctrl && e.Key == Key.Z) { Undo(); e.Handled = true; }
        else if (ctrl && e.Key == Key.Y) { Redo(); e.Handled = true; }
        else if (ctrl && e.Key == Key.D) { DuplicateSelected(); e.Handled = true; }
        else if (ctrl && e.Key == Key.C && _selected is not null) { _clipboard = _selected.Clone(); e.Handled = true; }
        else if (ctrl && e.Key == Key.V && _clipboard is not null)
        {
            var copy = _clipboard.Clone();
            copy.Id = Guid.NewGuid().ToString("N")[..8];
            copy.X += 16; copy.Y += 16;
            _clipboard = copy.Clone();
            AddItem(copy);
            e.Handled = true;
        }
        else if (e.Key == Key.Delete && _selected is not null && (Overlay.IsKeyboardFocusWithin || ItemList.IsKeyboardFocusWithin)) { DeleteSelected(); e.Handled = true; }
        else if (e.Key == Key.Escape && _selected is not null && Overlay.IsKeyboardFocusWithin) { Select(null); e.Handled = true; }
        else if (_selected is not null && Overlay.IsKeyboardFocusWithin && e.Key is Key.Left or Key.Right or Key.Up or Key.Down)
        {
            double step = (Keyboard.Modifiers & ModifierKeys.Shift) != 0 ? 10 : 1;
            Checkpoint();
            switch (e.Key)
            {
                case Key.Left: _selected.X -= step; break;
                case Key.Right: _selected.X += step; break;
                case Key.Up: _selected.Y -= step; break;
                case Key.Down: _selected.Y += step; break;
            }
            Changed();
            BuildProperties();
            e.Handled = true;
        }
    }

    private void OnWindowClosing(object? sender, CancelEventArgs e)
    {
        if (!ConfirmDiscard()) e.Cancel = true;
    }

    private void Log(string message, LogLevel level = LogLevel.Info) => LogMessage?.Invoke(message, level);
}
