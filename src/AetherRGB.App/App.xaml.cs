using System.IO;
using System.Threading.Tasks;
using System.Windows;
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

        base.OnStartup(e);
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
