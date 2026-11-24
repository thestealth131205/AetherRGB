using System.IO;
using System.Runtime.InteropServices;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Interop;
using System.Windows.Threading;

namespace AetherRGB.App;

public partial class App : Application
{
    private static readonly string LogPath =
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "AetherRGB", "crash.log");

    protected override void OnStartup(StartupEventArgs e)
    {
        // Ohne diese Handler stirbt die App bei jedem unbehandelten Fehler beim Start
        // komplett lautlos (kein Fenster, keine Meldung, kein Eintrag im Taskmanager mehr) —
        // genau das Symptom "startet einfach gar nicht". Jetzt landet jeder Fehler sichtbar
        // als Meldung + Log-Datei, statt im Nichts zu verschwinden.
        DispatcherUnhandledException += OnDispatcherUnhandledException;
        AppDomain.CurrentDomain.UnhandledException += OnAppDomainUnhandledException;
        TaskScheduler.UnobservedTaskException += (_, args) =>
        {
            LogCrash("UnobservedTaskException", args.Exception);
            args.SetObserved();
        };

        // Dunkle Titelleiste für jedes Fenster der App (Windows 10 1809+ / 11), passend zum Design.
        EventManager.RegisterClassHandler(typeof(Window), FrameworkElement.LoadedEvent,
            new RoutedEventHandler((s, _) => UseDarkTitleBar((Window)s)));

        base.OnStartup(e);

        // Hilfsprozess mit Adminrechten (bzw. Deinstallation): Chroma-Bridge nach System32 legen/entfernen,
        // ohne Fenster und ohne die Einzelinstanz-Sperre der laufenden App.
        if (e.Args.Contains(ChromaBridgeInstaller.Argument, StringComparer.OrdinalIgnoreCase))
        {
            Shutdown(ChromaBridgeInstaller.RunFromCommandLine(e.Args));
            return;
        }

        if (!AcquireSingleInstance(e.Args))
        {
            Shutdown();
            return;
        }

        var window = new MainWindow(startMinimized: e.Args.Contains(Autostart.MinimizedArgument, StringComparer.OrdinalIgnoreCase));
        MainWindow = window;
        ListenForShowRequests(window);
        window.Show();
    }

    // ===================== Nur eine laufende Instanz =====================

    /// <summary>Argument für Neustarts der App durch sich selbst (als Administrator, nach portablem Update):
    /// die neue Instanz wartet, bis die alte sich beendet hat, statt sofort aufzugeben.</summary>
    public const string RestartArgument = "--restart";

    private const string InstanceMutexName = @"Local\AetherRGB-SingleInstance";
    private const string ShowEventName = @"Local\AetherRGB-Show";
    private Mutex? _instanceMutex;
    private EventWaitHandle? _showEvent;

    /// <summary>Zwei Instanzen würden sich um dieselben Geräte streiten (beim MSI SDK sogar doppelt beim
    /// Dienst anmelden) — mit Autostart passiert ein zweiter Start schnell. Läuft schon eine, holt dieser
    /// Start deren Fenster nach vorn und beendet sich.</summary>
    private bool AcquireSingleInstance(string[] args)
    {
        bool isRestart = args.Contains(RestartArgument, StringComparer.OrdinalIgnoreCase);
        try
        {
            _instanceMutex = new Mutex(true, InstanceMutexName, out bool createdNew);
            if (createdNew) return true;
            try
            {
                if (_instanceMutex.WaitOne(isRestart ? TimeSpan.FromSeconds(15) : TimeSpan.Zero)) return true;
            }
            catch (AbandonedMutexException)
            {
                return true; // alte Instanz ist abgestürzt/beendet, ohne freizugeben — jetzt gehört er uns
            }
        }
        catch (UnauthorizedAccessException)
        {
            // Die laufende Instanz hat Administratorrechte, diese nicht — Windows verweigert dann den Zugriff
            // auf ihren Mutex. Heißt aber genauso: AetherRGB läuft schon.
        }

        _instanceMutex = null;
        if (args.Contains(Autostart.MinimizedArgument, StringComparer.OrdinalIgnoreCase)) return false; // Autostart: still beenden

        try
        {
            if (EventWaitHandle.TryOpenExisting(ShowEventName, out var showEvent))
            {
                using (showEvent) showEvent.Set();
                return false;
            }
        }
        catch (UnauthorizedAccessException)
        {
            // Siehe oben — eine Instanz ohne Adminrechte darf die mit Adminrechten nicht aufwecken.
        }
        MessageBox.Show("AetherRGB läuft bereits (Symbol im Infobereich der Taskleiste neben der Uhr).",
            "AetherRGB", MessageBoxButton.OK, MessageBoxImage.Information);
        return false;
    }

    private void ListenForShowRequests(MainWindow window)
    {
        try
        {
            _showEvent = new EventWaitHandle(false, EventResetMode.AutoReset, ShowEventName);
        }
        catch (Exception ex) when (ex is UnauthorizedAccessException or WaitHandleCannotBeOpenedException)
        {
            return; // dann bekommt ein zweiter Start eben die Meldung statt des Fensters
        }
        var showEvent = _showEvent;
        new Thread(() =>
        {
            try
            {
                while (showEvent.WaitOne())
                    Dispatcher.BeginInvoke(window.ShowFromOtherInstance);
            }
            catch (ObjectDisposedException) { /* App wird beendet */ }
        }) { IsBackground = true, Name = "AetherRGB Einzelinstanz" }.Start();
    }

    protected override void OnExit(ExitEventArgs e)
    {
        try { _instanceMutex?.ReleaseMutex(); } catch { /* gehört evtl. schon nicht mehr uns */ }
        _instanceMutex?.Dispose();
        _showEvent?.Dispose();
        base.OnExit(e);
    }

    [DllImport("dwmapi.dll")]
    private static extern int DwmSetWindowAttribute(IntPtr hwnd, int attribute, ref int value, int size);

    private static void UseDarkTitleBar(Window window)
    {
        try
        {
            var hwnd = new WindowInteropHelper(window).Handle;
            if (hwnd == IntPtr.Zero) return;
            int on = 1;
            // 20 = DWMWA_USE_IMMERSIVE_DARK_MODE, ältere Windows-10-Builds kennen nur 19.
            if (DwmSetWindowAttribute(hwnd, 20, ref on, sizeof(int)) != 0)
                DwmSetWindowAttribute(hwnd, 19, ref on, sizeof(int));
        }
        catch
        {
            // Rein optisch — ohne DWM bleibt die Titelleiste eben hell.
        }
    }

    private void OnDispatcherUnhandledException(object sender, DispatcherUnhandledExceptionEventArgs e)
    {
        LogCrash("DispatcherUnhandledException", e.Exception);
        MessageBox.Show(
            $"AetherRGB ist auf einen Fehler gestoßen und muss beendet werden:\n\n{e.Exception.Message}\n\nDetails in: {LogPath}",
            "AetherRGB — Fehler", MessageBoxButton.OK, MessageBoxImage.Error);
        e.Handled = true;
        Shutdown();
    }

    private void OnAppDomainUnhandledException(object sender, UnhandledExceptionEventArgs e)
    {
        LogCrash("AppDomain.UnhandledException", e.ExceptionObject as Exception);
        try
        {
            MessageBox.Show(
                $"AetherRGB ist auf einen schweren Fehler gestoßen und wird beendet.\n\nDetails in: {LogPath}",
                "AetherRGB — Fehler", MessageBoxButton.OK, MessageBoxImage.Error);
        }
        catch
        {
            // Falls selbst die MessageBox nicht mehr angezeigt werden kann (z. B. UI-Thread
            // bereits zerstört) — die Log-Datei ist dann der einzige Weg, den Fehler zu sehen.
        }
    }

    private static void LogCrash(string source, Exception? ex)
    {
        try
        {
            var dir = Path.GetDirectoryName(LogPath)!;
            Directory.CreateDirectory(dir);
            File.AppendAllText(LogPath,
                $"[{DateTime.Now:yyyy-MM-dd HH:mm:ss}] {source}:\n{ex}\n\n");
        }
        catch
        {
            // Kann die Log-Datei nicht geschrieben werden, gibt es ohnehin nichts mehr zu tun.
        }
    }
}
