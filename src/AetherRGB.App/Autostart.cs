using System.Diagnostics;
using System.IO;
using System.Security;
using System.Security.Principal;
using System.Text;
using Microsoft.Win32;

namespace AetherRGB.App;

/// <summary>Startet AetherRGB beim Anmelden an Windows minimiert (im Tray).
///
/// Normalfall: Eintrag unter <c>HKCU\...\CurrentVersion\Run</c>. Der startet die App aber immer ohne
/// Administratorrechte — das Mystic Light SDK liefert so keine Geräte, und die App müsste bei jeder
/// Anmeldung per UAC-Abfrage neu starten. Ist das SDK gewählt und läuft die App gerade mit Adminrechten,
/// legt sie deshalb stattdessen eine Aufgabe in der Windows-Aufgabenplanung an ("mit höchsten
/// Privilegien", nur für den angemeldeten Nutzer): die startet ohne UAC-Abfrage mit Adminrechten.</summary>
internal static class Autostart
{
    public const string MinimizedArgument = "--minimized";

    private const string RunKey = @"Software\Microsoft\Windows\CurrentVersion\Run";
    private const string ValueName = "AetherRGB";
    private const string TaskName = "AetherRGB";

    /// <summary>Bringt Registry/Aufgabenplanung auf den gewünschten Stand und liefert eine Zeile fürs Log
    /// (Stufe, Text). Darf im Hintergrund laufen — schtasks braucht einige 100 ms.</summary>
    public static (Core.Diagnostics.LogLevel Level, string Message) Apply(bool enabled, bool needsAdmin, bool isElevated)
    {
        if (Environment.ProcessPath is not { } exe)
            return (Core.Diagnostics.LogLevel.Warning, "Autostart: Pfad der laufenden EXE unbekannt — nichts geändert.");

        if (!enabled)
        {
            RemoveRunValue();
            if (TaskExists() && !DeleteTask(out var deleteError))
                return (Core.Diagnostics.LogLevel.Warning,
                    "Autostart aus, aber die Aufgabe „AetherRGB“ in der Aufgabenplanung ließ sich nicht entfernen (" + deleteError +
                    "). Starte AetherRGB einmal als Administrator, dann wird sie gelöscht.");
            return (Core.Diagnostics.LogLevel.Debug, "Autostart mit Windows: aus.");
        }

        // Eine vorhandene Aufgabe für genau diese EXE startet die App mit Adminrechten — die reicht in
        // jedem Fall, ein zusätzlicher Run-Eintrag würde die App doppelt starten.
        bool taskOk = TaskPointsTo(exe);
        string? createError = null;
        if (needsAdmin && isElevated && !taskOk) taskOk = CreateTask(exe, out createError);
        if (taskOk)
        {
            RemoveRunValue();
            return (Core.Diagnostics.LogLevel.Debug, "Autostart mit Windows: über die Aufgabenplanung, minimiert und mit Administratorrechten.");
        }

        if (TaskExists()) DeleteTask(out _); // zeigt auf eine alte EXE (z. B. portable Datei verschoben)
        SetRunValue($"\"{exe}\" {MinimizedArgument}");
        if (needsAdmin)
            return (Core.Diagnostics.LogLevel.Warning,
                "Autostart mit Windows: über den Run-Eintrag, aber ohne Administratorrechte — das Mystic Light SDK fragt dann bei jeder Anmeldung nach einem Neustart. " +
                (createError is not null
                    ? "Aufgabe mit Adminrechten konnte nicht angelegt werden: " + createError
                    : "Starte AetherRGB einmal als Administrator, dann richtet es den Autostart mit Adminrechten ein."));
        return (Core.Diagnostics.LogLevel.Debug, $"Autostart mit Windows: an (minimiert, {exe}).");
    }

    private static void SetRunValue(string command)
    {
        using var key = Registry.CurrentUser.CreateSubKey(RunKey, writable: true);
        if (key.GetValue(ValueName) as string != command) key.SetValue(ValueName, command);
    }

    private static void RemoveRunValue()
    {
        using var key = Registry.CurrentUser.OpenSubKey(RunKey, writable: true);
        key?.DeleteValue(ValueName, throwOnMissingValue: false);
    }

    private static bool TaskExists() => RunSchtasks($"/Query /TN \"{TaskName}\"", out _, out _) == 0;

    private static bool TaskPointsTo(string exe) =>
        RunSchtasks($"/Query /TN \"{TaskName}\" /XML", out var xml, out _) == 0
        && xml.Contains("<Command>" + SecurityElement.Escape(exe) + "</Command>", StringComparison.OrdinalIgnoreCase);

    private static bool DeleteTask(out string error)
    {
        bool ok = RunSchtasks($"/Delete /TN \"{TaskName}\" /F", out _, out error) == 0;
        return ok;
    }

    private static bool CreateTask(string exe, out string error)
    {
        var user = SecurityElement.Escape(WindowsIdentity.GetCurrent().Name);
        // Priorität 4 = normal (Standard der Aufgabenplanung wäre 7 = niedriger, das bremst die Effekt-Engine).
        // 10 s Verzögerung, damit MSI Center/LEDKeeper2 und das Netzwerk (WLED, HA) schon laufen.
        var xml = $"""
            <?xml version="1.0" encoding="UTF-16"?>
            <Task version="1.2" xmlns="http://schemas.microsoft.com/windows/2004/02/mit/task">
              <RegistrationInfo>
                <Description>AetherRGB beim Anmelden minimiert starten (mit Administratorrechten für das MSI Mystic Light SDK). Abschalten in AetherRGB unter Einstellungen.</Description>
              </RegistrationInfo>
              <Triggers>
                <LogonTrigger>
                  <Enabled>true</Enabled>
                  <UserId>{user}</UserId>
                  <Delay>PT10S</Delay>
                </LogonTrigger>
              </Triggers>
              <Principals>
                <Principal id="Author">
                  <UserId>{user}</UserId>
                  <LogonType>InteractiveToken</LogonType>
                  <RunLevel>HighestAvailable</RunLevel>
                </Principal>
              </Principals>
              <Settings>
                <MultipleInstancesPolicy>IgnoreNew</MultipleInstancesPolicy>
                <DisallowStartIfOnBatteries>false</DisallowStartIfOnBatteries>
                <StopIfGoingOnBatteries>false</StopIfGoingOnBatteries>
                <ExecutionTimeLimit>PT0S</ExecutionTimeLimit>
                <Priority>4</Priority>
              </Settings>
              <Actions Context="Author">
                <Exec>
                  <Command>{SecurityElement.Escape(exe)}</Command>
                  <Arguments>{MinimizedArgument}</Arguments>
                  <WorkingDirectory>{SecurityElement.Escape(Path.GetDirectoryName(exe) ?? "")}</WorkingDirectory>
                </Exec>
              </Actions>
            </Task>
            """;
        var file = Path.Combine(Path.GetTempPath(), $"AetherRGB-Autostart-{Guid.NewGuid():N}.xml");
        try
        {
            File.WriteAllText(file, xml, Encoding.Unicode);
            return RunSchtasks($"/Create /TN \"{TaskName}\" /XML \"{file}\" /F", out _, out error) == 0;
        }
        finally
        {
            try { File.Delete(file); } catch { /* Temp-Datei, egal */ }
        }
    }

    private static int RunSchtasks(string arguments, out string output, out string error)
    {
        output = error = "";
        try
        {
            using var process = Process.Start(new ProcessStartInfo("schtasks.exe", arguments)
            {
                UseShellExecute = false,
                CreateNoWindow = true,
                RedirectStandardOutput = true,
                RedirectStandardError = true
            });
            if (process is null) { error = "schtasks.exe startet nicht"; return -1; }
            var stdout = process.StandardOutput.ReadToEndAsync();
            var stderr = process.StandardError.ReadToEndAsync();
            if (!process.WaitForExit(15000))
            {
                try { process.Kill(); } catch { /* schon beendet */ }
                error = "schtasks.exe antwortet nicht";
                return -1;
            }
            output = stdout.Result;
            error = stderr.Result.Trim();
            if (error.Length == 0) error = $"Exitcode {process.ExitCode}";
            return process.ExitCode;
        }
        catch (Exception ex)
        {
            error = ex.Message;
            return -1;
        }
    }
}
