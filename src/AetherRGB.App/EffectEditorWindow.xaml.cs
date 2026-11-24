using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Shapes;
using System.Windows.Threading;
using AetherRGB.Core.Devices;
using AetherRGB.Core.Diagnostics;
using AetherRGB.Core.Effects;
using AetherRGB.Core.Effects.Custom;
using AetherRGB.Core.Layout;
using Forms = System.Windows.Forms;

namespace AetherRGB.App;

/// <summary>Effekt-Editor (Phase 2): eine Mal-Ebene mit einem Bild im Raster des Effekts (160 × 80).
/// Werkzeuge Pinsel, Radierer, Füllen, Linie, Rechteck, Ellipse, Pipette; Rückgängig; die Geräte eines
/// Effekt-Fensters liegen zur Orientierung an ihrer echten Position darüber, und „Live auf LEDs“ spielt
/// den aktuellen Stand sofort in diesem Fenster ab. Die Mal-Logik selbst liegt testbar in Core
/// (<see cref="PaintStroke"/>, <see cref="PaintTools"/>, <see cref="PaintHistory"/>).</summary>
public partial class EffectEditorWindow : Window
{
    /// <summary>Canvas-Einheiten pro Rasterzelle auf der Zeichenfläche (160 Zellen = 1600 Einheiten,
    /// genau wie die Standardfläche der Effekt-Fenster).</summary>
    private const double CellUnits = 10;

    private enum Tool { Brush, Eraser, Fill, Line, Rect, Ellipse, Picker }

    private readonly CustomEffectStore _store;
    private readonly SceneCollection _scenes;
    private readonly LedShapeStore _shapes;

    private CustomEffectDocument _doc = null!;
    private PaintLayer _layer = null!;
    private PaintSurface _surface = null!;
    private readonly PaintHistory _history = new();
    private bool _isNew, _modified, _suppress;

    private WriteableBitmap _bitmap = null!;
    private int[] _bitmapPixels = Array.Empty<int>();
    private CustomEffectFrame? _frame;

    private Tool _tool = Tool.Brush, _toolBeforePicker = Tool.Brush;
    private PaintStroke? _stroke;
    private Tool _strokeTool;
    private Point _strokeStart;
    private RgbColor _color = new(0xFF, 0x00, 0x00);
    private readonly List<RgbColor> _recent = new();

    private EffectScene? _liveScene;
    private bool _liveDirty;
    private readonly DispatcherTimer _liveTimer = new() { Interval = TimeSpan.FromMilliseconds(70) };

    private static readonly RgbColor[] Palette =
    {
        new(0xFF, 0x00, 0x00), new(0xFF, 0x60, 0x00), new(0xFF, 0xC0, 0x00), new(0x00, 0xFF, 0x00),
        new(0x00, 0xFF, 0xA0), new(0x00, 0xC0, 0xFF), new(0x00, 0x00, 0xFF), new(0x80, 0x00, 0xFF),
        new(0xFF, 0x00, 0xC0), new(0xFF, 0xFF, 0xFF)
    };

    /// <summary>Meldung für die Log-Leiste des Hauptfensters.</summary>
    public event Action<string, LogLevel>? LogMessage;

    /// <summary>„Im Effekt-Fenster verwenden“: Effekt-Fenster und Katalog-ID des gespeicherten Effekts.</summary>
    public event Action<EffectScene, string>? UseInScene;

    public EffectEditorWindow(CustomEffectStore store, SceneCollection scenes, LedShapeStore shapes, string? openEffectId, EffectScene? scene)
    {
        InitializeComponent();
        _store = store;
        _scenes = scenes;
        _shapes = shapes;

        BuildPalette();
        SetColor(_color, remember: false);
        GridOverlay.Fill = CreateGridBrush();

        _suppress = true;
        SceneCombo.ItemsSource = _scenes.Scenes;
        SceneCombo.SelectedItem = scene ?? _scenes.Scenes.FirstOrDefault();
        _suppress = false;

        var existing = openEffectId is null ? null : _store.Get(openEffectId);
        if (existing is not null) LoadDocument(existing, isNew: false);
        else LoadDocument(CreateNewDocument(), isNew: true);

        _liveTimer.Tick += (_, _) => { if (_liveDirty) PushLive(); };
        EffectCatalog.Changed += OnCatalogChanged;
        PreviewKeyDown += OnPreviewKeyDown;
        Activated += (_, _) => RefreshDeviceOverlay();
        Closing += OnClosing;
        Closed += (_, _) =>
        {
            EffectCatalog.Changed -= OnCatalogChanged;
            StopLive();
        };
        UpdateToolPanel();
    }

    // ===================== Dokument =====================

    private CustomEffectDocument CreateNewDocument()
    {
        var names = _store.Effects.Select(e => e.Name).ToHashSet(StringComparer.CurrentCultureIgnoreCase);
        int n = 1;
        while (names.Contains($"Eigener Effekt {n}")) n++;
        var doc = new CustomEffectDocument { Name = $"Eigener Effekt {n}" };
        doc.Layers.Add(new PaintLayer { Name = "Malen", Frames = { PaintFrame.CreateEmpty(doc.GridWidth, doc.GridHeight) } });
        return doc;
    }

    private void LoadDocument(CustomEffectDocument doc, bool isNew)
    {
        _doc = doc;
        _isNew = isNew;

        // Phase 2 bearbeitet genau ein Bild: das erste Bild der obersten Mal-Ebene. Andere Ebenen (z. B. aus
        // einer von Hand geschriebenen Datei) bleiben unverändert erhalten und werden mit angezeigt.
        var layer = doc.Layers.OfType<PaintLayer>().LastOrDefault();
        if (layer is null)
        {
            layer = new PaintLayer { Name = "Malen" };
            doc.Layers.Add(layer);
        }
        if (layer.Frames.Count == 0) layer.Frames.Add(PaintFrame.CreateEmpty(doc.GridWidth, doc.GridHeight));
        if (layer.Frames[0].Pixels.Length != doc.GridWidth * doc.GridHeight * 4)
            layer.Frames[0] = PaintFrame.CreateEmpty(doc.GridWidth, doc.GridHeight);
        _layer = layer;
        _surface = new PaintSurface(doc.GridWidth, doc.GridHeight, layer.Frames[0].Pixels);
        _history.Clear();
        _modified = false;

        int others = doc.Layers.Count - 1;
        LayerHint.Visibility = others > 0 || layer.Frames.Count > 1 ? Visibility.Visible : Visibility.Collapsed;
        LayerHint.Text = $"Dieser Effekt hat {doc.Layers.Count} Ebene(n) und {layer.Frames.Count} Bild(er). Bearbeitet wird das erste Bild der Ebene „{layer.Name}“, " +
                         "alles andere bleibt erhalten. Mehrere Ebenen und Bilder kommen in einer späteren Version des Editors.";

        _bitmap = new WriteableBitmap(doc.GridWidth, doc.GridHeight, 96, 96, PixelFormats.Bgra32, null);
        _bitmapPixels = new int[doc.GridWidth * doc.GridHeight];
        PaintImage.Source = _bitmap;
        SurfaceRoot.Width = InputCanvas.Width = doc.GridWidth * CellUnits;
        SurfaceRoot.Height = InputCanvas.Height = doc.GridHeight * CellUnits;
        _frame = null;

        _suppress = true;
        NameBox.Text = doc.Name;
        RefreshPicker();
        _suppress = false;

        RefreshImage();
        RefreshDeviceOverlay();
        UpdateTitle();
        UpdateUndoButtons();
        _liveDirty = true;
        StatusText.Text = isNew ? "Neuer Effekt — mit dem Pinsel auf die Fläche malen. Durchsichtige Stellen sind an den LEDs aus." : $"„{doc.Name}“ geöffnet.";
    }

    private void RefreshPicker()
    {
        var effects = _store.Effects.OrderBy(e => e.Name, StringComparer.CurrentCultureIgnoreCase).ToList();
        EffectPicker.ItemsSource = effects;
        EffectPicker.SelectedItem = effects.FirstOrDefault(e => e.Id == _doc.Id);
    }

    private void OnCatalogChanged() => Dispatcher.BeginInvoke(() =>
    {
        if (!IsLoaded) return;
        _suppress = true;
        RefreshPicker();
        _suppress = false;
    });

    private void MarkModified()
    {
        _liveDirty = true;
        if (_modified) return;
        _modified = true;
        UpdateTitle();
    }

    private void UpdateTitle() => Title = $"AetherRGB — Effekt-Editor — {_doc.Name}{(_modified || _isNew ? " *" : "")}";

    private void OnNameChanged(object sender, TextChangedEventArgs e)
    {
        if (_suppress) return;
        _doc.Name = string.IsNullOrWhiteSpace(NameBox.Text) ? "Eigener Effekt" : NameBox.Text.Trim();
        MarkModified();
        UpdateTitle();
    }

    /// <summary>Ungespeicherte Änderungen: speichern, verwerfen oder abbrechen. true = weitermachen.</summary>
    private bool ConfirmDiscard()
    {
        if (!_modified) return true;
        var answer = MessageBox.Show(this, $"„{_doc.Name}“ hat ungespeicherte Änderungen. Speichern?", "AetherRGB",
            MessageBoxButton.YesNoCancel, MessageBoxImage.Question);
        return answer switch
        {
            MessageBoxResult.Yes => Save(),
            MessageBoxResult.No => true,
            _ => false
        };
    }

    private void OnEffectPickerChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_suppress || EffectPicker.SelectedItem is not CustomEffectDocument picked || picked.Id == _doc.Id) return;
        if (!ConfirmDiscard())
        {
            _suppress = true;
            EffectPicker.SelectedItem = (EffectPicker.ItemsSource as IEnumerable<CustomEffectDocument>)?.FirstOrDefault(d => d.Id == _doc.Id);
            _suppress = false;
            return;
        }
        if (_store.Get(picked.Id) is { } doc) LoadDocument(doc, isNew: false);
    }

    private void OnNewClick(object sender, RoutedEventArgs e)
    {
        if (ConfirmDiscard()) LoadDocument(CreateNewDocument(), isNew: true);
    }

    private bool Save()
    {
        try
        {
            var path = _store.Save(_doc);
            bool wasNew = _isNew;
            _isNew = false;
            _modified = false;
            UpdateTitle();
            _suppress = true;
            RefreshPicker();
            _suppress = false;
            StatusText.Text = $"Gespeichert: {path}";
            LogMessage?.Invoke($"Eigener Effekt „{_doc.Name}“ {(wasNew ? "angelegt" : "gespeichert")} ({System.IO.Path.GetFileName(path)}).", LogLevel.Info);
            return true;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or InvalidDataException)
        {
            MessageBox.Show(this, "Speichern fehlgeschlagen: " + ex.Message, "AetherRGB", MessageBoxButton.OK, MessageBoxImage.Error);
            LogMessage?.Invoke($"Eigener Effekt „{_doc.Name}“ konnte nicht gespeichert werden: {ExceptionText.Describe(ex)}", LogLevel.Error);
            return false;
        }
    }

    private void OnSaveClick(object sender, RoutedEventArgs e) => Save();

    private void OnSaveCopyClick(object sender, RoutedEventArgs e)
    {
        var copy = _doc.Clone();
        copy.Id = Guid.NewGuid().ToString("N");
        copy.Name = _doc.Name + " (Kopie)";
        // Das Original bleibt wie zuletzt gespeichert; weiter geht es in der Kopie.
        LoadDocument(copy, isNew: true);
        Save();
    }

    private void OnDeleteClick(object sender, RoutedEventArgs e)
    {
        if (_isNew)
        {
            if (ConfirmDiscard()) LoadDocument(CreateNewDocument(), isNew: true);
            return;
        }
        int used = _scenes.Scenes.Count(s => s.EffectId == _doc.CatalogId);
        var text = $"Eigenen Effekt „{_doc.Name}“ löschen?" +
                   (used > 0 ? $"\n\n{used} Effekt-Fenster nutzen ihn und zeigen danach die Regenbogen-Welle." : "");
        if (MessageBox.Show(this, text, "AetherRGB", MessageBoxButton.YesNo, MessageBoxImage.Warning) != MessageBoxResult.Yes) return;
        try
        {
            _store.Delete(_doc.Id);
            LogMessage?.Invoke($"Eigener Effekt „{_doc.Name}“ gelöscht.", LogLevel.Info);
            _modified = false;
            LoadDocument(CreateNewDocument(), isNew: true);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            MessageBox.Show(this, "Löschen fehlgeschlagen: " + ex.Message, "AetherRGB", MessageBoxButton.OK, MessageBoxImage.Error);
        }
    }

    private void OnImportClick(object sender, RoutedEventArgs e)
    {
        if (!ConfirmDiscard()) return;
        var dialog = new Microsoft.Win32.OpenFileDialog { Filter = "AetherRGB-Effekt (*.aeffect)|*.aeffect", Title = "Effekt importieren" };
        if (dialog.ShowDialog(this) != true) return;
        try
        {
            var imported = _store.Import(dialog.FileName);
            LogMessage?.Invoke($"Eigener Effekt „{imported.Name}“ importiert aus {dialog.FileName}.", LogLevel.Info);
            if (_store.Get(imported.Id) is { } doc) LoadDocument(doc, isNew: false);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or InvalidDataException)
        {
            MessageBox.Show(this, "Import fehlgeschlagen: " + ex.Message, "AetherRGB", MessageBoxButton.OK, MessageBoxImage.Error);
        }
    }

    private void OnExportClick(object sender, RoutedEventArgs e)
    {
        var name = string.Concat(_doc.Name.Select(c => System.IO.Path.GetInvalidFileNameChars().Contains(c) ? '_' : c));
        var dialog = new Microsoft.Win32.SaveFileDialog
        {
            Filter = "AetherRGB-Effekt (*.aeffect)|*.aeffect",
            FileName = name + CustomEffectStore.Extension,
            InitialDirectory = Environment.GetFolderPath(Environment.SpecialFolder.DesktopDirectory),
            Title = "Effekt exportieren"
        };
        if (dialog.ShowDialog(this) != true) return;
        try
        {
            CustomEffectSerializer.Save(_doc.Clone(), dialog.FileName);
            StatusText.Text = $"Exportiert: {dialog.FileName}";
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or InvalidDataException)
        {
            MessageBox.Show(this, "Export fehlgeschlagen: " + ex.Message, "AetherRGB", MessageBoxButton.OK, MessageBoxImage.Error);
        }
    }

    private void OnClosing(object? sender, System.ComponentModel.CancelEventArgs e)
    {
        if (!ConfirmDiscard()) e.Cancel = true;
    }

    // ===================== Rückgängig =====================

    private void OnUndoClick(object sender, RoutedEventArgs e) => Undo();
    private void OnRedoClick(object sender, RoutedEventArgs e) => Redo();

    private void Undo()
    {
        if (_stroke is not null) return;
        var description = _history.UndoDescription;
        if (_history.Undo(_surface) is null) return;
        StatusText.Text = $"Rückgängig: {description}";
        AfterEdit();
    }

    private void Redo()
    {
        if (_stroke is not null) return;
        var description = _history.RedoDescription;
        if (_history.Redo(_surface) is null) return;
        StatusText.Text = $"Wiederholt: {description}";
        AfterEdit();
    }

    private void AfterEdit()
    {
        RefreshImage();
        MarkModified();
        UpdateUndoButtons();
    }

    private void UpdateUndoButtons()
    {
        UndoButton.IsEnabled = _history.CanUndo;
        RedoButton.IsEnabled = _history.CanRedo;
        UndoButton.ToolTip = _history.CanUndo ? $"Rückgängig: {_history.UndoDescription} (Strg+Z)" : "Rückgängig (Strg+Z)";
        RedoButton.ToolTip = _history.CanRedo ? $"Wiederholen: {_history.RedoDescription} (Strg+Y)" : "Wiederholen (Strg+Y)";
    }

    private void OnClearClick(object sender, RoutedEventArgs e)
    {
        _history.Push(PaintTools.Clear(_surface));
        AfterEdit();
    }

    private void OnFloodAllClick(object sender, RoutedEventArgs e)
    {
        _history.Push(PaintTools.Clear(_surface, _color));
        RememberColor(_color);
        AfterEdit();
    }

    // ===================== Werkzeuge =====================

    private void OnToolChanged(object sender, RoutedEventArgs e)
    {
        if (!IsInitialized) return;
        _tool = sender == ToolEraser ? Tool.Eraser
            : sender == ToolFill ? Tool.Fill
            : sender == ToolLine ? Tool.Line
            : sender == ToolRect ? Tool.Rect
            : sender == ToolEllipse ? Tool.Ellipse
            : sender == ToolPicker ? Tool.Picker
            : Tool.Brush;
        if (_tool != Tool.Picker) _toolBeforePicker = _tool;
        UpdateToolPanel();
    }

    private void SelectTool(Tool tool)
    {
        var radio = tool switch
        {
            Tool.Eraser => ToolEraser, Tool.Fill => ToolFill, Tool.Line => ToolLine, Tool.Rect => ToolRect,
            Tool.Ellipse => ToolEllipse, Tool.Picker => ToolPicker, _ => ToolBrush
        };
        radio.IsChecked = true;
    }

    private void UpdateToolPanel()
    {
        if (ToolTitle is null) return;
        ToolTitle.Text = _tool switch
        {
            Tool.Eraser => "Radierer", Tool.Fill => "Füllen", Tool.Line => "Linie", Tool.Rect => "Rechteck",
            Tool.Ellipse => "Ellipse", Tool.Picker => "Pipette", _ => "Pinsel"
        };
        bool stroke = _tool is Tool.Brush or Tool.Eraser or Tool.Line or Tool.Rect or Tool.Ellipse;
        bool shape = _tool is Tool.Rect or Tool.Ellipse;
        BrushSection.Visibility = stroke ? Visibility.Visible : Visibility.Collapsed;
        HardnessSection.Visibility = _tool is Tool.Brush or Tool.Eraser or Tool.Line ? Visibility.Visible : Visibility.Collapsed;
        OpacitySection.Visibility = _tool != Tool.Picker ? Visibility.Visible : Visibility.Collapsed;
        ColorSection.Visibility = _tool != Tool.Eraser ? Visibility.Visible : Visibility.Collapsed;
        FilledCheck.Visibility = shape ? Visibility.Visible : Visibility.Collapsed;
        FillSection.Visibility = _tool == Tool.Fill ? Visibility.Visible : Visibility.Collapsed;
        SymmetrySection.Visibility = _tool is Tool.Brush or Tool.Eraser ? Visibility.Visible : Visibility.Collapsed;
        UpdateCursor(null);
    }

    private void OnBrushSettingChanged(object sender, RoutedEventArgs e)
    {
        if (SizeText is null || HardnessText is null || OpacityText is null || ToleranceText is null) return;
        SizeText.Text = ((int)SizeSlider.Value).ToString();
        HardnessText.Text = $"{(int)HardnessSlider.Value} %";
        OpacityText.Text = $"{(int)OpacitySlider.Value} %";
        ToleranceText.Text = ((int)ToleranceSlider.Value).ToString();
    }

    private BrushSettings CurrentBrush() => new(
        (int)SizeSlider.Value,
        _tool is Tool.Rect or Tool.Ellipse ? 1 : HardnessSlider.Value / 100.0,
        OpacitySlider.Value / 100.0,
        TipCombo.SelectedIndex == 1 ? BrushTip.Square : BrushTip.Round);

    private PaintSymmetry CurrentSymmetry() =>
        (MirrorHCheck.IsChecked == true ? PaintSymmetry.Horizontal : PaintSymmetry.None) |
        (MirrorVCheck.IsChecked == true ? PaintSymmetry.Vertical : PaintSymmetry.None);

    // ===================== Maus auf der Fläche =====================

    private Point ToCell(MouseEventArgs e)
    {
        var p = e.GetPosition(InputCanvas);
        return new Point(p.X / CellUnits, p.Y / CellUnits);
    }

    private void OnCanvasMouseDown(object sender, MouseButtonEventArgs e)
    {
        if (_stroke is not null || e.ChangedButton is not (MouseButton.Left or MouseButton.Right)) return;
        var cell = ToCell(e);
        int cx = (int)Math.Floor(cell.X), cy = (int)Math.Floor(cell.Y);
        bool right = e.ChangedButton == MouseButton.Right;

        // Alt + Klick = Pipette, aus jedem Werkzeug heraus.
        if (_tool == Tool.Picker || (Keyboard.Modifiers.HasFlag(ModifierKeys.Alt) && !right))
        {
            PickColor(cx, cy);
            if (_tool == Tool.Picker) SelectTool(_toolBeforePicker);
            e.Handled = true;
            return;
        }

        if (_tool == Tool.Fill)
        {
            if (right) return;
            var edit = PaintTools.Fill(_surface, cx, cy, _color, OpacitySlider.Value / 100.0, (int)ToleranceSlider.Value, ContiguousCheck.IsChecked == true);
            if (edit is not null)
            {
                _history.Push(edit);
                RememberColor(_color);
                AfterEdit();
            }
            e.Handled = true;
            return;
        }

        // Rechte Maustaste malt mit jedem Strich-Werkzeug durchsichtig (radiert).
        bool erase = _tool == Tool.Eraser || right;
        _strokeTool = _tool;
        _strokeStart = cell;
        var symmetry = _tool is Tool.Brush or Tool.Eraser ? CurrentSymmetry() : PaintSymmetry.None;
        _stroke = new PaintStroke(_surface, CurrentBrush(), _color, erase, symmetry);
        InputCanvas.CaptureMouse();
        DrawStroke(cell, Keyboard.Modifiers.HasFlag(ModifierKeys.Shift));
        e.Handled = true;
    }

    private void OnCanvasMouseMove(object sender, MouseEventArgs e)
    {
        var cell = ToCell(e);
        int cx = (int)Math.Floor(cell.X), cy = (int)Math.Floor(cell.Y);
        CellText.Text = _surface.Contains(cx, cy) ? $"Zelle {cx + 1}, {cy + 1}  ·  {_surface.Width} × {_surface.Height}" : "";
        UpdateCursor(cell);
        if (_stroke is not null) DrawStroke(cell, Keyboard.Modifiers.HasFlag(ModifierKeys.Shift));
    }

    private void OnCanvasMouseUp(object sender, MouseButtonEventArgs e)
    {
        if (_stroke is null) return;
        FinishStroke();
        InputCanvas.ReleaseMouseCapture();
    }

    /// <summary>Fenster verliert die Maus mitten im Strich (Alt+Tab o. Ä.) — Strich so übernehmen, wie er ist.</summary>
    private void OnCanvasLostCapture(object sender, MouseEventArgs e)
    {
        if (_stroke is not null) FinishStroke();
    }

    private void OnCanvasMouseLeave(object sender, MouseEventArgs e)
    {
        if (_stroke is null) UpdateCursor(null);
        CellText.Text = "";
    }

    private void DrawStroke(Point cell, bool constrain)
    {
        var stroke = _stroke!;
        switch (_strokeTool)
        {
            case Tool.Brush:
            case Tool.Eraser:
                stroke.MoveTo(cell.X, cell.Y);
                break;
            case Tool.Line:
            {
                var end = constrain ? SnapAngle(_strokeStart, cell) : cell;
                stroke.Reset();
                stroke.Line(_strokeStart.X, _strokeStart.Y, end.X, end.Y);
                break;
            }
            case Tool.Rect:
            case Tool.Ellipse:
            {
                int x0 = (int)Math.Floor(_strokeStart.X), y0 = (int)Math.Floor(_strokeStart.Y);
                int x1 = (int)Math.Floor(cell.X), y1 = (int)Math.Floor(cell.Y);
                if (constrain)
                {
                    int side = Math.Max(Math.Abs(x1 - x0), Math.Abs(y1 - y0));
                    x1 = x0 + Math.Sign(x1 - x0 == 0 ? 1 : x1 - x0) * side;
                    y1 = y0 + Math.Sign(y1 - y0 == 0 ? 1 : y1 - y0) * side;
                }
                stroke.Reset();
                bool filled = FilledCheck.IsChecked == true;
                if (_strokeTool == Tool.Rect) stroke.Rectangle(x0, y0, x1, y1, filled);
                else stroke.Ellipse(x0, y0, x1, y1, filled);
                break;
            }
        }
        RefreshImage();
        _liveDirty = true;
    }

    private static Point SnapAngle(Point start, Point end)
    {
        double dx = end.X - start.X, dy = end.Y - start.Y;
        double angle = Math.Round(Math.Atan2(dy, dx) / (Math.PI / 4)) * (Math.PI / 4);
        double length = Math.Sqrt(dx * dx + dy * dy);
        return new Point(start.X + Math.Cos(angle) * length, start.Y + Math.Sin(angle) * length);
    }

    private void FinishStroke()
    {
        var stroke = _stroke;
        if (stroke is null) return;
        _stroke = null;
        var description = _strokeTool switch
        {
            Tool.Eraser => "Radieren", Tool.Line => "Linie", Tool.Rect => "Rechteck", Tool.Ellipse => "Ellipse",
            _ => "Pinselstrich"
        };
        var edit = stroke.Finish(description);
        if (edit is null) return;
        _history.Push(edit);
        if (_strokeTool != Tool.Eraser) RememberColor(_color);
        AfterEdit();
    }

    private void PickColor(int x, int y)
    {
        // Was man sieht: das gemalte Bild, sonst (durchsichtige Zelle) das fertig zusammengesetzte Bild.
        var color = _surface.GetColor(x, y) ?? (_frame is not null && _surface.Contains(x, y) ? _frame.GetCell(x, y) : (RgbColor?)null);
        if (color is not { } c) return;
        SetColor(c, remember: true);
        StatusText.Text = $"Farbe aufgenommen: {c.ToHex()}";
    }

    /// <summary>Umriss des Pinsels unter der Maus, damit man sieht, welche Zellen er trifft.</summary>
    private void UpdateCursor(Point? cell)
    {
        CursorOverlay.Children.Clear();
        if (cell is not { } c || _tool is Tool.Fill or Tool.Picker) return;
        int size = (int)SizeSlider.Value;
        double cx = size % 2 == 1 ? Math.Floor(c.X) + 0.5 : Math.Round(c.X);
        double cy = size % 2 == 1 ? Math.Floor(c.Y) + 0.5 : Math.Round(c.Y);
        bool square = TipCombo.SelectedIndex == 1;
        Shape outline = square ? new Rectangle() : new Ellipse();
        outline.Width = outline.Height = size * CellUnits;
        outline.Stroke = new SolidColorBrush(Color.FromArgb(0xC0, 0xFF, 0xFF, 0xFF));
        outline.StrokeThickness = 1.5;
        Canvas.SetLeft(outline, (cx - size / 2.0) * CellUnits);
        Canvas.SetTop(outline, (cy - size / 2.0) * CellUnits);
        CursorOverlay.Children.Add(outline);
    }

    // ===================== Farbe =====================

    private void SetColor(RgbColor color, bool remember)
    {
        _color = color;
        ColorSwatch.Background = new SolidColorBrush(ToWpf(color));
        ColorHexBox.Text = color.ToHex();
        if (remember) RememberColor(color);
    }

    private void RememberColor(RgbColor color)
    {
        _recent.Remove(color);
        _recent.Insert(0, color);
        if (_recent.Count > 10) _recent.RemoveAt(_recent.Count - 1);
        RecentPanel.Children.Clear();
        foreach (var c in _recent) RecentPanel.Children.Add(CreateSwatch(c, 22));
    }

    private void BuildPalette()
    {
        foreach (var c in Palette) PalettePanel.Children.Add(CreateSwatch(c, 24));
    }

    private Border CreateSwatch(RgbColor color, double size)
    {
        var swatch = new Border
        {
            Width = size, Height = size, CornerRadius = new CornerRadius(5), Margin = new Thickness(0, 0, 4, 4),
            BorderThickness = new Thickness(1), BorderBrush = (Brush)FindResource("BrushBorder"),
            Background = new SolidColorBrush(ToWpf(color)), Cursor = Cursors.Hand, ToolTip = color.ToHex()
        };
        swatch.MouseLeftButtonUp += (_, _) => SetColor(color, remember: false);
        return swatch;
    }

    private void OnColorSwatchClick(object sender, MouseButtonEventArgs e)
    {
        using var dialog = new Forms.ColorDialog
        {
            FullOpen = true, AnyColor = true,
            Color = System.Drawing.Color.FromArgb(_color.R, _color.G, _color.B)
        };
        if (dialog.ShowDialog() == Forms.DialogResult.OK)
            SetColor(new RgbColor(dialog.Color.R, dialog.Color.G, dialog.Color.B), remember: true);
    }

    private void OnColorHexCommit(object sender, RoutedEventArgs e)
    {
        var text = ColorHexBox.Text.Trim();
        if (!text.StartsWith('#')) text = "#" + text;
        try { SetColor(RgbColor.FromHex(text), remember: false); }
        catch (Exception ex) when (ex is FormatException or ArgumentException or OverflowException)
        {
            ColorHexBox.Text = _color.ToHex();
        }
    }

    private void OnColorHexKeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.Enter) OnColorHexCommit(sender, e);
    }

    private static Color ToWpf(RgbColor c) => Color.FromRgb(c.R, c.G, c.B);

    // ===================== Tastatur =====================

    private void OnPreviewKeyDown(object sender, KeyEventArgs e)
    {
        var key = e.Key == Key.System ? e.SystemKey : e.Key;
        bool ctrl = Keyboard.Modifiers.HasFlag(ModifierKeys.Control);
        bool shift = Keyboard.Modifiers.HasFlag(ModifierKeys.Shift);

        if (ctrl && key == Key.S) { Save(); e.Handled = true; return; }
        // In Textfeldern gelten Strg+Z und Buchstaben dem Text.
        if (Keyboard.FocusedElement is TextBox) return;

        if (ctrl && (key == Key.Y || (key == Key.Z && shift))) { Redo(); e.Handled = true; return; }
        if (ctrl && key == Key.Z) { Undo(); e.Handled = true; return; }
        if (key == Key.Escape && _stroke is not null)
        {
            // Strich/Form abbrechen, ohne etwas zu übernehmen.
            _stroke.Reset();
            _stroke = null;
            InputCanvas.ReleaseMouseCapture();
            RefreshImage();
            _liveDirty = true;
            e.Handled = true;
            return;
        }
        if (ctrl || _stroke is not null) return;

        switch (key)
        {
            case Key.B: SelectTool(Tool.Brush); break;
            case Key.E: SelectTool(Tool.Eraser); break;
            case Key.G: SelectTool(Tool.Fill); break;
            case Key.L: SelectTool(Tool.Line); break;
            case Key.R: SelectTool(Tool.Rect); break;
            case Key.O: SelectTool(Tool.Ellipse); break;
            case Key.I: SelectTool(Tool.Picker); break;
            // + / − statt [ / ]: auf deutschen Tastaturen liegen die Klammern auf AltGr.
            case Key.OemMinus or Key.Subtract: SizeSlider.Value = Math.Max(SizeSlider.Minimum, SizeSlider.Value - (SizeSlider.Value > 10 ? 5 : 1)); break;
            case Key.OemPlus or Key.Add: SizeSlider.Value = Math.Min(SizeSlider.Maximum, SizeSlider.Value + (SizeSlider.Value >= 10 ? 5 : 1)); break;
            default: return;
        }
        e.Handled = true;
    }

    // ===================== Anzeige =====================

    /// <summary>Zeigt genau das, was die LEDs zeigen würden: alle Ebenen zusammengesetzt, durchsichtig = schwarz.</summary>
    private void RefreshImage()
    {
        _frame = CustomEffectRenderer.Render(_doc, 0, _frame);
        int w = _frame.Width, h = _frame.Height;
        var rgb = _frame.Rgb;
        for (int i = 0; i < w * h; i++)
        {
            int r = (int)Math.Round(Math.Clamp(rgb[i * 3], 0, 1) * 255);
            int g = (int)Math.Round(Math.Clamp(rgb[i * 3 + 1], 0, 1) * 255);
            int b = (int)Math.Round(Math.Clamp(rgb[i * 3 + 2], 0, 1) * 255);
            _bitmapPixels[i] = unchecked((int)0xFF000000) | (r << 16) | (g << 8) | b;
        }
        _bitmap.WritePixels(new Int32Rect(0, 0, w, h), _bitmapPixels, w * 4, 0);
    }

    private void OnOverlayOptionChanged(object sender, RoutedEventArgs e)
    {
        if (!IsInitialized) return;
        GridOverlay.Visibility = ShowGridCheck.IsChecked == true ? Visibility.Visible : Visibility.Collapsed;
        RefreshDeviceOverlay();
    }

    /// <summary>Geräte des gewählten Effekt-Fensters an ihrer echten Position: Umriss und LED-Punkte,
    /// durchsichtig, damit man die Farbe darunter sieht. Die Fläche des Effekt-Fensters wird auf das
    /// Raster skaliert — genau so tastet die Engine den Effekt ab.</summary>
    private void RefreshDeviceOverlay()
    {
        if (DeviceOverlay is null || _doc is null) return;
        DeviceOverlay.Children.Clear();
        if (ShowDevicesCheck.IsChecked != true || SceneCombo.SelectedItem is not EffectScene scene) return;

        double sx = SurfaceRoot.Width / Math.Max(1, scene.CanvasWidth), sy = SurfaceRoot.Height / Math.Max(1, scene.CanvasHeight);
        var outline = new SolidColorBrush(Color.FromArgb(0xA0, 0xFF, 0xFF, 0xFF));
        var ledStroke = new SolidColorBrush(Color.FromArgb(0xE0, 0xFF, 0xFF, 0xFF));
        var ledFill = new SolidColorBrush(Color.FromArgb(0x30, 0x00, 0x00, 0x00));
        var labelBrush = new SolidColorBrush(Color.FromArgb(0xD0, 0xFF, 0xFF, 0xFF));

        foreach (var item in scene.Items)
        {
            var shape = _shapes.Get(item.DeviceId, item.ZoneIndex, item.LedCount);
            double rad = item.RotationDegrees * Math.PI / 180, cos = Math.Cos(rad), sin = Math.Sin(rad);
            var corners = new PointCollection();
            foreach (var (lx, ly) in new[] { (-0.5, -0.5), (0.5, -0.5), (0.5, 0.5), (-0.5, 0.5) })
            {
                double x = lx * item.Width, y = ly * item.Height;
                corners.Add(new Point((item.CenterX + x * cos - y * sin) * sx, (item.CenterY + x * sin + y * cos) * sy));
            }
            DeviceOverlay.Children.Add(new Polygon
            {
                Points = corners, Stroke = outline, StrokeThickness = 1.5, StrokeDashArray = new DoubleCollection { 4, 3 }
            });

            foreach (var (x, y) in item.ComputeLedPositions(shape))
            {
                const double d = 9;
                var dot = new Ellipse { Width = d, Height = d, Stroke = ledStroke, StrokeThickness = 1.2, Fill = ledFill };
                Canvas.SetLeft(dot, x * sx - d / 2);
                Canvas.SetTop(dot, y * sy - d / 2);
                DeviceOverlay.Children.Add(dot);
            }

            var label = new TextBlock { Text = item.DisplayName, FontSize = 16, Foreground = labelBrush };
            Canvas.SetLeft(label, corners.Min(p => p.X));
            Canvas.SetTop(label, Math.Max(0, corners.Min(p => p.Y) - 22));
            DeviceOverlay.Children.Add(label);
        }
    }

    /// <summary>Raster im Zellabstand, jede 10. Linie etwas kräftiger.</summary>
    private static Brush CreateGridBrush()
    {
        var minor = new Pen(new SolidColorBrush(Color.FromArgb(0x22, 0xFF, 0xFF, 0xFF)), 0.6);
        var major = new Pen(new SolidColorBrush(Color.FromArgb(0x45, 0xFF, 0xFF, 0xFF)), 1);
        var group = new DrawingGroup();
        double tile = CellUnits * 10;
        for (int i = 1; i < 10; i++)
        {
            group.Children.Add(new GeometryDrawing(null, minor, new LineGeometry(new Point(i * CellUnits, 0), new Point(i * CellUnits, tile))));
            group.Children.Add(new GeometryDrawing(null, minor, new LineGeometry(new Point(0, i * CellUnits), new Point(tile, i * CellUnits))));
        }
        group.Children.Add(new GeometryDrawing(null, major, new LineGeometry(new Point(0, 0), new Point(tile, 0))));
        group.Children.Add(new GeometryDrawing(null, major, new LineGeometry(new Point(0, 0), new Point(0, tile))));
        var brush = new DrawingBrush(group)
        {
            TileMode = TileMode.Tile,
            Viewport = new Rect(0, 0, tile, tile), ViewportUnits = BrushMappingMode.Absolute,
            Viewbox = new Rect(0, 0, tile, tile), ViewboxUnits = BrushMappingMode.Absolute
        };
        brush.Freeze();
        return brush;
    }

    // ===================== Effekt-Fenster / Live =====================

    private void OnSceneChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_suppress) return;
        RefreshDeviceOverlay();
        if (LiveCheck.IsChecked == true) StartLive();
    }

    private void OnLiveChanged(object sender, RoutedEventArgs e)
    {
        if (!IsInitialized) return;
        if (LiveCheck.IsChecked == true) StartLive(); else StopLive();
    }

    private void StartLive()
    {
        if (SceneCombo.SelectedItem is not EffectScene scene) return;
        if (_liveScene is not null && _liveScene != scene) _liveScene.SetLiveOverride(null);
        _liveScene = scene;
        PushLive();
        _liveTimer.Start();
        StatusText.Text = scene.IsRunning
            ? $"Live: „{scene.Name}“ zeigt jetzt, was du malst."
            : $"Live: „{scene.Name}“ ist pausiert — an den LEDs kommt erst etwas an, wenn das Effekt-Fenster läuft.";
        LogMessage?.Invoke($"Effekt-Editor: Live-Vorschau in '{scene.Name}' an.", LogLevel.Debug);
    }

    private void StopLive()
    {
        _liveTimer.Stop();
        if (_liveScene is null) return;
        _liveScene.SetLiveOverride(null);
        LogMessage?.Invoke($"Effekt-Editor: Live-Vorschau in '{_liveScene.Name}' aus.", LogLevel.Debug);
        _liveScene = null;
    }

    /// <summary>Höchstens alle 70 ms eine unveränderliche Kopie an das Effekt-Fenster geben — Engine und
    /// MSI-Thread lesen sie gleichzeitig, das Dokument hier wird weiter bemalt.</summary>
    private void PushLive()
    {
        _liveDirty = false;
        if (_liveScene is null) return;
        try
        {
            var snapshot = _doc.Clone();
            _liveScene.SetLiveOverride(new CustomCanvasEffect(snapshot));
        }
        catch (InvalidDataException ex)
        {
            StatusText.Text = "Live-Vorschau: " + ex.Message;
        }
    }

    private void OnUseInSceneClick(object sender, RoutedEventArgs e)
    {
        if (SceneCombo.SelectedItem is not EffectScene scene) return;
        if ((_modified || _isNew) && !Save()) return;
        UseInScene?.Invoke(scene, _doc.CatalogId);
        StatusText.Text = $"„{scene.Name}“ zeigt jetzt „{_doc.Name}“.";
    }
}
