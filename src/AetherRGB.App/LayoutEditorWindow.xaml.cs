using System.Windows;
using System.Windows.Media;
using AetherRGB.Core.Layout;

namespace AetherRGB.App;

/// <summary>Fenster zum visuellen Platzieren aller Geräte auf der virtuellen 2D-Fläche
/// (SignalRGB-Stil) — verschieben, an der Ecke skalieren, am oberen Griff drehen.
/// Arbeitet auf einer Arbeitskopie des Layouts; erst "Speichern" übernimmt die Änderungen.</summary>
public partial class LayoutEditorWindow : Window
{
    private readonly CanvasLayout _workingLayout;
    private readonly Action<CanvasLayout> _onSaved;
    private readonly List<DeviceVisual> _visuals = new();

    private static readonly Brush[] DeviceColors =
    {
        new SolidColorBrush(Color.FromRgb(0x3B, 0x82, 0xF6)),
        new SolidColorBrush(Color.FromRgb(0x8B, 0x5C, 0xF6)),
        new SolidColorBrush(Color.FromRgb(0x10, 0xB9, 0x81)),
        new SolidColorBrush(Color.FromRgb(0xF5, 0x9E, 0x0B)),
        new SolidColorBrush(Color.FromRgb(0xEF, 0x44, 0x44)),
        new SolidColorBrush(Color.FromRgb(0xEC, 0x48, 0x99)),
    };

    public LayoutEditorWindow(CanvasLayout currentLayout, Action<CanvasLayout> onSaved)
    {
        InitializeComponent();
        _onSaved = onSaved;

        // Arbeitskopie: Abbrechen/Schließen ohne Speichern darf das übergebene Layout nie verändern.
        _workingLayout = new CanvasLayout
        {
            CanvasWidth = currentLayout.CanvasWidth,
            CanvasHeight = currentLayout.CanvasHeight,
            Items = currentLayout.Items.Select(i => new DeviceLayoutItem
            {
                DeviceId = i.DeviceId,
                ZoneIndex = i.ZoneIndex,
                LedCount = i.LedCount,
                DisplayName = i.DisplayName,
                CenterX = i.CenterX,
                CenterY = i.CenterY,
                Width = i.Width,
                Height = i.Height,
                RotationDegrees = i.RotationDegrees
            }).ToList()
        };

        LayoutCanvas.Width = _workingLayout.CanvasWidth;
        LayoutCanvas.Height = _workingLayout.CanvasHeight;

        DrawGridLines();
        BuildDeviceVisuals();
    }

    private void DrawGridLines()
    {
        const double step = 100;
        for (double x = 0; x <= LayoutCanvas.Width; x += step)
        {
            var line = new System.Windows.Shapes.Line
            {
                X1 = x, Y1 = 0, X2 = x, Y2 = LayoutCanvas.Height,
                Stroke = Brushes.DimGray, StrokeThickness = 0.5, Opacity = 0.3
            };
            LayoutCanvas.Children.Add(line);
        }
        for (double y = 0; y <= LayoutCanvas.Height; y += step)
        {
            var line = new System.Windows.Shapes.Line
            {
                X1 = 0, Y1 = y, X2 = LayoutCanvas.Width, Y2 = y,
                Stroke = Brushes.DimGray, StrokeThickness = 0.5, Opacity = 0.3
            };
            LayoutCanvas.Children.Add(line);
        }
    }

    private void BuildDeviceVisuals()
    {
        _visuals.Clear();
        for (int i = 0; i < _workingLayout.Items.Count; i++)
        {
            var item = _workingLayout.Items[i];
            var brush = DeviceColors[i % DeviceColors.Length];
            var visual = new DeviceVisual(item, LayoutCanvas, brush);
            _visuals.Add(visual);
            LayoutCanvas.Children.Add(visual.Root);
        }

        HintText.Text = _workingLayout.Items.Count == 0
            ? "Keine platzierbaren Zonen gefunden — erst unter 'Geräte neu suchen' Geräte verbinden."
            : "Tipp: Die Rechteckbreite entspricht der Anordnung der LEDs entlang der langen Seite — so wie die LEDs real am Gehäuse verlaufen.";
    }

    private void OnResetClick(object sender, RoutedEventArgs e)
    {
        double nextX = 150;
        foreach (var visual in _visuals)
        {
            visual.Item.CenterX = nextX;
            visual.Item.CenterY = 120;
            visual.Item.Width = 260;
            visual.Item.Height = 36;
            visual.Item.RotationDegrees = 0;
            visual.UpdateGeometry();
            nextX += 300;
        }
    }

    private void OnSaveClick(object sender, RoutedEventArgs e)
    {
        _workingLayout.Save();
        _onSaved(_workingLayout);
        Close();
    }

    private void OnCloseClick(object sender, RoutedEventArgs e) => Close();
}
