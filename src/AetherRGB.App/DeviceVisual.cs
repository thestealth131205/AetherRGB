using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Shapes;
using AetherRGB.Core.Layout;

namespace AetherRGB.App;

/// <summary>Ein auf der Effekt-Fläche platziertes Gerät (genauer: eine Zone): dunkle Kopfzeile mit Namen,
/// darunter die LEDs in ihrer festgelegten <see cref="LedShape"/> als Kacheln, die per <see cref="SetLedColors"/>
/// genau die Farbe zeigen, die an die echte LED geht (die Effektfarbe unter ihrer Position). Kapselt Verschieben, Skalieren (Ecke) und Drehen (Griff oben) —
/// die Resize-Mathematik arbeitet im lokalen, unrotierten Koordinatensystem des Rechtecks.</summary>
public sealed class DeviceVisual
{
    private const double HeaderHeight = 22;

    public readonly DeviceLayoutItem Item;
    public readonly Grid Root;

    /// <summary>Klick aufs Gerät.</summary>
    public event Action<DeviceVisual>? Selected;
    /// <summary>Position/Größe/Rotation ändert sich gerade (während des Ziehens).</summary>
    public event Action<DeviceVisual>? Changed;
    /// <summary>Ziehen beendet — guter Zeitpunkt zum Speichern.</summary>
    public event Action<DeviceVisual>? DragCompleted;

    private readonly Canvas _parentCanvas;
    private readonly Border _body;
    private readonly Border _header;
    private readonly Canvas _ledCanvas;
    private readonly FrameworkElement _resizeHandle;
    private readonly FrameworkElement _rotateHandle;
    private readonly FrameworkElement _rotateStem;
    private readonly RotateTransform _rotateTransform;
    private LedShape _shape;
    private readonly List<Border> _leds = new();
    private readonly List<SolidColorBrush> _ledBrushes = new();

    private static readonly Brush LedFill = new SolidColorBrush(Color.FromArgb(0x55, 0xFF, 0xFF, 0xFF));
    private static readonly Brush LedStroke = new SolidColorBrush(Color.FromArgb(0x40, 0xFF, 0xFF, 0xFF));
    private static readonly Brush HeaderBrush = new SolidColorBrush(Color.FromArgb(0xE6, 0x1E, 0x24, 0x30));
    private static readonly Brush BodyBrush = new SolidColorBrush(Color.FromArgb(0x26, 0x00, 0x00, 0x00));
    private static readonly Brush BorderNormal = new SolidColorBrush(Color.FromArgb(0x70, 0xB8, 0xC0, 0xD0));
    private static readonly Brush BorderSelected = new SolidColorBrush(Color.FromRgb(0x7C, 0x5C, 0xFF));

    private enum DragMode { None, Move, Resize, Rotate }
    private DragMode _dragMode = DragMode.None;
    private Point _dragStartMouseCanvas;
    private double _dragStartCenterX, _dragStartCenterY, _dragStartWidth, _dragStartHeight;

    public DeviceVisual(DeviceLayoutItem item, LedShape shape, Canvas parentCanvas)
    {
        Item = item;
        _shape = shape;
        _parentCanvas = parentCanvas;

        _ledCanvas = new Canvas { ClipToBounds = false, IsHitTestVisible = true };
        _body = new Border
        {
            Background = BodyBrush,
            BorderBrush = BorderNormal,
            BorderThickness = new Thickness(1.5),
            CornerRadius = new CornerRadius(6),
            Cursor = Cursors.SizeAll,
            Child = _ledCanvas
        };

        _header = new Border
        {
            Height = HeaderHeight,
            Background = HeaderBrush,
            CornerRadius = new CornerRadius(4, 4, 0, 0),
            VerticalAlignment = VerticalAlignment.Top,
            Margin = new Thickness(0, -HeaderHeight, 0, 0),
            Cursor = Cursors.SizeAll,
            Child = new TextBlock
            {
                Text = item.DisplayName,
                Foreground = new SolidColorBrush(Color.FromRgb(0xC8, 0xCC, 0xD8)),
                FontSize = 12,
                Margin = new Thickness(6, 0, 6, 0),
                VerticalAlignment = VerticalAlignment.Center,
                TextTrimming = TextTrimming.CharacterEllipsis
            }
        };

        _resizeHandle = new Border
        {
            Width = 14,
            Height = 14,
            Background = Brushes.White,
            CornerRadius = new CornerRadius(3),
            Cursor = Cursors.SizeNWSE,
            HorizontalAlignment = HorizontalAlignment.Right,
            VerticalAlignment = VerticalAlignment.Bottom,
            Margin = new Thickness(0, 0, -7, -7)
        };
        _rotateStem = new Rectangle
        {
            Width = 2,
            Height = 18,
            Fill = BorderSelected,
            HorizontalAlignment = HorizontalAlignment.Center,
            VerticalAlignment = VerticalAlignment.Top,
            Margin = new Thickness(0, -HeaderHeight - 18, 0, 0)
        };
        _rotateHandle = new Ellipse
        {
            Width = 14,
            Height = 14,
            Fill = BorderSelected,
            Stroke = Brushes.White,
            StrokeThickness = 1.5,
            Cursor = Cursors.Hand,
            HorizontalAlignment = HorizontalAlignment.Center,
            VerticalAlignment = VerticalAlignment.Top,
            Margin = new Thickness(0, -HeaderHeight - 30, 0, 0),
            ToolTip = "Drehen (Shift = in 15°-Schritten)"
        };

        Root = new Grid();
        Root.Children.Add(_body);
        Root.Children.Add(_header);
        Root.Children.Add(_rotateStem);
        Root.Children.Add(_resizeHandle);
        Root.Children.Add(_rotateHandle);

        _rotateTransform = new RotateTransform(item.RotationDegrees);
        Root.RenderTransform = _rotateTransform;
        Root.RenderTransformOrigin = new Point(0.5, 0.5);

        IsSelected = false;
        RebuildLeds();
        UpdateGeometry();

        _body.MouseLeftButtonDown += (_, e) => BeginDrag(DragMode.Move, e);
        _header.MouseLeftButtonDown += (_, e) => BeginDrag(DragMode.Move, e);
        _resizeHandle.MouseLeftButtonDown += (_, e) => BeginDrag(DragMode.Resize, e);
        _rotateHandle.MouseLeftButtonDown += (_, e) => BeginDrag(DragMode.Rotate, e);

        Root.MouseMove += OnMouseMove;
        Root.MouseLeftButtonUp += (_, _) => EndDrag();
        Root.LostMouseCapture += (_, _) => _dragMode = DragMode.None;
    }

    public bool IsSelected
    {
        get => _body.BorderBrush == BorderSelected;
        set
        {
            _body.BorderBrush = value ? BorderSelected : BorderNormal;
            var handles = value ? Visibility.Visible : Visibility.Collapsed;
            _resizeHandle.Visibility = handles;
            _rotateHandle.Visibility = handles;
            _rotateStem.Visibility = handles;
            Panel.SetZIndex(Root, value ? 100 : 0);
        }
    }

    /// <summary>Gerät offline/nicht gefunden: abgedunkelt, bleibt aber platziert.</summary>
    public void SetOnline(bool online) => Root.Opacity = online ? 1.0 : 0.45;

    public void SetShape(LedShape shape)
    {
        _shape = shape;
        RebuildLeds();
        LayoutLeds();
    }

    private void RebuildLeds()
    {
        _ledCanvas.Children.Clear();
        _leds.Clear();
        _ledBrushes.Clear();
        int count = _shape.ComputeNormalizedPositions().Count;
        for (int i = 0; i < count; i++)
        {
            var brush = new SolidColorBrush(((SolidColorBrush)LedFill).Color);
            _ledBrushes.Add(brush);
            var led = new Border
            {
                Background = brush,
                BorderBrush = LedStroke,
                BorderThickness = new Thickness(1),
                ToolTip = $"LED {i + 1}"
            };
            _leds.Add(led);
            _ledCanvas.Children.Add(led);
        }
    }

    /// <summary>Färbt die LED-Kacheln mit den Farben, die gerade an die LEDs gesendet werden
    /// (LED i = colors[i]); <paramref name="factor"/> dimmt nur die Anzeige (pausierte Fenster).</summary>
    public void SetLedColors(IReadOnlyList<AetherRGB.Core.Devices.RgbColor> colors, double factor)
    {
        for (int i = 0; i < _ledBrushes.Count && i < colors.Count; i++)
        {
            var c = colors[i];
            var color = Color.FromRgb((byte)(c.R * factor), (byte)(c.G * factor), (byte)(c.B * factor));
            if (_ledBrushes[i].Color != color) _ledBrushes[i].Color = color;
        }
    }

    /// <summary>Positioniert die LED-Kacheln im aktuellen Rechteck; Größe abhängig von Form und Dichte.</summary>
    private void LayoutLeds()
    {
        var positions = _shape.ComputeNormalizedPositions();
        if (positions.Count == 0) return;

        double w = Item.Width, h = Item.Height, ledW, ledH;
        switch (_shape.Kind)
        {
            case LedShapeKind.Line:
                ledW = Math.Max(2, w / positions.Count - 3);
                ledH = Math.Max(2, h - 8);
                break;
            case LedShapeKind.Custom:
            {
                int cols = _shape.Points.Max(p => p.X) + 1, rows = _shape.Points.Max(p => p.Y) + 1;
                ledW = Math.Max(2, w / cols - 2);
                ledH = Math.Max(2, h / rows - 2);
                double s = Math.Min(ledW, ledH);
                ledW = ledH = s;
                break;
            }
            default:
            {
                double perimeter = _shape.Kind == LedShapeKind.Circle ? Math.PI * (w + h) / 2 : 2 * (w + h);
                double s = Math.Clamp(perimeter / positions.Count - 3, 2, Math.Min(w, h) / 3);
                ledW = ledH = s;
                break;
            }
        }

        double radius = Math.Min(ledW, ledH) * 0.3;
        for (int i = 0; i < _leds.Count && i < positions.Count; i++)
        {
            var led = _leds[i];
            led.Width = ledW;
            led.Height = ledH;
            led.CornerRadius = new CornerRadius(radius);
            // Die LED-Kacheln dürfen über das Rechteck hinausragen (Kreis-/Rahmen-LEDs sitzen auf der Kante).
            Canvas.SetLeft(led, positions[i].U * w - ledW / 2);
            Canvas.SetTop(led, positions[i].V * h - ledH / 2);
        }
    }

    private void BeginDrag(DragMode mode, MouseButtonEventArgs e)
    {
        Selected?.Invoke(this);
        _dragMode = mode;
        _dragStartMouseCanvas = e.GetPosition(_parentCanvas);
        _dragStartCenterX = Item.CenterX;
        _dragStartCenterY = Item.CenterY;
        _dragStartWidth = Item.Width;
        _dragStartHeight = Item.Height;
        Root.CaptureMouse();
        e.Handled = true;
    }

    private void EndDrag()
    {
        if (_dragMode == DragMode.None) return;
        _dragMode = DragMode.None;
        Root.ReleaseMouseCapture();
        DragCompleted?.Invoke(this);
    }

    private void OnMouseMove(object sender, MouseEventArgs e)
    {
        if (_dragMode == DragMode.None) return;
        var current = e.GetPosition(_parentCanvas);
        double dx = current.X - _dragStartMouseCanvas.X;
        double dy = current.Y - _dragStartMouseCanvas.Y;

        switch (_dragMode)
        {
            case DragMode.Move:
                Item.CenterX = Math.Clamp(_dragStartCenterX + dx, 0, _parentCanvas.Width);
                Item.CenterY = Math.Clamp(_dragStartCenterY + dy, 0, _parentCanvas.Height);
                break;

            case DragMode.Resize:
            {
                // Mausbewegung ins lokale (unrotierte) Koordinatensystem des Rechtecks zurückdrehen,
                // damit die Ecke bei einem bereits gedrehten Gerät intuitiv "mit der Maus" mitläuft.
                double angleRad = Item.RotationDegrees * Math.PI / 180.0;
                double cos = Math.Cos(-angleRad), sin = Math.Sin(-angleRad);
                double localDx = dx * cos - dy * sin;
                double localDy = dx * sin + dy * cos;

                double newWidth = Math.Max(20, _dragStartWidth + localDx);
                double newHeight = Math.Max(20, _dragStartHeight + localDy);
                double deltaW = newWidth - _dragStartWidth;
                double deltaH = newHeight - _dragStartHeight;

                // Gegenüberliegende Ecke (oben-links) bleibt fix — dafür verschiebt sich die Mitte
                // um die halbe Größenänderung, zurückgedreht in globale Canvas-Koordinaten.
                double cosF = Math.Cos(angleRad), sinF = Math.Sin(angleRad);
                double shiftLocalX = deltaW / 2.0, shiftLocalY = deltaH / 2.0;

                Item.Width = newWidth;
                Item.Height = newHeight;
                Item.CenterX = _dragStartCenterX + shiftLocalX * cosF - shiftLocalY * sinF;
                Item.CenterY = _dragStartCenterY + shiftLocalX * sinF + shiftLocalY * cosF;
                break;
            }

            case DragMode.Rotate:
            {
                double angleDeg = Math.Atan2(current.Y - Item.CenterY, current.X - Item.CenterX) * 180.0 / Math.PI + 90.0;
                if (Keyboard.Modifiers.HasFlag(ModifierKeys.Shift))
                    angleDeg = Math.Round(angleDeg / 15.0) * 15.0;
                Item.RotationDegrees = NormalizeAngle(angleDeg);
                break;
            }
        }

        UpdateGeometry();
        Changed?.Invoke(this);
    }

    public static double NormalizeAngle(double deg) => ((deg % 360) + 360) % 360;

    /// <summary>Spiegelt den aktuellen <see cref="Item"/>-Zustand auf die tatsächliche
    /// Canvas-Position/-Größe/-Rotation des Visuals.</summary>
    public void UpdateGeometry()
    {
        Root.Width = Item.Width;
        Root.Height = Item.Height;
        Canvas.SetLeft(Root, Item.CenterX - Item.Width / 2.0);
        Canvas.SetTop(Root, Item.CenterY - Item.Height / 2.0);
        _rotateTransform.Angle = Item.RotationDegrees;
        LayoutLeds();
    }
}
