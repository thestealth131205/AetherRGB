using System.Globalization;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Threading;
using AetherRGB.Core.Devices;
using AetherRGB.Core.Effects;
using AetherRGB.Core.Panels;
using Forms = System.Windows.Forms;

namespace AetherRGB.App.Panels;

/// <summary>Eigenschaften des gewählten Elements — je Art nur die passenden Felder.</summary>
public partial class SensorPanelEditorWindow
{
    private static readonly (string Format, string Label)[] ClockFormats =
    {
        ("HH:mm:ss", "14:05:09"),
        ("HH:mm", "14:05"),
        ("dddd, d. MMMM yyyy", "Dienstag, 24. November 2026"),
        ("dd.MM.yyyy", "24.11.2026"),
        ("ddd, dd.MM.", "Di., 24.11."),
        ("dddd", "Dienstag"),
        ("HH:mm · ddd d. MMM", "14:05 · Di. 24. Nov."),
    };

    private Style Muted => (Style)FindResource("MutedText");

    private static List<string>? _fontNames;
    private static List<string> FontNames => _fontNames ??= Fonts.SystemFontFamilies.Select(f => f.Source).Distinct().OrderBy(f => f, StringComparer.CurrentCultureIgnoreCase).ToList();

    private void BuildProperties()
    {
        PropertyPanel.Children.Clear();
        var item = _selected;
        if (item is null)
        {
            Header("Panel");
            TextField("Name", _doc.Name, v => Edit(() => _doc.Name = string.IsNullOrWhiteSpace(v) ? "Panel" : v.Trim(), structure: false));
            Label("Element auf der Fläche anklicken, um es zu bearbeiten. Neue Elemente über „Element hinzufügen“ links oder einen Sensor auf die Fläche ziehen.");
            Label("\nTasten: Entf löschen · Strg+D duplizieren · Strg+C/V kopieren/einfügen · Pfeile verschieben (Shift = 10 px) · Strg+Z/Y rückgängig/wiederholen · Alt beim Ziehen = ohne Raster · Shift beim Größe ändern = Seitenverhältnis halten.");
            return;
        }

        Header(PanelItem.KindName(item.Kind));
        TextField("Name (nur für die Liste)", item.Name, v => Edit(() => item.Name = v.Trim(), structure: true));
        Check("Sichtbar", item.Visible, v => Edit(() => item.Visible = v));
        RectFields(item);
        SliderField("Deckkraft", 0, 100, item.Opacity * 100, "0' %'", v => Edit(() => item.Opacity = v / 100));

        if (UsesSensor(item.Kind)) SensorFields(item);

        switch (item.Kind)
        {
            case PanelItemKind.Text:
                Header("Text");
                MultiLineField(item.Text, v => Edit(() => item.Text = v));
                FontFields(item, align: true, labelColor: false);
                break;
            case PanelItemKind.Clock:
                Header("Format");
                ComboField("Vorlage", ClockFormats.Select(f => $"{f.Label}   ({f.Format})").Prepend("Eigenes Format").ToArray(),
                    Math.Max(0, Array.FindIndex(ClockFormats, f => f.Format == item.Text) + 1),
                    i => { if (i > 0) { Edit(() => item.Text = ClockFormats[i - 1].Format); BuildProperties(); } });
                TextField("Format (.NET, z. B. HH:mm:ss, dddd, dd.MM.yyyy)", item.Text, v => Edit(() => item.Text = v));
                FontFields(item, align: true, labelColor: false);
                break;
            case PanelItemKind.Value:
                FontFields(item, align: true, labelColor: true);
                break;
            case PanelItemKind.Bar:
                Header("Balken");
                Check("Senkrecht (füllt von unten)", item.Vertical, v => Edit(() => item.Vertical = v));
                IntField("Segmente (0 = durchgehend)", item.Segments, 0, 100, v => Edit(() => item.Segments = v));
                ColorField("Füllung", () => item.AccentColor, c => Edit(() => item.AccentColor = c));
                ColorField("Leer", () => item.BackColor, c => Edit(() => item.BackColor = c));
                SliderField("Ecken", 0, 50, item.CornerRadius, "0", v => Edit(() => item.CornerRadius = v));
                FontFields(item, align: false, labelColor: true);
                break;
            case PanelItemKind.Gauge:
                Header("Rundinstrument");
                SliderField("Bogen", 90, 360, item.GaugeSweep, "0'°'", v => Edit(() => item.GaugeSweep = v));
                SliderField("Linienstärke", 2, 60, item.Thickness, "0", v => Edit(() => item.Thickness = v));
                ColorField("Füllung", () => item.AccentColor, c => Edit(() => item.AccentColor = c));
                ColorField("Leer", () => item.BackColor, c => Edit(() => item.BackColor = c));
                FontFields(item, align: false, labelColor: true);
                break;
            case PanelItemKind.Graph:
                Header("Verlauf");
                IntField("Zeitraum in Sekunden", item.GraphSeconds, 5, Core.Sensors.SensorHub.HistoryLength, v => Edit(() => item.GraphSeconds = v));
                SliderField("Linienstärke", 1, 10, item.Thickness, "0.#", v => Edit(() => item.Thickness = v));
                Check("Fläche unter der Linie füllen", item.GraphFill, v => Edit(() => item.GraphFill = v));
                Check("Hilfslinien", item.GraphGrid, v => Edit(() => item.GraphGrid = v));
                ColorField("Linie", () => item.AccentColor, c => Edit(() => item.AccentColor = c));
                ColorField("Hintergrund", () => item.BackColor, c => Edit(() => item.BackColor = c));
                SliderField("Hintergrund-Deckkraft", 0, 100, item.BackOpacity * 100, "0' %'", v => Edit(() => item.BackOpacity = v / 100));
                SliderField("Ecken", 0, 50, item.CornerRadius, "0", v => Edit(() => item.CornerRadius = v));
                FontFields(item, align: false, labelColor: true);
                break;
            case PanelItemKind.Box:
                Header("Rechteck");
                ColorField("Füllung", () => item.BackColor, c => Edit(() => item.BackColor = c));
                SliderField("Füllung-Deckkraft", 0, 100, item.BackOpacity * 100, "0' %'", v => Edit(() => item.BackOpacity = v / 100));
                ColorField("Rahmen", () => item.BorderColor, c => Edit(() => item.BorderColor = c));
                SliderField("Rahmenstärke", 0, 20, item.BorderWidth, "0", v => Edit(() => item.BorderWidth = v));
                SliderField("Ecken", 0, 200, item.CornerRadius, "0", v => Edit(() => item.CornerRadius = v));
                break;
            case PanelItemKind.Animation:
                AnimationFields(item);
                break;
            case PanelItemKind.Image:
                Header("Bild");
                Label(string.IsNullOrWhiteSpace(item.ImagePath) ? "Kein Bild gewählt." : item.ImagePath);
                var pick = new Button { Content = "🖼  Bild wählen …", HorizontalAlignment = HorizontalAlignment.Left, Margin = new Thickness(0, 4, 0, 8) };
                pick.Click += (_, _) => { var n = PickImage(); if (n is not null) { Edit(() => item.ImagePath = n); BuildProperties(); } };
                PropertyPanel.Children.Add(pick);
                Label("PNG, JPG, BMP oder animiertes GIF. Das Bild wird nach %APPDATA%\\AetherRGB\\panels\\assets kopiert.");
                ComboField("Einpassen", new[] { "Ganz zeigen", "Ausfüllen (beschneiden)", "Verzerren" }, (int)item.Stretch, i => Edit(() => item.Stretch = (PanelStretch)i));
                SliderField("Ecken", 0, 200, item.CornerRadius, "0", v => Edit(() => item.CornerRadius = v));
                break;
            case PanelItemKind.ScreenArea:
                ScreenAreaFields(item);
                break;
        }
    }

    private void SensorFields(PanelItem item)
    {
        Header("Sensor");
        var reading = Core.Panels.PanelLogic.Resolve(item.SensorId, _hub.Get);
        Label(reading is null ? "Kein Wert — Sensor nicht vorhanden (Quelle aus? AIDA64 nicht gestartet?)." : $"Aktuell: {reading.Name} = {Core.Sensors.SensorFormat.Format(reading)} ({reading.Source})");
        TextField("Sensor-ID(s) — mehrere mit | trennen, der erste vorhandene gilt", item.SensorId, v => Edit(() => item.SensorId = v.Trim()));
        var row = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(0, 0, 0, 8) };
        var take = new Button { Content = "Gewählten Sensor übernehmen", Margin = new Thickness(0, 0, 6, 0), ToolTip = "Sensor links in der Liste auswählen (oder einfach auf das Element ziehen)" };
        take.Click += (_, _) => { if (SelectedSensorRow() is { } s) { AssignSensor(item, s.Id, append: false); BuildProperties(); } };
        var append = new Button { Content = "+ als Ausweich", ToolTip = "Wird genommen, wenn die vorherigen Sensoren fehlen (z. B. AIDA64 aus)" };
        append.Click += (_, _) => { if (SelectedSensorRow() is { } s) { AssignSensor(item, s.Id, append: true); BuildProperties(); } };
        row.Children.Add(take);
        row.Children.Add(append);
        PropertyPanel.Children.Add(row);

        TextField("Beschriftung (leer = Sensorname)", item.Text, v => Edit(() => item.Text = v));
        Check("Beschriftung zeigen", item.ShowLabel, v => Edit(() => item.ShowLabel = v));
        if (item.Kind != PanelItemKind.Value) Check("Wert zeigen", item.ShowValue, v => Edit(() => item.ShowValue = v));
        Check("Einheit zeigen", item.ShowUnit, v => Edit(() => item.ShowUnit = v));
        ComboField("Nachkommastellen", new[] { "automatisch", "0", "1", "2", "3" }, item.Decimals + 1, i => Edit(() => item.Decimals = i - 1));

        if (item.Kind != PanelItemKind.Value)
        {
            var (dMin, dMax) = Core.Panels.PanelLogic.Range(new PanelItem(), reading);
            OptionalNumberPair("Skala von / bis (leer = automatisch)", item.Min, item.Max, $"{dMin:0.##}", $"{dMax:0.##}", (a, b) => Edit(() => { item.Min = a; item.Max = b; }));
        }
        OptionalNumberPair("Warnung ab / Kritisch ab (Kritisch kleiner als Warnung = kleine Werte sind schlecht)", item.WarnAt, item.CritAt, "—", "—", (a, b) => Edit(() => { item.WarnAt = a; item.CritAt = b; }));
        ColorField("Warnfarbe", () => item.WarnColor, c => Edit(() => item.WarnColor = c));
        ColorField("Kritisch-Farbe", () => item.CritColor, c => Edit(() => item.CritColor = c));
    }

    private void FontFields(PanelItem item, bool align, bool labelColor)
    {
        Header("Schrift");
        SliderField("Größe", 6, 200, item.FontSize, "0", v => Edit(() => item.FontSize = v));
        Check("Fett", item.Bold, v => Edit(() => item.Bold = v));
        Label("Schriftart");
        var combo = new ComboBox { IsEditable = true, ItemsSource = FontNames, Text = item.FontFamily, Margin = new Thickness(0, 2, 0, 6) };
        combo.LostFocus += (_, _) => { if (combo.Text.Trim() is { Length: > 0 } f && f != item.FontFamily) Edit(() => item.FontFamily = f); };
        combo.SelectionChanged += (_, _) => { if (combo.SelectedItem is string f && f != item.FontFamily) Edit(() => item.FontFamily = f); };
        PropertyPanel.Children.Add(combo);
        if (align) ComboField("Ausrichtung", new[] { "Links", "Mitte", "Rechts" }, (int)item.Align, i => Edit(() => item.Align = (PanelAlign)i));
        ColorField(item.Kind is PanelItemKind.Text or PanelItemKind.Clock ? "Farbe" : "Wertfarbe", () => item.Color, c => Edit(() => item.Color = c));
        if (labelColor) ColorField("Beschriftungsfarbe", () => item.LabelColor, c => Edit(() => item.LabelColor = c));
    }

    private void AnimationFields(PanelItem item)
    {
        Header("Animation");
        Label("Läuft wie in einem Effekt-Fenster. Eigene Animationen im Effekt-Editor anlegen (Effekte & Layout → 🎨 Effekt-Editor) — sie erscheinen hier automatisch.");
        var effects = EffectCatalog.All.ToList();
        var combo = new ComboBox { ItemsSource = effects, DisplayMemberPath = nameof(EffectDescriptor.Name), Margin = new Thickness(0, 4, 0, 6) };
        combo.SelectedItem = effects.FirstOrDefault(d => d.Id == item.EffectId) ?? effects[0];
        combo.SelectionChanged += (_, _) =>
        {
            if (combo.SelectedItem is not EffectDescriptor d || d.Id == item.EffectId) return;
            Edit(() => { item.EffectId = d.Id; item.EffectColors = d.DefaultColors.ToList(); });
            BuildProperties();
        };
        Label("Effekt");
        PropertyPanel.Children.Add(combo);

        var desc = EffectCatalog.Get(item.EffectId);
        if (item.EffectColors.Count != desc.ColorSlots.Count) item.EffectColors = desc.DefaultColors.ToList();
        for (int i = 0; i < desc.ColorSlots.Count; i++)
        {
            int slot = i;
            ColorField(desc.ColorSlots[i].Label, () => item.EffectColors[slot], c => Edit(() => item.EffectColors[slot] = c));
        }
        if (desc.IsAnimated)
            SliderField("Tempo", EffectCatalog.MinSpeedPercent, EffectCatalog.MaxSpeedPercent, item.SpeedPercent, "0' %'", v => Edit(() => item.SpeedPercent = (int)Math.Round(v)));
        SliderField("Auflösung (Pixel je Punkt, größer = gröber)", 1, 32, item.CellSize, "0", v => Edit(() => item.CellSize = (int)Math.Round(v)));
        SliderField("Ecken", 0, 200, item.CornerRadius, "0", v => Edit(() => item.CornerRadius = v));
        ColorField("Rahmen", () => item.BorderColor, c => Edit(() => item.BorderColor = c));
        SliderField("Rahmenstärke", 0, 20, item.BorderWidth, "0", v => Edit(() => item.BorderWidth = v));
    }

    /// <summary>Änderung an einem Feld: Rückgängig-Punkt, anwenden, neu zeichnen (die Felder bleiben, damit der Fokus nicht springt).</summary>
    private void Edit(Action apply, bool structure = false)
    {
        Checkpoint();
        apply();
        if (structure) { Changed(); RefreshItemList(); }
        else Changed();
    }

    // ===================== kleine Bausteine =====================

    private void Header(string text) => PropertyPanel.Children.Add(new TextBlock { Text = text, FontWeight = FontWeights.SemiBold, Margin = new Thickness(0, 10, 0, 4) });
    private void Label(string text) => PropertyPanel.Children.Add(new TextBlock { Text = text, Style = Muted, TextWrapping = TextWrapping.Wrap });

    private void TextField(string label, string value, Action<string> apply)
    {
        Label(label);
        var box = new TextBox { Text = value, Margin = new Thickness(0, 2, 0, 6) };
        box.LostFocus += (_, _) => { if (box.Text != value) { value = box.Text; apply(box.Text); } };
        box.KeyDown += (_, e) => { if (e.Key == Key.Enter && box.Text != value) { value = box.Text; apply(box.Text); } };
        PropertyPanel.Children.Add(box);
    }

    private void MultiLineField(string value, Action<string> apply)
    {
        var box = new TextBox { Text = value, AcceptsReturn = true, TextWrapping = TextWrapping.Wrap, MinHeight = 60, Margin = new Thickness(0, 2, 0, 6) };
        box.LostFocus += (_, _) => { if (box.Text != value) { value = box.Text; apply(box.Text); } };
        PropertyPanel.Children.Add(box);
    }

    private void Check(string label, bool value, Action<bool> apply)
    {
        var box = new CheckBox { Content = label, IsChecked = value, Foreground = (Brush)FindResource("BrushText"), Margin = new Thickness(0, 2, 0, 6) };
        box.Checked += (_, _) => apply(true);
        box.Unchecked += (_, _) => apply(false);
        PropertyPanel.Children.Add(box);
    }

    private void ComboField(string label, string[] options, int selected, Action<int> apply)
    {
        Label(label);
        var combo = new ComboBox { ItemsSource = options, SelectedIndex = Math.Clamp(selected, 0, options.Length - 1), Margin = new Thickness(0, 2, 0, 6) };
        combo.SelectionChanged += (_, _) => { if (combo.SelectedIndex >= 0) apply(combo.SelectedIndex); };
        PropertyPanel.Children.Add(combo);
    }

    private void IntField(string label, int value, int min, int max, Action<int> apply)
    {
        Label(label);
        var box = new TextBox { Text = value.ToString(CultureInfo.CurrentCulture), Width = 90, HorizontalAlignment = HorizontalAlignment.Left, Margin = new Thickness(0, 2, 0, 6) };
        void Commit()
        {
            if (!int.TryParse(box.Text, NumberStyles.Integer, CultureInfo.CurrentCulture, out int v)) { box.Text = value.ToString(CultureInfo.CurrentCulture); return; }
            v = Math.Clamp(v, min, max);
            box.Text = v.ToString(CultureInfo.CurrentCulture);
            if (v != value) { value = v; apply(v); }
        }
        box.LostFocus += (_, _) => Commit();
        box.KeyDown += (_, e) => { if (e.Key == Key.Enter) Commit(); };
        PropertyPanel.Children.Add(box);
    }

    private void OptionalNumberPair(string label, double? a, double? b, string hintA, string hintB, Action<double?, double?> apply)
    {
        Label(label);
        var grid = new UniformGrid { Columns = 2, Margin = new Thickness(0, 2, 0, 6) };
        string Text(double? v) => v?.ToString("0.###", CultureInfo.CurrentCulture) ?? "";
        var boxA = new TextBox { Text = Text(a), Margin = new Thickness(0, 0, 4, 0), ToolTip = "leer = " + hintA };
        var boxB = new TextBox { Text = Text(b), ToolTip = "leer = " + hintB };
        double? Parse(TextBox t) => double.TryParse(t.Text.Trim(), NumberStyles.Float, CultureInfo.CurrentCulture, out var d) && double.IsFinite(d) ? d : null;
        void Commit()
        {
            var na = Parse(boxA);
            var nb = Parse(boxB);
            boxA.Text = Text(na);
            boxB.Text = Text(nb);
            if (na != a || nb != b) { a = na; b = nb; apply(na, nb); }
        }
        foreach (var box in new[] { boxA, boxB })
        {
            box.LostFocus += (_, _) => Commit();
            box.KeyDown += (_, e) => { if (e.Key == Key.Enter) Commit(); };
            grid.Children.Add(box);
        }
        PropertyPanel.Children.Add(grid);
    }

    private void SliderField(string label, double min, double max, double value, string format, Action<double> apply)
    {
        var text = new TextBlock { Text = $"{label}: {value.ToString(format, CultureInfo.CurrentCulture)}", Style = Muted };
        var slider = new Slider { Minimum = min, Maximum = max, Value = Math.Clamp(value, min, max), Margin = new Thickness(0, 0, 0, 6) };
        // Erst nach kurzer Ruhe übernehmen — sonst landet jeder Zwischenschritt im Rückgängig-Verlauf.
        var debounce = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(120) };
        debounce.Tick += (_, _) => { debounce.Stop(); apply(slider.Value); };
        slider.ValueChanged += (_, e) =>
        {
            text.Text = $"{label}: {e.NewValue.ToString(format, CultureInfo.CurrentCulture)}";
            debounce.Stop();
            debounce.Start();
        };
        PropertyPanel.Children.Add(text);
        PropertyPanel.Children.Add(slider);
    }

    private void RectFields(PanelItem item)
    {
        Label("X · Y · Breite · Höhe (Pixel)");
        var grid = new UniformGrid { Columns = 4, Margin = new Thickness(0, 2, 0, 8) };
        var values = new[] { item.X, item.Y, item.W, item.H };
        var boxes = values.Select((v, i) => new TextBox { Text = v.ToString("0", CultureInfo.CurrentCulture), Margin = new Thickness(i == 0 ? 0 : 4, 0, 0, 0) }).ToArray();
        void Commit()
        {
            var vals = boxes.Select(b => double.TryParse(b.Text, NumberStyles.Float, CultureInfo.CurrentCulture, out var d) ? d : double.NaN).ToArray();
            if (vals.Any(v => !double.IsFinite(v))) return;
            if (vals[0] == item.X && vals[1] == item.Y && vals[2] == item.W && vals[3] == item.H) return;
            Edit(() => { item.X = vals[0]; item.Y = vals[1]; item.W = Math.Max(2, vals[2]); item.H = Math.Max(2, vals[3]); });
        }
        foreach (var b in boxes)
        {
            b.LostFocus += (_, _) => Commit();
            b.KeyDown += (_, e) => { if (e.Key == Key.Enter) Commit(); };
            grid.Children.Add(b);
        }
        PropertyPanel.Children.Add(grid);
    }

    private void ColorField(string label, Func<RgbColor> get, Action<RgbColor> apply)
    {
        var row = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(0, 2, 0, 6) };
        row.Children.Add(new TextBlock { Text = label, Style = Muted, Width = 150, VerticalAlignment = VerticalAlignment.Center, TextWrapping = TextWrapping.Wrap });
        row.Children.Add(ColorSwatch(get, apply));
        PropertyPanel.Children.Add(row);
    }

    private FrameworkElement ColorSwatch(Func<RgbColor> get, Action<RgbColor> apply)
    {
        var row = new StackPanel { Orientation = Orientation.Horizontal };
        var swatch = new Border
        {
            Width = 28, Height = 24, CornerRadius = new CornerRadius(5), BorderThickness = new Thickness(1),
            BorderBrush = (Brush)FindResource("BrushBorder"), Background = new SolidColorBrush(ToWpf(get())), Cursor = Cursors.Hand,
            Margin = new Thickness(0, 0, 6, 0), ToolTip = "Farbe wählen",
        };
        var hex = new TextBox { Text = get().ToHex(), Width = 80, VerticalAlignment = VerticalAlignment.Center };
        void Set(RgbColor c)
        {
            hex.Text = c.ToHex();
            swatch.Background = new SolidColorBrush(ToWpf(c));
            if (c != get()) apply(c);
        }
        swatch.MouseLeftButtonUp += (_, _) =>
        {
            var current = get();
            using var dialog = new Forms.ColorDialog { FullOpen = true, AnyColor = true, Color = System.Drawing.Color.FromArgb(current.R, current.G, current.B) };
            if (dialog.ShowDialog() == Forms.DialogResult.OK) Set(new RgbColor(dialog.Color.R, dialog.Color.G, dialog.Color.B));
        };
        void FromText()
        {
            var t = hex.Text.Trim();
            if (!t.StartsWith('#')) t = "#" + t;
            try { Set(RgbColor.FromHex(t)); }
            catch (Exception ex) when (ex is FormatException or ArgumentException or OverflowException) { hex.Text = get().ToHex(); }
        }
        hex.LostFocus += (_, _) => FromText();
        hex.KeyDown += (_, e) => { if (e.Key == Key.Enter) FromText(); };
        row.Children.Add(swatch);
        row.Children.Add(hex);
        return row;
    }

    private static Color ToWpf(RgbColor c) => Color.FromRgb(c.R, c.G, c.B);
}
