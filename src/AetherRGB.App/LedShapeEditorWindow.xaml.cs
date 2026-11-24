using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Shapes;
using AetherRGB.Core.Layout;

namespace AetherRGB.App;

/// <summary>Legt die physische LED-Anordnung eines Geräts fest: Schnur, Kreis, Quadrat oder frei auf einer
/// 100x100-Pixelmatrix gezeichnet (Klickreihenfolge = LED-Reihenfolge). Gespeichert wird nur Form + Anzahl,
/// nie die Position auf der Matrix — siehe <see cref="LedShape.FromPoints"/>.</summary>
public partial class LedShapeEditorWindow : Window
{
    private const int N = LedShape.MatrixSize;
    private const double Cell = 6;

    private readonly List<LedPoint> _customPoints = new();
    private readonly int _reportedCount;
    private bool _drawing;
    private bool _initializing = true;

    private static readonly Brush CellFill = new SolidColorBrush(Color.FromRgb(0x7C, 0x5C, 0xFF));
    private static readonly Brush FirstFill = new SolidColorBrush(Color.FromRgb(0x22, 0xC5, 0x5E));
    private static readonly Brush LastFill = new SolidColorBrush(Color.FromRgb(0xEF, 0x44, 0x44));
    private static readonly Brush GridMinor = new SolidColorBrush(Color.FromArgb(0x30, 0xFF, 0xFF, 0xFF));
    private static readonly Brush GridMajor = new SolidColorBrush(Color.FromArgb(0x60, 0xFF, 0xFF, 0xFF));
    private static readonly Brush PathBrush = new SolidColorBrush(Color.FromArgb(0x90, 0xFF, 0xFF, 0xFF));

    /// <summary>Ergebnis nach "Speichern", sonst null.</summary>
    public LedShape? Result { get; private set; }

    public LedShapeEditorWindow(string deviceName, int reportedLedCount, LedShape current)
    {
        InitializeComponent();
        _reportedCount = reportedLedCount;

        DeviceNameText.Text = deviceName;
        ReportedCountText.Text = reportedLedCount > 0
            ? $"Das Gerät meldet {reportedLedCount} LED(s)."
            : "Das Gerät meldet keine LED-Anzahl — bitte selbst angeben.";

        LedCountBox.Text = (current.Kind == LedShapeKind.Custom ? Math.Max(1, reportedLedCount) : current.LedCount).ToString();
        if (current.Kind == LedShapeKind.Custom)
            _customPoints.AddRange(current.ToMatrixPreview());

        if (current.Kind != LedShapeKind.Custom) _lastPresetKind = current.Kind;
        switch (current.Kind)
        {
            case LedShapeKind.Circle: KindCircle.IsChecked = true; break;
            case LedShapeKind.Square: KindSquare.IsChecked = true; break;
            case LedShapeKind.Custom: KindCustom.IsChecked = true; break;
            default: KindLine.IsChecked = true; break;
        }

        _initializing = false;
        UpdatePanels();
        Redraw();
    }

    private LedShapeKind SelectedKind =>
        KindCircle.IsChecked == true ? LedShapeKind.Circle :
        KindSquare.IsChecked == true ? LedShapeKind.Square :
        KindCustom.IsChecked == true ? LedShapeKind.Custom : LedShapeKind.Line;

    private int PresetCount => int.TryParse(LedCountBox.Text, out var n) ? Math.Clamp(n, 1, 2000) : Math.Max(1, _reportedCount);

    private LedShape BuildShape() => SelectedKind == LedShapeKind.Custom
        ? LedShape.FromPoints(_customPoints)
        : new LedShape { Kind = SelectedKind, LedCount = PresetCount };

    private void OnKindChanged(object sender, RoutedEventArgs e)
    {
        if (_initializing) return;

        // Beim Wechsel auf "Frei zeichnen" die bisherige Vorlage als Startpunkt übernehmen (doppelte
        // Zellen entfernt) — so lässt sich z. B. ein Kreis nachträglich anpassen. "Leeren" startet bei null.
        if (SelectedKind == LedShapeKind.Custom && _customPoints.Count == 0)
        {
            var preview = new LedShape { Kind = _lastPresetKind, LedCount = PresetCount };
            _customPoints.AddRange(preview.ToMatrixPreview().Distinct());
        }
        if (SelectedKind != LedShapeKind.Custom) _lastPresetKind = SelectedKind;

        UpdatePanels();
        Redraw();
    }

    private LedShapeKind _lastPresetKind = LedShapeKind.Line;

    private void OnLedCountChanged(object sender, TextChangedEventArgs e)
    {
        if (!_initializing) Redraw();
    }

    private void UpdatePanels()
    {
        bool custom = SelectedKind == LedShapeKind.Custom;
        PresetPanel.Visibility = custom ? Visibility.Collapsed : Visibility.Visible;
        CustomPanel.Visibility = custom ? Visibility.Visible : Visibility.Collapsed;
        MatrixCanvas.Cursor = custom ? Cursors.Pen : Cursors.Arrow;
    }

    // ===================== Zeichnen =====================

    private void Redraw()
    {
        MatrixCanvas.Children.Clear();
        DrawGrid();

        bool custom = SelectedKind == LedShapeKind.Custom;
        var points = custom ? (IReadOnlyList<LedPoint>)_customPoints : BuildShape().ToMatrixPreview();

        if (points.Count > 1)
        {
            var path = new Polyline { Stroke = PathBrush, StrokeThickness = 1, IsHitTestVisible = false };
            foreach (var p in points) path.Points.Add(new Point(p.X * Cell + Cell / 2, p.Y * Cell + Cell / 2));
            MatrixCanvas.Children.Add(path);
        }

        for (int i = 0; i < points.Count; i++)
        {
            var rect = new Rectangle
            {
                Width = Cell - 1,
                Height = Cell - 1,
                Fill = i == 0 ? FirstFill : i == points.Count - 1 ? LastFill : CellFill,
                IsHitTestVisible = false
            };
            Canvas.SetLeft(rect, points[i].X * Cell + 0.5);
            Canvas.SetTop(rect, points[i].Y * Cell + 0.5);
            MatrixCanvas.Children.Add(rect);
        }

        int count = custom ? _customPoints.Count : PresetCount;
        CountText.Text = $"{count} LED(s)" + (_reportedCount > 0 && count != _reportedCount ? $" — Gerät meldet {_reportedCount}" : "");
    }

    private void DrawGrid()
    {
        for (int i = 0; i <= N; i++)
        {
            var brush = i % 10 == 0 ? GridMajor : GridMinor;
            MatrixCanvas.Children.Add(new Line { X1 = i * Cell, Y1 = 0, X2 = i * Cell, Y2 = N * Cell, Stroke = brush, StrokeThickness = 0.5, IsHitTestVisible = false });
            MatrixCanvas.Children.Add(new Line { X1 = 0, Y1 = i * Cell, X2 = N * Cell, Y2 = i * Cell, Stroke = brush, StrokeThickness = 0.5, IsHitTestVisible = false });
        }
    }

    // ===================== Maus =====================

    private static LedPoint? CellAt(Point p)
    {
        int x = (int)(p.X / Cell), y = (int)(p.Y / Cell);
        return x is >= 0 and < N && y is >= 0 and < N ? new LedPoint(x, y) : null;
    }

    private void OnMatrixLeftDown(object sender, MouseButtonEventArgs e)
    {
        if (SelectedKind != LedShapeKind.Custom || CellAt(e.GetPosition(MatrixCanvas)) is not { } cell) return;
        _drawing = true;
        MatrixCanvas.CaptureMouse();
        if (TryAdd(cell)) Redraw();
    }

    private void OnMatrixMouseMove(object sender, MouseEventArgs e)
    {
        var cell = CellAt(e.GetPosition(MatrixCanvas));
        if (cell is { } c)
        {
            int index = _customPoints.IndexOf(c);
            HoverText.Text = SelectedKind == LedShapeKind.Custom && index >= 0 ? $"LED {index + 1} ({c.X}, {c.Y})" : $"({c.X}, {c.Y})";
        }

        if (!_drawing || cell is not { } target || _customPoints.Count == 0) return;

        // Schnelle Mausbewegungen überspringen Zellen — die Lücke per Bresenham-Linie auffüllen,
        // damit eine gezogene Linie lückenlos LED an LED reiht.
        bool changed = false;
        foreach (var p in LineCells(_customPoints[^1], target))
            changed |= TryAdd(p);
        if (changed) Redraw();
    }

    private void OnMatrixLeftUp(object sender, MouseButtonEventArgs e)
    {
        _drawing = false;
        MatrixCanvas.ReleaseMouseCapture();
    }

    private void OnMatrixMouseLeave(object sender, MouseEventArgs e)
    {
        if (!_drawing) HoverText.Text = "";
    }

    private void OnMatrixRightDown(object sender, MouseButtonEventArgs e)
    {
        if (SelectedKind != LedShapeKind.Custom || CellAt(e.GetPosition(MatrixCanvas)) is not { } cell) return;
        if (_customPoints.Remove(cell)) Redraw();
    }

    private bool TryAdd(LedPoint cell)
    {
        if (_customPoints.Contains(cell)) return false;
        _customPoints.Add(cell);
        return true;
    }

    private static IEnumerable<LedPoint> LineCells(LedPoint from, LedPoint to)
    {
        int x0 = from.X, y0 = from.Y, x1 = to.X, y1 = to.Y;
        int dx = Math.Abs(x1 - x0), sx = x0 < x1 ? 1 : -1;
        int dy = -Math.Abs(y1 - y0), sy = y0 < y1 ? 1 : -1;
        int err = dx + dy;
        while (true)
        {
            yield return new LedPoint(x0, y0);
            if (x0 == x1 && y0 == y1) yield break;
            int e2 = 2 * err;
            if (e2 >= dy) { err += dy; x0 += sx; }
            if (e2 <= dx) { err += dx; y0 += sy; }
        }
    }

    // ===================== Buttons =====================

    private void OnUndoClick(object sender, RoutedEventArgs e)
    {
        if (_customPoints.Count == 0) return;
        _customPoints.RemoveAt(_customPoints.Count - 1);
        Redraw();
    }

    private void OnClearClick(object sender, RoutedEventArgs e)
    {
        _customPoints.Clear();
        Redraw();
    }

    private void OnSaveClick(object sender, RoutedEventArgs e)
    {
        if (SelectedKind == LedShapeKind.Custom && _customPoints.Count == 0)
        {
            MessageBox.Show(this, "Bitte mindestens eine LED einzeichnen.", "LED-Anordnung", MessageBoxButton.OK, MessageBoxImage.Warning);
            return;
        }
        Result = BuildShape();
        DialogResult = true;
    }

    private void OnCancelClick(object sender, RoutedEventArgs e) => DialogResult = false;
}
