using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Data;
using System.Windows.Input;
using System.Windows.Media;
using AetherRGB.Core.Devices;
using AetherRGB.Core.Panels;
using AetherRGB.Core.Screen;

namespace AetherRGB.App.Panels;

/// <summary>Abgriffe aus dem Bildschirm-Sync als Quelle für Panel-Elemente: Liste aller Bereiche aller Profile, per
/// Drag &amp; Drop oder Doppelklick auf die Fläche.</summary>
public partial class SensorPanelEditorWindow
{
    private readonly ObservableCollection<ScreenAreaRow> _screenAreaRows = new();
    private IReadOnlyList<ScreenProfile>? _screenAreaProfiles;
    private Point _screenAreaDragStart;

    public sealed class ScreenAreaRow : INotifyPropertyChanged
    {
        public required string ProfileId { get; init; }
        public required string RegionId { get; init; }
        public required string Name { get; init; }
        public required string Group { get; init; }
        public required string Tip { get; init; }
        public required RelRect Area { get; init; }
        private Brush _swatch = Brushes.Transparent;
        public Brush Swatch { get => _swatch; set { _swatch = value; PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(Swatch))); } }
        public event PropertyChangedEventHandler? PropertyChanged;
    }

    private void InitScreenAreas()
    {
        var view = CollectionViewSource.GetDefaultView(_screenAreaRows);
        view.GroupDescriptions.Add(new PropertyGroupDescription(nameof(ScreenAreaRow.Group)));
        ScreenAreaList.ItemsSource = view;
        RefreshScreenAreas();
    }

    /// <summary>Liste neu aufbauen, wenn sich die Profile geändert haben; Farbfelder aus dem letzten Bild auffrischen.</summary>
    private void RefreshScreenAreas()
    {
        var hub = ScreenHub.Shared;
        var profiles = hub.Profiles;
        if (!ReferenceEquals(profiles, _screenAreaProfiles))
        {
            _screenAreaProfiles = profiles;
            var selected = ScreenAreaList.SelectedItem as ScreenAreaRow;
            _screenAreaRows.Clear();
            foreach (var p in profiles)
            foreach (var r in p.Regions.Where(IsScreenAreaSource))
            {
                _screenAreaRows.Add(new ScreenAreaRow
                {
                    ProfileId = p.Id, RegionId = r.Id, Name = r.Name, Group = p.Name, Area = r.Area,
                    Tip = $"{p.Name} · {r.Name}\n{RuleName(r.Rule)} · {r.Area.W * 100:0.#} × {r.Area.H * 100:0.#} % des Monitors",
                });
            }
            ScreenAreaList.SelectedItem = _screenAreaRows.FirstOrDefault(r => r.ProfileId == selected?.ProfileId && r.RegionId == selected?.RegionId);
            ScreenAreaEmptyText.Visibility = _screenAreaRows.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
        }

        // Farbfelder nur aus einer laufenden Aufnahme — die Liste allein startet sie nicht (kostet sonst dauernd CPU).
        var image = hub.IsLive ? hub.LatestImage : null;
        foreach (var row in _screenAreaRows)
        {
            if (image is null) { row.Swatch = Brushes.Transparent; continue; }
            var c = ColorBoost.Saturate(image.Average(row.Area), hub.Saturation);
            var brush = new SolidColorBrush(Color.FromRgb(c.R, c.G, c.B));
            brush.Freeze();
            row.Swatch = brush;
        }
    }

    /// <summary>„Schaden“ (ValueDrop) hat keine eigene Fläche — er misst andere Bereiche.</summary>
    private static bool IsScreenAreaSource(ScreenRegion r) => r.Rule != RegionRule.ValueDrop;

    private static string RuleName(RegionRule rule) => rule switch
    {
        RegionRule.Average => "Mischfarbe",
        RegionRule.ColorShare => "Farbanteil",
        RegionRule.BarFill => "Balken-Füllstand",
        RegionRule.ColorFlash => "Farbblitz",
        RegionRule.HudCheck => "HUD-Prüfung",
        RegionRule.ColorDistance => "Abstand zur Farbe",
        _ => rule.ToString(),
    };

    private ScreenAreaRow? SelectedScreenAreaRow() => ScreenAreaList.SelectedItem as ScreenAreaRow;

    private void OnScreenAreaListMouseDown(object sender, MouseButtonEventArgs e) => _screenAreaDragStart = e.GetPosition(ScreenAreaList);

    private void OnScreenAreaListMouseMove(object sender, MouseEventArgs e)
    {
        if (e.LeftButton != MouseButtonState.Pressed) return;
        var d = e.GetPosition(ScreenAreaList) - _screenAreaDragStart;
        if (Math.Abs(d.X) < SystemParameters.MinimumHorizontalDragDistance && Math.Abs(d.Y) < SystemParameters.MinimumVerticalDragDistance) return;
        var container = (e.OriginalSource as DependencyObject) is { } src ? ItemsControl.ContainerFromElement(ScreenAreaList, src) as ListBoxItem : null;
        if (container?.DataContext is not ScreenAreaRow row) return;
        DragDrop.DoDragDrop(ScreenAreaList, new DataObject(typeof(ScreenAreaRow), row), DragDropEffects.Copy);
    }

    /// <summary>Abgelegt auf der Fläche: auf einem Bildschirm-Bereich ersetzen, sonst neues Element an der Stelle.</summary>
    private void DropScreenArea(ScreenAreaRow row, Point p)
    {
        if (PanelLogic.HitTest(_doc, p.X, p.Y) is { Kind: PanelItemKind.ScreenArea } target)
        {
            AssignScreenArea(target, row);
            Select(target);
            return;
        }
        var item = CreateScreenAreaItem(row);
        item.X = Snap(p.X - item.W / 2);
        item.Y = Snap(p.Y - item.H / 2);
        AddItem(item);
    }

    private void OnScreenAreaListDoubleClick(object sender, MouseButtonEventArgs e)
    {
        if (SelectedScreenAreaRow() is not { } row) return;
        if (_selected is { Kind: PanelItemKind.ScreenArea })
        {
            AssignScreenArea(_selected, row);
            BuildProperties();
            return;
        }
        var item = CreateScreenAreaItem(row);
        item.X = Math.Round((_doc.Width - item.W) / 2);
        item.Y = Math.Round((_doc.Height - item.H) / 2);
        AddItem(item);
    }

    private void AssignScreenArea(PanelItem item, ScreenAreaRow row)
    {
        Checkpoint();
        bool defaultName = string.IsNullOrWhiteSpace(item.Name) || item.Name == PanelItem.KindName(item.Kind) ||
                           _screenAreaRows.Any(r => r.ProfileId == item.ScreenProfileId && r.RegionId == item.ScreenRegionId && r.Name == item.Name);
        item.ScreenProfileId = row.ProfileId;
        item.ScreenRegionId = row.RegionId;
        if (defaultName) item.Name = row.Name;
        StructureChanged();
    }

    /// <summary>Neues Element im Seitenverhältnis des Bereichs auf dem Monitor: ein flacher Lebensbalken wird ein breiter
    /// Balken, die Minimap ein Quadrat. Breite ein gutes Drittel des Panels.</summary>
    private PanelItem CreateScreenAreaItem(ScreenAreaRow row)
    {
        var item = CreateItem(PanelItemKind.ScreenArea, null);
        item.Name = row.Name;
        item.ScreenProfileId = row.ProfileId;
        item.ScreenRegionId = row.RegionId;
        var image = ScreenHub.Shared.LatestImage;
        double monitorAspect = image is { Height: > 0 } ? (double)image.Width / image.Height : 16.0 / 9.0;
        double aspect = row.Area.H > 0 ? row.Area.W * monitorAspect / row.Area.H : 4; // Breite : Höhe in echten Pixeln
        double w = Math.Round(Math.Clamp(_doc.Width * 0.4, 120, 600));
        double h = Math.Round(w / Math.Max(0.05, aspect));
        if (h > _doc.Height * 0.6) { h = Math.Round(_doc.Height * 0.6); w = Math.Round(h * aspect); }
        item.W = Math.Max(16, w);
        item.H = Math.Max(12, h);
        return item;
    }

    private void ScreenAreaFields(PanelItem item)
    {
        Header("Bildschirm-Bereich");
        var row = _screenAreaRows.FirstOrDefault(r => r.ProfileId == item.ScreenProfileId && r.RegionId == item.ScreenRegionId);
        Label(row is null
            ? (item.ScreenRegionId.Length == 0 ? "Noch kein Bereich — links aus „Bildschirm-Bereiche“ auf das Element ziehen." : "Der Bereich wurde im Bildschirm-Sync gelöscht.")
            : $"{row.Group} · {row.Name}");
        var take = new Button { Content = "Gewählten Bereich übernehmen", HorizontalAlignment = HorizontalAlignment.Left, Margin = new Thickness(0, 4, 0, 8),
                                ToolTip = "Bereich links in der Liste auswählen (oder einfach auf das Element ziehen)" };
        take.Click += (_, _) => { if (SelectedScreenAreaRow() is { } r) { AssignScreenArea(item, r); BuildProperties(); } };
        PropertyPanel.Children.Add(take);
        ComboField("Darstellung", new[] { "Bild des Bereichs (vergrößert)", "Mischfarbe", "Füllstand-Balken (Farbstufen wie auf den LEDs)" }, (int)item.ScreenView,
            i => { Edit(() => item.ScreenView = (PanelScreenView)i); BuildProperties(); });
        if (item.ScreenView == PanelScreenView.Gauge)
        {
            Check("Senkrecht", item.Vertical, v => Edit(() => item.Vertical = v));
            SliderField("Segmente (0 = durchgehend)", 0, 40, item.Segments, "0", v => Edit(() => item.Segments = (int)Math.Round(v)));
            Check("Prozentwert anzeigen", item.ShowValue, v => Edit(() => item.ShowValue = v));
            ColorField("Leerer Teil", () => item.BackColor, c => Edit(() => item.BackColor = c));
            Label("Farbstufen und Blinken stellst du im Bildschirm-Sync beim Bereich ein („Anzeige auf zugewiesenen Geräten“). Bei Blitz-Bereichen leuchtet das Element kurz auf.");
        }
        if (item.ScreenView == PanelScreenView.Image)
        {
            ComboField("Einpassen", new[] { "Ganz zeigen", "Ausfüllen (beschneiden)", "Verzerren (ganze Fläche)" }, (int)item.Stretch, i => Edit(() => item.Stretch = (PanelStretch)i));
            Check("Weich vergrößern (aus = harte Pixelkanten)", item.ScreenSmooth, v => Edit(() => item.ScreenSmooth = v));
        }
        if (item.ScreenView != PanelScreenView.Gauge) ColorField("Hintergrund ohne Aufnahme", () => item.BackColor, c => Edit(() => item.BackColor = c));
        ColorField("Rahmen", () => item.BorderColor, c => Edit(() => item.BorderColor = c));
        SliderField("Rahmenstärke", 0, 20, item.BorderWidth, "0", v => Edit(() => item.BorderWidth = v));
        SliderField("Ecken", 0, 200, item.CornerRadius, "0", v => Edit(() => item.CornerRadius = v));
        Label("Die Farben werden wie im Bildschirm-Sync gesättigt (Einstellungen → Bildschirm-Sync → Farbsättigung). Die Aufnahme läuft, solange ein solches Element auf dem Display oder im Editor zu sehen ist.");
    }
}
