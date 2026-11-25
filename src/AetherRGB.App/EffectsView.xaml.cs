using System.Globalization;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using AetherRGB.Core.Devices;
using AetherRGB.Core.Diagnostics;
using AetherRGB.Core.Effects;
using AetherRGB.Core.Effects.Custom;
using AetherRGB.Core.Layout;
using AetherRGB.Core.Screen;
using AetherRGB.Providers.Wled;
using Forms = System.Windows.Forms;

namespace AetherRGB.App;

/// <summary>Eine platzierbare Zone in der Geräteliste rechts neben dem Effekt-Player.</summary>
public sealed class AvailableZone
{
    public required string DeviceId { get; init; }
    public required int ZoneIndex { get; init; }
    public required int LedCount { get; init; }
    public required string DisplayName { get; init; }
    public required string Vendor { get; init; }
    public string Details { get; set; } = "";
    public string Key => LedShapeStore.KeyFor(DeviceId, ZoneIndex);
}

/// <summary>Effekte-Seite im SignalRGB-Stil: mehrere Effekt-Fenster als Tabs (Plus-Knopf), pro Fenster eine
/// Fläche mit Live-Vorschau des Effekts, auf die Geräte aus der Liste rechts gezogen und dort verschoben,
/// skaliert und gedreht werden. Darunter die Eigenschaften des gewählten Geräts.</summary>
public partial class EffectsView : UserControl
{
    private const int PreviewW = 192, PreviewH = 96;

    private CanvasEffectEngine? _engine;
    private SceneCollection _scenes = new();
    private LedShapeStore _shapes = new();
    private CustomEffectStore? _customEffects;
    private LightingProfileStore? _profiles;
    private EffectEditorWindow? _editor;
    private EffectScene? _scene;
    private readonly List<DeviceVisual> _visuals = new();
    private DeviceVisual? _selected;
    private List<AvailableZone> _available = new();
    private readonly HashSet<string> _onlineKeys = new();

    private readonly WriteableBitmap _preview = new(PreviewW, PreviewH, 96, 96, PixelFormats.Bgra32, null);
    private readonly int[] _previewPixels = new int[PreviewW * PreviewH];
    private readonly DispatcherTimer _previewTimer = new() { Interval = TimeSpan.FromMilliseconds(33) };
    // Beim Ziehen des Tempo-Reglers nicht bei jedem Wert scenes.json schreiben.
    private readonly DispatcherTimer _saveDebounce = new() { Interval = TimeSpan.FromMilliseconds(500) };

    private bool _suppressUiEvents;
    private Point _dragStart;
    private AvailableZone? _dragCandidate;

    /// <summary>Meldung für die Log-Leiste des Hauptfensters.</summary>
    public event Action<string, LogLevel>? LogMessage;

    /// <summary>Vom Hauptfenster gesetzt: übernimmt WLED-Geräte in die Konfiguration und verbindet neu.</summary>
    public Func<IReadOnlyList<(string Name, string Host)>, Task>? AddWledDevicesAsync { get; set; }

    public EffectsView()
    {
        InitializeComponent();

        PreviewImage.Source = _preview;
        _saveDebounce.Tick += (_, _) => { _saveDebounce.Stop(); SaveScenes(); };
        GridOverlay.Fill = CreateGridBrush();
        EffectCombo.ItemsSource = EffectCatalog.All;

        foreach (var box in new[] { FieldX, FieldY, FieldWidth, FieldHeight, FieldRotation, FieldDeviceBrightness, FieldLayoutBrightness })
        {
            box.LostFocus += (_, _) => ApplyFields();
            box.KeyDown += (_, e) => { if (e.Key == Key.Enter) ApplyFields(); };
        }
        PreviewKeyDown += OnPreviewKeyDown;

        // Eigener Effekt gespeichert/importiert/gelöscht → Auswahl neu aufbauen (kommt vom Dateiwächter-Thread).
        EffectCatalog.Changed += () => Dispatcher.BeginInvoke(OnCatalogChanged);

        _previewTimer.Tick += (_, _) => RenderPreview();
        IsVisibleChanged += (_, _) =>
        {
            if (IsVisible) _previewTimer.Start(); else _previewTimer.Stop();
            // Noch nicht gespeichertes Tempo beim Verlassen der Seite sofort schreiben.
            if (!IsVisible && _saveDebounce.IsEnabled) { _saveDebounce.Stop(); SaveScenes(); }
        };
    }

    public void Initialize(CanvasEffectEngine engine, SceneCollection scenes, LedShapeStore shapes, CustomEffectStore customEffects,
        LightingProfileStore profiles)
    {
        _engine = engine;
        _scenes = scenes;
        _profiles = profiles;
        _shapes = shapes;
        _customEffects = customEffects;
        _scenes.AlignSynced(engine.Elapsed);
        BuildTabs();
        ShowScene(_scenes.Scenes.FirstOrDefault(s => s.Id == _scenes.ActiveSceneId) ?? _scenes.Scenes[0]);
        WarnMissingEffects();
        RefreshProfileButton();
        ProfilesChanged += RefreshProfileButton;
    }

    /// <summary>Neue Geräteliste nach "Geräte neu suchen" — aktualisiert die Liste rechts, LED-Anzahlen und
    /// Namen bereits platzierter Geräte und dunkelt platzierte, aber nicht gefundene Geräte ab.</summary>
    public void SetDevices(IReadOnlyList<RgbDevice> devices)
    {
        _available = devices
            .SelectMany(d => d.Zones.Select(z => new AvailableZone
            {
                DeviceId = d.Id,
                ZoneIndex = z.Index,
                LedCount = z.LedCount,
                DisplayName = d.Zones.Count > 1 ? $"{d.Name} — {z.Name}" : d.Name,
                Vendor = d.Vendor.ToString()
            }))
            .ToList();

        _onlineKeys.Clear();
        foreach (var zone in _available) _onlineKeys.Add(zone.Key);

        UpdatePlacedItems();
        RefreshDeviceList();
        if (_scene is not null) ShowScene(_scene);
    }

    /// <summary>LED-Anzahl und Namen platzierter Geräte an die zuletzt gefundenen anpassen.</summary>
    private void UpdatePlacedItems()
    {
        foreach (var scene in _scenes.Scenes)
            foreach (var item in scene.Items)
                if (_available.FirstOrDefault(a => a.DeviceId == item.DeviceId && a.ZoneIndex == item.ZoneIndex) is { } zone)
                {
                    item.LedCount = zone.LedCount;
                    item.DisplayName = zone.DisplayName;
                }
    }

    // ===================== Profile =====================

    /// <summary>Die laufenden Effekt-Fenster (für „Mit jetzigem Stand überschreiben“ in der Profilverwaltung).</summary>
    public SceneCollection Scenes => _scenes;

    /// <summary>Nach jedem Wechsel, Anlegen oder Umbenennen — das Hauptfenster baut Tray-Menü und Hotkeys neu auf.</summary>
    public event Action? ProfilesChanged;

    /// <summary>Vom Hauptfenster gesetzt: öffnet die Profilverwaltung, optional mit diesem Profil ausgewählt.</summary>
    public Action<LightingProfile?>? OpenProfileManager { get; set; }

    /// <summary>Wechselt zu <paramref name="profile"/>: der jetzige Stand wandert (falls eingestellt) ins bisher
    /// aktive Profil, dann werden alle Effekt-Fenster ausgetauscht. <paramref name="via"/> steht im Log
    /// (z. B. „Hotkey Strg+Alt+1“).</summary>
    public void ActivateProfile(LightingProfile profile, string via)
    {
        if (_profiles is null || _engine is null) return;
        _saveDebounce.Stop();
        _editor?.BeforeScenesReplaced();
        _profiles.Activate(profile, _scenes, _engine.Elapsed);
        UpdatePlacedItems();
        ShowScene(_scenes.Scenes.FirstOrDefault(s => s.Id == _scenes.ActiveSceneId) ?? _scenes.Scenes[0]);
        RefreshDeviceList();
        WarnMissingEffects();
        _editor?.AfterScenesReplaced();
        SaveScenes();
        SaveProfiles();
        LogMessage?.Invoke($"Profil „{profile.Name}“ aktiv ({via}) — {profile.Summary}.", LogLevel.Info);
        ProfilesChanged?.Invoke();
    }

    public void SaveProfiles()
    {
        try { _profiles?.Save(); }
        catch (Exception ex) { LogMessage?.Invoke("Profile konnten nicht gespeichert werden: " + ExceptionText.Describe(ex), LogLevel.Error); }
    }

    /// <summary>Jetzigen Stand als neues Profil anlegen und gleich aktivieren — danach gehören weitere Änderungen
    /// zu ihm, das bisherige Profil behält seinen Stand.</summary>
    public LightingProfile? SaveAsNewProfile()
    {
        if (_profiles is null || _engine is null) return null;
        if (_profiles.RememberChanges && _profiles.Active is { } previous) _profiles.Capture(previous, _scenes);
        var profile = _profiles.CreateFromCurrent(_profiles.UniqueName("Profil"), _scenes);
        _profiles.ActiveProfileId = profile.Id;
        SaveProfiles();
        LogMessage?.Invoke($"Profil „{profile.Name}“ aus dem jetzigen Stand angelegt — {profile.Summary}.", LogLevel.Info);
        ProfilesChanged?.Invoke();
        return profile;
    }

    /// <summary>Aktives Profil mit dem jetzigen Stand überschreiben.</summary>
    public void SaveToActiveProfile()
    {
        if (_profiles?.Active is not { } active) return;
        _profiles.Capture(active, _scenes);
        SaveProfiles();
        LogMessage?.Invoke($"Profil „{active.Name}“ mit dem jetzigen Stand gespeichert — {active.Summary}.", LogLevel.Info);
        ProfilesChanged?.Invoke();
    }

    /// <summary>Profilverwaltung hat Namen, Hotkeys oder Reihenfolge geändert: speichern und alle benachrichtigen.</summary>
    public void NotifyProfilesChanged()
    {
        SaveProfiles();
        ProfilesChanged?.Invoke();
    }

    /// <summary>Profilverwaltung hat etwas geändert (Name, Hotkey, Reihenfolge) — Knopf neu beschriften.</summary>
    public void RefreshProfileButton()
    {
        var name = _profiles?.Active?.Name;
        ProfileButton.Content = name is null ? "🗂  Profil ▾" : $"🗂  {name} ▾";
    }

    private void OnProfileButtonClick(object sender, RoutedEventArgs e)
    {
        if (_profiles is null) return;
        var menu = new ContextMenu { PlacementTarget = ProfileButton, Placement = System.Windows.Controls.Primitives.PlacementMode.Bottom };
        menu.Items.Add(new MenuItem { Header = "Profil wechseln:", IsEnabled = false });
        foreach (var profile in _profiles.Profiles)
        {
            bool active = profile.Id == _profiles.ActiveProfileId;
            var key = GlobalHotkeys.Format(profile.Hotkey);
            var entry = new MenuItem
            {
                Header = (active ? "●  " : "     ") + profile.Name + (key.Length > 0 ? $"     [{key}]" : ""),
                FontWeight = active ? FontWeights.SemiBold : FontWeights.Normal,
                ToolTip = profile.Summary
            };
            entry.Click += (_, _) => ActivateProfile(profile, "Auswahl");
            menu.Items.Add(entry);
        }
        menu.Items.Add(new Separator());

        var saveNew = new MenuItem { Header = "＋  Jetzigen Stand als neues Profil speichern…" };
        saveNew.Click += (_, _) => { if (SaveAsNewProfile() is { } p) OpenProfileManager?.Invoke(p); };
        menu.Items.Add(saveNew);

        var overwrite = new MenuItem
        {
            Header = $"💾  „{_profiles.Active?.Name}“ mit jetzigem Stand überschreiben",
            IsEnabled = _profiles.Active is not null,
            ToolTip = _profiles.RememberChanges
                ? "Passiert beim Wechseln ohnehin automatisch — hiermit sofort"
                : "Änderungen werden beim Wechseln nicht automatisch übernommen (Profilverwaltung)"
        };
        overwrite.Click += (_, _) => SaveToActiveProfile();
        menu.Items.Add(overwrite);

        var manage = new MenuItem { Header = "⚙  Profile und Hotkeys verwalten…" };
        manage.Click += (_, _) => OpenProfileManager?.Invoke(null);
        menu.Items.Add(manage);
        menu.IsOpen = true;
    }

    // ===================== Effekt-Fenster (Tabs) =====================

    private void BuildTabs()
    {
        SceneTabs.Children.Clear();
        foreach (var scene in _scenes.Scenes)
        {
            var content = new StackPanel { Orientation = Orientation.Horizontal };
            content.Children.Add(new TextBlock
            {
                Text = (scene.IsRunning ? "▶ " : "⏸ ") + scene.Name,
                VerticalAlignment = VerticalAlignment.Center
            });
            if (scene.SyncWithOthers)
                content.Children.Add(new TextBlock
                {
                    Text = "⇄",
                    Margin = new Thickness(8, 0, 0, 0),
                    VerticalAlignment = VerticalAlignment.Center,
                    Foreground = (Brush)FindResource("BrushAccentHover"),
                    ToolTip = "Läuft synchron mit den anderen markierten Effekt-Fenstern"
                });
            if (_scenes.Scenes.Count > 1)
            {
                var close = new TextBlock
                {
                    Text = "✕",
                    Margin = new Thickness(10, 0, 0, 0),
                    VerticalAlignment = VerticalAlignment.Center,
                    Foreground = (Brush)FindResource("BrushTextMuted"),
                    ToolTip = "Effekt-Fenster schließen"
                };
                close.MouseLeftButtonDown += (_, e) => { e.Handled = true; RemoveScene(scene); };
                content.Children.Add(close);
            }

            var tab = new Button
            {
                Style = (Style)FindResource("TabButton"),
                Content = content,
                Tag = scene == _scene ? "Active" : null
            };
            tab.Click += (_, _) => ShowScene(scene);
            SceneTabs.Children.Add(tab);
        }
        UpdateSyncButton();
    }

    // ===================== Synchron laufende Effekt-Fenster =====================

    private void UpdateSyncButton()
    {
        int synced = _scenes.Scenes.Count(s => s.SyncWithOthers);
        SyncButton.Content = synced > 0 ? $"⇄  Synchron ({synced}) ▾" : "⇄  Synchron ▾";
        SyncButton.Tag = synced > 1 ? "Active" : null;
    }

    /// <summary>Dropdown mit allen Effekt-Fenstern, je mit Haken: markierte Fenster laufen mit derselben
    /// Effektzeit. Bleibt beim Anklicken offen, damit man mehrere Haken hintereinander setzen kann.</summary>
    private void OnSyncButtonClick(object sender, RoutedEventArgs e)
    {
        var menu = new ContextMenu { PlacementTarget = SyncButton, Placement = System.Windows.Controls.Primitives.PlacementMode.Bottom };
        menu.Items.Add(new MenuItem { Header = "Synchron mit den anderen markierten Effekten:", IsEnabled = false });
        foreach (var scene in _scenes.Scenes)
        {
            var entry = new MenuItem
            {
                Header = $"{(scene.IsRunning ? "▶" : "⏸")}  {scene.Name} — {EffectCatalog.Get(scene.EffectId).Name}",
                IsCheckable = true,
                IsChecked = scene.SyncWithOthers,
                StaysOpenOnClick = true
            };
            entry.Click += (_, _) => SetSceneSync(scene, entry.IsChecked);
            menu.Items.Add(entry);
        }
        menu.Items.Add(new Separator());
        var restart = new MenuItem
        {
            Header = "↺  Markierte Effekte gemeinsam neu starten",
            IsEnabled = _scenes.Scenes.Any(s => s.SyncWithOthers),
            ToolTip = "Alle markierten Fenster beginnen im selben Moment von vorn"
        };
        restart.Click += (_, _) =>
        {
            if (_engine is null) return;
            int n = _scenes.RestartSynced(_engine.Elapsed);
            LogMessage?.Invoke($"{n} synchrone Effekt-Fenster gemeinsam neu gestartet.", LogLevel.Info);
        };
        menu.Items.Add(restart);
        menu.IsOpen = true;
    }

    private void SetSceneSync(EffectScene scene, bool sync)
    {
        if (_engine is null || scene.SyncWithOthers == sync) return;
        _scenes.SetSync(scene, sync, _engine.Elapsed);
        BuildTabs();
        SaveScenes();
        int others = _scenes.Scenes.Count(s => s.SyncWithOthers && s != scene);
        LogMessage?.Invoke(sync
            ? $"'{scene.Name}' läuft synchron" + (others > 0 ? $" mit {others} weiteren Effekt-Fenster(n)." : " — markiere ein weiteres Fenster, mit dem es gleich laufen soll.")
            : $"'{scene.Name}' läuft nicht mehr synchron.", LogLevel.Info);
    }

    private void OnAddSceneClick(object sender, RoutedEventArgs e)
    {
        // Plus-Menü: entweder der aktuelle Effekt läuft im neuen Fenster weiter, oder ein anderer wird gewählt.
        var menu = new ContextMenu { PlacementTarget = AddSceneButton, Placement = System.Windows.Controls.Primitives.PlacementMode.Bottom };
        var current = EffectCatalog.Get(_scene?.EffectId);
        var keep = new MenuItem { Header = $"Aktuellen Effekt weiterlaufen lassen ({current.Name})", FontWeight = FontWeights.SemiBold };
        keep.Click += (_, _) => AddScene(current.Id);
        menu.Items.Add(keep);
        menu.Items.Add(new Separator());
        menu.Items.Add(new MenuItem { Header = "Anderen Effekt wählen:", IsEnabled = false });
        foreach (var effect in EffectCatalog.All)
        {
            var entry = new MenuItem { Header = "    " + effect.Name };
            entry.Click += (_, _) => AddScene(effect.Id);
            menu.Items.Add(entry);
        }
        menu.IsOpen = true;
    }

    private void AddScene(string effectId)
    {
        int n = 1;
        while (_scenes.Scenes.Any(s => s.Name == $"Effekt {n}")) n++;
        var scene = new EffectScene
        {
            Name = $"Effekt {n}",
            EffectId = effectId,
            CanvasWidth = _scene?.CanvasWidth ?? 1600,
            CanvasHeight = _scene?.CanvasHeight ?? 800
        };
        _scenes.Scenes.Add(scene);
        ShowScene(scene);
        SaveScenes();
        LogMessage?.Invoke($"Effekt-Fenster '{scene.Name}' mit '{EffectCatalog.Get(effectId).Name}' angelegt.", LogLevel.Info);
    }

    private void RemoveScene(EffectScene scene)
    {
        if (_scenes.Scenes.Count <= 1) return;
        if (scene.Items.Count > 0 && MessageBox.Show(Window.GetWindow(this),
                $"Effekt-Fenster '{scene.Name}' mit {scene.Items.Count} platzierten Gerät(en) schließen?",
                "AetherRGB", MessageBoxButton.YesNo, MessageBoxImage.Question) != MessageBoxResult.Yes)
            return;

        int index = _scenes.Scenes.IndexOf(scene);
        _scenes.Scenes.Remove(scene);
        if (scene == _scene) ShowScene(_scenes.Scenes[Math.Min(index, _scenes.Scenes.Count - 1)]);
        else BuildTabs();
        SaveScenes();
        RefreshDeviceList();
    }

    private void ShowScene(EffectScene scene)
    {
        _scene = scene;
        _scenes.ActiveSceneId = scene.Id;

        _suppressUiEvents = true;
        SceneNameBox.Text = scene.Name;
        EffectCombo.SelectedItem = EffectCatalog.Get(scene.EffectId);
        PlayPauseButton.Content = scene.IsRunning ? "⏸  Pausieren" : "▶  Abspielen";
        BuildEffectColorControls();
        _suppressUiEvents = false;

        SurfaceRoot.Width = DeviceCanvas.Width = scene.CanvasWidth;
        SurfaceRoot.Height = DeviceCanvas.Height = scene.CanvasHeight;
        SurfaceRoot.Clip = new RectangleGeometry(new Rect(0, 0, scene.CanvasWidth, scene.CanvasHeight), 24, 24);

        DeviceCanvas.Children.Clear();
        _visuals.Clear();
        _selected = null;
        foreach (var item in scene.Items)
            AddVisual(item);

        EmptyHint.Visibility = scene.Items.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
        BuildTabs();
        UpdateFields();
        RenderPreview();
    }

    private void OnCatalogChanged()
    {
        _suppressUiEvents = true;
        EffectCombo.ItemsSource = EffectCatalog.All;
        if (_scene is not null) EffectCombo.SelectedItem = EffectCatalog.Get(_scene.EffectId);
        _suppressUiEvents = false;
        if (_scene is not null) BuildEffectColorControls();
        WarnMissingEffects();
    }

    private readonly HashSet<string> _warnedMissing = new();

    /// <summary>Fenster, deren eigener Effekt fehlt (Datei gelöscht/umbenannt), zeigen die Regenbogen-Welle —
    /// einmal pro Effekt im Log erwähnen, damit das nicht wie ein Fehler der App aussieht.</summary>
    private void WarnMissingEffects()
    {
        foreach (var scene in _scenes.Scenes)
        {
            if (EffectCatalog.Contains(scene.EffectId)) { _warnedMissing.Remove(scene.EffectId); continue; }
            if (_warnedMissing.Add(scene.EffectId))
                LogMessage?.Invoke($"'{scene.Name}': Effekt {scene.EffectId} nicht gefunden (Datei in {CustomEffectStore.DefaultDirectory} fehlt?) — " +
                                   $"zeige stattdessen '{EffectCatalog.BuiltIn[0].Name}'.", LogLevel.Warning);
        }
    }

    private void OnSceneNameChanged(object sender, TextChangedEventArgs e)
    {
        if (_suppressUiEvents || _scene is null) return;
        _scene.Name = string.IsNullOrWhiteSpace(SceneNameBox.Text) ? "Effekt" : SceneNameBox.Text.Trim();
        BuildTabs();
        SaveScenes();
        RefreshDeviceList();
    }

    private void OnEffectChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_suppressUiEvents || _scene is null || EffectCombo.SelectedItem is not EffectDescriptor effect) return;
        _scene.EffectId = effect.Id;
        BuildEffectColorControls();
        SaveScenes();
        LogMessage?.Invoke($"'{_scene.Name}': Effekt '{effect.Name}'.", LogLevel.Info);
    }

    // ===================== Effekt-Farben und -Tempo =====================

    /// <summary>Pro wählbarer Farbe des aktuellen Effekts: Beschriftung, Farbfeld (Klick = Farbdialog)
    /// und Hex-Feld; bei bewegten Effekten zusätzlich der Tempo-Regler. Statische Effekte zeigen nichts.</summary>
    private void BuildEffectColorControls()
    {
        EffectColorPanel.Children.Clear();
        if (_scene is null) return;
        var scene = _scene;
        var descriptor = EffectCatalog.Get(scene.EffectId);
        var colors = scene.GetEffectColors();

        for (int i = 0; i < descriptor.ColorSlots.Count; i++)
        {
            int slot = i;
            var label = new TextBlock
            {
                Text = descriptor.ColorSlots[i].Label,
                Style = (Style)FindResource("MutedText"),
                VerticalAlignment = VerticalAlignment.Center,
                Margin = new Thickness(i == 0 ? 0 : 14, 0, 8, 0)
            };
            var swatch = new Border
            {
                Width = 30, Height = 30, CornerRadius = new CornerRadius(6),
                BorderThickness = new Thickness(1), BorderBrush = (Brush)FindResource("BrushBorder"),
                Background = new SolidColorBrush(ToWpf(colors[i])),
                Cursor = Cursors.Hand, ToolTip = "Farbe wählen", Margin = new Thickness(0, 0, 6, 0)
            };
            var hexBox = new TextBox { Text = colors[i].ToHex(), Width = 80, VerticalAlignment = VerticalAlignment.Center, ToolTip = "Hex-Farbe, z. B. #3B82F6" };

            void Apply(RgbColor color)
            {
                // LostFocus kann noch feuern, nachdem auf ein anderes Fenster/einen anderen Effekt gewechselt wurde.
                if (_scene != scene || scene.EffectId != descriptor.Id) return;
                hexBox.Text = color.ToHex();
                if (scene.GetEffectColors()[slot] == color) return;
                scene.SetEffectColor(slot, color);
                swatch.Background = new SolidColorBrush(ToWpf(color));
                SaveScenes();
                RenderPreview();
                LogMessage?.Invoke($"'{scene.Name}': {descriptor.Name} — {descriptor.ColorSlots[slot].Label} = {color.ToHex()}.", LogLevel.Debug);
            }

            void ApplyHex()
            {
                var text = hexBox.Text.Trim();
                if (!text.StartsWith('#')) text = "#" + text;
                try { Apply(RgbColor.FromHex(text)); }
                catch (Exception ex) when (ex is FormatException or ArgumentException or OverflowException)
                {
                    hexBox.Text = scene.GetEffectColors()[slot].ToHex();
                }
            }

            swatch.MouseLeftButtonUp += (_, _) =>
            {
                var current = scene.GetEffectColors()[slot];
                using var dialog = new Forms.ColorDialog
                {
                    FullOpen = true, AnyColor = true,
                    Color = System.Drawing.Color.FromArgb(current.R, current.G, current.B)
                };
                if (dialog.ShowDialog() == Forms.DialogResult.OK)
                    Apply(new RgbColor(dialog.Color.R, dialog.Color.G, dialog.Color.B));
            };
            hexBox.LostFocus += (_, _) => ApplyHex();
            hexBox.KeyDown += (_, e) => { if (e.Key == Key.Enter) ApplyHex(); };

            EffectColorPanel.Children.Add(label);
            EffectColorPanel.Children.Add(swatch);
            EffectColorPanel.Children.Add(hexBox);
        }

        if (descriptor.IsAnimated) AddSpeedControl(scene, descriptor);

        BuildGamePanel();

        if (descriptor.Id.StartsWith("screen-", StringComparison.Ordinal) || CanvasGameProfileEffect.ProfileIdOf(descriptor.Id) is not null)
        {
            var regions = new Button { Content = "🖥  Bildschirm-Sync …", ToolTip = "Monitor, Spiel-Profile und Bereiche (Leben, Sturm, Schaden …) einstellen" };
            regions.Click += (_, _) => OpenScreenSync?.Invoke();
            EffectColorPanel.Children.Add(regions);
        }
    }

    /// <summary>Öffnet den Bereichs-Editor des Bildschirm-Syncs (setzt das Hauptfenster).</summary>
    public Action? OpenScreenSync { get; set; }

    private void AddSpeedControl(EffectScene scene, EffectDescriptor descriptor)
    {
        var label = new TextBlock
        {
            Text = "Tempo",
            Style = (Style)FindResource("MutedText"),
            VerticalAlignment = VerticalAlignment.Center,
            Margin = new Thickness(descriptor.ColorSlots.Count == 0 ? 0 : 18, 0, 8, 0)
        };
        var slider = new Slider
        {
            Minimum = EffectCatalog.MinSpeedPercent, Maximum = EffectCatalog.MaxSpeedPercent,
            Value = scene.GetEffectSpeed(), Width = 160, VerticalAlignment = VerticalAlignment.Center,
            SmallChange = 5, LargeChange = 25, IsSnapToTickEnabled = true, TickFrequency = 5,
            ToolTip = "Geschwindigkeit des Effekts — Doppelklick setzt auf 100 % zurück"
        };
        var valueText = new TextBlock
        {
            Text = $"{scene.GetEffectSpeed()} %", Width = 48, VerticalAlignment = VerticalAlignment.Center,
            Margin = new Thickness(8, 0, 0, 0)
        };

        slider.ValueChanged += (_, e) =>
        {
            if (_scene != scene || scene.EffectId != descriptor.Id) return;
            int percent = (int)Math.Round(e.NewValue);
            scene.SetEffectSpeed(percent);
            valueText.Text = $"{percent} %";
            _saveDebounce.Stop();
            _saveDebounce.Start();
        };
        slider.MouseDoubleClick += (_, _) => slider.Value = EffectCatalog.DefaultSpeedPercent;

        EffectColorPanel.Children.Add(label);
        EffectColorPanel.Children.Add(slider);
        EffectColorPanel.Children.Add(valueText);
    }

    private static Color ToWpf(RgbColor c) => Color.FromRgb(c.R, c.G, c.B);

    // ===================== Effekt-Editor =====================

    /// <summary>Öffnet den Editor (nur einmal gleichzeitig, nicht modal — man soll nebenher die Effekte-Seite
    /// sehen). Ist im aktuellen Fenster ein eigener Effekt gewählt, wird dieser geöffnet, sonst ein neuer.</summary>
    private void OnEditorClick(object sender, RoutedEventArgs e)
    {
        if (_customEffects is null) return;
        if (_editor is not null)
        {
            if (_editor.WindowState == WindowState.Minimized) _editor.WindowState = WindowState.Normal;
            _editor.Activate();
            return;
        }
        var openId = EffectCatalog.Get(_scene?.EffectId).IsCustom && EffectCatalog.Contains(_scene?.EffectId) ? _scene!.EffectId : null;
        _editor = new EffectEditorWindow(_customEffects, _scenes, _shapes, openId, _scene) { Owner = Window.GetWindow(this) };
        _editor.LogMessage += (msg, level) => LogMessage?.Invoke(msg, level);
        _editor.UseInScene += OnEditorUseInScene;
        _editor.Closed += (_, _) => _editor = null;
        _editor.Show();
    }

    private void OnEditorUseInScene(EffectScene scene, string effectId)
    {
        scene.EffectId = effectId;
        SaveScenes();
        if (scene == _scene) ShowScene(scene);
        else BuildTabs();
        LogMessage?.Invoke($"'{scene.Name}': Effekt '{EffectCatalog.Get(effectId).Name}' (aus dem Effekt-Editor).", LogLevel.Info);
    }

    private void OnPlayPauseClick(object sender, RoutedEventArgs e)
    {
        if (_scene is null) return;
        _scene.IsRunning = !_scene.IsRunning;
        // Beim Fortsetzen wieder im Gleichschritt mit den anderen synchronen Fenstern einsteigen.
        if (_scene.IsRunning && _engine is not null) _scenes.ResyncScene(_scene, _engine.Elapsed);
        PlayPauseButton.Content = _scene.IsRunning ? "⏸  Pausieren" : "▶  Abspielen";
        BuildTabs();
        SaveScenes();
        LogMessage?.Invoke($"'{_scene.Name}' {(_scene.IsRunning ? "läuft" : "pausiert")}.", LogLevel.Info);
    }

    // ===================== Fläche / Geräte-Visuals =====================

    private DeviceVisual AddVisual(DeviceLayoutItem item)
    {
        var visual = new DeviceVisual(item, _shapes.Get(item.DeviceId, item.ZoneIndex, item.LedCount), DeviceCanvas);
        visual.SetOnline(_onlineKeys.Count == 0 || _onlineKeys.Contains(LedShapeStore.KeyFor(item.DeviceId, item.ZoneIndex)));
        visual.Selected += Select;
        visual.Changed += _ => UpdateFields();
        visual.DragCompleted += _ => SaveScenes();
        _visuals.Add(visual);
        DeviceCanvas.Children.Add(visual.Root);
        return visual;
    }

    private void Select(DeviceVisual? visual)
    {
        if (_selected is not null) _selected.IsSelected = false;
        _selected = visual;
        if (_selected is not null) _selected.IsSelected = true;
        Focus();
        UpdateFields();
    }

    private void OnCanvasBackgroundClick(object sender, MouseButtonEventArgs e)
    {
        if (e.OriginalSource == DeviceCanvas) Select(null);
    }

    private void OnCanvasDragOver(object sender, DragEventArgs e)
    {
        e.Effects = e.Data.GetDataPresent(typeof(AvailableZone)) ? DragDropEffects.Copy : DragDropEffects.None;
        e.Handled = true;
    }

    private void OnCanvasDrop(object sender, DragEventArgs e)
    {
        if (_scene is null || e.Data.GetData(typeof(AvailableZone)) is not AvailableZone zone) return;
        var pos = e.GetPosition(DeviceCanvas);

        var existing = _visuals.FirstOrDefault(v => v.Item.DeviceId == zone.DeviceId && v.Item.ZoneIndex == zone.ZoneIndex);
        if (existing is not null)
        {
            existing.Item.CenterX = pos.X;
            existing.Item.CenterY = pos.Y;
            existing.UpdateGeometry();
            Select(existing);
            SaveScenes();
            return;
        }

        // Eine LED kann nur eine Farbe zeigen — liegt die Zone schon in einem anderen Fenster, wird sie verschoben.
        foreach (var other in _scenes.Scenes.Where(s => s != _scene))
            if (other.Items.RemoveAll(i => i.DeviceId == zone.DeviceId && i.ZoneIndex == zone.ZoneIndex) > 0)
                LogMessage?.Invoke($"'{zone.DisplayName}' aus '{other.Name}' nach '{_scene.Name}' verschoben.", LogLevel.Info);

        var shape = _shapes.Get(zone.DeviceId, zone.ZoneIndex, zone.LedCount);
        var (w, h) = DefaultSize(shape);
        var item = new DeviceLayoutItem
        {
            DeviceId = zone.DeviceId,
            ZoneIndex = zone.ZoneIndex,
            LedCount = zone.LedCount,
            DisplayName = zone.DisplayName,
            CenterX = pos.X,
            CenterY = pos.Y,
            Width = w,
            Height = h
        };
        _scene.Items.Add(item);
        EmptyHint.Visibility = Visibility.Collapsed;
        Select(AddVisual(item));
        SaveScenes();
        RefreshDeviceList();
    }

    private static (double W, double H) DefaultSize(LedShape shape) => shape.Kind switch
    {
        LedShapeKind.Line => (Math.Clamp(shape.LedCount * 30, 120, 1200), 50),
        LedShapeKind.Custom => shape.AspectRatio >= 1
            ? (Math.Min(600, 220 * shape.AspectRatio), 220)
            : (220, Math.Min(600, 220 / shape.AspectRatio)),
        _ => (220, 220)
    };

    // ===================== Eigenschaften des gewählten Geräts =====================

    private void UpdateFields()
    {
        _suppressUiEvents = true;
        var item = _selected?.Item;
        SelectedTitle.Text = item?.DisplayName ?? "Kein Gerät ausgewählt";
        foreach (var b in new[] { ShapeButton, CenterButton, ResetRotationButton, RemoveButton }) b.IsEnabled = item is not null;
        foreach (var box in new[] { FieldX, FieldY, FieldWidth, FieldHeight, FieldRotation, FieldDeviceBrightness }) box.IsEnabled = item is not null;

        FieldX.Text = item is null ? "-" : Fmt(item.CenterX);
        FieldY.Text = item is null ? "-" : Fmt(item.CenterY);
        FieldWidth.Text = item is null ? "-" : Fmt(item.Width);
        FieldHeight.Text = item is null ? "-" : Fmt(item.Height);
        FieldRotation.Text = item is null ? "-" : Fmt(item.RotationDegrees);
        FieldDeviceBrightness.Text = item is null ? "-" : item.Brightness.ToString();
        FieldLayoutBrightness.Text = _scene?.Brightness.ToString() ?? "100";
        _suppressUiEvents = false;
        BuildGamePanel();
    }

    // ===================== Spiel-Anzeige auf dem gewählten Gerät =====================

    /// <summary>Profil des Effekts im aktuellen Fenster, wenn es ein Spiel-Effekt ist („Spiel: …“ oder
    /// „Bildschirm – Spiel-Profil“ mit dem gerade aktiven Profil).</summary>
    private ScreenProfile? GameProfileOfScene()
    {
        if (_scene is null) return null;
        if (CanvasGameProfileEffect.ProfileIdOf(_scene.EffectId) is { } id) return ScreenHub.Shared.FindProfile(id);
        return _scene.EffectId == "screen-profile" ? ScreenHub.Shared.CurrentProfile : null;
    }

    private static bool IsGameEffect(string effectId) =>
        effectId == "screen-profile" || CanvasGameProfileEffect.ProfileIdOf(effectId) is not null;

    /// <summary>Unter den Feldern des gewählten Geräts: welche Bereiche des Spiel-Profils es zeigt. Hauptanzeige
    /// (Leben, Schild, Sturm …) als Füllstand über die LEDs, dazu Bereiche, die sie nur überdecken, solange sie
    /// kritisch sind — z. B. Leben auf dem Strip, der nur bei sehr nahem Sturm lila blinkt.</summary>
    private void BuildGamePanel()
    {
        UpdateGameCaptions();
        GamePanel.Children.Clear();
        if (_scene is null || !IsGameEffect(_scene.EffectId))
        {
            GamePanel.Visibility = Visibility.Collapsed;
            return;
        }
        GamePanel.Visibility = Visibility.Visible;
        var muted = (Style)FindResource("MutedText");
        GamePanel.Children.Add(new TextBlock { Text = "Spiel-Anzeige auf diesem Gerät", FontWeight = FontWeights.SemiBold, FontSize = 15 });

        var profile = GameProfileOfScene();
        if (profile is null)
        {
            GamePanel.Children.Add(new TextBlock
            {
                Text = "Gerade ist kein Spiel-Profil aktiv. Wähle oben ein Profil als „Spiel: …“ oder lege eines im Bildschirm-Sync an.",
                Style = muted, TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 4, 0, 0),
            });
            return;
        }
        if (_selected?.Item is not { } item)
        {
            int assigned = _scene.Items.Count(i => i.ScreenRegions.Count > 0);
            GamePanel.Children.Add(new TextBlock
            {
                Text = $"Gerät auf der Fläche anklicken und ihm einen Bereich aus „{profile.Name}“ zuweisen (Leben, Schild, Sturm-Abstand, Schaden …). " +
                       $"Der Wert erscheint dann direkt auf seinen LEDs, z. B. volles Leben = alle LEDs grün. " +
                       (assigned == 0 ? "Noch kein Gerät zugewiesen — alle zeigen die Effekt-Fläche." : $"{assigned} Gerät(e) zugewiesen."),
                Style = muted, TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 4, 0, 0),
            });
            return;
        }

        int leds = item.ComputeLedPositions(_shapes.Get(item.DeviceId, item.ZoneIndex, item.LedCount)).Count;
        var regions = profile.Regions.Where(r => r.Enabled && r.CanShowOnDevice).ToList();
        string main = item.ScreenRegions.Count > 0 ? item.ScreenRegions[0] : "";
        var overlays = item.ScreenRegions.Skip(1).ToList();

        string Describe(ScreenRegion r) =>
            r.Rule switch
            {
                RegionRule.ColorFlash or RegionRule.ValueDrop => "Blitz über alle LEDs",
                RegionRule.Average => "Mischfarbe auf allen LEDs",
                _ when !r.IsGauge => "Stufenfarbe auf allen LEDs",
                _ when r.DeviceFill == DeviceFill.Inverse => "Füllstand, je näher desto mehr LEDs",
                _ => "Füllstand über die LEDs",
            };

        var grid = new Grid { Margin = new Thickness(0, 6, 0, 0) };
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        grid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        grid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });

        var mainLabel = new TextBlock { Text = "Hauptanzeige", Style = muted, VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(0, 0, 12, 0) };
        grid.Children.Add(mainLabel);
        var combo = new ComboBox { MinWidth = 320, HorizontalAlignment = HorizontalAlignment.Left };
        combo.Items.Add(new ComboBoxItem { Content = "Effekt-Fläche (Ebenen des Profils)", Tag = "" });
        foreach (var r in regions)
        {
            bool ok = ScreenGauge.CanAssign(r, leds);
            combo.Items.Add(new ComboBoxItem
            {
                Content = ok ? $"{r.Name} — {Describe(r)}" : $"{r.Name} — braucht mindestens {ScreenRegion.MinGaugeLeds} LEDs",
                Tag = r.Id, IsEnabled = ok,
            });
        }
        combo.SelectedItem = combo.Items.OfType<ComboBoxItem>().FirstOrDefault(i => (string)i.Tag == main) ?? combo.Items[0];
        combo.SelectionChanged += (_, _) =>
        {
            if (combo.SelectedItem is ComboBoxItem { Tag: string id }) SetGameAssignment(item, id, overlays.Where(o => o != id).ToList(), profile);
        };
        Grid.SetColumn(combo, 1);
        grid.Children.Add(combo);

        var overlayLabel = new TextBlock { Text = "Überdecken, wenn kritisch", Style = muted, VerticalAlignment = VerticalAlignment.Top, Margin = new Thickness(0, 8, 12, 0) };
        Grid.SetRow(overlayLabel, 1);
        grid.Children.Add(overlayLabel);
        var overlayPanel = new WrapPanel { Margin = new Thickness(0, 6, 0, 0) };
        foreach (var r in regions.Where(r => r.Id != main))
        {
            bool ok = ScreenGauge.CanAssign(r, leds);
            var box = new CheckBox
            {
                Content = r.Name, IsChecked = overlays.Contains(r.Id), IsEnabled = ok, Margin = new Thickness(0, 2, 16, 2),
                Foreground = (Brush)FindResource("BrushText"),
                ToolTip = ok ? (r.Rule is RegionRule.ColorFlash or RegionRule.ValueDrop
                                ? "Blitzt über die Hauptanzeige, solange der Blitz läuft."
                                : "Übernimmt das ganze Gerät nur, solange eine blinkende Stufe erreicht ist (z. B. Sturm ganz nah).")
                             : $"Füllstände brauchen mindestens {ScreenRegion.MinGaugeLeds} LEDs.",
            };
            string id = r.Id;
            box.Checked += (_, _) => { if (!overlays.Contains(id)) overlays.Add(id); SetGameAssignment(item, main, overlays, profile); };
            box.Unchecked += (_, _) => { overlays.Remove(id); SetGameAssignment(item, main, overlays, profile); };
            overlayPanel.Children.Add(box);
        }
        Grid.SetRow(overlayPanel, 1);
        Grid.SetColumn(overlayPanel, 1);
        grid.Children.Add(overlayPanel);
        GamePanel.Children.Add(grid);

        GamePanel.Children.Add(new TextBlock
        {
            Text = $"{leds} LEDs laut LED-Anordnung. Farbstufen und Blinken stellst du im Bildschirm-Sync beim jeweiligen Bereich ein. " +
                   "Hat jeder Bereich sein eigenes Gerät, braucht es kein Überdecken.",
            Style = muted, FontSize = 12, TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 6, 0, 0),
        });
    }

    /// <summary>Kopfzeilen der Geräte auf der Fläche: bei Spiel-Effekten die zugewiesenen Bereiche dazuschreiben.</summary>
    private void UpdateGameCaptions()
    {
        var profile = _scene is not null && IsGameEffect(_scene.EffectId) ? GameProfileOfScene() : null;
        foreach (var visual in _visuals)
        {
            var ids = visual.Item.ScreenRegions;
            if (profile is null || ids.Count == 0) { visual.SetCaption(null); continue; }
            string Name(string id) => id.Length == 0 ? "Fläche" : profile.Regions.FirstOrDefault(r => r.Id == id)?.Name ?? "?";
            visual.SetCaption(Name(ids[0]) + (ids.Count > 1 ? " + " + string.Join(", ", ids.Skip(1).Select(Name)) + " (kritisch)" : ""));
        }
    }

    private void SetGameAssignment(DeviceLayoutItem item, string main, List<string> overlays, ScreenProfile profile)
    {
        var list = new List<string> { main };
        list.AddRange(overlays.Where(o => o != main).Distinct());
        // Neue Liste statt Ändern der alten: Engine- und Sende-Thread lesen sie gleichzeitig.
        item.ScreenRegions = list.Count == 1 && main.Length == 0 ? new List<string>() : list;
        SaveScenes();
        RenderPreview();
        string Name(string id) => profile.Regions.FirstOrDefault(r => r.Id == id)?.Name ?? id;
        LogMessage?.Invoke(item.ScreenRegions.Count == 0
            ? $"'{item.DisplayName}': zeigt die Effekt-Fläche."
            : $"'{item.DisplayName}': zeigt {(main.Length == 0 ? "die Effekt-Fläche" : "„" + Name(main) + "“")}" +
              (overlays.Count > 0 ? ", überdeckt bei kritischem " + string.Join(", ", overlays.Select(o => "„" + Name(o) + "“")) : "") + ".", LogLevel.Info);
        // Nicht innerhalb des SelectionChanged/Checked-Ereignisses das auslösende Element entfernen.
        Dispatcher.BeginInvoke(BuildGamePanel);
    }

    private static string Fmt(double v) => Math.Round(v).ToString(CultureInfo.InvariantCulture);

    private static double? Parse(string text) =>
        double.TryParse(text.Replace(',', '.'), NumberStyles.Float, CultureInfo.InvariantCulture, out var v) ? v : null;

    private void ApplyFields()
    {
        if (_suppressUiEvents || _scene is null) return;

        if (Parse(FieldLayoutBrightness.Text) is { } lb) _scene.Brightness = (int)Math.Clamp(lb, 0, 100);

        if (_selected?.Item is { } item)
        {
            if (Parse(FieldX.Text) is { } x) item.CenterX = Math.Clamp(x, 0, _scene.CanvasWidth);
            if (Parse(FieldY.Text) is { } y) item.CenterY = Math.Clamp(y, 0, _scene.CanvasHeight);
            if (Parse(FieldWidth.Text) is { } w) item.Width = Math.Max(20, w);
            if (Parse(FieldHeight.Text) is { } h) item.Height = Math.Max(20, h);
            if (Parse(FieldRotation.Text) is { } r) item.RotationDegrees = DeviceVisual.NormalizeAngle(r);
            if (Parse(FieldDeviceBrightness.Text) is { } db) item.Brightness = (int)Math.Clamp(db, 0, 100);
            _selected.UpdateGeometry();
        }

        UpdateFields();
        SaveScenes();
    }

    private void OnCenterClick(object sender, RoutedEventArgs e)
    {
        if (_selected is null || _scene is null) return;
        _selected.Item.CenterX = _scene.CanvasWidth / 2;
        _selected.Item.CenterY = _scene.CanvasHeight / 2;
        _selected.UpdateGeometry();
        UpdateFields();
        SaveScenes();
    }

    private void OnResetRotationClick(object sender, RoutedEventArgs e)
    {
        if (_selected is null) return;
        _selected.Item.RotationDegrees = 0;
        _selected.UpdateGeometry();
        UpdateFields();
        SaveScenes();
    }

    private void OnRemoveClick(object sender, RoutedEventArgs e) => RemoveSelected();

    private void RemoveSelected()
    {
        if (_selected is null || _scene is null) return;
        _scene.Items.Remove(_selected.Item);
        DeviceCanvas.Children.Remove(_selected.Root);
        _visuals.Remove(_selected);
        _selected = null;
        EmptyHint.Visibility = _scene.Items.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
        UpdateFields();
        SaveScenes();
        RefreshDeviceList();
    }

    private void OnPreviewKeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.Delete && Keyboard.FocusedElement is not TextBox && _selected is not null)
        {
            RemoveSelected();
            e.Handled = true;
        }
    }

    // ===================== LED-Anordnung =====================

    private void OnEditShapeClick(object sender, RoutedEventArgs e)
    {
        if (_selected?.Item is { } item) EditShape(item.DeviceId, item.ZoneIndex, item.LedCount, item.DisplayName);
    }

    private void OnDeviceListDoubleClick(object sender, MouseButtonEventArgs e)
    {
        if (DeviceListBox.SelectedItem is AvailableZone zone) EditShape(zone.DeviceId, zone.ZoneIndex, zone.LedCount, zone.DisplayName);
    }

    private void EditShape(string deviceId, int zoneIndex, int reportedCount, string name)
    {
        var current = _shapes.Get(deviceId, zoneIndex, reportedCount);
        var editor = new LedShapeEditorWindow(name, reportedCount, current) { Owner = Window.GetWindow(this) };
        if (editor.ShowDialog() != true || editor.Result is not { } shape) return;

        _shapes.Set(deviceId, zoneIndex, shape);
        try { _shapes.Save(); }
        catch (Exception ex) { LogMessage?.Invoke("LED-Anordnung konnte nicht gespeichert werden: " + ExceptionText.Describe(ex), LogLevel.Error); }

        // Andere Grundform -> Rechteck in allen Fenstern auf eine passende Startgröße setzen, sonst wird
        // z. B. ein Kreis in einen flachen Streifen gequetscht.
        if (shape.Kind != current.Kind || shape.Kind == LedShapeKind.Custom)
        {
            var (w, h) = DefaultSize(shape);
            foreach (var item in _scenes.Scenes.SelectMany(s => s.Items).Where(i => i.DeviceId == deviceId && i.ZoneIndex == zoneIndex))
            {
                item.Width = w;
                item.Height = h;
            }
            SaveScenes();
        }

        foreach (var visual in _visuals.Where(v => v.Item.DeviceId == deviceId && v.Item.ZoneIndex == zoneIndex))
        {
            visual.SetShape(shape);
            visual.UpdateGeometry();
        }
        UpdateFields();
        RefreshDeviceList();
        LogMessage?.Invoke($"LED-Anordnung für '{name}' gespeichert: {ShapeName(shape)}.", LogLevel.Info);
    }

    private static string ShapeName(LedShape shape) => shape.Kind switch
    {
        LedShapeKind.Circle => $"Kreis, {shape.LedCount} LEDs",
        LedShapeKind.Square => $"Quadrat, {shape.LedCount} LEDs",
        LedShapeKind.Custom => $"frei, {shape.LedCount} LEDs",
        _ => $"Schnur, {shape.LedCount} LEDs"
    };

    // ===================== Geräteliste + Drag & Drop =====================

    private void RefreshDeviceList()
    {
        var selectedKey = (DeviceListBox.SelectedItem as AvailableZone)?.Key;
        foreach (var zone in _available)
        {
            var shape = _shapes.Get(zone.DeviceId, zone.ZoneIndex, zone.LedCount);
            var placedIn = _scenes.Scenes.FirstOrDefault(s => s.Items.Any(i => i.DeviceId == zone.DeviceId && i.ZoneIndex == zone.ZoneIndex));
            zone.Details = $"{zone.Vendor} · {(_shapes.HasCustom(zone.DeviceId, zone.ZoneIndex) ? ShapeName(shape) : $"{zone.LedCount} LEDs")}"
                           + (placedIn is null ? "" : $" · in „{placedIn.Name}“");
        }
        DeviceListBox.ItemsSource = null;
        DeviceListBox.ItemsSource = _available;
        DeviceListBox.SelectedItem = _available.FirstOrDefault(z => z.Key == selectedKey);
    }

    private void OnDeviceListMouseDown(object sender, MouseButtonEventArgs e)
    {
        _dragStart = e.GetPosition(DeviceListBox);
        var container = ItemsControl.ContainerFromElement(DeviceListBox, (DependencyObject)e.OriginalSource) as ListBoxItem;
        _dragCandidate = container?.DataContext as AvailableZone;
    }

    private void OnDeviceListMouseMove(object sender, MouseEventArgs e)
    {
        if (_dragCandidate is null || e.LeftButton != MouseButtonState.Pressed) return;
        var diff = e.GetPosition(DeviceListBox) - _dragStart;
        if (Math.Abs(diff.X) < SystemParameters.MinimumHorizontalDragDistance &&
            Math.Abs(diff.Y) < SystemParameters.MinimumVerticalDragDistance) return;

        var zone = _dragCandidate;
        _dragCandidate = null;
        DragDrop.DoDragDrop(DeviceListBox, new DataObject(typeof(AvailableZone), zone), DragDropEffects.Copy);
    }

    // ===================== WLED =====================

    private async void OnWledAddClick(object sender, RoutedEventArgs e)
    {
        var host = WledHostBox.Text.Trim();
        if (string.IsNullOrWhiteSpace(host))
        {
            WledHintText.Text = "Bitte eine IP-Adresse oder einen Hostnamen eintragen.";
            return;
        }
        var name = string.IsNullOrWhiteSpace(WledNameBox.Text) ? host : WledNameBox.Text.Trim();
        if (AddWledDevicesAsync is null) return;

        WledHintText.Text = $"Verbinde mit {host} …";
        await AddWledDevicesAsync(new[] { (name, host) });
        WledNameBox.Text = WledHostBox.Text = "";
        WledHintText.Text = _available.Any(z => z.DeviceId == $"wled:{host}")
            ? $"'{name}' hinzugefügt — jetzt auf die Fläche ziehen."
            : $"'{name}' gespeichert, aber unter {host} antwortet (noch) kein WLED.";
    }

    private async void OnWledDiscoverClick(object sender, RoutedEventArgs e)
    {
        WledDiscoverButton.IsEnabled = false;
        WledHintText.Text = "Suche WLED-Geräte im lokalen Netz …";
        try
        {
            var found = await WledDiscovery.DiscoverAsync(TimeSpan.FromSeconds(3));
            var fresh = found.Where(f => !_available.Any(z => z.DeviceId == $"wled:{f.Host}")).ToList();
            if (fresh.Count > 0 && AddWledDevicesAsync is not null)
                await AddWledDevicesAsync(fresh.Select(f => (f.Name, f.Host)).ToList());

            WledHintText.Text = found.Count == 0
                ? "Keine WLED-Geräte gefunden. Ggf. per IP hinzufügen (mDNS wird manchmal von Router/Firewall blockiert)."
                : $"{found.Count} WLED-Gerät(e) gefunden, {fresh.Count} neu hinzugefügt.";
        }
        catch (Exception ex)
        {
            WledHintText.Text = "Suche fehlgeschlagen: " + ex.Message;
        }
        finally
        {
            WledDiscoverButton.IsEnabled = true;
        }
    }

    // ===================== Live-Vorschau =====================

    private void RenderPreview()
    {
        if (_scene is null || _engine is null) return;
        var effect = _scene.Effect;
        var elapsed = _scene.GetEffectTime(_engine.Elapsed);
        // Pausierte Fenster gedimmt anzeigen, damit sofort sichtbar ist, was gerade wirklich an die LEDs geht.
        double factor = Math.Clamp(_scene.Brightness, 0, 100) / 100.0 * (_scene.IsRunning ? 1.0 : 0.35);
        double sx = _scene.CanvasWidth / PreviewW, sy = _scene.CanvasHeight / PreviewH;

        for (int py = 0; py < PreviewH; py++)
            for (int px = 0; px < PreviewW; px++)
            {
                var c = effect.Sample((px + 0.5) * sx, (py + 0.5) * sy, _scene.CanvasWidth, _scene.CanvasHeight, elapsed);
                _previewPixels[py * PreviewW + px] = unchecked((int)0xFF000000)
                                                     | ((int)(c.R * factor) << 16) | ((int)(c.G * factor) << 8) | (int)(c.B * factor);
            }
        _preview.WritePixels(new Int32Rect(0, 0, PreviewW, PreviewH), _previewPixels, PreviewW * 4, 0);

        // Jede LED-Kachel zeigt dieselbe Farbe, die die Engine an die echte LED schickt: die Effektfarbe
        // genau unter ihrer Position (inkl. Helligkeit). Pausiert nur zur Anzeige gedimmt.
        double ledFactor = _scene.IsRunning ? 1.0 : 0.35;
        foreach (var visual in _visuals)
        {
            var item = visual.Item;
            visual.SetLedColors(CanvasEffectEngine.RenderItem(_scene, item, _shapes.Get(item.DeviceId, item.ZoneIndex, item.LedCount), _engine.Elapsed), ledFactor);
        }
    }

    /// <summary>Feines dunkles Raster über der Vorschau, wie im SignalRGB-Layout.</summary>
    private static Brush CreateGridBrush()
    {
        var pen = new Pen(new SolidColorBrush(Color.FromArgb(0x38, 0, 0, 0)), 1);
        var geometry = new GeometryGroup();
        geometry.Children.Add(new LineGeometry(new Point(0, 0), new Point(20, 0)));
        geometry.Children.Add(new LineGeometry(new Point(0, 0), new Point(0, 20)));
        var brush = new DrawingBrush(new GeometryDrawing(null, pen, geometry))
        {
            TileMode = TileMode.Tile,
            Viewport = new Rect(0, 0, 20, 20),
            ViewportUnits = BrushMappingMode.Absolute,
            Viewbox = new Rect(0, 0, 20, 20),
            ViewboxUnits = BrushMappingMode.Absolute
        };
        brush.Freeze();
        return brush;
    }

    private void SaveScenes()
    {
        try { _scenes.Save(); }
        catch (Exception ex) { LogMessage?.Invoke("Effekt-Fenster konnten nicht gespeichert werden: " + ExceptionText.Describe(ex), LogLevel.Error); }
    }
}
