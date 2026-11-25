using System.IO;
using System.Windows;
using Forms = System.Windows.Forms;
using AetherRGB.Core.Diagnostics;
using AetherRGB.Core.Layout;

namespace AetherRGB.App;

/// <summary>Profile: Laden beim Start, globale Hotkeys, Untermenü im Tray und Steuerung über die lokale API.
/// Gewechselt wird immer in <see cref="EffectsView.ActivateProfile"/>, damit Oberfläche und Engine denselben Stand haben.</summary>
public partial class MainWindow
{
    private LightingProfileStore _profiles = null!;
    private GlobalHotkeys? _hotkeys;
    private ProfileManagerWindow? _profileManager;
    private Forms.ToolStripMenuItem? _trayProfileMenu;
    private ProfileControl? _profileControl;

    private void InitializeProfiles()
    {
        bool existed = File.Exists(LightingProfileStore.DefaultPath);
        _profiles = LightingProfileStore.LoadOrCreate(_scenes);
        if (!existed)
        {
            try { _profiles.Save(); }
            catch (Exception ex) { Log("Profile konnten nicht gespeichert werden: " + ExceptionText.Describe(ex), LogLevel.Error); }
            Log("Profile: Die bisherigen Effekt-Fenster sind jetzt das Profil „Standard“.");
        }
        _profileControl = new ProfileControl(this);
    }

    /// <summary>Nach dem Aufbau von Effekte-Seite und Tray: Hotkeys anmelden und Tray-Menü füllen.</summary>
    private void StartProfiles()
    {
        EffectsPanel.OpenProfileManager = OpenProfileManager;
        EffectsPanel.ProfilesChanged += OnProfilesChanged;
        try { _hotkeys = new GlobalHotkeys(this); }
        catch (Exception ex) { Log("Globale Hotkeys nicht verfügbar: " + ExceptionText.Describe(ex), LogLevel.Warning); }
        OnProfilesChanged();
        Log($"Profile: {_profiles.Profiles.Count}, aktiv „{_profiles.Active?.Name}“ — " +
            string.Join(", ", _profiles.Profiles.Select(p => p.Hotkey is null ? p.Name : $"{p.Name} [{GlobalHotkeys.Format(p.Hotkey)}]")),
            LogLevel.Debug);
    }

    private void OnProfilesChanged()
    {
        RegisterProfileHotkeys();
        RebuildTrayProfileMenu();
        if (_trayIcon is not null)
            _trayIcon.Text = _profiles.Active is { } a ? Truncate($"AetherRGB — {a.Name}", 63) : "AetherRGB";
    }

    private static string Truncate(string text, int max) => text.Length <= max ? text : text[..(max - 1)] + "…";

    private void RegisterProfileHotkeys()
    {
        if (_hotkeys is null) return;
        var list = new List<(ProfileHotkey, Action, string)>();
        foreach (var profile in _profiles.Profiles)
            if (profile.Hotkey is { } key)
            {
                var p = profile;
                list.Add((key, () => ActivateFromHotkey(p, key), $"Profil „{p.Name}“"));
            }
        if (_profiles.NextHotkey is { } next) list.Add((next, () => StepProfile(+1, "Hotkey " + GlobalHotkeys.Format(next)), "Nächstes Profil"));
        if (_profiles.PreviousHotkey is { } prev) list.Add((prev, () => StepProfile(-1, "Hotkey " + GlobalHotkeys.Format(prev)), "Vorheriges Profil"));

        var failed = _hotkeys.SetAll(list);
        foreach (var f in failed)
            Log($"Hotkey {f} konnte nicht angemeldet werden — vermutlich benutzt ihn schon ein anderes Programm. " +
                "Bitte in der Profilverwaltung eine andere Kombination wählen.", LogLevel.Warning);
    }

    private void ActivateFromHotkey(LightingProfile profile, ProfileHotkey key)
    {
        if (profile.Id == _profiles.ActiveProfileId)
        {
            Log($"Hotkey {GlobalHotkeys.Format(key)}: „{profile.Name}“ ist schon aktiv.", LogLevel.Debug);
            return;
        }
        EffectsPanel.ActivateProfile(profile, "Hotkey " + GlobalHotkeys.Format(key));
    }

    private LightingProfile? StepProfile(int direction, string via)
    {
        if (_profiles.Neighbor(direction) is not { } target) return null;
        EffectsPanel.ActivateProfile(target, via);
        return target;
    }

    private void RebuildTrayProfileMenu()
    {
        if (_trayIcon?.ContextMenuStrip is not { } menu) return;
        if (_trayProfileMenu is null)
        {
            _trayProfileMenu = new Forms.ToolStripMenuItem("Profil");
            menu.Items.Insert(1, _trayProfileMenu);
        }
        _trayProfileMenu.DropDownItems.Clear();
        foreach (var profile in _profiles.Profiles)
        {
            var p = profile;
            var item = new Forms.ToolStripMenuItem(p.Name)
            {
                Checked = p.Id == _profiles.ActiveProfileId,
                ShortcutKeyDisplayString = GlobalHotkeys.Format(p.Hotkey)
            };
            item.Click += (_, _) => Dispatcher.BeginInvoke(() => EffectsPanel.ActivateProfile(p, "Infobereich"));
            _trayProfileMenu.DropDownItems.Add(item);
        }
        _trayProfileMenu.Text = _profiles.Active is { } a ? $"Profil: {a.Name}" : "Profil";
    }

    private void OpenProfileManager(LightingProfile? select)
    {
        if (_profileManager is not null)
        {
            if (_profileManager.WindowState == WindowState.Minimized) _profileManager.WindowState = WindowState.Normal;
            _profileManager.Activate();
            return;
        }
        if (!IsVisible) RestoreFromTray();
        _profileManager = new ProfileManagerWindow(_profiles, EffectsPanel, _hotkeys, select) { Owner = this };
        _profileManager.Closed += (_, _) => _profileManager = null;
        _profileManager.Show();
    }

    /// <summary>Für die lokale API: alles auf den UI-Thread holen, dort lebt die Profilliste.</summary>
    private sealed class ProfileControl : ILightingProfileControl
    {
        private readonly MainWindow _window;
        public ProfileControl(MainWindow window) => _window = window;

        public IReadOnlyList<(string Id, string Name, bool Active, string Summary)> ListProfiles() =>
            _window.Dispatcher.Invoke(() => _window._profiles.Profiles
                .Select(p => (p.Id, p.Name, p.Id == _window._profiles.ActiveProfileId, p.Summary))
                .ToList());

        public Task<string?> ActivateAsync(string idOrName) =>
            _window.Dispatcher.InvokeAsync(() =>
            {
                if (_window._profiles.Find(idOrName) is not { } p) return null;
                if (p.Id != _window._profiles.ActiveProfileId) _window.EffectsPanel.ActivateProfile(p, "lokale API");
                return (string?)p.Name;
            }).Task;

        public Task<string?> StepAsync(int direction) =>
            _window.Dispatcher.InvokeAsync(() => _window.StepProfile(direction, "lokale API")?.Name).Task;
    }
}
