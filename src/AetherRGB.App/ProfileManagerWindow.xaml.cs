using System.Globalization;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using AetherRGB.Core.Layout;

namespace AetherRGB.App;

/// <summary>Profile anlegen, umbenennen, sortieren, löschen und Hotkeys vergeben. Jede Änderung wird sofort gespeichert
/// und über <see cref="EffectsView.NotifyProfilesChanged"/> an Tray-Menü und Hotkeys weitergegeben.</summary>
public partial class ProfileManagerWindow : Window
{
    private readonly LightingProfileStore _store;
    private readonly EffectsView _effects;
    private readonly GlobalHotkeys? _hotkeys;
    private bool _suppress;

    public ProfileManagerWindow(LightingProfileStore store, EffectsView effects, GlobalHotkeys? hotkeys, LightingProfile? select)
    {
        InitializeComponent();
        _store = store;
        _effects = effects;
        _hotkeys = hotkeys;

        SetupHotkeyBox(HotkeyBox, HotkeyStatus,
            () => Selected?.Hotkey, k => { if (Selected is { } p) p.Hotkey = k; }, () => Selected);
        SetupHotkeyBox(NextHotkeyBox, GlobalHotkeyStatus,
            () => _store.NextHotkey, k => _store.NextHotkey = k, () => nameof(LightingProfileStore.NextHotkey));
        SetupHotkeyBox(PreviousHotkeyBox, GlobalHotkeyStatus,
            () => _store.PreviousHotkey, k => _store.PreviousHotkey = k, () => nameof(LightingProfileStore.PreviousHotkey));

        _suppress = true;
        RememberCheck.IsChecked = _store.RememberChanges;
        _suppress = false;

        // Wechsel per Hotkey/Tray, während das Fenster offen ist → Liste aktualisieren.
        _effects.ProfilesChanged += OnExternalChange;
        Closed += (_, _) => _effects.ProfilesChanged -= OnExternalChange;

        BuildList(select ?? _store.Active);
        if (select is not null)
            Loaded += (_, _) => { NameBox.Focus(); NameBox.SelectAll(); };
    }

    private LightingProfile? Selected => (ProfileList.SelectedItem as ListBoxItem)?.Tag as LightingProfile;

    private void OnExternalChange() => BuildList(Selected);

    private void BuildList(LightingProfile? select)
    {
        _suppress = true;
        ProfileList.Items.Clear();
        foreach (var profile in _store.Profiles)
        {
            bool active = profile.Id == _store.ActiveProfileId;
            var panel = new StackPanel { Margin = new Thickness(10, 7, 10, 7) };
            var head = new DockPanel();
            var key = GlobalHotkeys.Format(profile.Hotkey);
            if (key.Length > 0)
            {
                var keyText = new TextBlock
                {
                    Text = key,
                    Foreground = (Brush)FindResource("BrushAccentHover"),
                    Margin = new Thickness(8, 0, 0, 0)
                };
                DockPanel.SetDock(keyText, Dock.Right);
                head.Children.Add(keyText);
            }
            head.Children.Add(new TextBlock
            {
                Text = (active ? "● " : "") + profile.Name,
                FontWeight = active ? FontWeights.SemiBold : FontWeights.Normal,
                TextTrimming = TextTrimming.CharacterEllipsis
            });
            panel.Children.Add(head);
            panel.Children.Add(new TextBlock
            {
                Text = profile.Summary,
                Style = (Style)FindResource("MutedText"),
                FontSize = 11,
                TextTrimming = TextTrimming.CharacterEllipsis
            });
            var item = new ListBoxItem { Content = panel, Tag = profile, ToolTip = profile.Summary };
            ProfileList.Items.Add(item);
            if (profile == select) ProfileList.SelectedItem = item;
        }
        if (ProfileList.SelectedItem is null && ProfileList.Items.Count > 0) ProfileList.SelectedIndex = 0;
        _suppress = false;
        ShowDetails();
    }

    private void ShowDetails()
    {
        var p = Selected;
        DetailPanel.IsEnabled = p is not null;
        if (p is null) return;
        bool active = p.Id == _store.ActiveProfileId;

        _suppress = true;
        NameBox.Text = p.Name;
        _suppress = false;
        HotkeyBox.Text = HotkeyText(p.Hotkey);
        NextHotkeyBox.Text = HotkeyText(_store.NextHotkey);
        PreviousHotkeyBox.Text = HotkeyText(_store.PreviousHotkey);

        var updated = p.UpdatedUtc.ToLocalTime().ToString("g", CultureInfo.GetCultureInfo("de-DE"));
        InfoText.Text = (active
                ? "Aktiv. Änderungen an den Effekt-Fenstern gehören zu diesem Profil"
                  + (_store.RememberChanges ? " und werden beim nächsten Wechsel übernommen." : " — gespeichert wird nur über „Mit jetzigem Stand überschreiben“.")
                : "Nicht aktiv.")
            + $"\n{p.Summary}\nZuletzt gespeichert: {updated}";

        ActivateButton.IsEnabled = !active;
        DeleteButton.IsEnabled = !active && _store.Profiles.Count > 1;
        DeleteButton.ToolTip = active ? "Das aktive Profil lässt sich nicht löschen — erst ein anderes aktivieren." : null;
    }

    private static string HotkeyText(ProfileHotkey? key) => key is null ? "— keiner —" : GlobalHotkeys.Format(key);

    private void Changed(LightingProfile? select = null)
    {
        _effects.NotifyProfilesChanged(); // speichert, baut Tray/Hotkeys neu, ruft OnExternalChange
        if (select is not null) BuildList(select);
    }

    // ===================== Hotkey-Eingabe =====================

    private void SetupHotkeyBox(TextBox box, TextBlock status, Func<ProfileHotkey?> get, Action<ProfileHotkey?> set, Func<object?> owner)
    {
        string defaultStatus = status.Text;
        box.GotKeyboardFocus += (_, _) =>
        {
            _hotkeys?.Suspend();
            box.Text = "Tasten drücken …  (Esc = abbrechen)";
        };
        box.LostKeyboardFocus += (_, _) =>
        {
            _hotkeys?.Resume();
            box.Text = HotkeyText(get());
        };
        box.PreviewKeyDown += (_, e) =>
        {
            e.Handled = true;
            var key = e.Key == Key.System ? e.SystemKey : e.Key;
            if (key == Key.Escape && Keyboard.Modifiers == ModifierKeys.None) { Keyboard.ClearFocus(); ProfileList.Focus(); return; }
            if (key == Key.Tab) { e.Handled = false; return; }

            var hotkey = GlobalHotkeys.FromKeyEvent(e);
            if (hotkey is null) return; // nur Modifier gedrückt — auf die eigentliche Taste warten
            if (!hotkey.IsUsable)
            {
                status.Text = $"{GlobalHotkeys.Format(hotkey)} geht nicht: Bitte mit Strg, Alt oder Win kombinieren — sonst löst die Taste im Spiel mit aus.";
                return;
            }
            if (_store.HotkeyUsedBy(hotkey, owner()) is { } usedBy)
            {
                status.Text = $"{GlobalHotkeys.Format(hotkey)} ist schon für {usedBy} vergeben.";
                return;
            }
            set(hotkey);
            status.Text = defaultStatus;
            Keyboard.ClearFocus();
            ProfileList.Focus();
            Changed(Selected);
        };
    }

    private void OnClearHotkeyClick(object sender, RoutedEventArgs e)
    {
        if (Selected is not { } p || p.Hotkey is null) return;
        p.Hotkey = null;
        Changed(p);
    }

    private void OnClearNextClick(object sender, RoutedEventArgs e)
    {
        _store.NextHotkey = null;
        Changed(Selected);
    }

    private void OnClearPreviousClick(object sender, RoutedEventArgs e)
    {
        _store.PreviousHotkey = null;
        Changed(Selected);
    }

    // ===================== Liste / Details =====================

    private void OnSelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (!_suppress) ShowDetails();
    }

    private void OnListDoubleClick(object sender, MouseButtonEventArgs e)
    {
        if (Selected is { } p && p.Id != _store.ActiveProfileId) _effects.ActivateProfile(p, "Profilverwaltung");
    }

    private void OnNameKeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.Enter) OnNameCommit(sender, e);
    }

    private void OnNameCommit(object sender, RoutedEventArgs e)
    {
        if (_suppress || Selected is not { } p) return;
        var name = NameBox.Text.Trim();
        if (name.Length == 0) { NameBox.Text = p.Name; return; }
        if (name == p.Name) return;
        p.Name = name;
        Changed(p);
    }

    private void OnNewClick(object sender, RoutedEventArgs e)
    {
        if (_effects.SaveAsNewProfile() is { } p)
        {
            BuildList(p);
            NameBox.Focus();
            NameBox.SelectAll();
        }
    }

    private void OnActivateClick(object sender, RoutedEventArgs e)
    {
        if (Selected is { } p) _effects.ActivateProfile(p, "Profilverwaltung");
    }

    private void OnOverwriteClick(object sender, RoutedEventArgs e)
    {
        if (Selected is not { } p) return;
        if (p.Id != _store.ActiveProfileId && MessageBox.Show(this,
                $"„{p.Name}“ durch die Effekt-Fenster ersetzen, wie sie gerade laufen?\n\nDer bisherige Inhalt von „{p.Name}“ geht dabei verloren.",
                "AetherRGB", MessageBoxButton.YesNo, MessageBoxImage.Question) != MessageBoxResult.Yes)
            return;
        _store.Capture(p, _effects.Scenes);
        Changed(p);
    }

    private void OnDuplicateClick(object sender, RoutedEventArgs e)
    {
        if (Selected is not { } p) return;
        // Das aktive Profil im jetzigen Stand duplizieren, nicht im zuletzt gespeicherten.
        if (p.Id == _store.ActiveProfileId && _store.RememberChanges) _store.Capture(p, _effects.Scenes);
        Changed(_store.Duplicate(p));
    }

    private void OnMoveUpClick(object sender, RoutedEventArgs e) => Move(-1);
    private void OnMoveDownClick(object sender, RoutedEventArgs e) => Move(+1);

    private void Move(int delta)
    {
        if (Selected is not { } p) return;
        _store.Move(p, delta);
        Changed(p);
    }

    private void OnDeleteClick(object sender, RoutedEventArgs e)
    {
        if (Selected is not { } p) return;
        if (MessageBox.Show(this, $"Profil „{p.Name}“ löschen?", "AetherRGB",
                MessageBoxButton.YesNo, MessageBoxImage.Question) != MessageBoxResult.Yes)
            return;
        int index = _store.Profiles.IndexOf(p);
        if (!_store.Remove(p)) return;
        Changed(_store.Profiles[Math.Min(index, _store.Profiles.Count - 1)]);
    }

    private void OnRememberChanged(object sender, RoutedEventArgs e)
    {
        if (_suppress) return;
        _store.RememberChanges = RememberCheck.IsChecked == true;
        Changed(Selected);
    }

    private void OnCloseClick(object sender, RoutedEventArgs e) => Close();
}
