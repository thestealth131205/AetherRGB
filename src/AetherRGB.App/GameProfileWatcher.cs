using System.Diagnostics;
using System.Windows.Threading;
using AetherRGB.Core.Diagnostics;
using AetherRGB.Core.Layout;
using AetherRGB.Core.Screen;

namespace AetherRGB.App;

/// <summary>Schaltet automatisch zu einem Profil unter Effekte &amp; Layout, sobald der Prozess eines
/// Bildschirm-Sync-Profils läuft (<see cref="ScreenProfile.AutoLightingProfileId"/>) — unabhängig davon, ob
/// gerade ein Bildschirm-Effekt gewählt ist, denn die Aufnahme selbst läuft nur, solange so ein Effekt aktiv ist.
/// Prüft deshalb unabhängig davon per <see cref="Process.GetProcesses"/>, ob der Prozess irgendwo läuft — nicht
/// nur im Vordergrund, damit der Wechsel schon beim Start des Spiels passiert, nicht erst wenn es den Fokus hat.
/// Beendet sich der Prozess wieder, kehrt AetherRGB zum Profil zurück, das davor aktiv war. Wechselt der Nutzer
/// währenddessen von Hand zu einem anderen Profil, lässt der Beobachter das in Ruhe, bis das Spiel neu startet.</summary>
public sealed class GameProfileWatcher : IDisposable
{
    private readonly DispatcherTimer _timer;
    private readonly Func<ScreenProfileStore> _screenProfiles;
    private readonly Func<LightingProfileStore> _profiles;
    private readonly Action<LightingProfile, string> _activate;
    private readonly Action<string, LogLevel>? _log;

    private string? _matchedScreenProfileId;
    private string? _previousProfileId;

    public GameProfileWatcher(Func<ScreenProfileStore> screenProfiles, Func<LightingProfileStore> profiles,
        Action<LightingProfile, string> activate, Action<string, LogLevel>? log)
    {
        _screenProfiles = screenProfiles;
        _profiles = profiles;
        _activate = activate;
        _log = log;
        _timer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(1500) };
        _timer.Tick += (_, _) => Tick();
        _timer.Start();
    }

    private void Tick()
    {
        var candidates = _screenProfiles().Profiles
            .Where(p => p.ProcessName.Length > 0 && p.AutoLightingProfileId.Length > 0)
            .ToList();
        if (candidates.Count == 0) { _matchedScreenProfileId = null; return; }

        HashSet<string> running;
        try { running = RunningProcessNames(); }
        catch { return; }

        var match = candidates.FirstOrDefault(p => running.Contains(p.ProcessName));
        var profiles = _profiles();

        if (match is not null)
        {
            if (match.Id == _matchedScreenProfileId) return; // schon behandelt, nichts Neues
            _matchedScreenProfileId = match.Id;
            if (profiles.Find(match.AutoLightingProfileId) is not { } target || target.Id == profiles.ActiveProfileId)
            {
                _previousProfileId = null;
                return;
            }
            _previousProfileId = profiles.ActiveProfileId;
            _log?.Invoke($"Profil „{target.Name}“ automatisch aktiv ({match.ProcessName}.exe läuft).", LogLevel.Debug);
            _activate(target, $"automatisch ({match.ProcessName}.exe läuft)");
        }
        else
        {
            if (_matchedScreenProfileId is null) return; // kein zugeordnetes Spiel lief zuvor
            _matchedScreenProfileId = null;
            if (_previousProfileId is { } prevId && profiles.Find(prevId) is { } previous && previous.Id != profiles.ActiveProfileId)
                _activate(previous, "automatisch (Spiel beendet)");
            _previousProfileId = null;
        }
    }

    private static HashSet<string> RunningProcessNames()
    {
        var names = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var process in Process.GetProcesses())
        {
            try { names.Add(process.ProcessName); }
            finally { process.Dispose(); }
        }
        return names;
    }

    public void Dispose() => _timer.Stop();
}
