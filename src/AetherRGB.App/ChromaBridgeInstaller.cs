using System.Diagnostics;
using System.IO;
using System.Reflection;
using System.Security.Principal;
using System.Text;

namespace AetherRGB.App;

/// <summary>Legt die Chroma-Bridge (<c>native/ChromaBridge</c>) als RzChromaSDK64.dll nach System32 und als
/// RzChromaSDK.dll nach SysWOW64 — dort suchen Spiele die Razer-DLL. Eine vorhandene Datei, die nicht von
/// AetherRGB stammt (also die echte Razer-DLL von Synapse), wird nie überschrieben oder gelöscht.
/// Schreiben nach System32 braucht Adminrechte; ohne startet sich die App dafür kurz selbst per UAC mit
/// <see cref="Argument"/>.</summary>
public static class ChromaBridgeInstaller
{
    public const string Argument = "--chroma-bridge";

    private static readonly byte[] Marker = Encoding.ASCII.GetBytes("AetherRGB-ChromaBridge/");

    public enum FileState { Missing, Ours, OursOutdated, Foreign }

    private sealed record Target(string Directory, string FileName, string Resource, string Label);

    private static IEnumerable<Target> Targets()
    {
        yield return new Target(Environment.GetFolderPath(Environment.SpecialFolder.System), "RzChromaSDK64.dll", "ChromaBridge.x64.dll", "64-Bit-Spiele");
        if (Environment.Is64BitOperatingSystem)
            yield return new Target(Environment.GetFolderPath(Environment.SpecialFolder.SystemX86), "RzChromaSDK.dll", "ChromaBridge.x86.dll", "32-Bit-Spiele");
    }

    /// <summary>Die DLLs sind nur im CI-Build eingebettet (MSVC); ein lokaler Build ohne sie kann nichts installieren.</summary>
    public static bool IsAvailable => Assembly.GetExecutingAssembly().GetManifestResourceNames().Contains("ChromaBridge.x64.dll");

    public static IReadOnlyList<(string Label, string Path, FileState State)> GetStatus() =>
        Targets().Select(t =>
        {
            var path = Path.Combine(t.Directory, t.FileName);
            return (t.Label, path, StateOf(path, t.Resource));
        }).ToList();

    private static FileState StateOf(string path, string resource)
    {
        byte[] content;
        try
        {
            if (!File.Exists(path)) return FileState.Missing;
            content = File.ReadAllBytes(path);
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            return FileState.Foreign; // unlesbar — im Zweifel nicht anfassen
        }
        if (content.AsSpan().IndexOf(Marker) < 0) return FileState.Foreign;
        var embedded = ReadResource(resource);
        return embedded is null || content.AsSpan().SequenceEqual(embedded) ? FileState.Ours : FileState.OursOutdated;
    }

    private static byte[]? ReadResource(string name)
    {
        using var stream = Assembly.GetExecutingAssembly().GetManifestResourceStream(name);
        if (stream is null) return null;
        using var ms = new MemoryStream();
        stream.CopyTo(ms);
        return ms.ToArray();
    }

    public static bool IsElevated()
    {
        using var identity = WindowsIdentity.GetCurrent();
        return new WindowsPrincipal(identity).IsInRole(WindowsBuiltInRole.Administrator);
    }

    /// <summary>Installiert bzw. entfernt die Bridge; ohne Adminrechte über einen eigenen Prozess mit
    /// UAC-Abfrage. Liefert eine Meldung für Dialog und Log, null = UAC abgelehnt.</summary>
    public static string? Run(bool install)
    {
        if (IsElevated()) return install ? Install() : Uninstall();
        if (Environment.ProcessPath is not { } exe) return "Programmpfad unbekannt.";

        var resultFile = Path.Combine(Path.GetTempPath(), $"AetherRGB-chroma-{Guid.NewGuid():N}.txt");
        try
        {
            using var process = Process.Start(new ProcessStartInfo(exe, $"{Argument} {(install ? "install" : "uninstall")} \"{resultFile}\"")
            {
                UseShellExecute = true,
                Verb = "runas",
            });
            if (process is null) return "Hilfsprozess konnte nicht gestartet werden.";
            if (!process.WaitForExit(30000)) return "Zeitüberschreitung beim Installieren.";
            return File.Exists(resultFile) ? File.ReadAllText(resultFile) : $"Hilfsprozess beendet (Code {process.ExitCode}), ohne Ergebnis.";
        }
        catch (System.ComponentModel.Win32Exception)
        {
            return null; // UAC abgelehnt
        }
        finally
        {
            try { File.Delete(resultFile); } catch (IOException) { }
        }
    }

    /// <summary>Einstieg für den Hilfsprozess (<c>--chroma-bridge install|uninstall [Ergebnisdatei]</c>), auch
    /// vom Deinstallationsprogramm genutzt. Gibt den Exit-Code zurück.</summary>
    public static int RunFromCommandLine(string[] args)
    {
        int index = Array.FindIndex(args, a => a.Equals(Argument, StringComparison.OrdinalIgnoreCase));
        string action = index + 1 < args.Length ? args[index + 1] : "";
        string? resultFile = index + 2 < args.Length ? args[index + 2] : null;
        string message;
        int code;
        try
        {
            message = action.Equals("install", StringComparison.OrdinalIgnoreCase) ? Install()
                    : action.Equals("uninstall", StringComparison.OrdinalIgnoreCase) ? Uninstall()
                    : "Unbekannte Aktion: " + action;
            code = 0;
        }
        catch (Exception ex)
        {
            message = "Fehler: " + ex.Message;
            code = 1;
        }
        // Nur eine Datei im Temp-Ordner beschreiben, die der Aufrufer selbst vorgegeben hat.
        if (resultFile is not null && Path.GetFileName(resultFile).StartsWith("AetherRGB-chroma-", StringComparison.Ordinal)
            && string.Equals(Path.GetFullPath(Path.GetDirectoryName(resultFile)!).TrimEnd('\\'), Path.GetFullPath(Path.GetTempPath()).TrimEnd('\\'), StringComparison.OrdinalIgnoreCase))
        {
            try { File.WriteAllText(resultFile, message); } catch (IOException) { }
        }
        return code;
    }

    private static string Install()
    {
        if (!IsAvailable) return "Diese AetherRGB-Version enthält keine Chroma-Bridge-DLL (nur im offiziellen Release-Build).";
        var lines = new List<string>();
        foreach (var t in Targets())
        {
            var path = Path.Combine(t.Directory, t.FileName);
            switch (StateOf(path, t.Resource))
            {
                case FileState.Foreign:
                    lines.Add($"{t.FileName} ({t.Label}): Es gibt schon eine andere DLL (vermutlich von Razer Synapse) — nicht ersetzt.");
                    continue;
                case FileState.Ours:
                    lines.Add($"{t.FileName} ({t.Label}): bereits aktuell.");
                    continue;
            }
            var data = ReadResource(t.Resource)!;
            var temp = path + ".aether-new";
            try
            {
                File.WriteAllBytes(temp, data);
                File.Move(temp, path, overwrite: true);
                lines.Add($"{t.FileName} ({t.Label}): installiert.");
            }
            catch (Exception e) when (e is IOException or UnauthorizedAccessException)
            {
                try { File.Delete(temp); } catch (IOException) { } catch (UnauthorizedAccessException) { }
                lines.Add($"{t.FileName} ({t.Label}): konnte nicht geschrieben werden — läuft gerade ein Spiel, das sie geladen hat? ({e.Message})");
            }
        }
        return string.Join("\n", lines);
    }

    private static string Uninstall()
    {
        var lines = new List<string>();
        foreach (var t in Targets())
        {
            var path = Path.Combine(t.Directory, t.FileName);
            switch (StateOf(path, t.Resource))
            {
                case FileState.Missing:
                    continue;
                case FileState.Foreign:
                    lines.Add($"{t.FileName}: gehört nicht zu AetherRGB — nicht gelöscht.");
                    continue;
            }
            try
            {
                File.Delete(path);
                lines.Add($"{t.FileName}: entfernt.");
            }
            catch (Exception e) when (e is IOException or UnauthorizedAccessException)
            {
                lines.Add($"{t.FileName}: konnte nicht gelöscht werden — läuft gerade ein Spiel? ({e.Message})");
            }
        }
        return lines.Count == 0 ? "Die Chroma-Bridge war nicht installiert." : string.Join("\n", lines);
    }
}
