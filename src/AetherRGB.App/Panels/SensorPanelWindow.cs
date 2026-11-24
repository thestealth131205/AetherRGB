using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Threading;
using AetherRGB.Core.Panels;
using AetherRGB.Core.Sensors;
using Forms = System.Windows.Forms;

namespace AetherRGB.App.Panels;

/// <summary>Randloses Vollbildfenster auf dem Zweitdisplay. Das 5"-USB-Display meldet sich mit seinem Treiber als ganz
/// normaler Windows-Monitor (1024 × 600) — genauso arbeitet auch AIDA64s SensorPanel. Das Fenster nimmt nie den Fokus,
/// erscheint nicht in der Taskleiste und nicht bei Alt+Tab.</summary>
internal sealed class SensorPanelWindow : Window
{
    private readonly SensorPanelSurface _surface;
    private readonly DispatcherTimer _timer = new(DispatcherPriority.Render);
    private Forms.Screen _screen;

    public event Action? OpenEditorRequested;
    public event Action? HideRequested;

    public SensorPanelWindow(SensorPanelRenderer renderer, Forms.Screen screen, int fps, bool topmost)
    {
        _screen = screen;
        Title = "AetherRGB Sensor-Panel";
        WindowStyle = WindowStyle.None;
        ResizeMode = ResizeMode.NoResize;
        ShowInTaskbar = false;
        ShowActivated = false;
        Topmost = topmost;
        Background = Brushes.Black;
        WindowStartupLocation = WindowStartupLocation.Manual;

        _surface = new SensorPanelSurface(renderer);
        Content = new Viewbox { Stretch = Stretch.Uniform, Child = _surface };

        var menu = new ContextMenu();
        var edit = new MenuItem { Header = "Panel-Editor öffnen" };
        edit.Click += (_, _) => OpenEditorRequested?.Invoke();
        var hide = new MenuItem { Header = "Panel ausblenden" };
        hide.Click += (_, _) => HideRequested?.Invoke();
        menu.Items.Add(edit);
        menu.Items.Add(hide);
        ContextMenu = menu;

        SetFps(fps);
        _timer.Tick += (_, _) =>
        {
            SensorHub.Shared.Demand();
            _surface.InvalidateVisual();
        };
        Loaded += (_, _) => _timer.Start();
        Closed += (_, _) => _timer.Stop();
        PlaceOnScreen();
    }

    public string DeviceName => _screen.DeviceName;

    public SensorPanelDocument? Document
    {
        get => _surface.Document;
        set => _surface.Document = value;
    }

    public void SetFps(int fps) => _timer.Interval = TimeSpan.FromMilliseconds(1000.0 / Math.Clamp(fps, 1, 60));

    public void MoveTo(Forms.Screen screen)
    {
        _screen = screen;
        PlaceOnScreen();
    }

    private void PlaceOnScreen()
    {
        // Vorläufige Lage in WPF-Einheiten; die exakte Lage setzt SetWindowPos in Gerätepixeln (DPI des Displays egal).
        var b = _screen.Bounds;
        Left = b.X;
        Top = b.Y;
        Width = b.Width;
        Height = b.Height;
        var hwnd = new WindowInteropHelper(this).Handle;
        if (hwnd != IntPtr.Zero) SetWindowPos(hwnd, Topmost ? HWND_TOPMOST : HWND_NOTOPMOST, b.X, b.Y, b.Width, b.Height, SWP_NOACTIVATE | SWP_SHOWWINDOW);
    }

    protected override void OnSourceInitialized(EventArgs e)
    {
        base.OnSourceInitialized(e);
        var hwnd = new WindowInteropHelper(this).Handle;
        long ex = GetWindowLongPtr(hwnd, GWL_EXSTYLE).ToInt64();
        SetWindowLongPtr(hwnd, GWL_EXSTYLE, new IntPtr(ex | WS_EX_TOOLWINDOW | WS_EX_NOACTIVATE));
        PlaceOnScreen();
    }

    private const int GWL_EXSTYLE = -20;
    private const long WS_EX_TOOLWINDOW = 0x00000080, WS_EX_NOACTIVATE = 0x08000000;
    private const uint SWP_NOACTIVATE = 0x0010, SWP_SHOWWINDOW = 0x0040;
    private static readonly IntPtr HWND_TOPMOST = new(-1), HWND_NOTOPMOST = new(-2);

    [DllImport("user32.dll", EntryPoint = "GetWindowLongPtrW")] private static extern IntPtr GetWindowLongPtr(IntPtr hwnd, int index);
    [DllImport("user32.dll", EntryPoint = "SetWindowLongPtrW")] private static extern IntPtr SetWindowLongPtr(IntPtr hwnd, int index, IntPtr value);
    [DllImport("user32.dll")] private static extern bool SetWindowPos(IntPtr hwnd, IntPtr after, int x, int y, int cx, int cy, uint flags);
}
