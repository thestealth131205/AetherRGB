using System.Globalization;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Shapes;
using AetherRGB.Core.Devices;
using AetherRGB.Core.Effects.Custom;

namespace AetherRGB.App;

/// <summary>Effekt-Editor, Teil „Ebenen &amp; Animation“: Ebenen-Liste (Mal- und Form-Ebenen), Werte der
/// gewählten Ebene mit Keyframes, Animations-Vorlagen (Wandern, Ausdehnender Kreis, Zusammenziehen,
/// Pulsieren, Drehen, Atmen/Blinken, Farbe wechseln), Zeit-Leiste mit Keyframe-Spuren und ein gemeinsamer
/// Rückgängig-Verlauf für alles, was nicht gemalt ist. Die Logik dahinter liegt testbar in Core
/// (<see cref="LayerAnimator"/>, <see cref="KeyframeTools"/>, <see cref="DocumentStructureSnapshot"/>).</summary>
public partial class EffectEditorWindow
{
    /// <summary>Gewählte Ebene (Mal- oder Form-Ebene). <see cref="_layer"/> ist immer eine Mal-Ebene: die
    /// gewählte oder, wenn eine Form gewählt ist, die zuletzt gewählte — deren Frames zeigt die Zeitleiste.</summary>
    private EffectLayer _selLayer = null!;
    private LayerAnimation? _selAnim;

    /// <summary>Angezeigter Zeitpunkt auf der Zeitleiste (Sekunden).</summary>
    private double _time;

    /// <summary>Bearbeiten-Ansicht: die gewählte Ebene liegt ohne Transformation und Animation da (beim Malen
    /// und Verschieben einer Form). Aus, sobald ein Zeitpunkt gewählt wird — dann zeigt die Fläche alles animiert.</summary>
    private bool _editView = true;

    private bool _layerTab, _pickCenter, _draggingShape, _scrubbing, _keyDragMoved;
    private (AnimatedValue Value, Keyframe Key)? _selKey;
    private (AnimatedValue Value, Keyframe Key)? _keyDrag;
    private double _keyDragStartX;
    private Rectangle? _playhead;

    private readonly List<(DocumentStructureSnapshot Snapshot, long Sequence)> _structUndo = new(), _structRedo = new();
    private const int MaxStructureSteps = 200;

    private sealed record ValueRow(AnimatedValue Value, string Name, TextBox Box, Button Key, double Factor);
    private readonly List<ValueRow> _valueRows = new();

    private const double KeyRowHeight = 18;

    /// <summary>Anzeige der animierbaren Werte: Beschriftung, Faktor zur Anzeige, Einheit, Erklärung.</summary>
    private static readonly Dictionary<string, (string Label, double Factor, string Unit, string Tip)> ValueInfo = new()
    {
        [nameof(EffectLayer.Opacity)] = ("Deckkraft", 100, "%", "0 % = unsichtbar"),
        [nameof(EffectLayer.OffsetX)] = ("Versatz X", 100, "%", "Verschiebung in Anteilen der Flächenbreite (100 % = einmal über die Fläche)"),
        [nameof(EffectLayer.OffsetY)] = ("Versatz Y", 100, "%", "Verschiebung in Anteilen der Flächenhöhe"),
        [nameof(EffectLayer.Scale)] = ("Größe", 100, "%", "Skalierung um den Drehpunkt (Mitte der Fläche)"),
        [nameof(EffectLayer.Rotation)] = ("Drehung", 1, "°", "Im Uhrzeigersinn um die Mitte der Fläche"),
        [nameof(EffectLayer.HueShift)] = ("Farbton", 1, "°", "Farbton-Verschiebung, 120° = Rot wird Grün"),
        [nameof(ShapeLayer.CenterX)] = ("Mitte X", 100, "%", "Mittelpunkt der Form, Anteil der Flächenbreite"),
        [nameof(ShapeLayer.CenterY)] = ("Mitte Y", 100, "%", "Mittelpunkt der Form, Anteil der Flächenhöhe"),
        [nameof(ShapeLayer.Radius)] = ("Radius", 100, "%", "Anteil der Flächenhöhe (50 % = so hoch wie die Fläche)"),
        [nameof(ShapeLayer.Width)] = ("Breite", 100, "%", "Anteil der Flächenbreite"),
        [nameof(ShapeLayer.Height)] = ("Höhe", 100, "%", "Anteil der Flächenhöhe"),
        [nameof(ShapeLayer.Thickness)] = ("Dicke", 100, "%", "Ring- bzw. Rahmendicke, Anteil der Flächenhöhe"),
        [nameof(ShapeLayer.Softness)] = ("Weiche Kante", 100, "%", "Breite der weichen Kante, Anteil der Flächenhöhe"),
    };

    private static readonly (Easing Value, string Name)[] EasingNames =
    {
        (Easing.Linear, "Gleichmäßig"), (Easing.EaseIn, "Weich anfahren"), (Easing.EaseOut, "Weich abbremsen"),
        (Easing.EaseInOut, "Weich an und ab"), (Easing.Spring, "Federnd"), (Easing.Hold, "Stufen (springen)")
    };

    private void InitAnimationUi()
    {
        KeyTrack.MouseRightButtonUp += (_, e) => e.Handled = true;
    }

    // ===================== Reiter =====================

    private void OnToolTabClick(object sender, RoutedEventArgs e) => SetTab(layerTab: false);
    private void OnLayerTabClick(object sender, RoutedEventArgs e) => SetTab(layerTab: true);

    private void SetTab(bool layerTab)
    {
        _layerTab = layerTab;
        ToolPanel.Visibility = layerTab ? Visibility.Collapsed : Visibility.Visible;
        LayerPanel.Visibility = layerTab ? Visibility.Visible : Visibility.Collapsed;
        ToolTabButton.Tag = layerTab ? null : "Active";
        LayerTabButton.Tag = layerTab ? "Active" : null;
        if (!layerTab) CancelPickCenter();
        UpdateCursor(null);
        RefreshAnimOverlay();
    }

    // ===================== Ebenen =====================

    /// <summary>Alles neu aufbauen, was von der gewählten Ebene abhängt.</summary>
    private void RebuildLayerUi()
    {
        RebuildLayerList();
        RebuildLayerProps();
        RebuildAnimList(rebuildProps: true);
        UpdateTimeRow();
        RebuildKeyTrack();
        UpdateFramesPanel();
        RefreshAnimOverlay();
    }

    private static string LayerIcon(EffectLayer layer) => layer switch
    {
        ShapeLayer { Kind: ShapeKind.Ring } => "◯",
        ShapeLayer { Kind: ShapeKind.Rectangle } => "▭",
        ShapeLayer => "●",
        _ => "🖌"
    };

    private void RebuildLayerList()
    {
        _suppress = true;
        LayerList.Items.Clear();
        for (int i = _doc.Layers.Count - 1; i >= 0; i--)
        {
            var layer = _doc.Layers[i];
            var visible = new CheckBox
            {
                IsChecked = layer.Visible, VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(0, 0, 6, 0),
                Focusable = false, ToolTip = "Ebene ein-/ausblenden"
            };
            visible.Click += (_, _) =>
            {
                bool show = visible.IsChecked == true;
                PushStructureUndo(show ? "Ebene einblenden" : "Ebene ausblenden");
                layer.Visible = show;
                AfterStructureChangeDeferred();
            };
            int animations = layer.Animations.Count(a => a.Enabled);
            var info = new TextBlock
            {
                Style = (Style)FindResource("MutedText"), FontSize = 11,
                Text = (layer is PaintLayer p ? (p.Frames.Count == 1 ? "1 Frame" : $"{p.Frames.Count} Frames") : "Form") +
                       (animations > 0 ? $" · {animations} Animation{(animations == 1 ? "" : "en")}" : "") +
                       (layer.Blend != LayerBlendMode.Normal ? " · " + BlendName(layer.Blend) : "")
            };
            var text = new StackPanel
            {
                Children =
                {
                    new TextBlock { Text = $"{LayerIcon(layer)}  {layer.Name}", TextTrimming = TextTrimming.CharacterEllipsis },
                    info
                }
            };
            var row = new DockPanel { Margin = new Thickness(8, 5, 8, 5), Tag = layer, Background = Brushes.Transparent };
            DockPanel.SetDock(visible, Dock.Left);
            row.Children.Add(visible);
            row.Children.Add(text);
            LayerList.Items.Add(row);
        }
        LayerList.SelectedIndex = _doc.Layers.Count - 1 - _doc.Layers.IndexOf(_selLayer);
        _suppress = false;
    }

    private void OnLayerSelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_suppress || (LayerList.SelectedItem as FrameworkElement)?.Tag is not EffectLayer layer || layer == _selLayer) return;
        SelectLayer(layer);
    }

    private void SelectLayer(EffectLayer layer)
    {
        if (_stroke is not null) return;
        StopPlayback();
        _selLayer = layer;
        _selAnim = layer.Animations.FirstOrDefault();
        _selKey = null;
        _pickCenter = false;
        _editView = true;
        if (layer is PaintLayer paint && paint != _layer)
        {
            _layer = paint;
            _frameIndex = Math.Clamp(CustomEffectRenderer.PickFrames(paint, _time).Current, 0, paint.Frames.Count - 1);
            AttachFrame();
            _suppress = true;
            FrameDurationBox.Text = FormatMs(paint.FrameDurationSeconds);
            CrossFadeCheck.IsChecked = paint.CrossFade;
            _suppress = false;
            RebuildTimeline();
        }
        RebuildLayerList();
        RebuildLayerProps();
        RebuildAnimList(rebuildProps: true);
        RebuildKeyTrack();
        UpdateFramesPanel();
        UpdateFrameInfo();
        UpdateUndoButtons();
        RefreshImage();
        RefreshOnion();
        StatusText.Text = layer is ShapeLayer
            ? $"Form „{layer.Name}“ gewählt — auf der Fläche ziehen verschiebt sie."
            : $"Mal-Ebene „{layer.Name}“ gewählt — Malen wirkt auf diese Ebene.";
    }

    private void UpdateFramesPanel()
    {
        bool paint = _selLayer is PaintLayer;
        FramesPanel.Visibility = paint ? Visibility.Visible : Visibility.Collapsed;
        FramesHint.Visibility = paint ? Visibility.Collapsed : Visibility.Visible;
    }

    private string UniqueLayerName(string baseName)
    {
        var names = _doc.Layers.Select(l => l.Name).ToHashSet(StringComparer.CurrentCultureIgnoreCase);
        int n = 1;
        while (names.Contains($"{baseName} {n}")) n++;
        return $"{baseName} {n}";
    }

    private bool CanAddLayer()
    {
        if (_doc.Layers.Count < CustomEffectSerializer.MaxLayers) return true;
        StatusText.Text = $"Höchstens {CustomEffectSerializer.MaxLayers} Ebenen.";
        return false;
    }

    /// <summary>Neue Ebene direkt über der gewählten einfügen und wählen.</summary>
    private void InsertLayer(EffectLayer layer, string description)
    {
        PushStructureUndo(description);
        int index = _doc.Layers.IndexOf(_selLayer) + 1;
        _doc.Layers.Insert(Math.Clamp(index, 0, _doc.Layers.Count), layer);
        Flipbook.SyncDuration(_doc);
        MarkModified();
        SelectLayer(layer);
        UpdateTimeRow();
    }

    private void OnAddPaintLayerClick(object sender, RoutedEventArgs e)
    {
        if (!CanAddLayer()) return;
        InsertLayer(new PaintLayer { Name = UniqueLayerName("Malen"), Frames = { PaintFrame.CreateEmpty(_doc.GridWidth, _doc.GridHeight) } }, "Mal-Ebene hinzufügen");
        StatusText.Text = "Neue Mal-Ebene — im Reiter „Werkzeug“ den Pinsel wählen und malen.";
    }

    private void OnAddShapeClick(object sender, RoutedEventArgs e)
    {
        var menu = new ContextMenu { PlacementTarget = AddShapeButton, Placement = System.Windows.Controls.Primitives.PlacementMode.Bottom };
        void Item(string header, Action action)
        {
            var item = new MenuItem { Header = header };
            item.Click += (_, _) => action();
            menu.Items.Add(item);
        }
        Item("●  Kreis", () => AddShapeLayer(ShapeKind.Circle, wave: false));
        Item("◯  Ring", () => AddShapeLayer(ShapeKind.Ring, wave: false));
        Item("▭  Rechteck", () => AddShapeLayer(ShapeKind.Rectangle, wave: false));
        menu.Items.Add(new Separator());
        Item("◎  Kreis-Welle (Ring, der sich ausdehnt)", () => AddShapeLayer(ShapeKind.Ring, wave: true));
        menu.IsOpen = true;
    }

    private void AddShapeLayer(ShapeKind kind, bool wave)
    {
        if (!CanAddLayer()) return;
        var shape = new ShapeLayer
        {
            Kind = kind,
            Name = UniqueLayerName(wave ? "Kreis-Welle" : kind switch { ShapeKind.Ring => "Ring", ShapeKind.Rectangle => "Rechteck", _ => "Kreis" }),
            Color = _color.ToHex(),
            Radius = wave ? 0.6 : 0.25,
            Width = 0.3,
            Height = 0.5,
            Thickness = wave ? 0.08 : 0.06,
            Softness = 0.03,
            Blend = wave ? LayerBlendMode.Add : LayerBlendMode.Normal
        };
        if (wave)
        {
            var expand = LayerAnimation.CreatePreset(AnimationKind.Expand);
            shape.Animations.Add(expand);
        }
        InsertLayer(shape, wave ? "Kreis-Welle hinzufügen" : "Form hinzufügen");
        if (wave)
        {
            StartPickCenter();
            StatusText.Text = "Kreis-Welle angelegt — auf die Fläche klicken, um ihren Mittelpunkt zu setzen (Esc = Mitte lassen).";
        }
        else StatusText.Text = $"„{shape.Name}“ angelegt in der Pinselfarbe — auf der Fläche ziehen verschiebt die Form.";
    }

    private void OnLayerUpClick(object sender, RoutedEventArgs e) => MoveLayer(1);
    private void OnLayerDownClick(object sender, RoutedEventArgs e) => MoveLayer(-1);

    private void MoveLayer(int delta)
    {
        int from = _doc.Layers.IndexOf(_selLayer), to = from + delta;
        if (from < 0 || to < 0 || to >= _doc.Layers.Count) return;
        PushStructureUndo(delta > 0 ? "Ebene nach vorn" : "Ebene nach hinten");
        _doc.Layers.RemoveAt(from);
        _doc.Layers.Insert(to, _selLayer);
        AfterStructureChange();
    }

    private void OnDuplicateLayerClick(object sender, RoutedEventArgs e)
    {
        if (!CanAddLayer()) return;
        int index = _doc.Layers.IndexOf(_selLayer);
        if (index < 0) return;
        // Über das Dateiformat kopieren: tiefe Kopie inklusive Pixel, Keyframes und Animationen.
        var copy = _doc.Clone().Layers[index];
        copy.Name = _selLayer.Name + " (Kopie)";
        InsertLayer(copy, "Ebene duplizieren");
    }

    private void OnDeleteLayerClick(object sender, RoutedEventArgs e)
    {
        if (_doc.Layers.Count <= 1)
        {
            StatusText.Text = "Die einzige Ebene kann nicht gelöscht werden.";
            return;
        }
        if (_selLayer is PaintLayer && _doc.Layers.OfType<PaintLayer>().Count() == 1)
        {
            StatusText.Text = "Die letzte Mal-Ebene bleibt erhalten — zum Leeren im Reiter „Werkzeug“ auf „Leeren“ klicken.";
            return;
        }
        PushStructureUndo("Ebene löschen");
        int index = _doc.Layers.IndexOf(_selLayer);
        var removed = _selLayer;
        _doc.Layers.RemoveAt(index);
        // Rückgängig-Verläufe der Frames bleiben, falls die Ebene per Strg+Z zurückkommt.
        if (removed == _layer)
        {
            _layer = _doc.Layers.OfType<PaintLayer>().Last();
            _frameIndex = 0;
            AttachFrame();
            RebuildTimeline();
        }
        _selLayer = _doc.Layers[Math.Clamp(index - 1, 0, _doc.Layers.Count - 1)];
        _selAnim = _selLayer.Animations.FirstOrDefault();
        Flipbook.SyncDuration(_doc);
        MarkModified();
        RebuildLayerUi();
        UpdateFrameInfo();
        RefreshImage();
        UpdateUndoButtons();
        StatusText.Text = $"Ebene „{removed.Name}“ gelöscht (Strg+Z holt sie zurück).";
    }

    /// <summary>Wie <see cref="AfterStructureChange"/>, aber erst nach dem laufenden Klick/Fokuswechsel — für
    /// Steuerelemente in den Listen, die dabei neu aufgebaut werden.</summary>
    private void AfterStructureChangeDeferred() => Dispatcher.BeginInvoke(AfterStructureChange);

    /// <summary>Nach jeder Änderung an Ebenen, Werten, Animationen oder Keyframes.</summary>
    private void AfterStructureChange()
    {
        Flipbook.SyncDuration(_doc);
        if (_time > _doc.DurationSeconds) _time = _doc.DurationSeconds;
        MarkModified();
        RebuildLayerList();
        RebuildAnimList(rebuildProps: false);
        RefreshValueRows();
        UpdateTimeRow();
        RebuildKeyTrack();
        UpdateFrameInfo();
        RefreshImage();
        UpdateUndoButtons();
    }

    private static string BlendName(LayerBlendMode mode) => mode switch
    {
        LayerBlendMode.Add => "Addieren",
        LayerBlendMode.Multiply => "Multiplizieren",
        LayerBlendMode.Screen => "Aufhellen",
        _ => "Normal"
    };

    // ----- Eigenschaften der Ebene -----

    private void RebuildLayerProps()
    {
        LayerProps.Children.Clear();
        _valueRows.Clear();
        var layer = _selLayer;

        LayerProps.Children.Add(Label("Name"));
        var name = new TextBox { Text = layer.Name };
        void CommitName()
        {
            var text = string.IsNullOrWhiteSpace(name.Text) ? layer.Name : name.Text.Trim();
            if (text.Length > 100) text = text[..100];
            if (text == layer.Name) { name.Text = text; return; }
            PushStructureUndo("Ebene umbenennen");
            layer.Name = text;
            AfterStructureChangeDeferred();
        }
        name.LostFocus += (_, _) => CommitName();
        name.KeyDown += (_, e) => { if (e.Key == Key.Enter) CommitName(); };
        LayerProps.Children.Add(name);

        LayerProps.Children.Add(Label("Mischen mit den Ebenen darunter"));
        var blend = new ComboBox { ToolTip = "Addieren: Licht überlagert sich (heller). Multiplizieren: färbt darunter ein. Aufhellen: heller ohne Übersteuern." };
        foreach (var mode in Enum.GetValues<LayerBlendMode>()) blend.Items.Add(new ComboBoxItem { Content = BlendName(mode), Tag = mode });
        blend.SelectedIndex = (int)layer.Blend;
        blend.SelectionChanged += (_, _) =>
        {
            if ((blend.SelectedItem as ComboBoxItem)?.Tag is not LayerBlendMode mode || mode == layer.Blend) return;
            PushStructureUndo("Mischmodus ändern");
            layer.Blend = mode;
            AfterStructureChange();
        };
        LayerProps.Children.Add(blend);

        if (layer is ShapeLayer shape)
        {
            LayerProps.Children.Add(Label("Form"));
            var kind = new ComboBox();
            kind.Items.Add("Kreis");
            kind.Items.Add("Ring");
            kind.Items.Add("Rechteck");
            kind.SelectedIndex = (int)shape.Kind;
            kind.SelectionChanged += (_, _) =>
            {
                var k = (ShapeKind)kind.SelectedIndex;
                if (k == shape.Kind) return;
                PushStructureUndo("Form ändern");
                shape.Kind = k;
                AfterStructureChange();
                Dispatcher.BeginInvoke(RebuildLayerProps);
            };
            LayerProps.Children.Add(kind);

            LayerProps.Children.Add(Label("Farbe"));
            LayerProps.Children.Add(BuildShapeColorRow(shape));
            if (shape.Kind == ShapeKind.Rectangle)
            {
                var filled = new CheckBox { Content = "Gefüllt", IsChecked = shape.Filled, Margin = new Thickness(0, 8, 0, 0) };
                filled.Click += (_, _) =>
                {
                    PushStructureUndo("Füllung ändern");
                    shape.Filled = filled.IsChecked == true;
                    AfterStructureChange();
                    Dispatcher.BeginInvoke(RebuildLayerProps);
                };
                LayerProps.Children.Add(filled);
            }
        }

        var heading = Label("Werte  ·  ◆ = Keyframe beim aktuellen Zeitpunkt");
        heading.ToolTip = "Ist ein Wert animiert (hat Keyframes), legt jede Änderung am aktuellen Zeitpunkt einen Keyframe an.";
        LayerProps.Children.Add(heading);
        foreach (var (valueName, value) in layer.AnimatedValues())
            if (ShowValue(layer, valueName)) LayerProps.Children.Add(BuildValueRow(valueName, value));

        var wrap = new WrapPanel { Margin = new Thickness(0, 6, 0, 0) };
        wrap.Children.Add(BuildLayerCheck("Umlaufend ↔", () => layer.WrapX, v => layer.WrapX = v,
            "Was rechts hinausläuft, kommt links wieder herein (für Versatz/Keyframes; „Wandern“ hat eine eigene Einstellung)"));
        wrap.Children.Add(BuildLayerCheck("Umlaufend ↕", () => layer.WrapY, v => layer.WrapY = v, "Was unten hinausläuft, kommt oben wieder herein"));
        LayerProps.Children.Add(wrap);
        RefreshValueRows();
    }

    private static bool ShowValue(EffectLayer layer, string name)
    {
        if (layer is not ShapeLayer shape) return ValueInfo.ContainsKey(name);
        return name switch
        {
            nameof(ShapeLayer.Radius) => shape.Kind is ShapeKind.Circle or ShapeKind.Ring,
            nameof(ShapeLayer.Width) or nameof(ShapeLayer.Height) => shape.Kind == ShapeKind.Rectangle,
            nameof(ShapeLayer.Thickness) => shape.Kind == ShapeKind.Ring || (shape.Kind == ShapeKind.Rectangle && !shape.Filled),
            _ => ValueInfo.ContainsKey(name)
        };
    }

    private TextBlock Label(string text) => new() { Text = text, Style = (Style)FindResource("PanelLabel") };

    private CheckBox BuildLayerCheck(string text, Func<bool> get, Action<bool> set, string tip)
    {
        var check = new CheckBox { Content = text, IsChecked = get(), Margin = new Thickness(0, 0, 14, 0), ToolTip = tip };
        check.Click += (_, _) =>
        {
            PushStructureUndo(text);
            set(check.IsChecked == true);
            AfterStructureChange();
        };
        return check;
    }

    private FrameworkElement BuildShapeColorRow(ShapeLayer shape)
    {
        var swatch = new Border
        {
            Width = 30, Height = 30, CornerRadius = new CornerRadius(6), BorderThickness = new Thickness(1), Margin = new Thickness(0, 0, 8, 0),
            BorderBrush = (Brush)FindResource("BrushBorder"), ToolTip = "Farbe der Form"
        };
        var hex = new TextBox { Width = 84, VerticalAlignment = VerticalAlignment.Center, ToolTip = "Hex-Farbe, z. B. #FF8800" };
        void Show()
        {
            hex.Text = shape.Color;
            try { swatch.Background = new SolidColorBrush(ToWpf(RgbColor.FromHex(shape.Color))); }
            catch (Exception e) when (e is FormatException or ArgumentException or OverflowException) { swatch.Background = Brushes.Black; }
        }
        void Apply(string text)
        {
            text = text.Trim();
            if (!text.StartsWith('#')) text = "#" + text;
            RgbColor color;
            try { color = RgbColor.FromHex(text); }
            catch (Exception e) when (e is FormatException or ArgumentException or OverflowException) { Show(); return; }
            if (string.Equals(color.ToHex(), shape.Color, StringComparison.OrdinalIgnoreCase)) { Show(); return; }
            PushStructureUndo("Farbe der Form ändern");
            shape.Color = color.ToHex();
            Show();
            AfterStructureChange();
        }
        hex.LostFocus += (_, _) => Apply(hex.Text);
        hex.KeyDown += (_, e) => { if (e.Key == Key.Enter) Apply(hex.Text); };
        var take = new Button { Content = "← Pinselfarbe", Margin = new Thickness(8, 0, 0, 0), ToolTip = "Aktuelle Pinselfarbe aus dem Reiter „Werkzeug“ übernehmen" };
        take.Click += (_, _) => Apply(_color.ToHex());
        Show();
        return new StackPanel { Orientation = Orientation.Horizontal, Children = { swatch, hex, take } };
    }

    private FrameworkElement BuildValueRow(string name, AnimatedValue value)
    {
        var (label, factor, unit, tip) = ValueInfo[name];
        var box = new TextBox { Width = 64, VerticalAlignment = VerticalAlignment.Center, TextAlignment = TextAlignment.Right, ToolTip = tip };
        var key = new Button
        {
            Style = (Style)FindResource("IconButton"), Width = 28, Height = 28, FontSize = 13, Margin = new Thickness(6, 0, 0, 0), Focusable = false
        };
        var row = new ValueRow(value, name, box, key, factor);
        _valueRows.Add(row);

        void Commit()
        {
            if (!TryParseNumber(box.Text, out double shown)) { RefreshValueRows(); return; }
            double v = shown / factor;
            if (Math.Abs(v - value.Evaluate(_time)) < 1e-9) { RefreshValueRows(); return; }
            bool newKey = value.IsAnimated && KeyframeTools.IndexAt(value, _time) < 0;
            PushStructureUndo($"{label} ändern");
            try { KeyframeTools.SetValue(value, _time, v); }
            catch (InvalidOperationException ex) { StatusText.Text = ex.Message; return; }
            AfterStructureChangeDeferred();
            if (newKey) StatusText.Text = $"{label}: Keyframe bei {_time:0.00} s angelegt.";
        }
        box.LostFocus += (_, _) => Commit();
        box.KeyDown += (_, e) => { if (e.Key == Key.Enter) Commit(); };
        key.Click += (_, _) =>
        {
            PushStructureUndo($"{label}: Keyframe");
            bool exists;
            try { exists = KeyframeTools.Toggle(value, _time); }
            catch (InvalidOperationException ex) { StatusText.Text = ex.Message; return; }
            _selKey = null;
            AfterStructureChange();
            StatusText.Text = exists
                ? $"{label}: Keyframe bei {_time:0.00} s gesetzt. Zu einem anderen Zeitpunkt gehen und den Wert ändern — dazwischen wird übergeblendet."
                : $"{label}: Keyframe bei {_time:0.00} s gelöscht.";
        };

        var panel = new DockPanel { Margin = new Thickness(0, 3, 0, 0) };
        DockPanel.SetDock(key, Dock.Right);
        var unitText = new TextBlock { Text = unit, Style = (Style)FindResource("MutedText"), Width = 16, Margin = new Thickness(4, 0, 0, 0), VerticalAlignment = VerticalAlignment.Center };
        DockPanel.SetDock(unitText, Dock.Right);
        DockPanel.SetDock(box, Dock.Right);
        panel.Children.Add(key);
        panel.Children.Add(unitText);
        panel.Children.Add(box);
        panel.Children.Add(new TextBlock { Text = label, VerticalAlignment = VerticalAlignment.Center, ToolTip = tip });
        return panel;
    }

    /// <summary>Werte der gewählten Ebene zum aktuellen Zeitpunkt anzeigen (nicht in einem Feld, in dem gerade getippt wird).</summary>
    private void RefreshValueRows()
    {
        var accent = (Brush)FindResource("BrushAccent");
        var muted = (Brush)FindResource("BrushTextMuted");
        var text = (Brush)FindResource("BrushText");
        foreach (var row in _valueRows)
        {
            if (!row.Box.IsKeyboardFocusWithin) row.Box.Text = FormatNumber(row.Value.Evaluate(_time) * row.Factor);
            bool keyed = KeyframeTools.IndexAt(row.Value, _time) >= 0;
            row.Key.Content = keyed ? "◆" : "◇";
            row.Key.Foreground = keyed ? accent : row.Value.IsAnimated ? text : muted;
            row.Key.ToolTip = keyed
                ? $"Keyframe bei {_time:0.00} s löschen"
                : $"Keyframe bei {_time:0.00} s setzen" + (row.Value.IsAnimated ? "" : " — macht den Wert animierbar");
        }
    }

    private static string FormatNumber(double v) => v.ToString("0.##", CultureInfo.CurrentCulture);

    /// <summary>Zahl mit Komma oder Punkt.</summary>
    private static bool TryParseNumber(string text, out double value) =>
        double.TryParse(text.Trim().Replace(',', '.'), NumberStyles.Float, CultureInfo.InvariantCulture, out value) && double.IsFinite(value);

    // ===================== Animationen =====================

    private void RebuildAnimList(bool rebuildProps)
    {
        _suppress = true;
        AnimList.Items.Clear();
        if (_selAnim is not null && !_selLayer.Animations.Contains(_selAnim)) _selAnim = _selLayer.Animations.FirstOrDefault();
        foreach (var animation in _selLayer.Animations)
        {
            var a = animation;
            var enabled = new CheckBox { IsChecked = a.Enabled, VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(0, 0, 6, 0), Focusable = false, ToolTip = "Animation an/aus" };
            enabled.Click += (_, _) =>
            {
                PushStructureUndo(enabled.IsChecked == true ? "Animation einschalten" : "Animation ausschalten");
                a.Enabled = enabled.IsChecked == true;
                AfterStructureChangeDeferred();
            };
            var row = new DockPanel { Margin = new Thickness(8, 5, 8, 5), Tag = a, Background = Brushes.Transparent };
            DockPanel.SetDock(enabled, Dock.Left);
            row.Children.Add(enabled);
            row.Children.Add(new TextBlock { Text = a.Describe(), TextWrapping = TextWrapping.Wrap, VerticalAlignment = VerticalAlignment.Center });
            AnimList.Items.Add(row);
        }
        AnimList.SelectedIndex = _selAnim is null ? -1 : _selLayer.Animations.IndexOf(_selAnim);
        AnimList.Visibility = _selLayer.Animations.Count > 0 ? Visibility.Visible : Visibility.Collapsed;
        AnimHint.Visibility = _selLayer.Animations.Count > 0 ? Visibility.Collapsed : Visibility.Visible;
        AddAnimButton.IsEnabled = _selLayer.Animations.Count < CustomEffectSerializer.MaxAnimationsPerLayer;
        _suppress = false;
        if (rebuildProps) RebuildAnimProps();
        RefreshAnimOverlay();
    }

    private void OnAnimSelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_suppress || (AnimList.SelectedItem as FrameworkElement)?.Tag is not LayerAnimation a || a == _selAnim) return;
        _selAnim = a;
        CancelPickCenter();
        RebuildAnimProps();
        RefreshAnimOverlay();
    }

    private void OnAddAnimationClick(object sender, RoutedEventArgs e)
    {
        var menu = new ContextMenu { PlacementTarget = AddAnimButton, Placement = System.Windows.Controls.Primitives.PlacementMode.Bottom };
        void Item(string header, string tip, Func<LayerAnimation> create)
        {
            var item = new MenuItem { Header = header, ToolTip = tip };
            item.Click += (_, _) => AddAnimation(create());
            menu.Items.Add(item);
        }
        LayerAnimation Move(double angle)
        {
            var a = LayerAnimation.CreatePreset(AnimationKind.Move);
            a.AngleDegrees = angle;
            return a;
        }
        Item("→  Wandern nach rechts", "Ebene läuft nach rechts; was rechts hinausläuft, kommt links wieder herein", () => Move(0));
        Item("←  Wandern nach links", "", () => Move(180));
        Item("↑  Wandern nach oben", "", () => Move(270));
        Item("↓  Wandern nach unten", "", () => Move(90));
        Item("↗  Wandern diagonal / frei", "Richtung danach mit den Pfeilen oder als Winkel einstellen", () => Move(315));
        menu.Items.Add(new Separator());
        Item("◎  Ausdehnender Kreis", "Wächst vom Mittelpunkt nach außen und verblasst — auch als mehrere Wellen", () => LayerAnimation.CreatePreset(AnimationKind.Expand));
        Item("◉  Zusammenziehen", "Von außen zur Mitte", () => LayerAnimation.CreatePreset(AnimationKind.Contract));
        Item("♥  Pulsieren", "Größe schwingt weich zwischen zwei Werten", () => LayerAnimation.CreatePreset(AnimationKind.Pulse));
        Item("⟳  Drehen", "Dreht sich um einen Mittelpunkt", () => LayerAnimation.CreatePreset(AnimationKind.Rotate));
        Item("☀  Atmen", "Helligkeit schwingt weich", () => LayerAnimation.CreatePreset(AnimationKind.Breathe));
        Item("✦  Blinken", "Hart an und aus", () =>
        {
            var a = LayerAnimation.CreatePreset(AnimationKind.Breathe);
            a.Wave = AnimationWave.Square;
            a.MinOpacity = 0;
            a.DurationSeconds = 1;
            return a;
        });
        Item("🌈  Farbe wechseln", "Farbton läuft durch alle Farben", () => LayerAnimation.CreatePreset(AnimationKind.HueCycle));
        menu.Items.Add(new Separator());
        var wave = new MenuItem { Header = "◎  Kreis-Welle als neue Form-Ebene …", ToolTip = "Legt einen Ring an, der sich vom angeklickten Punkt ausdehnt" };
        wave.Click += (_, _) => AddShapeLayer(ShapeKind.Ring, wave: true);
        menu.Items.Add(wave);
        menu.IsOpen = true;
    }

    private void AddAnimation(LayerAnimation animation)
    {
        if (_selLayer.Animations.Count >= CustomEffectSerializer.MaxAnimationsPerLayer)
        {
            StatusText.Text = $"Höchstens {CustomEffectSerializer.MaxAnimationsPerLayer} Animationen pro Ebene.";
            return;
        }
        if (_selLayer is ShapeLayer shape)
        {
            // Bei Formen geht Größe/Drehung von der Mitte der Form aus.
            animation.CenterX = shape.CenterX.Evaluate(_time);
            animation.CenterY = shape.CenterY.Evaluate(_time);
        }
        PushStructureUndo($"Animation „{LayerAnimation.KindName(animation.Kind)}“ hinzufügen");
        _selLayer.Animations.Add(animation);
        _selAnim = animation;
        AfterStructureChange();
        RebuildAnimProps();
        // Gleich zeigen, wie es aussieht.
        StartPlayback();
        StatusText.Text = animation.HasCenter && _selLayer is PaintLayer
            ? $"„{LayerAnimation.KindName(animation.Kind)}“ läuft — über „⌖ Mittelpunkt setzen“ den Punkt auf der Fläche wählen."
            : $"„{LayerAnimation.KindName(animation.Kind)}“ läuft. Einstellungen unten, Klick auf die Fläche hält die Vorschau an.";
    }

    /// <summary>Einstellungen der gewählten Animation, je nach Art.</summary>
    private void RebuildAnimProps()
    {
        AnimProps.Children.Clear();
        var a = _selAnim;
        if (a is null) return;

        var header = new DockPanel { Margin = new Thickness(0, 4, 0, 2) };
        var remove = new Button { Content = "Entfernen", ToolTip = "Animation von der Ebene entfernen" };
        remove.Click += (_, _) =>
        {
            PushStructureUndo($"Animation „{LayerAnimation.KindName(a.Kind)}“ entfernen");
            int index = _selLayer.Animations.IndexOf(a);
            _selLayer.Animations.Remove(a);
            _selAnim = _selLayer.Animations.Count == 0 ? null : _selLayer.Animations[Math.Clamp(index, 0, _selLayer.Animations.Count - 1)];
            CancelPickCenter();
            AfterStructureChange();
            Dispatcher.BeginInvoke(RebuildAnimProps);
        };
        DockPanel.SetDock(remove, Dock.Right);
        header.Children.Add(remove);
        header.Children.Add(new TextBlock { Text = LayerAnimation.KindName(a.Kind), FontSize = 15, FontWeight = FontWeights.SemiBold, VerticalAlignment = VerticalAlignment.Center });
        AnimProps.Children.Add(header);

        string kindName = LayerAnimation.KindName(a.Kind);
        switch (a.Kind)
        {
            case AnimationKind.Move:
                AnimProps.Children.Add(Label("Richtung"));
                AnimProps.Children.Add(BuildDirectionPad(a));
                AddAnimNumber("Winkel", () => a.AngleDegrees, v => a.AngleDegrees = v, 1, "°", "0° = rechts, 90° = unten, 180° = links, 270° = oben", rebuild: true);
                AddAnimNumber("Strecke pro Durchlauf", () => a.Distance, v => a.Distance = v, 100, "%", "100 % = einmal über die ganze Fläche");
                AddAnimCheck("Umlaufend (rechts raus, links rein)", () => a.Wrap, v => a.Wrap = v,
                    "Aus: die Ebene läuft aus der Fläche hinaus und springt am Ende des Durchlaufs zurück");
                break;
            case AnimationKind.Expand or AnimationKind.Contract:
                AddAnimNumber("Größe am Anfang", () => a.StartSize, v => a.StartSize = v, 100, "%", "Bezogen auf die Originalgröße der Form bzw. des Bildes");
                AddAnimNumber("Größe am Ende", () => a.EndSize, v => a.EndSize = v, 100, "%");
                AddAnimCheck("Zum Ende hin ausblenden", () => a.FadeOut, v => a.FadeOut = v, "Damit jede Welle sanft verschwindet und die nächste ohne Sprung beginnt");
                AddAnimNumber("Wellen gleichzeitig", () => a.Waves, v => a.Waves = (int)Math.Round(v), 1, "", "1–8 Kopien, gleichmäßig über den Durchlauf verteilt (nicht bei „einmal“)");
                AddCenterControls(a);
                break;
            case AnimationKind.Pulse:
                AddAnimNumber("Kleinste Größe", () => a.StartSize, v => a.StartSize = v, 100, "%");
                AddAnimNumber("Größte Größe", () => a.EndSize, v => a.EndSize = v, 100, "%");
                AddCenterControls(a);
                break;
            case AnimationKind.Rotate:
                AddAnimNumber("Grad pro Durchlauf", () => Math.Abs(a.Degrees), v => a.Degrees = Math.Sign(a.Degrees == 0 ? 1 : a.Degrees) * Math.Abs(v), 1, "°", "360° = eine volle Umdrehung");
                AddAnimCheck("Gegen den Uhrzeigersinn", () => a.Degrees < 0, v => a.Degrees = v ? -Math.Abs(a.Degrees) : Math.Abs(a.Degrees));
                AddCenterControls(a);
                break;
            case AnimationKind.Breathe:
                AnimProps.Children.Add(Label("Kurve"));
                var wave = new ComboBox();
                wave.Items.Add("Weich (Atmen)");
                wave.Items.Add("Gleichmäßig auf und ab");
                wave.Items.Add("Hart an/aus (Blinken)");
                wave.SelectedIndex = (int)a.Wave;
                wave.SelectionChanged += (_, _) =>
                {
                    if (wave.SelectedIndex < 0 || (AnimationWave)wave.SelectedIndex == a.Wave) return;
                    PushStructureUndo($"{kindName}: Kurve");
                    a.Wave = (AnimationWave)wave.SelectedIndex;
                    AfterStructureChange();
                    Dispatcher.BeginInvoke(RebuildAnimProps);
                };
                AnimProps.Children.Add(wave);
                AddAnimNumber("Dunkelster Wert", () => a.MinOpacity, v => a.MinOpacity = v, 100, "%");
                AddAnimNumber("Hellster Wert", () => a.MaxOpacity, v => a.MaxOpacity = v, 100, "%");
                if (a.Wave == AnimationWave.Square)
                    AddAnimNumber("An-Anteil", () => a.DutyCycle, v => a.DutyCycle = v, 100, "%", "Wie lange die Ebene pro Durchlauf an ist (5–95 %)");
                break;
            case AnimationKind.HueCycle:
                AddAnimNumber("Farbton pro Durchlauf", () => a.Degrees, v => a.Degrees = v, 1, "°", "360° = einmal durch alle Farben, negativ = andersherum");
                break;
        }

        AnimProps.Children.Add(new Border { Height = 1, Background = (Brush)FindResource("BrushBorder"), Margin = new Thickness(0, 10, 0, 2) });
        AddAnimNumber("Dauer pro Durchlauf", () => a.DurationSeconds, v => a.DurationSeconds = v, 1, "s", "Bei 100 % Tempo im Effekt-Fenster");
        AddAnimNumber(a.Repeat == AnimationRepeat.Once ? "Startet nach" : "Versatz", () => a.DelaySeconds, v => a.DelaySeconds = v, 1, "s",
            "Schleife/Hin und zurück: verschiebt den Durchlauf (z. B. zwei Ebenen versetzt). Einmal: Wartezeit bis zum Start.");

        AnimProps.Children.Add(Label("Wiederholen"));
        var repeat = new ComboBox();
        repeat.Items.Add("Schleife");
        repeat.Items.Add("Hin und zurück");
        repeat.Items.Add("Einmal");
        repeat.SelectedIndex = (int)a.Repeat;
        repeat.SelectionChanged += (_, _) =>
        {
            if (repeat.SelectedIndex < 0 || (AnimationRepeat)repeat.SelectedIndex == a.Repeat) return;
            PushStructureUndo($"{kindName}: Wiederholen");
            a.Repeat = (AnimationRepeat)repeat.SelectedIndex;
            AfterStructureChange();
            Dispatcher.BeginInvoke(RebuildAnimProps);
        };
        AnimProps.Children.Add(repeat);

        AnimProps.Children.Add(Label("Verlauf"));
        var easing = new ComboBox { ToolTip = "Wie sich die Geschwindigkeit innerhalb eines Durchlaufs ändert" };
        foreach (var (_, n) in EasingNames) easing.Items.Add(n);
        easing.SelectedIndex = Array.FindIndex(EasingNames, x => x.Value == a.Easing);
        easing.SelectionChanged += (_, _) =>
        {
            if (easing.SelectedIndex < 0 || EasingNames[easing.SelectedIndex].Value == a.Easing) return;
            PushStructureUndo($"{kindName}: Verlauf");
            a.Easing = EasingNames[easing.SelectedIndex].Value;
            AfterStructureChange();
        };
        AnimProps.Children.Add(easing);
    }

    /// <summary>Acht Pfeile für die Richtung beim Wandern.</summary>
    private FrameworkElement BuildDirectionPad(LayerAnimation a)
    {
        var grid = new UniformGrid3();
        (string Arrow, double Angle)?[] cells =
        {
            ("↖", 225), ("↑", 270), ("↗", 315),
            ("←", 180), null, ("→", 0),
            ("↙", 135), ("↓", 90), ("↘", 45)
        };
        var tabStyle = (Style)FindResource("TabButton");
        for (int i = 0; i < cells.Length; i++)
        {
            if (cells[i] is not { } cell)
            {
                grid.Children.Add(new Border());
                continue;
            }
            bool active = Math.Abs(((a.AngleDegrees - cell.Angle) % 360 + 360) % 360) < 0.5;
            var button = new Button
            {
                Content = cell.Arrow, Style = tabStyle, Margin = new Thickness(0, 0, 4, 4), Padding = new Thickness(0), Height = 32, FontSize = 15,
                Tag = active ? "Active" : null
            };
            button.Click += (_, _) =>
            {
                PushStructureUndo("Wandern: Richtung");
                a.AngleDegrees = cell.Angle;
                AfterStructureChange();
                Dispatcher.BeginInvoke(RebuildAnimProps);
            };
            grid.Children.Add(button);
        }
        return grid;
    }

    /// <summary>3 × 3-Raster für das Richtungsfeld.</summary>
    private sealed class UniformGrid3 : System.Windows.Controls.Primitives.UniformGrid
    {
        public UniformGrid3()
        {
            Rows = 3;
            Columns = 3;
            Width = 132;
            HorizontalAlignment = HorizontalAlignment.Left;
        }
    }

    private void AddCenterControls(LayerAnimation a)
    {
        bool shape = _selLayer is ShapeLayer;
        // Bei Formen gilt für die Größe die Mitte der Form; Drehen hat einen eigenen Mittelpunkt.
        if (shape && a.Kind != AnimationKind.Rotate)
        {
            AnimProps.Children.Add(new TextBlock
            {
                Text = "Mittelpunkt = Mitte der Form (Form auf der Fläche ziehen oder unten „Mitte X/Y“).",
                Style = (Style)FindResource("MutedText"), FontSize = 11, TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 8, 0, 0)
            });
            return;
        }
        var row = new DockPanel { Margin = new Thickness(0, 8, 0, 0) };
        var button = new Button { Content = "⌖  Mittelpunkt setzen", ToolTip = "Danach auf die Fläche klicken (Esc bricht ab)" };
        button.Click += (_, _) => StartPickCenter();
        DockPanel.SetDock(button, Dock.Left);
        row.Children.Add(button);
        row.Children.Add(new TextBlock
        {
            Text = $"{a.CenterX * 100:0} % / {a.CenterY * 100:0} %", Style = (Style)FindResource("MutedText"),
            VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(10, 0, 0, 0)
        });
        AnimProps.Children.Add(row);
    }

    private void AddAnimNumber(string label, Func<double> get, Action<double> set, double factor, string unit, string? tip = null, bool rebuild = false)
    {
        var a = _selAnim!;
        var box = new TextBox { Width = 64, TextAlignment = TextAlignment.Right, VerticalAlignment = VerticalAlignment.Center, ToolTip = tip, Text = FormatNumber(get() * factor) };
        void Commit()
        {
            if (!TryParseNumber(box.Text, out double shown)) { box.Text = FormatNumber(get() * factor); return; }
            double v = shown / factor;
            if (Math.Abs(v - get()) < 1e-9) { box.Text = FormatNumber(get() * factor); return; }
            PushStructureUndo($"{LayerAnimation.KindName(a.Kind)}: {label}");
            set(v);
            a.Normalize();
            box.Text = FormatNumber(get() * factor);
            AfterStructureChange();
            if (rebuild) Dispatcher.BeginInvoke(RebuildAnimProps);
        }
        box.LostFocus += (_, _) => Commit();
        box.KeyDown += (_, e) => { if (e.Key == Key.Enter) Commit(); };
        var panel = new DockPanel { Margin = new Thickness(0, 6, 0, 0) };
        var unitText = new TextBlock { Text = unit, Style = (Style)FindResource("MutedText"), Width = 16, Margin = new Thickness(4, 0, 0, 0), VerticalAlignment = VerticalAlignment.Center };
        DockPanel.SetDock(unitText, Dock.Right);
        DockPanel.SetDock(box, Dock.Right);
        panel.Children.Add(unitText);
        panel.Children.Add(box);
        panel.Children.Add(new TextBlock { Text = label, VerticalAlignment = VerticalAlignment.Center, ToolTip = tip, TextWrapping = TextWrapping.Wrap });
        AnimProps.Children.Add(panel);
    }

    private void AddAnimCheck(string label, Func<bool> get, Action<bool> set, string? tip = null)
    {
        var a = _selAnim!;
        var check = new CheckBox { Content = label, IsChecked = get(), Margin = new Thickness(0, 8, 0, 0), ToolTip = tip };
        check.Click += (_, _) =>
        {
            PushStructureUndo($"{LayerAnimation.KindName(a.Kind)}: {label}");
            set(check.IsChecked == true);
            a.Normalize();
            AfterStructureChange();
        };
        AnimProps.Children.Add(check);
    }

    // ===================== Mittelpunkt / Formen auf der Fläche =====================

    private void StartPickCenter()
    {
        StopPlayback();
        _pickCenter = true;
        InputCanvas.Cursor = Cursors.Hand;
        UpdateCursor(null);
        StatusText.Text = "Mittelpunkt: auf die Fläche klicken (Esc oder Rechtsklick bricht ab).";
    }

    private void CancelPickCenter()
    {
        if (!_pickCenter) return;
        _pickCenter = false;
        InputCanvas.Cursor = Cursors.Cross;
        StatusText.Text = "";
    }

    private bool HandleAnimationMouseDown(Point cell, bool right)
    {
        if (_pickCenter)
        {
            if (right) { CancelPickCenter(); return true; }
            double u = cell.X / _doc.GridWidth, v = cell.Y / _doc.GridHeight;
            PushStructureUndo("Mittelpunkt setzen");
            if (_selLayer is ShapeLayer shape) MoveShapeTo(shape, u, v);
            else if (_selAnim is { HasCenter: true } a) { a.CenterX = u; a.CenterY = v; a.Normalize(); }
            CancelPickCenter();
            AfterStructureChange();
            RebuildAnimProps();
            StatusText.Text = $"Mittelpunkt bei {u * 100:0} % / {v * 100:0} %.";
            return true;
        }
        if (_selLayer is ShapeLayer s)
        {
            if (right) return true;
            PushStructureUndo("Form verschieben");
            _draggingShape = true;
            _editView = true;
            InputCanvas.CaptureMouse();
            MoveShapeTo(s, cell.X / _doc.GridWidth, cell.Y / _doc.GridHeight);
            return true;
        }
        return false;
    }

    private bool HandleAnimationMouseMove(Point cell)
    {
        if (!_draggingShape || _selLayer is not ShapeLayer shape) return false;
        MoveShapeTo(shape, cell.X / _doc.GridWidth, cell.Y / _doc.GridHeight);
        return true;
    }

    private bool HandleAnimationMouseUp()
    {
        if (!_draggingShape) return false;
        _draggingShape = false;
        InputCanvas.ReleaseMouseCapture();
        AfterStructureChange();
        RebuildAnimProps();
        return true;
    }

    /// <summary>Mitte der Form setzen (bei animierter Mitte als Keyframe) — Animationen mit Mittelpunkt ziehen mit.</summary>
    private void MoveShapeTo(ShapeLayer shape, double u, double v)
    {
        u = Math.Clamp(u, 0, 1);
        v = Math.Clamp(v, 0, 1);
        try
        {
            KeyframeTools.SetValue(shape.CenterX, _time, u);
            KeyframeTools.SetValue(shape.CenterY, _time, v);
        }
        catch (InvalidOperationException ex) { StatusText.Text = ex.Message; return; }
        foreach (var a in shape.Animations.Where(a => a.HasCenter))
        {
            a.CenterX = u;
            a.CenterY = v;
        }
        MarkModified();
        RefreshValueRows();
        RefreshImage();
    }

    /// <summary>Malen geht nur auf Mal-Ebenen.</summary>
    private bool EnsurePaintLayer()
    {
        if (_selLayer is PaintLayer) return true;
        StatusText.Text = "Gewählt ist eine Form-Ebene — zum Malen im Reiter „Ebenen & Animation“ eine Mal-Ebene wählen.";
        return false;
    }

    /// <summary>Vor dem Malen: Fläche auf die Bearbeiten-Ansicht umstellen, damit die Zellen genau unter dem
    /// Pinsel liegen. Bewegt sich die Ebene, wird der erste Klick dafür verbraucht. true = jetzt malen.</summary>
    private bool EnsureEditView()
    {
        if (!EnsurePaintLayer()) return false;
        if (_editView) return true;
        _editView = true;
        RefreshImage();
        RefreshOnion();
        if (!LayerHasMotion(_selLayer)) return true;
        StatusText.Text = "Zum Malen zeigt die Fläche die Ebene jetzt ohne Animation — noch einmal klicken, um zu malen.";
        return false;
    }

    private static bool LayerHasMotion(EffectLayer layer) =>
        layer.Animations.Any(a => a.Enabled) ||
        layer.AnimatedValues().Any(v => v.Value.IsAnimated) ||
        layer.OffsetX.Value != 0 || layer.OffsetY.Value != 0 || layer.Scale.Value != 1 || layer.Rotation.Value != 0;

    private bool HasAnyMotion() =>
        _doc.Layers.Any(l => l.Animations.Any(a => a.Enabled) || l.AnimatedValues().Any(v => v.Value.IsAnimated));

    /// <summary>Mittelpunkt und Richtung der gewählten Animation auf der Fläche (nur im Reiter „Ebenen &amp; Animation“).</summary>
    private void RefreshAnimOverlay()
    {
        if (AnimOverlay is null || _doc is null) return;
        AnimOverlay.Children.Clear();
        if (!_layerTab) return;
        double w = SurfaceRoot.Width, h = SurfaceRoot.Height;
        var accent = (Brush)FindResource("BrushAccentHover");

        void Cross(double x, double y)
        {
            var ring = new Ellipse { Width = 34, Height = 34, Stroke = accent, StrokeThickness = 3 };
            Canvas.SetLeft(ring, x - 17);
            Canvas.SetTop(ring, y - 17);
            AnimOverlay.Children.Add(ring);
            AnimOverlay.Children.Add(new Line { X1 = x - 26, X2 = x + 26, Y1 = y, Y2 = y, Stroke = accent, StrokeThickness = 2 });
            AnimOverlay.Children.Add(new Line { X1 = x, X2 = x, Y1 = y - 26, Y2 = y + 26, Stroke = accent, StrokeThickness = 2 });
        }

        double cx = 0.5, cy = 0.5;
        if (_selLayer is ShapeLayer shape)
        {
            cx = shape.CenterX.Evaluate(_time);
            cy = shape.CenterY.Evaluate(_time);
            Cross(cx * w, cy * h);
        }
        if (_selAnim is not { Enabled: true } a) return;
        if (a.HasCenter && !(_selLayer is ShapeLayer && a.Kind != AnimationKind.Rotate))
        {
            cx = a.CenterX;
            cy = a.CenterY;
            Cross(cx * w, cy * h);
        }
        if (a.Kind == AnimationKind.Move)
        {
            double rad = a.AngleDegrees * Math.PI / 180;
            double length = Math.Min(Math.Abs(a.Distance), 1) * (a.Distance < 0 ? -1 : 1);
            double x0 = cx * w, y0 = cy * h;
            double x1 = x0 + Math.Cos(rad) * length * w * 0.5, y1 = y0 + Math.Sin(rad) * length * h * 0.5;
            AnimOverlay.Children.Add(new Line
            {
                X1 = x0, Y1 = y0, X2 = x1, Y2 = y1, Stroke = accent, StrokeThickness = 4,
                StrokeDashArray = new DoubleCollection { 3, 2 }
            });
            double back = Math.Atan2(y1 - y0, x1 - x0) + Math.PI;
            var head = new Polygon { Fill = accent };
            head.Points.Add(new Point(x1, y1));
            head.Points.Add(new Point(x1 + Math.Cos(back - 0.45) * 36, y1 + Math.Sin(back - 0.45) * 36));
            head.Points.Add(new Point(x1 + Math.Cos(back + 0.45) * 36, y1 + Math.Sin(back + 0.45) * 36));
            AnimOverlay.Children.Add(head);
        }
    }

    // ===================== Zeit =====================

    private void UpdateTimeRow()
    {
        if (TimeSlider is null || _doc is null) return;
        _suppress = true;
        double d = _doc.DurationSeconds;
        TimeSlider.Maximum = d;
        TimeSlider.Value = Math.Clamp(_time, 0, d);
        TimeSlider.SmallChange = 0.05;
        TimeSlider.LargeChange = Math.Max(0.1, d / 10);
        DurationBox.Text = FormatNumber(d);
        DurationBox.IsEnabled = !_doc.AutoDuration;
        AutoDurationCheck.IsChecked = _doc.AutoDuration;
        LoopCombo.SelectedIndex = (int)_doc.Loop;
        _suppress = false;
        ShowTimeText(_time);
        DrawPlayhead(_time);
        RefreshValueRows();
    }

    private void ShowTimeText(double time) =>
        TimeText.Text = $"{time.ToString("0.00", CultureInfo.CurrentCulture)} / {FormatNumber(_doc.DurationSeconds)} s";

    /// <summary>Während der Wiedergabe: Regler und Spur mitlaufen lassen, ohne die Wiedergabe zu stoppen.</summary>
    private void ShowPlayheadTime(double time)
    {
        _suppress = true;
        TimeSlider.Value = Math.Clamp(time, 0, TimeSlider.Maximum);
        _suppress = false;
        ShowTimeText(time);
        DrawPlayhead(time);
    }

    private void OnTimeSliderChanged(object sender, RoutedPropertyChangedEventArgs<double> e)
    {
        if (_suppress || !IsLoaded) return;
        SetTime(TimeSlider.Value);
    }

    /// <summary>Zeitpunkt wählen: Die Fläche zeigt den ganzen Effekt animiert, die Werte der Ebene gelten dort.</summary>
    private void SetTime(double time)
    {
        if (_stroke is not null) return;
        StopPlayback();
        _time = Math.Clamp(time, 0, _doc.DurationSeconds);
        _editView = false;
        if (_layer.Frames.Count > 1)
        {
            int index = Math.Clamp(CustomEffectRenderer.PickFrames(_layer, _time).Current, 0, _layer.Frames.Count - 1);
            if (index != _frameIndex)
            {
                _frameIndex = index;
                AttachFrame();
                UpdateTimelineSelection(_frameIndex);
                UpdateFrameInfo();
                UpdateUndoButtons();
            }
        }
        _suppress = true;
        TimeSlider.Value = _time;
        _suppress = false;
        ShowTimeText(_time);
        DrawPlayhead(_time);
        RefreshValueRows();
        RefreshImage();
        RefreshOnion();
    }

    private void OnDurationCommit(object sender, RoutedEventArgs e)
    {
        if (_suppress || _doc is null) return;
        if (TryParseNumber(DurationBox.Text, out double seconds))
        {
            seconds = Math.Clamp(seconds, CustomEffectSerializer.MinDurationSeconds, CustomEffectSerializer.MaxDurationSeconds);
            if (Math.Abs(seconds - _doc.DurationSeconds) > 1e-9)
            {
                PushStructureUndo("Länge der Zeitleiste ändern");
                _doc.DurationSeconds = seconds;
                AfterStructureChange();
            }
        }
        UpdateTimeRow();
    }

    private void OnDurationKeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.Enter) OnDurationCommit(sender, e);
    }

    private void OnAutoDurationChanged(object sender, RoutedEventArgs e)
    {
        if (_suppress || _doc is null) return;
        bool auto = AutoDurationCheck.IsChecked == true;
        if (auto == _doc.AutoDuration) return;
        PushStructureUndo(auto ? "Länge automatisch" : "Länge von Hand");
        _doc.AutoDuration = auto;
        AfterStructureChange();
        if (auto) StatusText.Text = $"Länge automatisch: {FormatNumber(_doc.DurationSeconds)} s — so wiederholen sich alle Animationen nahtlos.";
    }

    private void OnLoopChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_suppress || _doc is null || LoopCombo.SelectedIndex < 0) return;
        var loop = (EffectLoopMode)LoopCombo.SelectedIndex;
        if (loop == _doc.Loop) return;
        PushStructureUndo("Schleife ändern");
        _doc.Loop = loop;
        AfterStructureChange();
    }

    // ===================== Keyframe-Spuren =====================

    private double TimeToX(double time) => Math.Clamp(time / Math.Max(0.001, _doc.DurationSeconds), 0, 1) * Math.Max(1, KeyTrack.ActualWidth);
    private double XToTime(double x) => Math.Clamp(x / Math.Max(1, KeyTrack.ActualWidth), 0, 1) * _doc.DurationSeconds;

    private void OnKeyTrackSizeChanged(object sender, SizeChangedEventArgs e)
    {
        if (e.WidthChanged) RebuildKeyTrack();
    }

    /// <summary>Eine Spur pro animiertem Wert der gewählten Ebene, Keyframes als Rauten.</summary>
    private void RebuildKeyTrack()
    {
        if (KeyTrack is null || _doc is null || _selLayer is null) return;
        KeyLabels.Children.Clear();
        KeyTrack.Children.Clear();
        _playhead = null;
        var rows = _selLayer.AnimatedValues().Where(v => v.Value.IsAnimated && ValueInfo.ContainsKey(v.Name)).ToList();
        if (_selKey is { } sk && !rows.Any(r => r.Value == sk.Value && sk.Value.Keyframes.Contains(sk.Key))) _selKey = null;

        if (rows.Count == 0)
        {
            KeyLabels.Children.Add(new TextBlock { Text = "Keyframes", Style = (Style)FindResource("MutedText"), FontSize = 11, Margin = new Thickness(0, 3, 0, 0) });
            KeyTrack.Height = 22;
            var hint = new TextBlock
            {
                Text = "Keine Keyframes. „◆“ neben einem Wert (Reiter „Ebenen & Animation“) setzt einen beim aktuellen Zeitpunkt.",
                Style = (Style)FindResource("MutedText"), FontSize = 11, IsHitTestVisible = false
            };
            Canvas.SetLeft(hint, 8);
            Canvas.SetTop(hint, 3);
            KeyTrack.Children.Add(hint);
        }
        else
        {
            KeyTrack.Height = rows.Count * KeyRowHeight + 4;
            var accent = (Brush)FindResource("BrushAccent");
            var normal = (Brush)FindResource("BrushText");
            var line = new SolidColorBrush(Color.FromArgb(0x30, 0xFF, 0xFF, 0xFF));
            for (int r = 0; r < rows.Count; r++)
            {
                var (name, value) = rows[r];
                var (label, factor, unit, _) = ValueInfo[name];
                KeyLabels.Children.Add(new TextBlock
                {
                    Text = label, Height = KeyRowHeight, FontSize = 11, Style = (Style)FindResource("MutedText"),
                    Margin = new Thickness(0, r == 0 ? 2 : 0, 0, 0)
                });
                double y = r * KeyRowHeight + 2 + KeyRowHeight / 2;
                KeyTrack.Children.Add(new Line { X1 = 0, X2 = Math.Max(1, KeyTrack.ActualWidth), Y1 = y, Y2 = y, Stroke = line, StrokeThickness = 1, IsHitTestVisible = false });
                foreach (var key in value.Keyframes)
                {
                    var k = key;
                    bool selected = _selKey is { } s && s.Key == k;
                    bool beyond = k.Time > _doc.DurationSeconds + 1e-6;
                    var diamond = new Rectangle
                    {
                        Width = 10, Height = 10, Fill = selected ? accent : normal, Opacity = beyond ? 0.4 : 1, Cursor = Cursors.Hand,
                        RenderTransform = new RotateTransform(45, 5, 5),
                        ToolTip = $"{label}: {FormatNumber(k.Value * factor)} {unit} bei {k.Time.ToString("0.00", CultureInfo.CurrentCulture)} s · " +
                                  $"{EasingNames.FirstOrDefault(n => n.Value == k.Easing).Name}" +
                                  (beyond ? " (hinter dem Ende der Zeitleiste)" : "") + "\nZiehen = verschieben, Rechtsklick = Verlauf/Löschen, Entf = löschen"
                    };
                    Canvas.SetLeft(diamond, TimeToX(k.Time) - 5);
                    Canvas.SetTop(diamond, y - 5);
                    diamond.MouseLeftButtonDown += (_, e) =>
                    {
                        e.Handled = true;
                        _selKey = (value, k);
                        _keyDrag = (value, k);
                        _keyDragMoved = false;
                        _keyDragStartX = e.GetPosition(KeyTrack).X;
                        KeyTrack.CaptureMouse();
                        SetTime(k.Time);
                        RebuildKeyTrack();
                    };
                    diamond.MouseRightButtonUp += (_, e) =>
                    {
                        e.Handled = true;
                        _selKey = (value, k);
                        RebuildKeyTrack();
                        ShowKeyMenu(value, k, label);
                    };
                    KeyTrack.Children.Add(diamond);
                }
            }
        }
        _playhead = new Rectangle { Width = 2, Height = KeyTrack.Height, Fill = (Brush)FindResource("BrushAccentHover"), IsHitTestVisible = false };
        KeyTrack.Children.Add(_playhead);
        DrawPlayhead(_playTimer.IsEnabled ? TimeSlider.Value : _time);
    }

    private void DrawPlayhead(double time)
    {
        if (_playhead is null) return;
        Canvas.SetLeft(_playhead, TimeToX(time) - 1);
    }

    private void ShowKeyMenu(AnimatedValue value, Keyframe key, string label)
    {
        var menu = new ContextMenu();
        menu.Items.Add(new MenuItem { Header = "Verlauf bis zum nächsten Keyframe", IsEnabled = false });
        foreach (var (easing, name) in EasingNames)
        {
            var e = easing;
            var item = new MenuItem { Header = name, IsCheckable = true, IsChecked = key.Easing == e };
            item.Click += (_, _) =>
            {
                if (key.Easing == e) return;
                PushStructureUndo($"{label}: Verlauf");
                key.Easing = e;
                AfterStructureChange();
            };
            menu.Items.Add(item);
        }
        menu.Items.Add(new Separator());
        var delete = new MenuItem { Header = "Keyframe löschen" };
        delete.Click += (_, _) =>
        {
            _selKey = (value, key);
            DeleteSelectedKeyframe();
        };
        menu.Items.Add(delete);
        menu.IsOpen = true;
    }

    private bool DeleteSelectedKeyframe()
    {
        if (_selKey is not { } sel || Keyboard.FocusedElement is TextBox) return false;
        int index = sel.Value.Keyframes.IndexOf(sel.Key);
        if (index < 0) { _selKey = null; return false; }
        PushStructureUndo("Keyframe löschen");
        KeyframeTools.Remove(sel.Value, index);
        _selKey = null;
        AfterStructureChange();
        StatusText.Text = "Keyframe gelöscht.";
        return true;
    }

    private void OnKeyTrackMouseDown(object sender, MouseButtonEventArgs e)
    {
        // Klick in eine freie Stelle: Zeitpunkt wählen, mit gedrückter Maus scrubben.
        _selKey = null;
        _scrubbing = true;
        KeyTrack.CaptureMouse();
        SetTime(XToTime(e.GetPosition(KeyTrack).X));
        RebuildKeyTrack();
        e.Handled = true;
    }

    private void OnKeyTrackMouseMove(object sender, MouseEventArgs e)
    {
        if (e.LeftButton != MouseButtonState.Pressed) return;
        double x = e.GetPosition(KeyTrack).X;
        if (_scrubbing)
        {
            SetTime(XToTime(x));
            return;
        }
        if (_keyDrag is not { } drag) return;
        if (!_keyDragMoved)
        {
            if (Math.Abs(x - _keyDragStartX) < 4) return;
            PushStructureUndo("Keyframe verschieben");
            _keyDragMoved = true;
        }
        double time = XToTime(x);
        // Auf 50 ms einrasten, mit Shift frei.
        if (!Keyboard.Modifiers.HasFlag(ModifierKeys.Shift)) time = Math.Round(time / 0.05) * 0.05;
        int index = drag.Value.Keyframes.IndexOf(drag.Key);
        if (index < 0) return;
        KeyframeTools.Move(drag.Value, index, time);
        MarkModified();
        SetTime(time);
        RebuildKeyTrack();
    }

    private void OnKeyTrackMouseUp(object sender, MouseButtonEventArgs e)
    {
        bool moved = _keyDrag is not null && _keyDragMoved;
        _keyDrag = null;
        _keyDragMoved = false;
        _scrubbing = false;
        KeyTrack.ReleaseMouseCapture();
        if (moved) AfterStructureChange();
    }

    // ===================== Rückgängig für Ebenen, Animationen, Keyframes =====================

    /// <summary>Vor jeder Änderung, die nicht gemalt ist, aufrufen.</summary>
    private void PushStructureUndo(string description)
    {
        _structUndo.Add((DocumentStructureSnapshot.Capture(_doc, description), EditSequence.Next()));
        if (_structUndo.Count > MaxStructureSteps) _structUndo.RemoveAt(0);
        _structRedo.Clear();
        foreach (var history in _histories.Values) history.ClearRedo();
    }

    private void PushPaint(PaintEdit? edit)
    {
        _history.Push(edit);
        if (edit is not null) _structRedo.Clear();
    }

    private bool StructureUndoIsNewer() => _structUndo.Count > 0 && _structUndo[^1].Sequence > _history.UndoSequence;

    private bool StructureRedoIsNext() =>
        _structRedo.Count > 0 && (_history.RedoSequence < 0 || _structRedo[^1].Sequence < _history.RedoSequence);

    private void StructureUndo()
    {
        var (snapshot, sequence) = _structUndo[^1];
        _structUndo.RemoveAt(_structUndo.Count - 1);
        _structRedo.Add((DocumentStructureSnapshot.Capture(_doc, snapshot.Description), sequence));
        ApplyStructure(snapshot);
        StatusText.Text = $"Rückgängig: {snapshot.Description}";
    }

    private void StructureRedo()
    {
        var (snapshot, sequence) = _structRedo[^1];
        _structRedo.RemoveAt(_structRedo.Count - 1);
        _structUndo.Add((DocumentStructureSnapshot.Capture(_doc, snapshot.Description), sequence));
        ApplyStructure(snapshot);
        StatusText.Text = $"Wiederholt: {snapshot.Description}";
    }

    private void ApplyStructure(DocumentStructureSnapshot snapshot)
    {
        StopPlayback();
        CancelPickCenter();
        int selected = _doc.Layers.IndexOf(_selLayer);
        int paint = _doc.Layers.IndexOf(_layer);
        int animation = _selAnim is null ? -1 : _selLayer.Animations.IndexOf(_selAnim);
        try { snapshot.Restore(_doc); }
        catch (System.IO.InvalidDataException ex)
        {
            StatusText.Text = "Rückgängig fehlgeschlagen: " + ex.Message;
            return;
        }
        var layers = _doc.Layers;
        _layer = (paint >= 0 && paint < layers.Count ? layers[paint] as PaintLayer : null) ?? layers.OfType<PaintLayer>().LastOrDefault()!;
        if (_layer is null)
        {
            _layer = new PaintLayer { Name = "Malen", Frames = { PaintFrame.CreateEmpty(_doc.GridWidth, _doc.GridHeight) } };
            layers.Add(_layer);
        }
        if (_layer.Frames.Count == 0) _layer.Frames.Add(PaintFrame.CreateEmpty(_doc.GridWidth, _doc.GridHeight));
        _selLayer = layers[Math.Clamp(selected, 0, layers.Count - 1)];
        _selAnim = animation >= 0 && animation < _selLayer.Animations.Count ? _selLayer.Animations[animation] : _selLayer.Animations.FirstOrDefault();
        _selKey = null;
        _frameIndex = Math.Clamp(_frameIndex, 0, _layer.Frames.Count - 1);
        _time = Math.Clamp(_time, 0, _doc.DurationSeconds);
        AttachFrame();
        _suppress = true;
        FrameDurationBox.Text = FormatMs(_layer.FrameDurationSeconds);
        CrossFadeCheck.IsChecked = _layer.CrossFade;
        _suppress = false;
        MarkModified();
        RebuildLayerUi();
        RebuildTimeline();
        RefreshImage();
        RefreshOnion();
        UpdateUndoButtons();
    }
}
