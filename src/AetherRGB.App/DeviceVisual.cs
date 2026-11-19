using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Shapes;
using AetherRGB.Core.Layout;

namespace AetherRGB.App;

/// <summary>Eine einzelne, auf dem Layout-<see cref="Canvas"/> ziehbare/skalierbare/drehbare Darstellung
/// eines Geräts (genauer: einer Zone). Kapselt die gesamte Maus-Interaktion für genau ein Rechteck —
/// die Mathematik für Resize/Rotate arbeitet im lokalen, unrotierten Koordinatensystem des Rechtecks,
/// damit sich Ecke und Drehgriff auch bei bereits gedrehten Geräten intuitiv verhalten.</summary>
public sealed class DeviceVisual
{
    public readonly DeviceLayoutItem Item;
    public readonly Grid Root;

    private readonly Canvas _parentCanvas;
    private readonly Border _body;
    private readonly RotateTransform _rotateTransform;

    private enum DragMode { None, Move, Resize, Rotate }
    private DragMode _dragMode = DragMode.None;
    private Point _dragStartMouseCanvas;
    private double _dragStartCenterX, _dragStartCenterY, _dragStartWidth, _dragStartHeight;

    public DeviceVisual(DeviceLayoutItem item, Canvas parentCanvas, Brush bodyBrush)
    {
        Item = item;
        _parentCanvas = parentCanvas;

        _body = new Border
        {
            Background = bodyBrush,
            BorderBrush = Brushes.White,
            BorderThickness = new Thickness(1.5),
            CornerRadius = new CornerRadius(4),
            Cursor = Cursors.SizeAll
        };
        var label = new TextBlock
        {
            Text = item.DisplayName,
            Foreground = Brushes.White,
            FontSize = 12,
            FontWeight = FontWeights.SemiBold,
            HorizontalAlignment = HorizontalAlignment.Center,
            VerticalAlignment = VerticalAlignment.Center,
            TextAlignment = TextAlignment.Center,
            TextWrapping = TextWrapping.Wrap,
            Margin = new Thickness(4)
        };
        _body.Child = label;

        var resizeHandle = new Border
        {
            Width = 16,
            Height = 16,
            Background = Brushes.White,
            CornerRadius = new CornerRadius(3),
            Cursor = Cursors.SizeNWSE,
            HorizontalAlignment = HorizontalAlignment.Right,
            VerticalAlignment = VerticalAlignment.Bottom,
            Margin = new Thickness(0, 0, -8, -8)
        };

        var rotateHandle = new Ellipse
        {
            Width = 14,
            Height = 14,
            Fill = Brushes.Orange,
            Stroke = Brushes.White,
            StrokeThickness = 1,
            Cursor = Cursors.Hand,
            HorizontalAlignment = HorizontalAlignment.Center,
            VerticalAlignment = VerticalAlignment.Top,
            Margin = new Thickness(0, -26, 0, 0)
        };
        var rotateStem = new Rectangle
        {
            Width = 2,
            Height = 18,
            Fill = Brushes.Orange,
            HorizontalAlignment = HorizontalAlignment.Center,
            VerticalAlignment = VerticalAlignment.Top,
            Margin = new Thickness(0, -18, 0, 0)
        };

        Root = new Grid();
        Root.Children.Add(_body);
        Root.Children.Add(rotateStem);
        Root.Children.Add(resizeHandle);
        Root.Children.Add(rotateHandle);

        _rotateTransform = new RotateTransform(item.RotationDegrees);
        Root.RenderTransform = _rotateTransform;
        Root.RenderTransformOrigin = new Point(0.5, 0.5);

        UpdateGeometry();

        _body.MouseLeftButtonDown += (_, e) => BeginDrag(DragMode.Move, e);
        resizeHandle.MouseLeftButtonDown += (_, e) => BeginDrag(DragMode.Resize, e);
        rotateHandle.MouseLeftButtonDown += (_, e) => BeginDrag(DragMode.Rotate, e);

        Root.MouseMove += OnMouseMove;
        Root.MouseLeftButtonUp += (_, _) => EndDrag();
        Root.LostMouseCapture += (_, _) => _dragMode = DragMode.None;
    }

    private void BeginDrag(DragMode mode, MouseButtonEventArgs e)
    {
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
        _dragMode = DragMode.None;
        Root.ReleaseMouseCapture();
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
                Item.CenterX = Clamp(_dragStartCenterX + dx, 0, _parentCanvas.Width);
                Item.CenterY = Clamp(_dragStartCenterY + dy, 0, _parentCanvas.Height);
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
                double angleFwd = Item.RotationDegrees * Math.PI / 180.0;
                double cosF = Math.Cos(angleFwd), sinF = Math.Sin(angleFwd);
                double shiftLocalX = deltaW / 2.0, shiftLocalY = deltaH / 2.0;
                double shiftGlobalX = shiftLocalX * cosF - shiftLocalY * sinF;
                double shiftGlobalY = shiftLocalX * sinF + shiftLocalY * cosF;

                Item.Width = newWidth;
                Item.Height = newHeight;
                Item.CenterX = _dragStartCenterX + shiftGlobalX;
                Item.CenterY = _dragStartCenterY + shiftGlobalY;
                break;
            }

            case DragMode.Rotate:
            {
                double angleDeg = Math.Atan2(current.Y - Item.CenterY, current.X - Item.CenterX) * 180.0 / Math.PI;
                Item.RotationDegrees = angleDeg + 90.0;
                break;
            }
        }

        UpdateGeometry();
    }

    /// <summary>Spiegelt den aktuellen <see cref="Item"/>-Zustand auf die tatsächliche
    /// Canvas-Position/-Größe/-Rotation des Visuals.</summary>
    public void UpdateGeometry()
    {
        Root.Width = Item.Width;
        Root.Height = Item.Height;
        Canvas.SetLeft(Root, Item.CenterX - Item.Width / 2.0);
        Canvas.SetTop(Root, Item.CenterY - Item.Height / 2.0);
        _rotateTransform.Angle = Item.RotationDegrees;
    }

    private static double Clamp(double value, double min, double max) => Math.Max(min, Math.Min(max, value));
}
